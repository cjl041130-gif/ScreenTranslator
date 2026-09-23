using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace ScreenTranslator.Document;

public sealed record RenderedPdfPage(byte[] PngBytes, byte[] BgraPixels, int Width, int Height, int Stride);

public sealed class PdfPageRenderer
{
    public async Task<RenderedPdfPage> RenderAsync(string path, uint pageIndex, CancellationToken cancellationToken)
    {
        var file = await StorageFile.GetFileFromPathAsync(path).AsTask(cancellationToken).ConfigureAwait(false);
        var pdf = await PdfDocument.LoadFromFileAsync(file).AsTask(cancellationToken).ConfigureAwait(false);
        using var page = pdf.GetPage(pageIndex);
        using var stream = new InMemoryRandomAccessStream();
        var scale = Math.Min(2d, 2200d / Math.Max(page.Size.Width, page.Size.Height));
        var options = new PdfPageRenderOptions
        {
            DestinationWidth = (uint)Math.Max(1, Math.Round(page.Size.Width * scale)),
            DestinationHeight = (uint)Math.Max(1, Math.Round(page.Size.Height * scale))
        };
        await page.RenderToStreamAsync(stream, options).AsTask(cancellationToken).ConfigureAwait(false);
        stream.Seek(0);
        var png = new byte[stream.Size];
        using (var reader = new DataReader(stream.GetInputStreamAt(0)))
        {
            await reader.LoadAsync((uint)stream.Size).AsTask(cancellationToken).ConfigureAwait(false);
            reader.ReadBytes(png);
        }
        using var memory = new MemoryStream(png, writable: false);
        var frame = BitmapFrame.Create(memory, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        BitmapSource source = frame.Format == PixelFormats.Bgra32 ? frame : new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var stride = source.PixelWidth * 4;
        var pixels = new byte[stride * source.PixelHeight];
        source.CopyPixels(pixels, stride, 0);
        return new(png, pixels, source.PixelWidth, source.PixelHeight, stride);
    }
}

