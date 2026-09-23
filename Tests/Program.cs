using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Interop;
using ScreenTranslator.Capture;
using ScreenTranslator.Core;
using ScreenTranslator.Services;
using Vortice.DXGI;
using ScreenTranslator.ViewModels;

namespace ScreenTranslator.Tests;

internal static partial class Program
{
    private static string _output = "";
    private static int _seconds;
    private static readonly List<string> Checks = new();
    [STAThread]
    public static int Main(string[] args)
    {
        var visionMode = args.FirstOrDefault()?.StartsWith("--") == true ? args[0] : null;
        if (visionMode is not null) args = args.Skip(1).ToArray();
        _seconds = args.Length > 0 ? int.Parse(args[0], CultureInfo.InvariantCulture) : 30;
        _output = Path.GetFullPath(args.Length > 1 ? args[1] : "test-results");
        Directory.CreateDirectory(_output);
        Environment.SetEnvironmentVariable("SCREEN_TRANSLATOR_DATA_DIR", Path.Combine(_output, "userdata"));
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        var exitCode = 1;
        app.Startup += (_, _) => app.Dispatcher.BeginInvoke(async () =>
        {
            try { if (visionMode == "--vision-unit") await RunVisionUnitsAsync((MainWindow)app.MainWindow); else if (visionMode == "--vision-live") await RunVisionLiveAsync((MainWindow)app.MainWindow); else if (visionMode == "--wps-ocr-live") await RunWpsOcrLiveAsync((MainWindow)app.MainWindow); else if (visionMode == "--web-ocr-live") await RunWebOcrLiveAsync((MainWindow)app.MainWindow); else if (visionMode == "--current-document-live") await RunCurrentDocumentLiveAsync((MainWindow)app.MainWindow); else if (visionMode == "--document-page-fixture") await RunDocumentPageFixtureAsync((MainWindow)app.MainWindow); else if (visionMode == "--document-unit") await RunDocumentUnitTestsAsync((MainWindow)app.MainWindow); else if (visionMode == "--compact-ui") await RunCompactUiTestsAsync((MainWindow)app.MainWindow); else if (visionMode == "--reading-unit") { TestReadingOrderAndPostProcessing(); ((MainWindow)app.MainWindow).Close(); } else if (visionMode == "--phase3-unit") await RunPhase3UnitsAsync((MainWindow)app.MainWindow); else await RunAsync((MainWindow)app.MainWindow); exitCode = 0; }
            catch (Exception ex) { File.WriteAllText(Path.Combine(_output, "failure.txt"), ex.ToString()); Console.WriteLine(ex); }
            finally { File.WriteAllLines(Path.Combine(_output, "checks.txt"), Checks); app.Shutdown(exitCode); }
        });
        app.Run();
        return exitCode;
    }

