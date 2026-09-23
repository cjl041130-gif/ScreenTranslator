using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScreenTranslator.Models;
using ScreenTranslator.OCR;

namespace ScreenTranslator.Tests;

internal static partial class Program
{
    private static async Task TestPaddleOcrSmokeAsync()
    {
        const int width = 1600, height = 900;
        var expected = new[]
        {
            "Introduction to Computer Vision",
            "Computer vision enables machines to understand images.",
            "Reliable recognition preserves every important sentence."
        };
        var pixels = RenderOcrFixture(width, height, expected, dark: false, 1.0);
        var modelRoot = FindOcrModelRoot();
        var loadWatch = Stopwatch.StartNew();
        var log = new ScreenTranslator.Services.AppLogger(Path.Combine(_output, "paddle-ocr-logs"));
        var settings = new OcrSettings { Engine="PaddleOCR", AccuracyMode="High", EnableTileRecognition=true,
            TileSize=1408, TileOverlap=96, EnableWindowsFallback=true, LowConfidenceThreshold=.78 };
        await using var engine = new AdaptiveOcrEngine(settings, log, new PaddleOcrOnnxEngine(modelRoot), new WindowsOcrEngine());
        await engine.InitializeAsync(InferenceDevice.Cpu, CancellationToken.None);
        loadWatch.Stop();
        var watch = Stopwatch.StartNew();
        var blocks = await engine.RecognizeAsync(new OcrInput(pixels, width, height, width * 4), CancellationToken.None);
        watch.Stop();
        var actual = string.Join(" ", blocks.Select(x => x.OriginalText));
        File.WriteAllText(Path.Combine(_output, "paddle-ocr-smoke.json"), JsonSerializer.Serialize(new
        {
            Expected = expected,
            Actual = actual,
            Blocks = blocks.Select(x => new { x.Id, x.OriginalText, x.Confidence, x.ConfidenceSource, x.X, x.Y, x.Width, x.Height }),
            ModelLoadMs = loadWatch.Elapsed.TotalMilliseconds,
            OcrMs = watch.Elapsed.TotalMilliseconds
        }, new JsonSerializerOptions { WriteIndented = true }));
        Check(engine.IsInitialized, "PaddleOCR ONNX detection and English recognition models initialize locally");
        Check(blocks.Count >= 3, $"PaddleOCR detects all rendered English lines ({blocks.Count} blocks)");
        Check(blocks.All(x => x.ConfidenceSource == OcrConfidenceSource.Model), "PaddleOCR exposes model confidence instead of heuristic confidence");
        Check(actual.Contains("Computer Vision", StringComparison.OrdinalIgnoreCase), $"PaddleOCR recognizes the rendered title: {actual}");
        await RunOcrBenchmarkAsync(engine);
    }

