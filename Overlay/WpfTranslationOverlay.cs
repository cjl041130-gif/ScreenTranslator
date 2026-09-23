using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using ScreenTranslator.Capture;
using ScreenTranslator.Models;

namespace ScreenTranslator.Overlay;

public sealed class WpfTranslationOverlay : ITranslationOverlay
{
    private TranslationOverlayWindow? _window;
    public bool IsVisible => _window?.IsVisible == true;
    public Task ShowAsync(SlideRecognitionResult result, MonitorInfo monitor, TranslationDisplayMode mode,
        double backgroundOpacity, double fontScale, bool showOriginalText, CancellationToken cancellationToken) => OnUiAsync(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        _window ??= new TranslationOverlayWindow();
        _window.Render(result, monitor, mode, backgroundOpacity, fontScale, showOriginalText);
    });
    public Task UpdateRegionAsync(PresentationRegionInfo region, MonitorInfo monitor, CancellationToken cancellationToken) => OnUiAsync(() =>
    { cancellationToken.ThrowIfCancellationRequested(); _window?.MoveTo(region, monitor); });
    public Task HideAsync(CancellationToken cancellationToken = default) => OnUiAsync(() => { if (!cancellationToken.IsCancellationRequested) _window?.Hide(); else _window?.Hide(); });
    private static Task OnUiAsync(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) { action(); return Task.CompletedTask; }
        return dispatcher.InvokeAsync(action).Task;
    }
    public async ValueTask DisposeAsync()
    {
        await OnUiAsync(() => { _window?.Close(); _window = null; });
    }
}

