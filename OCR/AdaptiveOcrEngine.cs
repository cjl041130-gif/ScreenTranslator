using System.Windows;
using System.Runtime.InteropServices;
using Cv2 = OpenCvSharp.Cv2;
using Mat = OpenCvSharp.Mat;
using MatType = OpenCvSharp.MatType;
using Size = OpenCvSharp.Size;
using ColorConversionCodes = OpenCvSharp.ColorConversionCodes;
using ScreenTranslator.Models;
using ScreenTranslator.Services;

namespace ScreenTranslator.OCR;

public sealed class AdaptiveOcrEngine : IOcrEngine
{
    private readonly OcrSettings _settings;
    private readonly PaddleOcrOnnxEngine _paddle;
    private readonly WindowsOcrEngine _windows;
    private readonly AppLogger _log;
    private bool _usingPaddle;
    private bool _windowsAvailable;
    public string Name => _usingPaddle ? _paddle.Name : "Windows OCR · PaddleOCR 模型不可用（备用）";
    public bool IsInitialized { get; private set; }
    public AdaptiveOcrEngine(OcrSettings settings,AppLogger log,PaddleOcrOnnxEngine? paddle=null,WindowsOcrEngine? windows=null)
    {_settings=settings;_log=log;_paddle=paddle??new();_windows=windows??new();}
    public async Task InitializeAsync(InferenceDevice preferredDevice,CancellationToken token)
    {
        if(IsInitialized)return; _settings.Normalize();
        Exception? paddleFailure=null;
        try{await _paddle.InitializeAsync(preferredDevice,token);_usingPaddle=true;_log.Info("PaddleOCR local models initialized: PP-OCRv5 server detection + English recognition");}
        catch(Exception ex) when(_settings.EnableWindowsFallback){paddleFailure=ex;_usingPaddle=false;_log.Error("PaddleOCR initialization failed; preparing Windows OCR fallback",ex);}
        if(_settings.EnableWindowsFallback)
        {
            try{await _windows.InitializeAsync(preferredDevice,token);_windowsAvailable=true;_log.Info("Windows OCR secondary fallback initialized");}
            catch(Exception ex)
            {
                _windowsAvailable=false;_log.Error("Windows OCR secondary fallback is unavailable",ex);
                if(!_usingPaddle)throw new AggregateException("PaddleOCR 与 Windows OCR 均无法初始化。",paddleFailure!,ex);
            }
        }
        IsInitialized=true;
    }
    public async Task<IReadOnlyList<OcrBlock>> RecognizeAsync(OcrInput input,CancellationToken token)
    {
        if(!IsInitialized)throw new InvalidOperationException("OCR 尚未初始化。");
        if(!_usingPaddle)return await _windows.RecognizeAsync(input,token);
        // Clean browser, online-document and PDF text is handled much faster by the
        // Windows on-device engine (hundreds of ms instead of several seconds on CPU).
        // Keep PaddleOCR for slides, pictures and as the document fallback.
        if(input.ContentKind==OcrContentKind.WebDocument&&_windowsAvailable)
        {
            var fast=await _windows.RecognizeAsync(input,token);
            if(fast.Count>0)
            {
                _log.Info($"OCR strategy: Windows document fast path; resolution={input.Width}x{input.Height}; blocks={fast.Count}");
                return fast;
            }
            _log.Info("Windows document OCR returned no text; falling back to PaddleOCR");
        }
        var blocks=await RecognizePrimaryAsync(input,token);
        var average=blocks.Count==0?0:blocks.Average(x=>x.Confidence);
        if(_settings.EnableAdaptivePreprocessing&&(blocks.Count==0||average<_settings.LowConfidenceThreshold))
        {
            var enhanced=Enhance(input);
            var enhancedBlocks=await RecognizePrimaryAsync(enhanced,token);
            blocks=Merge(blocks,enhancedBlocks,input.Width,input.Height);
            average=blocks.Count==0?0:blocks.Average(x=>x.Confidence);
            _log.Info($"Adaptive OCR enhancement completed: blocks={blocks.Count}; averageConfidence={average:F3}");
        }
        if(_settings.EnableWindowsFallback&&_windowsAvailable&&(blocks.Count==0||average<_settings.LowConfidenceThreshold))
        {
            var secondary=await _windows.RecognizeAsync(input,token);
            blocks=Merge(blocks,secondary,input.Width,input.Height);
        }
        return blocks;
    }
    private Task<IReadOnlyList<OcrBlock>> RecognizePrimaryAsync(OcrInput input,CancellationToken token)
    {
        var tiled=_settings.EnableTileRecognition&&Math.Max(input.Width,input.Height)>_settings.TileActivationLongSide;
        _log.Info($"OCR strategy: {(tiled ? "tiled" : "single-pass")}; resolution={input.Width}x{input.Height}");
        return tiled?RecognizeTilesAsync(input,token):_paddle.RecognizeAsync(input,token);
    }
    private async Task<IReadOnlyList<OcrBlock>> RecognizeTilesAsync(OcrInput input,CancellationToken token)
    {
        var all=new List<OcrBlock>();var size=_settings.TileSize;var step=size-_settings.TileOverlap;var bytes=input.BgraPixels.ToArray();
        for(var y=0;y<input.Height;y+=step)for(var x=0;x<input.Width;x+=step)
        {
            token.ThrowIfCancellationRequested();var w=Math.Min(size,input.Width-x);var h=Math.Min(size,input.Height-y);if(w<96||h<48)continue;
            var tile=new byte[w*h*4];for(var row=0;row<h;row++)bytes.AsSpan((y+row)*input.Stride+x*4,w*4).CopyTo(tile.AsSpan(row*w*4,w*4));
            var recognized=await _paddle.RecognizeAsync(new(tile,w,h,w*4),token);
            all.AddRange(recognized.Select(b=>b with{BoundingBox=new Rect(b.X+x,b.Y+y,b.Width,b.Height)}));
            if(x+w>=input.Width)break;
        }
        return Merge([],all,input.Width,input.Height);
    }
    private static OcrInput Enhance(OcrInput input)
    {
        var sourceBytes=input.BgraPixels.ToArray();
        using var source=Mat.FromPixelData(input.Height,input.Width,MatType.CV_8UC4,sourceBytes);
        using var gray=new Mat();using var contrasted=new Mat();using var blurred=new Mat();using var sharpened=new Mat();using var bgra=new Mat();
        Cv2.CvtColor(source,gray,ColorConversionCodes.BGRA2GRAY);
        using(var clahe=Cv2.CreateCLAHE(2.0,new Size(8,8)))clahe.Apply(gray,contrasted);
        Cv2.GaussianBlur(contrasted,blurred,new Size(0,0),1.0);
        Cv2.AddWeighted(contrasted,1.35,blurred,-.35,0,sharpened);
        Cv2.CvtColor(sharpened,bgra,ColorConversionCodes.GRAY2BGRA);
        var pixels=new byte[input.Width*input.Height*4];Marshal.Copy(bgra.Data,pixels,0,pixels.Length);
        return new OcrInput(pixels,input.Width,input.Height,input.Width*4);
    }
    private static IReadOnlyList<OcrBlock> Merge(IReadOnlyList<OcrBlock> primary,IReadOnlyList<OcrBlock> secondary,int width,int height)
    {
        var result=primary.ToList();
        foreach(var block in secondary.OrderByDescending(x=>x.Confidence))
        {
            var match=result.FindIndex(x=>IoU(x.BoundingBox,block.BoundingBox)>.45);
            if(match<0)result.Add(block);else if(block.Confidence>result[match].Confidence+.04)result[match]=block;
        }
        return result.OrderBy(x=>x.Y).ThenBy(x=>x.X).Select((x,i)=>x with{Id=$"ocr-{i+1}",LineIndex=i,ReadingOrder=i}).ToArray();
    }
    private static double IoU(Rect a,Rect b){var intersection=Rect.Intersect(a,b);if(intersection.IsEmpty)return 0;var area=intersection.Width*intersection.Height;return area/(a.Width*a.Height+b.Width*b.Height-area);}
    public async ValueTask DisposeAsync(){await _paddle.DisposeAsync();await _windows.DisposeAsync();_windowsAvailable=false;IsInitialized=false;}
}
