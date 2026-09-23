using System.Diagnostics;
using System.IO;
using System.Text.Json;
using WpfRect = System.Windows.Rect;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using ScreenTranslator.Models;
using ScreenTranslator.Services;

namespace ScreenTranslator.OCR;

public sealed class PaddleOcrOnnxEngine : IOcrEngine
{
    private readonly string _modelRoot;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private InferenceSession? _detector;
    private InferenceSession? _recognizer;
    private string[] _characters = [];
    public string Name => "PaddleOCR v5 · Server Detection + English Recognition · ONNX";
    public bool IsInitialized => _detector is not null && _recognizer is not null;

    public PaddleOcrOnnxEngine(string? modelRoot = null) => _modelRoot = modelRoot ?? ResolveModelRoot();

    public async Task InitializeAsync(InferenceDevice preferredDevice, CancellationToken cancellationToken)
    {
        if (IsInitialized) return;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsInitialized) return;
            var det = Path.Combine(_modelRoot, "PP-OCRv5_server_det", "inference.onnx");
            var rec = Path.Combine(_modelRoot, "en_PP-OCRv5_mobile_rec", "inference.onnx");
            var chars = Path.Combine(_modelRoot, "en_PP-OCRv5_mobile_rec", "characters.json");
            foreach (var path in new[] { det, rec, chars }) if (!File.Exists(path)) throw new FileNotFoundException("缺少 PaddleOCR 本地模型文件。", path);
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var options = new Microsoft.ML.OnnxRuntime.SessionOptions
                {
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                    ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                    IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 8),
                    InterOpNumThreads = 1,
                    // Paddle's detector accepts dynamic image shapes.  The CPU arena and
                    // memory-pattern cache otherwise retain the largest allocation for every
                    // tile shape seen during a session (particularly visible on 2K/4K pages).
                    // Disabling those caches keeps long-running private memory bounded while
                    // the sessions and model weights remain resident.
                    EnableCpuMemArena = false,
                    EnableMemoryPattern = false
                };
                _characters = JsonSerializer.Deserialize<string[]>(File.ReadAllText(chars)) ?? throw new InvalidDataException("PaddleOCR 字符表无效。");
                _detector = new Microsoft.ML.OnnxRuntime.InferenceSession(det, options);
                _recognizer = new Microsoft.ML.OnnxRuntime.InferenceSession(rec, options);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch { _detector?.Dispose(); _detector=null; _recognizer?.Dispose(); _recognizer=null; throw; }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<OcrBlock>> RecognizeAsync(OcrInput input, CancellationToken cancellationToken)
    {
        if (!IsInitialized) throw new InvalidOperationException("PaddleOCR 尚未初始化。");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await Task.Run(() => RecognizeCore(input, cancellationToken), cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private IReadOnlyList<OcrBlock> RecognizeCore(OcrInput input, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var bytes = input.BgraPixels.ToArray();
        using var source = Mat.FromPixelData(input.Height, input.Width, MatType.CV_8UC4, bytes);
        using var bgr = new Mat(); Cv2.CvtColor(source, bgr, ColorConversionCodes.BGRA2BGR);
        var boxes = Detect(bgr, token);
        var raw = new List<(string Text, double Score, WpfRect Box, bool DarkBackground)>();
        foreach (var box in boxes)
        {
            token.ThrowIfCancellationRequested();
            var roi = Clamp(box, bgr.Width, bgr.Height);
            if (roi.Width < 3 || roi.Height < 3) continue;
            using var crop = new Mat(bgr, roi);
            var (text, score) = RecognizeLine(crop);
            var mean = Cv2.Mean(crop);
            var luminance = mean.Val2 * .299 + mean.Val1 * .587 + mean.Val0 * .114;
            if (!string.IsNullOrWhiteSpace(text) && score >= .35)
                raw.Add((text.Trim(), score, new WpfRect(roi.X, roi.Y, roi.Width, roi.Height), luminance < 92));
        }
        var medianHeight = raw.Count == 0 ? 0 : raw.Select(x => x.Box.Height).Order().ElementAt(raw.Count / 2);
        return raw.Select((x,i) => new OcrBlock($"ocr-{i+1}", x.Text, Math.Clamp(x.Score,0,1), x.Box, i, i,
            Classify(x.Text, x.Box, input.Width, input.Height, medianHeight, x.DarkBackground))
            { ConfidenceSource=OcrConfidenceSource.Model, SourceLineHeight=x.Box.Height, VisualLineCount=1 }).ToArray();
    }

    private List<OpenCvSharp.Rect> Detect(Mat bgr, CancellationToken token)
    {
        const int limit = 1280;
        var scale = Math.Min(1d, limit / (double)Math.Max(bgr.Width, bgr.Height));
        var width = Math.Max(32, (int)Math.Round(bgr.Width * scale / 32) * 32);
        var height = Math.Max(32, (int)Math.Round(bgr.Height * scale / 32) * 32);
        using var resized = new Mat(); Cv2.Resize(bgr, resized, new OpenCvSharp.Size(width,height), 0,0, InterpolationFlags.Linear);
        var data = new float[3 * width * height];
        var means = new[] { .485f,.456f,.406f }; var stds = new[] { .229f,.224f,.225f };
        unsafe
        {
            for (var y=0;y<height;y++)
            {
                var row=(byte*)resized.Ptr(y);
                for(var x=0;x<width;x++) for(var c=0;c<3;c++)
                    data[c*width*height+y*width+x]=((row[x*3+c]/255f)-means[c])/stds[c];
            }
        }
        token.ThrowIfCancellationRequested();
        var tensor = new DenseTensor<float>(data,[1,3,height,width]);
        var input = NamedOnnxValue.CreateFromTensor(_detector!.InputMetadata.Keys.First(), tensor);
        using var output = _detector.Run([input]);
        var map = output.First().AsTensor<float>();
        var dimensions = map.Dimensions.ToArray(); var mh=dimensions[^2]; var mw=dimensions[^1];
        var probabilities = map.ToArray();
        using var probability = Mat.FromPixelData(mh,mw,MatType.CV_32FC1,probabilities);
        using var binaryFloat = new Mat(); using var binary = new Mat();
        Cv2.Threshold(probability,binaryFloat,.3,255,ThresholdTypes.Binary); binaryFloat.ConvertTo(binary,MatType.CV_8UC1);
        Cv2.FindContours(binary,out var contours,out _,RetrievalModes.List,ContourApproximationModes.ApproxSimple);
        var results = new List<OpenCvSharp.Rect>();
        foreach(var contour in contours.OrderByDescending(contour => Cv2.ContourArea(contour)).Take(1000))
        {
            token.ThrowIfCancellationRequested();
            var rect=Cv2.BoundingRect(contour); if(rect.Width<3||rect.Height<3) continue;
            using var roi=new Mat(probability,rect); var score=Cv2.Mean(roi).Val0; if(score<.58) continue;
            var padX=Math.Max(2,(int)(rect.Width*.08)); var padY=Math.Max(2,(int)(rect.Height*.18));
            var x=(int)Math.Floor((rect.X-padX)*bgr.Width/(double)mw); var y=(int)Math.Floor((rect.Y-padY)*bgr.Height/(double)mh);
            var right=(int)Math.Ceiling((rect.Right+padX)*bgr.Width/(double)mw); var bottom=(int)Math.Ceiling((rect.Bottom+padY)*bgr.Height/(double)mh);
            results.Add(Clamp(new OpenCvSharp.Rect(x,y,right-x,bottom-y),bgr.Width,bgr.Height));
        }
        return MergeRects(results).OrderBy(x=>x.Y).ThenBy(x=>x.X).ToList();
    }

    private (string Text,double Score) RecognizeLine(Mat crop)
    {
        const int targetH=48, maxW=3200;
        var ratio=crop.Width/(double)Math.Max(1,crop.Height); var targetW=Math.Clamp((int)Math.Ceiling(targetH*ratio),1,maxW);
        using var resized=new Mat(); Cv2.Resize(crop,resized,new OpenCvSharp.Size(targetW,targetH),0,0,InterpolationFlags.Linear);
        var data=new float[3*targetH*targetW];
        unsafe
        {
            for(var y=0;y<targetH;y++)
            {
                var row=(byte*)resized.Ptr(y);
                for(var x=0;x<targetW;x++) for(var c=0;c<3;c++) data[c*targetH*targetW+y*targetW+x]=row[x*3+c]/127.5f-1f;
            }
        }
        var tensor=new DenseTensor<float>(data,[1,3,targetH,targetW]);
        var input=NamedOnnxValue.CreateFromTensor(_recognizer!.InputMetadata.Keys.First(),tensor);
        using var output=_recognizer.Run([input]); var scores=output.First().AsTensor<float>();
        var dims=scores.Dimensions.ToArray(); if(dims.Length!=3) throw new InvalidDataException("PaddleOCR recognition output shape invalid.");
        var values=scores.ToArray(); var steps=dims[1]; var classes=dims[2]; var previous=-1; var text=new System.Text.StringBuilder(); var confidence=0d; var count=0;
        for(var step=0;step<steps;step++)
        {
            var maxIndex=0; var maxValue=float.NegativeInfinity; var offset=step*classes;
            for(var c=0;c<classes;c++) if(values[offset+c]>maxValue){maxValue=values[offset+c];maxIndex=c;}
            if(maxIndex>0&&maxIndex!=previous&&maxIndex-1<_characters.Length){text.Append(_characters[maxIndex-1]);confidence+=maxValue;count++;}
            previous=maxIndex;
        }
        return (text.ToString(),count==0?0:confidence/count);
    }

    private static IEnumerable<OpenCvSharp.Rect> MergeRects(IEnumerable<OpenCvSharp.Rect> input)
    {
        var accepted=new List<OpenCvSharp.Rect>();
        foreach(var rect in input)
        {
            var duplicate=accepted.FindIndex(x=>IoU(x,rect)>.55 || (x.Contains(rect) && rect.Width*rect.Height < x.Width*x.Height*.85));
            if(duplicate<0) accepted.Add(rect); else accepted[duplicate]=Union(accepted[duplicate],rect);
        }
        return accepted;
    }
    private static double IoU(OpenCvSharp.Rect a,OpenCvSharp.Rect b){var i=a.Intersect(b);var area=i.Width*i.Height;return area<=0?0:area/(double)(a.Width*a.Height+b.Width*b.Height-area);}
    private static OpenCvSharp.Rect Union(OpenCvSharp.Rect a,OpenCvSharp.Rect b){var x=Math.Min(a.X,b.X);var y=Math.Min(a.Y,b.Y);var r=Math.Max(a.Right,b.Right);var d=Math.Max(a.Bottom,b.Bottom);return new(x,y,r-x,d-y);}
    private static OpenCvSharp.Rect Clamp(OpenCvSharp.Rect r,int width,int height){var x=Math.Clamp(r.X,0,width);var y=Math.Clamp(r.Y,0,height);var right=Math.Clamp(r.Right,x,width);var bottom=Math.Clamp(r.Bottom,y,height);return new(x,y,right-x,bottom-y);}
    private static OcrBlockType Classify(string text,WpfRect box,int width,int height,double median,bool darkBackground)
    {
        if(darkBackground&&text.Any(char.IsAsciiLetter))return OcrBlockType.Code;
        var trimmed=text.TrimStart(); if(trimmed.StartsWith('•')||trimmed.StartsWith("- ")||trimmed.StartsWith("* "))return OcrBlockType.Bullet;
        if(box.Y>height*.9)return OcrBlockType.Footer; if(box.Y<height*.28&&box.Height>=Math.Max(18,median*1.25))return OcrBlockType.Title;
        if(box.Y<height*.38&&box.Height>=median*1.05)return OcrBlockType.Subtitle; if(text.Any(c=>c is '∑' or '√' or '≈' or '∞'))return OcrBlockType.Formula;
        return OcrBlockType.Body;
    }
    private static string ResolveModelRoot()
    {
        var local=Path.Combine(AppContext.BaseDirectory,"ModelsData","OCR"); if(Directory.Exists(local))return local;
        var source=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..","..","ModelsData","OCR")); return source;
    }
    public async ValueTask DisposeAsync(){await _gate.WaitAsync();try{_detector?.Dispose();_detector=null;_recognizer?.Dispose();_recognizer=null;}finally{_gate.Release();_gate.Dispose();}}
}
