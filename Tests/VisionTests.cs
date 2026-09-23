using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ScreenTranslator.Capture;
using ScreenTranslator.Core;
using ScreenTranslator.Services;
using ScreenTranslator.ViewModels;
using ScreenTranslator.Vision;
using Cv = OpenCvSharp;

namespace ScreenTranslator.Tests;
internal static partial class Program
{
    private static object RectData(Rect r) => r.IsEmpty ? new { X=0d, Y=0d, Width=0d, Height=0d } : new { r.X, r.Y, r.Width, r.Height };
    private static object SnapshotData(DetectionSnapshot? d) => new
    {
        Timestamp = d?.Timestamp, Error = d?.Error,
        Target = d?.Target is not { } t ? null : new { Handle = t.Handle.ToInt64(), t.ProcessName, t.Title, t.ClassName, t.DpiScale, t.IsFullscreen, t.IsForeground, t.IsLastExternalForeground, Client = RectData(t.ClientRect) },
        Search = RectData(d?.SearchRegion ?? Rect.Empty),
        Region = d?.Region is not { } r ? null : new { Bounds = RectData(r.Bounds), r.Confidence, r.AspectRatio, r.DetectionMethod }, d?.LatencyMs,
        Candidates = d?.Candidates.Select(c => new { Bounds=RectData(c.Bounds), c.Method, c.Scores })
    };
    private static async Task RunVisionLiveAsync(MainWindow window)
    {
        var vm = (MainViewModel)window.DataContext;
        await Task.Delay(600);
        Check(vm.VisionCompletedDetections == 0 && vm.Preview is null, "Idle: no vision processing or capture");
        SaveWindow(window, "01-before.png");
        using (var rankingProbe = new TargetWindowTracker(new VisionSettings()))
        {
            rankingProbe.Start(); var selected = rankingProbe.SelectTarget(vm.SelectedMonitor!);
            File.WriteAllText(Path.Combine(_output,"start-target-ranking.json"),JsonSerializer.Serialize(new { StartWindowIsActive=window.IsActive, OwnProcessId=Environment.ProcessId,
                SelectedProcessId=selected?.ProcessId, SelectedProcessName=selected?.ProcessName, SelectedWindowHandle=selected?.Handle.ToInt64(), SelectedIsForeground=selected?.IsForeground },new JsonSerializerOptions{WriteIndented=true}));
        }
        vm.CurrentPage = vm.Pages.OfType<RecognitionViewModel>().Single();
        vm.StartCommand.Execute(null);
        await Until(() => vm.State == ApplicationState.Capturing, "Manual start enters capture");
        // MainWindow excludes itself from Windows Graphics Capture while running, so it can remain usable
        // without becoming part of the preview or blocking the presentation beneath it.
        await Task.Delay(2500);
        var rows = new List<object>(); var boxes = new List<Rect>(); var fps = new List<double>();
        var watch = Stopwatch.StartNew(); using var process = Process.GetCurrentProcess();
        var cpu = process.TotalProcessorTime; var frames = vm.PresentedFrames; var detections = vm.VisionCompletedDetections;
        var previousFrames = frames; var previousTime = 0d;
        while (watch.Elapsed.TotalSeconds < _seconds)
        {
            await Task.Delay(250); process.Refresh();
            var elapsed = watch.Elapsed.TotalSeconds;
            var rate = (vm.PresentedFrames - previousFrames) / (elapsed - previousTime); fps.Add(rate);
            previousFrames = vm.PresentedFrames; previousTime = elapsed;
            var d = vm.Detection;
            if (d?.Region is { } r) boxes.Add(r.Bounds);
            rows.Add(new { Seconds = elapsed, PreviewFPS = rate, PrivateMB = process.PrivateMemorySize64 / 1048576d, Snapshot = SnapshotData(d) });
        }
        var measuredDuration = watch.Elapsed.TotalSeconds; var cpuPercent = (process.TotalProcessorTime - cpu).TotalSeconds / watch.Elapsed.TotalSeconds / Environment.ProcessorCount * 100;
        var previewFps = (vm.PresentedFrames - frames) / watch.Elapsed.TotalSeconds;
        var visionFps = (vm.VisionCompletedDetections - detections) / watch.Elapsed.TotalSeconds;
        var last = vm.Detection;
        await Task.Delay(500);
        SaveWindow(window, "02-running.png");
        vm.PauseCommand.Execute(null); await Until(() => vm.State == ApplicationState.Paused, "Pause vision and capture");
        var pausedFrame = vm.Preview; var pausedDetection = vm.Detection; var pausedCount = vm.VisionCompletedDetections;
        await Task.Delay(600);
        Check(ReferenceEquals(pausedFrame, vm.Preview) && ReferenceEquals(pausedDetection, vm.Detection) && pausedCount == vm.VisionCompletedDetections, "Pause retains frame and region; vision work stops");
        if (vm.Preview is not null)
        {
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(vm.Preview));
            using var file = File.Create(Path.Combine(_output, "desktop-frame.png")); encoder.Save(file);
        }
        window.WindowState = WindowState.Normal;
        await Task.Delay(500); SaveWindow(window, "02-recognition-actual.png");
        vm.CurrentPage = vm.Pages.OfType<HomeViewModel>().Single(); await Task.Delay(200); SaveWindow(window, "03-home-actual.png");
        File.WriteAllText(Path.Combine(_output, "last-detection.json"), JsonSerializer.Serialize(SnapshotData(last), new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(_output, "samples.json"), JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
        double Std(IEnumerable<double> values) { var a = values.ToArray(); return a.Length == 0 ? 0 : Math.Sqrt(a.Average(x => Math.Pow(x - a.Average(), 2))); }
        File.WriteAllText(Path.Combine(_output, "performance.json"), JsonSerializer.Serialize(new
        {
            DurationSeconds=measuredDuration, PreviewFPS=previewFps, VisionFPS=visionFps, CpuPercentAllCores=cpuPercent,
            DetectionFound = boxes.Count > 0, RegionSampleCount=boxes.Count, StdX=Std(boxes.Select(b=>b.X)), StdY=Std(boxes.Select(b=>b.Y)), StdWidth=Std(boxes.Select(b=>b.Width)), StdHeight=Std(boxes.Select(b=>b.Height)),
            AverageStableIoU=boxes.Count == 0 ? 0 : boxes.Average(b=>CoordinateMapper.IoU(b,boxes[boxes.Count/2]))
        }, new JsonSerializerOptions { WriteIndented=true }));
        vm.StartCommand.Execute(null); await Until(()=>vm.State==ApplicationState.Capturing,"Resume shared state");
        vm.StopCommand.Execute(null); await Until(()=>vm.State==ApplicationState.Stopped,"Stop shared state");
        Check(vm.Detection is null && vm.Preview is null, "Stop clears candidates, region and preview");
        var stopped = vm.VisionCompletedDetections; await Task.Delay(500); Check(stopped == vm.VisionCompletedDetections,"Stopped vision does no further work");
        SaveWindow(window, "04-stopped.png");
        await vm.DisposeAsync(); window.Close();
    }
    private static async Task RunVisionUnitsAsync(MainWindow window)
    {
        var settings = new VisionSettings(); settings.Normalize();
        Check(TargetWindowTracker.IsPresentation("POWERPNT") && TargetWindowTracker.IsPresentation("wpp") && !TargetWindowTracker.IsPresentation("wps") && !TargetWindowTracker.IsPresentation("WINWORD") && !TargetWindowTracker.IsPresentation("chrome"), "Presentation classification remains strict");
        Check(TargetWindowTracker.ClassifyApplication("chrome", "Feishu Docs") == ApplicationType.Browser &&
              TargetWindowTracker.ClassifyApplication("msedge", "course.pdf") == ApplicationType.PdfViewer &&
              TargetWindowTracker.ClassifyApplication("AcroRd32", "lesson") == ApplicationType.PdfViewer &&
              TargetWindowTracker.ClassifyApplication("Feishu", "Online document") == ApplicationType.Browser &&
              TargetWindowTracker.ClassifyApplication("WINWORD", "report") == ApplicationType.Unknown,
              "Browser, browser PDF, Feishu and PDF reader targets are admitted without widening to unrelated apps");
        foreach (var scale in new[] { 1d, 1.25, 1.5, 1.75 })
        {
            var r=CoordinateMapper.PhysicalToDip(new(300,150,900,600),scale);
            Check(Math.Abs(r.Width*scale-900)<.001,$"Physical to DIP roundtrip {scale}");
            var p=PreviewCoordinateMapper.Map(new(0,0,1920,1080),1920,1080,800,600);
            Check(Math.Abs(p.Top-75)<.001 && p.Width==800 && p.Height==450,$"Uniform preview letterbox mapping {scale}");
        }
        var mon=new MonitorInfo("fixture","fixture",1920,1080,-1920,0,1.5,false,0,1);
        Check(CoordinateMapper.ScreenToFrame(new(-2000,30,400,600),mon,1920,1080)==new Rect(0,30,320,600),"Negative monitor origin and cross-monitor clipping");
        Check(PresentationDetector.IsOccluded(new(200,100,600,400),mon,[new(-1700,150,200,200)]),"Candidate intersecting a higher window is rejected");
        Check(!PresentationDetector.IsOccluded(new(200,100,600,400),mon,[new(-1000,150,200,200)]),"Unrelated higher window does not reject candidate");
        Check(PresentationDetector.OccludedRatio(new(200,100,600,400),mon,[new(-1700,150,60,60)])<.35,"Small floating UI does not disable a whole web document");
        Check(PresentationDetector.OccludedRatio(new(200,100,600,400),mon,[new(-1720,100,500,400)])>=.35,"Large covering window still blocks web OCR");
        var generator = new PresentationCandidateGenerator(settings); var scorer = new PresentationRegionScorer(settings);
        var cases = new List<object>();
        foreach(var ratio in new[]{16d/9,4d/3,1.6}) foreach(var background in new[]{245,15,95})
        {
            using var image=new Cv.Mat(700,1200,Cv.MatType.CV_8UC4,new Cv.Scalar(210,210,210,255));
            var expected=new Rect(240,120,720,Math.Round(720/ratio));
            Cv.Cv2.Rectangle(image,new Cv.Rect((int)expected.X,(int)expected.Y,(int)expected.Width,(int)expected.Height),new Cv.Scalar(background,background,background,255),-1);
            Cv.Cv2.PutText(image,"Vision test page",new Cv.Point(300,270),Cv.HersheyFonts.HersheySimplex,1.4,new Cv.Scalar(background>100?20:235,100,180,255),3);
            var ranked=generator.Generate(image,false).Select(c=>scorer.Score(c,new Rect(0,0,1200,700),Rect.Empty,false)).OrderByDescending(c=>c.Scores!.FinalScore).ToArray();
            var best=ranked.FirstOrDefault(); var iou=best is null?0:CoordinateMapper.IoU(best.Bounds,expected);
            cases.Add(new { Kind="Synthetic edit fixture, not application evidence", ratio, background, Expected=RectData(expected), Detected=RectData(best?.Bounds??Rect.Empty), IoU=iou, Confidence=best?.Scores?.FinalScore });
            Check(iou>=.85,$"Synthetic edit {ratio:F3} background {background}: IoU {iou:F3}");
        }
        foreach(var ratio in new[]{16d/9,4d/3})
        {
            using var image=new Cv.Mat(675,1200,Cv.MatType.CV_8UC4,new Cv.Scalar(0,0,0,255));
            var width=(int)Math.Round(675*ratio); var expected=new Rect((1200-width)/2,0,width,675);
            Cv.Cv2.Rectangle(image,new Cv.Rect((int)expected.X,0,width,675),new Cv.Scalar(220,240,250,255),-1);
            var actual=new FullscreenPresentationDetector(settings).Detect(image);
            Check(CoordinateMapper.IoU(actual,expected)>=.95,$"Synthetic full screen {ratio:F3}: excludes pillarbox");
        }
        using (var black=new Cv.Mat(675,1200,Cv.MatType.CV_8UC4,new Cv.Scalar(0,0,0,255)))
            Check(new FullscreenPresentationDetector(settings).Detect(black).IsEmpty,"All-black frame does not invent a full-screen page boundary");
        var tracker=new PresentationRegionTracker(settings);
        var target=new ApplicationWindowInfo(1,1,"POWERPNT","fixture","fixture",new(0,0,1200,700),new(0,0,1200,700),new(0,0,1200,700),1,"fixture",true,true,false,ApplicationType.PowerPoint,1,false);
        var high=scorer.Score(new(new Rect(200,180,700,394),"fixture",1,1,1),new(0,0,1200,700),Rect.Empty,false);
        var stable=high with { Scores=high.Scores! with { FinalScore=.80 } };
        var now=DateTimeOffset.UtcNow;
        Check(tracker.Update(stable,target,now) is null,"Single frame does not create stable region");
        tracker.Update(stable,target,now.AddMilliseconds(250));
        Check(tracker.Update(stable,target,now.AddMilliseconds(500)) is not null,"Repeated observations create stable region");
        var bad=stable with { Bounds=new(20,20,500,300) };
        Check(tracker.Update(bad,target,now.AddMilliseconds(750))?.Bounds==stable.Bounds,"One outlier does not switch region");
        Check(tracker.Update(null,target,now.AddSeconds(4)) is null,"Missing evidence expires stable region");
        Check(tracker.Update(high,target,now.AddSeconds(5)) is not null,"Very high confidence initial region is accepted without waiting for a changing desktop");
        using var testFrame=new CapturedFrame(64,64,"fixture",ArrayPool<byte>.Shared);
        var detector=new PresentationDetector(settings);
        Check(detector.Detect(testFrame,mon,null).Region is null,"No target returns no recognition region");
        var webMonitor=new MonitorInfo("web","web",1920,1080,0,0,1,false,0,1);
        var browserTarget=new ApplicationWindowInfo(2,2,"chrome","Feishu Docs","Chrome_WidgetWin_1",new(0,0,1920,1080),new(0,0,1920,1080),new(0,0,1920,1080),1,"web",true,true,false,ApplicationType.Browser,1,false);
        var webBounds=GeneralDocumentRegion.GetContentBounds(browserTarget,new Rect(0,0,1920,1080));
        Check(webBounds.Y>=100 && webBounds.Width>1880 && webBounds.Height>950,"Browser content fallback removes chrome while preserving the visible document body");
        using(var webFrame=new CapturedFrame(1920,1080,"web",ArrayPool<byte>.Shared))
        {
            var webDetection=detector.Detect(webFrame,webMonitor,browserTarget);
            Check(webDetection.Region is not null && webDetection.Region.DetectionMethod=="Browser visible content" && CoordinateMapper.IoU(webDetection.Region.Bounds,webBounds)>.99,
                "Browser and Feishu content enters recognition immediately without a slide rectangle");
        }
        using (var portrait = new Cv.Mat(1080,1920,Cv.MatType.CV_8UC4,new Cv.Scalar(238,238,238,255)))
        {
            var expected = new Rect(520,120,880,850);
            Cv.Cv2.Rectangle(portrait,new Cv.Rect(520,120,880,850),new Cv.Scalar(255,255,255,255),-1);
            Cv.Cv2.PutText(portrait,"Portrait document page",new Cv.Point(650,280),Cv.HersheyFonts.HersheySimplex,1.4,new Cv.Scalar(30,30,30,255),3);
            var actual = DocumentPageDetector.Detect(portrait,new Rect(0,0,1920,1080),out var documentConfidence);
            var pageIoU = CoordinateMapper.IoU(actual,expected);
            Check(pageIoU>=.90 && documentConfidence>=.72,$"Portrait browser/PDF page detection: IoU={pageIoU:F3}, confidence={documentConfidence:F3}");
        }
        var fakeWindows = new FakeWindows(); var slow = new SlowDetector();
        await using (var pipeline = new VisionPipeline(settings,new AppLogger(Path.Combine(_output,"pipeline-logs")),fakeWindows,slow))
        {
            pipeline.Submit(testFrame,mon); Check(pipeline.CompletedDetections==0 && !pipeline.IsProcessing,"Idle rejects vision frame admission");
            pipeline.Start(); pipeline.Submit(testFrame,mon);
            await Until(()=>slow.Entered,"Vision worker started off UI thread");
            for(var i=0;i<50;i++) pipeline.Submit(testFrame,mon);
            var stopping=pipeline.StopAsync(); slow.Release.Set(); await stopping;
            Check(pipeline.CompletedDetections==1 && pipeline.Latest is null && !pipeline.IsProcessing,"Backpressure bounds work to one frame; Stop waits and discards late results");
            Check(fakeWindows.Stops>0,"Stop detaches window tracking");
        }
        File.WriteAllText(Path.Combine(_output,"synthetic-accuracy.json"),JsonSerializer.Serialize(cases,new JsonSerializerOptions{WriteIndented=true}));
        var vm=(MainViewModel)window.DataContext;
        Check(vm.VisionCompletedDetections==0,"Unit suite does not auto start vision pipeline");
        await vm.DisposeAsync(); window.Close();
    }
    private sealed class FakeWindows : ITargetWindowTracker
    {
        public nint LastExternalForegroundWindow=>0; public int Stops;
        public void Start(){} public void Stop()=>Stops++; public void Dispose()=>Stop();
        public ApplicationWindowInfo? SelectTarget(MonitorInfo monitor)=>null;
    }
    private sealed class SlowDetector : IPresentationDetector
    {
        public volatile bool Entered; public ManualResetEventSlim Release=new();
        public void Reset(){}
        public DetectionSnapshot Detect(CapturedFrame frame,MonitorInfo monitor,ApplicationWindowInfo? target)
        { Entered=true; if(!Release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException(); return new(null,Rect.Empty,[],null,0,frame.Timestamp); }
    }
}


