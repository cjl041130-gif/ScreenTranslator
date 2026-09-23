namespace ScreenTranslator.Capture;

internal static class PixelCopy
{
    internal static unsafe void CopyBgraRows(nint source, int sourceStride, CapturedFrame destination)
    {
        if (source == 0 || sourceStride < destination.Stride) throw new ArgumentException("Invalid mapped surface pitch.");
        fixed (byte* target = destination.Buffer)
        {
            for (var y = 0; y < destination.Height; y++)
                System.Buffer.MemoryCopy((byte*)source + y * sourceStride, target + y * destination.Stride, destination.Stride, destination.Stride);
        }
    }
}