internal sealed class TranslationOverlayWindow : Window
{
    private readonly Canvas _canvas = new();
    private PresentationRegionInfo? _region;
    public TranslationOverlayWindow()
    {
        AllowsTransparency = true; Background = Brushes.Transparent; WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; ShowActivated = false; Topmost = true; Focusable = false;
        Content = _canvas;
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            var style = GetWindowLongPtr(handle, -20).ToInt64();
            SetWindowLongPtr(handle, -20, new nint(style | 0x20L | 0x08000000L | 0x80L)); // transparent, no-activate, toolwindow
            SetWindowDisplayAffinity(handle, 0x11); // keep translations out of the capture/OCR feedback loop
        };
    }
    public void Render(SlideRecognitionResult result, MonitorInfo monitor, TranslationDisplayMode mode, double opacity, double fontScale, bool showOriginal)
    {
        _canvas.Children.Clear(); _region = result.Presentation;
        if (mode == TranslationDisplayMode.Sidebar)
        {
            if (!PlaceSidebar(result.Presentation, monitor)) { Hide(); return; }
            var panel = new StackPanel { Margin = new Thickness(14) };
            panel.Children.Add(new TextBlock { Text = "实时翻译", FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });
            foreach (var block in result.Blocks)
                panel.Children.Add(CreatePair(block, opacity, fontScale, true, result.Presentation.DpiScale, true));
            _canvas.Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(242, 255, 255, 255)), CornerRadius = new CornerRadius(12), Child = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });
            Canvas.SetLeft(_canvas.Children[0], 0); Canvas.SetTop(_canvas.Children[0], 0); ((FrameworkElement)_canvas.Children[0]).Width = Width; ((FrameworkElement)_canvas.Children[0]).Height = Height;
        }
        else
        {
            MoveTo(result.Presentation, monitor); var occupied = new List<Rect>();
            foreach (var block in result.Blocks.Where(b => !string.IsNullOrWhiteSpace(b.TranslatedText)))
            {
                var source = block.Source; var x = source.X / result.Presentation.DpiScale; var y = source.Y / result.Presentation.DpiScale;
                var w = Math.Max(80, source.Width / result.Presentation.DpiScale); var h = Math.Max(26, source.Height / result.Presentation.DpiScale * (showOriginal ? 1.8 : 1.35));
                if (mode == TranslationDisplayMode.Bilingual) y += source.Height / result.Presentation.DpiScale + 3;
                var desired = SmartPlace(new Rect(x, y, w, h), occupied, ActualWidth > 0 ? ActualWidth : Width, ActualHeight > 0 ? ActualHeight : Height);
                occupied.Add(desired); var border = CreatePair(block, opacity, fontScale,
                    showOriginal && mode == TranslationDisplayMode.Bilingual, result.Presentation.DpiScale, false);
                border.Width = desired.Width; border.MinHeight = desired.Height; _canvas.Children.Add(border); Canvas.SetLeft(border, desired.X); Canvas.SetTop(border, desired.Y);
            }
        }
        if (!IsVisible) Show();
    }
    public void MoveTo(PresentationRegionInfo region, MonitorInfo monitor)
    {
        _region = region; Left = region.X / region.DpiScale; Top = region.Y / region.DpiScale;
        Width = Math.Max(1, region.Width / region.DpiScale); Height = Math.Max(1, region.Height / region.DpiScale);
    }
    private bool PlaceSidebar(PresentationRegionInfo region, MonitorInfo monitor)
    {
        const double desiredPhysical = 420; double x;
        if (monitor.Left + monitor.Width - (region.X + region.Width) >= desiredPhysical + 12) x = region.X + region.Width + 8;
        else if (region.X - monitor.Left >= desiredPhysical + 12) x = region.X - desiredPhysical - 8;
        else return false;
        Left = x / region.DpiScale; Top = region.Y / region.DpiScale; Width = desiredPhysical / region.DpiScale; Height = region.Height / region.DpiScale; return true;
    }
    private static Border CreatePair(TranslatedBlock block, double opacity, double fontScale, bool includeOriginal,
        double dpiScale, bool sidebar)
    {
        var stack = new StackPanel();
        var translatedSize = CalculateTranslatedFontSize(block.Source, dpiScale, fontScale, sidebar);
        var weight = block.Source.BlockType is OcrBlockType.Title or OcrBlockType.Subtitle
            ? FontWeights.SemiBold
            : FontWeights.Normal;
        if (includeOriginal)
            stack.Children.Add(new TextBlock
            {
                Text = block.Source.OriginalText,
                Foreground = new SolidColorBrush(Color.FromRgb(75, 85, 105)),
                FontSize = Math.Max(9, translatedSize * .62),
                LineHeight = Math.Max(11, translatedSize * .78),
                TextWrapping = TextWrapping.Wrap
            });
        stack.Children.Add(new TextBlock
        {
            Text = block.TranslatedText,
            Foreground = new SolidColorBrush(Color.FromRgb(22, 33, 52)),
            FontSize = translatedSize,
            FontWeight = weight,
            LineHeight = translatedSize * 1.24,
            TextWrapping = TextWrapping.Wrap
        });
        return new Border { Background = new SolidColorBrush(Color.FromArgb((byte)(Math.Clamp(opacity, .15, 1) * 255), 255, 255, 255)), CornerRadius = new CornerRadius(6), Padding = new Thickness(6, 3, 6, 3), Margin = new Thickness(0, 0, 0, 7), Child = stack, IsHitTestVisible = false };
    }

    internal static double CalculateTranslatedFontSize(OcrBlock source, double dpiScale, double fontScale, bool sidebar = false)
    {
        // OCR geometry is measured in physical pixels. WPF uses device-independent
        // pixels, so convert before deriving a font size. EffectiveLineHeight keeps
        // merged multi-line paragraphs at their original per-line scale.
        var lineHeightDip = source.EffectiveLineHeight / Math.Max(.75, dpiScale);
        var measuredSize = lineHeightDip * .92 * Math.Clamp(fontScale, .5, 2);
        return sidebar ? Math.Clamp(measuredSize, 11, 24) : Math.Clamp(measuredSize, 10, 46);
    }
    private static Rect SmartPlace(Rect desired, IReadOnlyList<Rect> occupied, double maxWidth, double maxHeight)
    {
        var value = desired; for (var i = 0; i < 12 && occupied.Any(x => x.IntersectsWith(value)); i++) value.Y += Math.Max(8, desired.Height * .45);
        value.X = Math.Clamp(value.X, 0, Math.Max(0, maxWidth - value.Width)); value.Y = Math.Clamp(value.Y, 0, Math.Max(0, maxHeight - value.Height)); return value;
    }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hWnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint hWnd, int index, nint value);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowDisplayAffinity(nint hWnd, uint affinity);
}
