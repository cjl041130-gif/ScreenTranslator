using System.Windows;

namespace ScreenTranslator.Vision;

/// <summary>
/// Produces a conservative OCR area for browser pages and PDF viewers. Coordinates are
/// physical frame pixels. The small top inset removes browser/PDF chrome while keeping
/// the complete visible document body, including Feishu and other canvas-based editors.
/// </summary>
public static class GeneralDocumentRegion
{
    public static Rect GetContentBounds(ApplicationWindowInfo target, Rect clientInFrame)
    {
        if (clientInFrame.IsEmpty) return Rect.Empty;

        var scale = Math.Clamp(target.DpiScale, .75, 3.5);
        var browserProcess = IsBrowserProcess(target.ProcessName);
        var topPixels = target.ApplicationType switch
        {
            ApplicationType.PdfViewer when browserProcess => 138 * scale,
            ApplicationType.PdfViewer => 78 * scale,
            ApplicationType.Browser when LooksLikeOnlineOfficeDocument(target.Title) => 205 * scale,
            ApplicationType.Browser => 104 * scale,
            _ => 0
        };
        // Never throw away a large portion of a small snapped window.
        var topInset = Math.Min(topPixels, clientInFrame.Height * .22);
        var sideInset = Math.Min(6 * scale, clientInFrame.Width * .01);
        var bottomInset = Math.Min(6 * scale, clientInFrame.Height * .01);
        var width = clientInFrame.Width - sideInset * 2;
        var height = clientInFrame.Height - topInset - bottomInset;
        return width >= 160 && height >= 100
            ? new Rect(clientInFrame.X + sideInset, clientInFrame.Y + topInset, width, height)
            : clientInFrame;
    }

    internal static bool IsBrowserProcess(string name) =>
        name.Equals("chrome", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("msedge", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("firefox", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("brave", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("opera", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("vivaldi", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("360chrome", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("360se", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("quark", StringComparison.OrdinalIgnoreCase);

    internal static bool LooksLikeOnlineOfficeDocument(string title) =>
        title.Contains(".docx", StringComparison.OrdinalIgnoreCase) ||
        title.Contains(".doc ", StringComparison.OrdinalIgnoreCase) ||
        title.Contains("Word Online", StringComparison.OrdinalIgnoreCase) ||
        title.Contains("Word for the web", StringComparison.OrdinalIgnoreCase);
}
