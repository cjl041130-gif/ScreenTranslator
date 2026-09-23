using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using ScreenTranslator.Capture;

namespace ScreenTranslator.Vision;

public sealed class TargetWindowTracker(VisionSettings settings) : ITargetWindowTracker
{
    private readonly int _ownPid = Environment.ProcessId;
    private WinEvent? _callback;
    private nint _hook, _last;
    private long _lastChanged;
    public nint LastExternalForegroundWindow => Interlocked.CompareExchange(ref _last, 0, 0);
    // Metadata-only hook on the UI message loop. No pixels, OpenCV or capture session at startup.
    public void Start()
    {
        if (_hook != 0) return;
        Observe(GetForegroundWindow());
        _callback = (_, _, hwnd, _, _, _, _) => Observe(hwnd);
        _hook = SetWinEventHook(3, 3, 0, _callback, 0, 0, 0);
        if (_hook == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }
    private void Observe(nint hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        if (hwnd == 0 || pid == _ownPid || !IsWindowVisible(hwnd)) return;
        var cls = Text(hwnd, true);
        if (cls is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Progman" or "WorkerW") return;
        if (Interlocked.Exchange(ref _last, hwnd) != hwnd) Interlocked.Exchange(ref _lastChanged, Stopwatch.GetTimestamp());
    }
    public void Stop()
    {
        if (_hook != 0) { UnhookWinEvent(_hook); _hook = 0; }
        _callback = null; Interlocked.Exchange(ref _last, 0);
    }
    public ApplicationWindowInfo? SelectTarget(MonitorInfo monitor)
    {
        var candidates = new List<ApplicationWindowInfo>(); var z = 0;
        var surfacesAbove = new List<Rect>();
        var foreground = GetForegroundWindow(); var last = LastExternalForegroundWindow;
        GetWindowThreadProcessId(foreground, out var foregroundPid);
        // Switching to an unsupported application releases the target after a short grace period.
        // Browsers and PDF readers are first-class recognition targets: previously this check
        // rejected them before any pixels could reach OCR.
        if (foregroundPid != _ownPid && foreground == last && !IsSupportedContentWindow(last) &&
            Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastChanged)).TotalSeconds > settings.ExternalApplicationGraceSeconds) return null;
        EnumWindows((hwnd, _) =>
        {
            var order = z++;
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return true;
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == _ownPid) return true;
            var style = GetWindowLongPtr(hwnd, -20).ToInt64();
            if ((style & (0x80 | 0x20)) != 0) return true;
            if (DwmGetWindowAttribute(hwnd, 14, out int cloaked, 4) == 0 && cloaked != 0) return true;
            if (!GetWindowRect(hwnd, out var wr)) return true;
            var window = wr.Rect;
            var occlusions = surfacesAbove.Where(r => r.IntersectsWith(window)).ToArray();
            if (window.Width > 0 && window.Height > 0) surfacesAbove.Add(window);
            var title = Text(hwnd, false);
            var process = ContentProcess(hwnd, pid);
            var applicationType = ClassifyApplication(process, title);
            if (applicationType == ApplicationType.Unknown) return true;
            if (!GetClientRect(hwnd, out var cr)) return true;
            var origin = new NativePoint(); if (!ClientToScreen(hwnd, ref origin)) return true;
            var client = new Rect(origin.X, origin.Y, Math.Max(0, cr.Right), Math.Max(0, cr.Bottom));
            var overlap = Rect.Intersect(client, new Rect(monitor.Left, monitor.Top, monitor.Width, monitor.Height));
            if (overlap.IsEmpty || overlap.Width < 160 || overlap.Height < 100) return true;
            var canResize = (GetWindowLongPtr(hwnd, -16).ToInt64() & 0x40000) != 0;
            var fullscreen = !canResize && Math.Abs(client.Width - monitor.Width) < 8 && Math.Abs(client.Height - monitor.Height) < 8 &&
                Math.Abs(client.X - monitor.Left) < 8 && Math.Abs(client.Y - monitor.Top) < 8;
            // A supported window the user is actively viewing must outrank a stale
            // previously-observed browser that merely remains high in the Z order.
            var score = .35 + .10 / (1 + order) + (hwnd == last ? .12 : 0) + (hwnd == foreground ? .28 : 0) +
                .15 * overlap.Width * overlap.Height / (monitor.Width * (double)monitor.Height);
            candidates.Add(new(hwnd, (int)pid, process, title, Text(hwnd, true), window, client, window,
                GetDpiForWindow(hwnd) / 96d, monitor.Id, hwnd == foreground, hwnd == last, false,
                applicationType,
                Math.Clamp(score, 0, 1), fullscreen) { Occlusions = occlusions });
            return true;
        }, 0);
        return candidates.OrderByDescending(c => c.Confidence).FirstOrDefault();
    }
    internal static bool IsPresentation(string name) => name.Equals("POWERPNT", StringComparison.OrdinalIgnoreCase) || name.Equals("wpp", StringComparison.OrdinalIgnoreCase);
    internal static ApplicationType ClassifyApplication(string name, string title = "")
    {
        if (name.Equals("POWERPNT", StringComparison.OrdinalIgnoreCase)) return ApplicationType.PowerPoint;
        if (name.Equals("wpp", StringComparison.OrdinalIgnoreCase)) return ApplicationType.WpsPresentation;

        var browser = name.Equals("chrome", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("msedge", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("firefox", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("brave", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("opera", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("vivaldi", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("360chrome", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("360se", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("quark", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Feishu", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Lark", StringComparison.OrdinalIgnoreCase);
        if (browser)
            return LooksLikePdf(title) ? ApplicationType.PdfViewer : ApplicationType.Browser;

        if (name.Equals("AcroRd32", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Acrobat", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("FoxitPDFReader", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("FoxitPhantomPDF", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("SumatraPDF", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("PDFXEdit", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("wpspdf", StringComparison.OrdinalIgnoreCase) ||
            (name.Equals("wps", StringComparison.OrdinalIgnoreCase) && LooksLikePdf(title)))
            return ApplicationType.PdfViewer;

        return ApplicationType.Unknown;
    }
    internal static bool LooksLikePdf(string title) =>
        title.Contains(".pdf", StringComparison.OrdinalIgnoreCase) ||
        title.Contains("PDF", StringComparison.OrdinalIgnoreCase);
    private static bool IsSupportedContentWindow(nint hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        var title = Text(hwnd, false);
        return ClassifyApplication(ContentProcess(hwnd, pid), title) != ApplicationType.Unknown;
    }
    private static string ContentProcess(nint hwnd, uint pid)
    {
        var name = ProcessName(pid);
        if (!name.Equals("wps", StringComparison.OrdinalIgnoreCase)) return name;
        // WPS uses a shared tab host. Preserve its presentation child identity, while
        // allowing the outer WPS title to classify a PDF tab below.
        var presentation = false;
        EnumChildWindows(hwnd, (child, _) => { if (!IsWindowVisible(child)) return true; GetWindowThreadProcessId(child, out var childPid); if (ProcessName(childPid).Equals("wpp", StringComparison.OrdinalIgnoreCase)) presentation = true; return !presentation; }, 0);
        return presentation ? "wpp" : name;
    }
    private static string ProcessName(uint pid) { try { using var p = Process.GetProcessById((int)pid); return p.ProcessName; } catch { return ""; } }
    private static string Text(nint hwnd, bool cls) { var s = new StringBuilder(512); if (cls) GetClassName(hwnd, s, s.Capacity); else GetWindowText(hwnd, s, s.Capacity); return s.ToString(); }
    public void Dispose() => Stop();
    private delegate void WinEvent(nint hook, uint evt, nint hwnd, int obj, int child, uint thread, uint time);
    private delegate bool EnumWindow(nint hwnd, nint param);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; public readonly Rect Rect => new(Left, Top, Math.Max(0, Right - Left), Math.Max(0, Bottom - Top)); }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindow callback, nint param);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(nint hwnd, EnumWindow callback, nint param);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll", EntryPoint="GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint hwnd, ref NativePoint point);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetWindowText(nint hwnd, StringBuilder s, int count);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetClassName(nint hwnd, StringBuilder s, int count);
    [DllImport("user32.dll", SetLastError=true)] private static extern nint SetWinEventHook(uint min, uint max, nint module, WinEvent callback, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);
}