    private static async Task RunAsync(MainWindow window)
    {
        PixelOwnershipTests();
        var vm = (MainViewModel)window.DataContext;
        await ManualStartPolicyAsync();
        await Task.Delay(800);
        Check(vm.State == ApplicationState.Stopped && vm.Preview is null && !vm.AutoStart && vm.PresentedFrames == 0, "UI startup idle; no automatic capture or preview");
        Check(vm.CurrentPage is HomeViewModel, "Home is default navigation page");
        Check(vm.TranslationModes.SequenceEqual(new[] { "DeepL", "DeepSeek V4 Pro", "DeepSeek Flash" }) && vm.SelectedTranslationMode == "DeepL",
            "Translation mode is limited to DeepL, DeepSeek V4 Pro and DeepSeek Flash with DeepL as default");
        Check(vm.TranslationProviders.SequenceEqual(new[] { "DeepL", "DeepSeek" }) && !vm.IsDeepSeekProvider,
            "Settings provider dropdown contains only DeepL and DeepSeek; model selector is disabled for DeepL");
        vm.SelectedTranslationMode = "DeepSeek Flash";
        Check(vm.SelectedTranslationProvider == "DeepSeek" && vm.IsDeepSeekProvider && vm.SelectedDeepSeekModel == "DeepSeek Flash" && vm.TranslationStatus == "DeepSeek Flash",
            "Home translation selection and DeepSeek model setting share one state");
        vm.SelectedDeepSeekModel = "DeepSeek V4 Pro";
        Check(vm.SelectedTranslationMode == "DeepSeek V4 Pro", "DeepSeek model dropdown selects V4 Pro without free-form input");
        vm.SelectedTranslationMode = "DeepL";
        SaveWindow(window, "01-main-home-idle.png");
        vm.CurrentPage = vm.Pages.OfType<SettingsViewModel>().Single();
        await Task.Delay(150);
        var deepSeekModelPicker = FindElement<ComboBox>(window, "DeepSeekModel");
        Check(!deepSeekModelPicker.IsEnabled, "DeepSeek model dropdown is visually disabled while DeepL is selected");
        vm.SelectedTranslationProvider = "DeepSeek"; await Task.Delay(50);
        Check(deepSeekModelPicker.IsEnabled, "DeepSeek model dropdown becomes enabled when DeepSeek is selected");
        vm.SelectedTranslationProvider = "DeepL"; await Task.Delay(50);
        SaveWindow(window, "02-settings.png");
        vm.CurrentPage = vm.Pages.OfType<DocumentViewModel>().Single();
        await Task.Delay(150); SaveWindow(window, "03-document.png");
        vm.CurrentPage = vm.Pages.OfType<LanguagePackViewModel>().Single();
        await Task.Delay(150); SaveWindow(window, "04-language-pack.png");
        vm.CurrentPage = vm.Pages.OfType<RecognitionViewModel>().Single();
        await Task.Delay(150); SaveWindow(window, "05-recognition-idle.png");
        Check(vm.Monitors.Count > 0, "Monitor enumeration returns attached monitors");
        File.WriteAllText(Path.Combine(_output, "monitors.json"), JsonSerializer.Serialize(vm.Monitors.Select(m => new { m.Id, m.Name, m.Width, m.Height, m.Scale, m.IsPrimary }), new JsonSerializerOptions { WriteIndented = true }));
        Check(vm.StartCommand.CanExecute(null) && !vm.PauseCommand.CanExecute(null) && !vm.StopCommand.CanExecute(null), "Initial command states");
        Check(FindButton(window, "StartCapture").Command == vm.StartCommand, "Start button binding");
        Check(FindButton(window, "Pause").Command == vm.PauseCommand, "Pause button binding");
        Check(FindButton(window, "Stop").Command == vm.StopCommand, "Stop button binding");
        Check(Equals(FindElement<ComboBox>(window, "MonitorPicker").SelectedItem, vm.SelectedMonitor), "Monitor selector binding");

        var stimulus = new Window { Title = "ScreenTranslator capture test", Width = 180, Height = 120, Left = 0, Top = 0, Topmost = true, ShowInTaskbar = false, WindowStyle = WindowStyle.ToolWindow };
        var brush = new SolidColorBrush(Colors.Coral);
        stimulus.Background = brush;
        var pulse = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        var phase = 0;
        pulse.Tick += (_, _) => { phase = (phase + 7) % 256; brush.Color = Color.FromRgb((byte)phase, 95, (byte)(255 - phase)); };
        stimulus.Show(); pulse.Start();
        try
        {
            foreach (var monitor in vm.Monitors.ToArray())
            {
                vm.SelectedMonitor = monitor;
                if (!SetWindowPos(new WindowInteropHelper(stimulus).Handle, 0, monitor.Left + 20, monitor.Top + 20, 0, 0, 0x15))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                var framesBefore = vm.PresentedFrames;
                vm.StartCommand.Execute(null);
                await Until(() => vm.Preview is not null, "First GPU frame / " + monitor.DisplayLabel);
                Check(vm.Preview!.PixelWidth == monitor.Width && vm.Preview.PixelHeight == monitor.Height, "Native capture resolution / " + monitor.DisplayLabel);
                await Until(() => vm.PresentedFrames >= framesBefore + 15, "Continuous preview frames");
                Check(!vm.StartCommand.CanExecute(null) && vm.PauseCommand.CanExecute(null) && vm.StopCommand.CanExecute(null) && vm.StateLabel == "识别中", "Running UI command and visual states");
                await Task.Delay(1600); // Capture a populated FPS sample, not the first half-second.
                SaveWindow(window, "06-recognition-running.png");
                vm.CurrentPage = vm.Pages.OfType<HomeViewModel>().Single();
                await Task.Delay(150);
                Check(FindButton(window, "StartCapture").Command == vm.StartCommand && vm.State == ApplicationState.Capturing, "Home and recognition share running service and commands");
                SaveWindow(window, "07-home-running.png");
                vm.CurrentPage = vm.Pages.OfType<RecognitionViewModel>().Single();
                await Task.Delay(150);
                vm.PauseCommand.Execute(null);
                await Until(() => vm.State == ApplicationState.Paused, "Pause completes");
                var count = vm.PresentedFrames; var preview = vm.Preview;
                await Task.Delay(1000);
                Check(vm.PresentedFrames == count && ReferenceEquals(preview, vm.Preview), "Pause freezes and preserves preview");
                Check(vm.FpsText.StartsWith("FPS: 0"), "Pause resets FPS");
                Check(vm.StartCommand.CanExecute(null) && !vm.PauseCommand.CanExecute(null) && vm.StopCommand.CanExecute(null) && vm.StateLabel == "已暂停", "Paused UI command and visual states");
                SaveWindow(window, "08-recognition-paused.png");
                vm.StartCommand.Execute(null);
                await Until(() => vm.PresentedFrames > count + 5, "Resume delivers frames");
                vm.StopCommand.Execute(null);
                await Until(() => vm.State == ApplicationState.Stopped && vm.Preview is null, "Stop clears preview");
                vm.CurrentPage = vm.Pages.OfType<HomeViewModel>().Single();
                await Task.Delay(150);
                Check(vm.StateLabel == "待机中" && vm.StartCommand.CanExecute(null) && !vm.StopCommand.CanExecute(null) && vm.Preview is null, "Recognition Stop synchronizes Home idle and cleared preview");
                SaveWindow(window, "09-home-stopped.png");
                vm.CurrentPage = vm.Pages.OfType<RecognitionViewModel>().Single();
                await Task.Delay(150);
            }
            // Repeated resource creation/destruction catches COM/session ownership mistakes.
            for (var i = 0; i < 8; i++)
            {
                vm.StartCommand.Execute(null);
                await Until(() => vm.Preview is not null, "Restart cycle " + i);
                vm.StopCommand.Execute(null);
                await Until(() => vm.State == ApplicationState.Stopped && vm.Preview is null, "Stop cycle " + i);
            }
            vm.StartCommand.Execute(null);
            await Until(() => vm.Preview is not null, "Soak capture starts");
            await Task.Delay(3000);
            SaveWindow(window, "preview.png");
            var pixels = new byte[checked(vm.Preview!.PixelWidth * vm.Preview.PixelHeight * 4)];
            vm.Preview.CopyPixels(pixels, vm.Preview.PixelWidth * 4, 0);
            Check(pixels.Where((_, i) => i % 4 != 3).Distinct().Take(10).Count() >= 10, "Captured pixels contain nonblank desktop content");
            pixels = null!;
            var samples = new List<Sample>();
            using var process = Process.GetCurrentProcess();
            var clock = Stopwatch.StartNew(); var previousTime = 0.0; var previousFrames = vm.PresentedFrames;
            File.WriteAllText(Path.Combine(_output, "samples.csv"), "Seconds,PreviewFPS,PrivateMB,WorkingMB,ManagedMB,Handles\n");
            while (clock.Elapsed.TotalSeconds < _seconds)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(10, Math.Max(1, _seconds - clock.Elapsed.TotalSeconds))));
                var now = clock.Elapsed.TotalSeconds;
                process.Refresh();
                var sample = new Sample(now, (vm.PresentedFrames - previousFrames) / (now - previousTime), process.PrivateMemorySize64 / 1048576.0,
                    process.WorkingSet64 / 1048576.0, GC.GetTotalMemory(false) / 1048576.0, process.HandleCount);
                samples.Add(sample); previousTime = now; previousFrames = vm.PresentedFrames;
                var line = FormattableString.Invariant($"{sample.Seconds:F2},{sample.PreviewFPS:F2},{sample.PrivateMB:F2},{sample.WorkingMB:F2},{sample.ManagedMB:F2},{sample.Handles}");
                File.AppendAllText(Path.Combine(_output, "samples.csv"), line + "\n");
                Console.WriteLine(line);
                Check(vm.State == ApplicationState.Capturing, "Capture remains healthy at " + (int)now + "s");
            }
            Check(samples.Average(s => s.PreviewFPS) >= 20, "Average real preview FPS >= 20");
            Check(samples.Min(s => s.PreviewFPS) >= 20, "Every sampled interval preview FPS >= 20");
            if (_seconds >= 600)
            {
                // Recognition is deliberately lazy: a stable page can cause the ONNX model to
                // perform its first real inference several minutes after capture started.  Treat
                // that one-time allocation as a warm-up boundary, then require a real plateau for
                // the remainder of the run.  A late or repeatedly growing process still fails.
                var largestJumpIndex = Enumerable.Range(1, samples.Count - 1)
                    .OrderByDescending(i => samples[i].PrivateMB - samples[i - 1].PrivateMB)
                    .First();
                var largestJump = samples[largestJumpIndex].PrivateMB - samples[largestJumpIndex - 1].PrivateMB;
                var warmupBoundary = largestJump > 128 ? samples[largestJumpIndex].Seconds : 0;
                Check(warmupBoundary <= _seconds - 180,
                    $"One-time OCR allocation leaves at least 3 minutes for plateau verification (boundary={warmupBoundary:F1}s)");
                var baselineStart = Math.Max(60, warmupBoundary + 60);
                var baselineEnd = Math.Min(_seconds - 120, baselineStart + 60);
                var baselineWindow = samples.Where(s => s.Seconds >= baselineStart && s.Seconds <= baselineEnd).ToArray();
                Check(baselineWindow.Length >= 3, "Memory plateau baseline contains at least three samples");
                var baseline = baselineWindow.Average(s => s.PrivateMB);
                var ending = samples.Where(s => s.Seconds >= _seconds - 120).Average(s => s.PrivateMB);
                Check(ending - baseline < 64, $"10-minute private memory plateau after lazy OCR warm-up: baseline={baseline:F1}MB, ending={ending:F1}MB, delta={ending - baseline:F1}MB < 64MB");
                var endingWindow = samples.Where(s => s.Seconds >= _seconds - 120).ToArray();
                var baselineHandles = baselineWindow.Average(s => s.Handles);
                var endingHandles = endingWindow.Average(s => s.Handles);
                Check(endingHandles - baselineHandles < 30,
                    $"No sustained handle growth after lazy OCR warm-up: baseline={baselineHandles:F1}, ending={endingHandles:F1}, delta={endingHandles - baselineHandles:F1} < 30");
            }
            vm.StopCommand.Execute(null);
            await Until(() => vm.Preview is null && vm.State == ApplicationState.Stopped, "Final stop releases preview");
            await LayoutChecksAsync(window, vm);
            window.Close();
            await Until(() => !window.IsVisible, "Real window closes after async cleanup without Closing re-entry");
            Check(!vm.StartCommand.CanExecute(null), "Disposed UI cannot restart capture");
            File.WriteAllText(Path.Combine(_output, "result.json"), JsonSerializer.Serialize(new { Passed = true, DurationSeconds = _seconds, Monitors = vm.Monitors.Count,
                AveragePreviewFPS = samples.Average(s => s.PreviewFPS), MinimumIntervalFPS = samples.Min(s => s.PreviewFPS), Samples = samples }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { pulse.Stop(); stimulus.Close(); await vm.DisposeAsync(); }
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + description);
        Checks.Add("PASS: " + description); Console.WriteLine("PASS: " + description);
    }
    private static async Task Until(Func<bool> condition, string description)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition()) { if (deadline.Elapsed.TotalSeconds > 12) throw new TimeoutException(description); await Task.Delay(50); }
        await Task.Delay(100); // Allow command continuations and bindings to finish.
        Check(condition(), description);
    }
    private static Button FindButton(DependencyObject root, string automationId)
    {
        if (root is Button button && AutomationProperties.GetAutomationId(button) == automationId) return button;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            try { return FindButton(VisualTreeHelper.GetChild(root, i), automationId); } catch (KeyNotFoundException) { }
        }
        throw new KeyNotFoundException(automationId);
    }
    private static void SaveWindow(Window window, string filename)
    {
        window.UpdateLayout();
        var root = (FrameworkElement)window.Content;
        var dpi = VisualTreeHelper.GetDpi(window);
        var image = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY), 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        image.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(Path.Combine(_output, filename)); encoder.Save(stream);
    }
    private static IEnumerable<FrameworkElement> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is FrameworkElement element) yield return element;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
    private static async Task LayoutChecksAsync(MainWindow window, MainViewModel vm)
    {
        var nativeDpi = VisualTreeHelper.GetDpi(window);
        var observations = new List<object>();
        try
        {
            foreach (var scale in new[] { 1.25, 1.5, 1.75 })
            {
                // WPF visual-tree DPI test; does not change the user's Windows display setting.
                VisualTreeHelper.SetRootDpi(window, new DpiScale(scale, scale));
                foreach (var size in new[] { new Size(620, 460), new Size(760, 520), new Size(1100, 700), new Size(1400, 900) })
                {
                    window.Width = size.Width; window.Height = size.Height;
                    foreach (var page in vm.Pages)
                    {
                        vm.CurrentPage = page;
                        await Task.Delay(100);
                        window.UpdateLayout();
                        var host = (FrameworkElement)window.FindName("PageHost");
                        var failures = new List<string>();
                        foreach (var control in Descendants(host).Where(e => e.IsVisible && e is Button or ComboBox or CheckBox or RadioButton or Slider))
                        {
                            var bounds = control.TransformToAncestor(host).TransformBounds(new Rect(control.RenderSize));
                            if (bounds.Left < -2 || bounds.Right > host.ActualWidth + 2) failures.Add(control.GetType().Name + " " + bounds);
                        }
                        Check(failures.Count == 0, $"DPI {scale:P0}, {size.Width}x{size.Height}, {page.Title}: interactive controls fit horizontally ({string.Join(",", failures)})");
                        observations.Add(new { RequestedScale = scale, EffectiveScale = VisualTreeHelper.GetDpi(window).DpiScaleX, Width = window.ActualWidth, Height = window.ActualHeight, Page = page.Title, HorizontalOverflow = failures.Count });
                        if (page is HomeViewModel or SettingsViewModel)
                            SaveWindow(window, $"dpi-{scale * 100:0}-{size.Width:0}-{page.GetType().Name}.png");
                    }
                }
            }
        }
        finally
        {
            VisualTreeHelper.SetRootDpi(window, nativeDpi);
            window.Width = 760; window.Height = 520;
            vm.CurrentPage = vm.Pages.OfType<HomeViewModel>().Single();
        }
        File.WriteAllText(Path.Combine(_output, "dpi-layout.json"), JsonSerializer.Serialize(new { Method = "WPF SetRootDpi visual-tree injection plus real Window resize; OS display settings unchanged", NativeScale = nativeDpi.DpiScaleX, Observations = observations }, new JsonSerializerOptions { WriteIndented = true }));
        vm.CurrentPage = vm.Pages.OfType<SettingsViewModel>().Single();
        await Task.Delay(150);
        var scroll = Descendants(window).OfType<ScrollViewer>().First(s => s.Content is ContentControl);
        scroll.ScrollToBottom(); await Task.Delay(150); SaveWindow(window, "10-settings-advanced.png");
        scroll.ScrollToTop();
    }
    private static T FindElement<T>(DependencyObject root, string automationId) where T : FrameworkElement
    {
        if (root is T element && AutomationProperties.GetAutomationId(element) == automationId) return element;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            try { return FindElement<T>(VisualTreeHelper.GetChild(root, i), automationId); } catch (KeyNotFoundException) { }
        }
        throw new KeyNotFoundException(automationId);
    }
    private static async Task ManualStartPolicyAsync()
    {
        var log = new AppLogger(Path.Combine(_output, "policy-logs"));
        var dataRoot = Environment.GetEnvironmentVariable("SCREEN_TRANSLATOR_DATA_DIR");
        var isolated = Path.Combine(_output, "policy-data");
        Directory.CreateDirectory(isolated);
        Environment.SetEnvironmentVariable("SCREEN_TRANSLATOR_DATA_DIR", isolated);
        try
        {
            Check(!AppSettings.Load(log).AutoStart, "Missing config defaults AutoStart to false");
            File.WriteAllText(AppSettings.UserPath, "invalid json");
            Check(!AppSettings.Load(log).AutoStart, "Corrupt config defaults AutoStart to false");
            File.WriteAllText(AppSettings.UserPath, "{\"AutoStart\":true,\"TargetFPS\":30}");
            var settings = AppSettings.Load(log);
            Check(!settings.AutoStart, "Persisted AutoStart true cannot enable automatic capture");
            var spy = new CaptureSpy();
            await using var policyVm = new MainViewModel(log, spy, settings);
            await Task.Delay(300);
            Check(spy.Starts == 0 && policyVm.Preview is null, "No capture Start call during view model construction or idle ticks");
        }
        finally { Environment.SetEnvironmentVariable("SCREEN_TRANSLATOR_DATA_DIR", dataRoot); }
    }
    private sealed class CaptureSpy : IScreenCaptureService
    {
        public int Starts { get; private set; }
        public ApplicationState State => ApplicationState.Stopped;
        public string Status => "Stopped";
        public double FramesPerSecond => 0;
        public long TotalFrames => 0;
        public CapturedFrame? TakeLatestFrame() => null;
        public Task StartAsync(MonitorInfo monitor, int targetFps, CancellationToken cancellationToken = default) { Starts++; return Task.CompletedTask; }
        public Task PauseAsync() => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

    private static unsafe void PixelOwnershipTests()
    {
        var pool = ArrayPool<byte>.Create();
        uint[] source = [1, 2, 3, 0xDEADBEEF, 4, 5, 6, 0xDEADBEEF];
        var cases = new (ModeRotation rotation, uint[] expected)[]
        {
            (ModeRotation.Identity, [1,2,3,4,5,6])
        };
        fixed (uint* ptr = source)
        {
            foreach (var (rotation, expected) in cases)
            {
                var swap = rotation is ModeRotation.Rotate90 or ModeRotation.Rotate270;
                using var frame = new CapturedFrame(swap ? 2 : 3, swap ? 3 : 2, "test", pool);
                PixelCopy.CopyBgraRows((nint)ptr, 16, frame);
                var actual = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(frame.PixelData.Span).ToArray();
                Check(actual.SequenceEqual(expected), "GPU row padding removed without corrupting BGRA pixels");
                frame.Dispose(); frame.Dispose();
                try { _ = frame.PixelData; throw new Exception("Disposed pixels remained available"); } catch (ObjectDisposedException) { }
            }
        }
    }
    private sealed record Sample(double Seconds, double PreviewFPS, double PrivateMB, double WorkingMB, double ManagedMB, int Handles);
}