    private static byte[] RenderOcrFixture(int width, int height, IReadOnlyList<string> lines, bool dark, double scale)
    {
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(dark ? Brushes.Black : Brushes.White, null, new Rect(0, 0, width, height));
            var foreground = dark ? Brushes.White : Brushes.Black;
            for (var i = 0; i < lines.Count; i++)
            {
                var text = new FormattedText(lines[i], CultureInfo.GetCultureInfo("en-US"), FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"), (i == 0 ? 58 : 36) * scale, foreground, 1);
                drawing.DrawText(text, new Point(70, 80 + i * 150));
            }
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var pixels = new byte[width * height * 4]; bitmap.CopyPixels(pixels, width * 4, 0); return pixels;
    }

    private static string FindOcrModelRoot()
    {
        var output = Path.Combine(AppContext.BaseDirectory, "ModelsData", "OCR");
        if (File.Exists(Path.Combine(output, "PP-OCRv5_server_det", "inference.onnx"))) return output;
        const string source = @"C:\翻译器\ModelsData\OCR";
        if (File.Exists(Path.Combine(source, "PP-OCRv5_server_det", "inference.onnx"))) return source;
        throw new DirectoryNotFoundException("PaddleOCR model directory was not found.");
    }

    private static async Task RunOcrBenchmarkAsync(IOcrEngine engine)
    {
        var fixtures = new[]
        {
            new OcrFixture("wps-editing-1080p-100-light",1920,1080,false,1.0,false,
                ["Editing a presentation with WPS Office", "Review every paragraph before the meeting.", "- Keep important details and technical terms."]),
            new OcrFixture("wps-fullscreen-2k-125-dark",2560,1440,true,1.25,false,
                ["Reliable Screen Translation", "Local OCR protects the original screen image.", "AI translation receives text only after recognition."]),
            new OcrFixture("browser-courseware-4k-150-light",3840,2160,false,1.5,false,
                ["Modern Computer Vision", "Feature extraction converts visual patterns into useful data.", "Accurate text recognition improves the learning experience."]),
            new OcrFixture("pdf-small-font-1080p",1920,1080,false,.62,false,
                ["Research methods and experimental results", "Small text must remain readable after document region detection.", "The complete sentence should never be reduced to a few keywords."]),
            new OcrFixture("two-column-list-1080p",1920,1080,false,.88,true,
                ["Input pipeline", "- Capture the stable page", "Output pipeline", "- Preserve every text block"])
        };
        var reports = new List<object>();
        long totalCharacters=0,totalCharacterErrors=0,totalWords=0,totalWordErrors=0,totalLines=0,recalledLines=0;
        var timings = new List<double>(); var confidences = new List<double>();
        using var process=Process.GetCurrentProcess(); process.Refresh(); var memoryBefore=process.PrivateMemorySize64/1048576d;
        foreach(var fixture in fixtures)
        {
            var ordered=Layout(fixture).OrderBy(x=>x.Y).ThenBy(x=>x.X).ToArray();
            var expectedText=string.Join(" ",ordered.Select(x=>x.Text));
            var image=RenderFixture(fixture,ordered);
            var watch=Stopwatch.StartNew();
            var blocks=await engine.RecognizeAsync(new OcrInput(image,fixture.Width,fixture.Height,fixture.Width*4),CancellationToken.None);
            watch.Stop(); timings.Add(watch.Elapsed.TotalMilliseconds); confidences.AddRange(blocks.Where(x=>x.ConfidenceSource==OcrConfidenceSource.Model).Select(x=>x.Confidence));
            var actualText=string.Join(" ",blocks.OrderBy(x=>x.Y).ThenBy(x=>x.X).Select(x=>x.OriginalText));
            var expectedChars=Normalize(expectedText);var actualChars=Normalize(actualText);var charErrors=EditDistance(expectedChars,actualChars);
            var expectedWords=Words(expectedText);var actualWords=Words(actualText);var wordErrors=EditDistance(expectedWords,actualWords);
            var lineHits=ordered.Count(line=>blocks.Any(block=>Similarity(Normalize(line.Text),Normalize(block.OriginalText))>=.82));
            totalCharacters+=expectedChars.Length;totalCharacterErrors+=charErrors;totalWords+=expectedWords.Length;totalWordErrors+=wordErrors;totalLines+=ordered.Length;recalledLines+=lineHits;
            reports.Add(new { fixture.Name, fixture.Width,fixture.Height,fixture.Dark,fixture.Scale,Expected=expectedText,Actual=actualText,
                Blocks=blocks.Count,CER=charErrors/(double)Math.Max(1,expectedChars.Length),WER=wordErrors/(double)Math.Max(1,expectedWords.Length),
                LineRecall=lineHits/(double)ordered.Length,AverageModelConfidence=blocks.Where(x=>x.ConfidenceSource==OcrConfidenceSource.Model).DefaultIfEmpty().Average(x=>x?.Confidence??0),OcrMs=watch.Elapsed.TotalMilliseconds });
        }
        process.Refresh();var memoryAfter=process.PrivateMemorySize64/1048576d;
        var summary=new { CharacterErrorRate=totalCharacterErrors/(double)Math.Max(1,totalCharacters),WordErrorRate=totalWordErrors/(double)Math.Max(1,totalWords),
            LineRecall=recalledLines/(double)Math.Max(1,totalLines),AverageModelConfidence=confidences.DefaultIfEmpty().Average(),
            AverageOcrMs=timings.Average(),MinimumOcrMs=timings.Min(),MaximumOcrMs=timings.Max(),MemoryBeforeMB=memoryBefore,MemoryAfterMB=memoryAfter,MemoryDeltaMB=memoryAfter-memoryBefore,Cases=reports };
        File.WriteAllText(Path.Combine(_output,"ocr-accuracy-report.json"),JsonSerializer.Serialize(summary,new JsonSerializerOptions{WriteIndented=true}));
        Check(summary.CharacterErrorRate<=.12,$"OCR benchmark CER <= 12% ({summary.CharacterErrorRate:P2})");
        Check(summary.WordErrorRate<=.20,$"OCR benchmark WER <= 20% ({summary.WordErrorRate:P2})");
        Check(summary.LineRecall>=.85,$"OCR benchmark line recall >= 85% ({summary.LineRecall:P2})");
    }

    private static IReadOnlyList<PositionedLine> Layout(OcrFixture fixture)
    {
        if(!fixture.TwoColumn)return fixture.Lines.Select((text,i)=>new PositionedLine(text,70,80+i*150)).ToArray();
        var half=(fixture.Lines.Count+1)/2;
        return fixture.Lines.Select((text,i)=>i<half?new PositionedLine(text,70,90+i*170):new PositionedLine(text,fixture.Width/2d+45,90+(i-half)*170)).ToArray();
    }
    private static byte[] RenderFixture(OcrFixture fixture,IReadOnlyList<PositionedLine> lines)
    {
        var visual=new DrawingVisual();using(var drawing=visual.RenderOpen())
        {
            drawing.DrawRectangle(fixture.Dark?Brushes.Black:Brushes.White,null,new Rect(0,0,fixture.Width,fixture.Height));var foreground=fixture.Dark?Brushes.White:Brushes.Black;
            for(var i=0;i<lines.Count;i++){var line=lines[i];var size=(i==0?54:32)*fixture.Scale;var text=new FormattedText(line.Text,CultureInfo.GetCultureInfo("en-US"),FlowDirection.LeftToRight,new Typeface("Segoe UI"),size,foreground,1);drawing.DrawText(text,new Point(line.X,line.Y));}
        }
        var bitmap=new RenderTargetBitmap(fixture.Width,fixture.Height,96,96,PixelFormats.Pbgra32);bitmap.Render(visual);var pixels=new byte[fixture.Width*fixture.Height*4];bitmap.CopyPixels(pixels,fixture.Width*4,0);return pixels;
    }
    private static string Normalize(string value)=>string.Join(' ',value.ToLowerInvariant().Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries));
    private static string[] Words(string value)=>Normalize(value).Split(' ',StringSplitOptions.RemoveEmptyEntries);
    private static double Similarity(string expected,string actual)=>1-EditDistance(expected,actual)/(double)Math.Max(1,Math.Max(expected.Length,actual.Length));
    private static int EditDistance(string a,string b)=>EditDistance(a.ToCharArray(),b.ToCharArray());
    private static int EditDistance<T>(IReadOnlyList<T> a,IReadOnlyList<T> b)
    {
        var previous=Enumerable.Range(0,b.Count+1).ToArray();var current=new int[b.Count+1];
        for(var i=1;i<=a.Count;i++){current[0]=i;for(var j=1;j<=b.Count;j++)current[j]=Math.Min(Math.Min(current[j-1]+1,previous[j]+1),previous[j-1]+(EqualityComparer<T>.Default.Equals(a[i-1],b[j-1])?0:1));(previous,current)=(current,previous);}return previous[b.Count];
    }
    private sealed record OcrFixture(string Name,int Width,int Height,bool Dark,double Scale,bool TwoColumn,IReadOnlyList<string> Lines);
    private sealed record PositionedLine(string Text,double X,double Y);
}
