namespace EQLWikiEditorAssistant.Core.Ocr;

/// <summary>
/// A portable, decoded bitmap: tightly-packed top-down BGRA32 pixels (4 bytes/pixel, stride = Width * 4).
/// Deliberately not System.Drawing.Bitmap or a WinRT SoftwareBitmap, so Core stays free of Windows APIs.
/// Produced by EQLWikiEditorAssistant.Capture (from a live window capture) or by test/tooling code (from a
/// screenshot file on disk); consumed by IOcrEngine implementations and by icon cropping/comparison.
/// </summary>
public sealed class CapturedImage
{
    public int Width { get; }
    public int Height { get; }

    /// <summary>BGRA32 pixel data, length == Width * Height * 4.</summary>
    public byte[] Pixels { get; }

    public CapturedImage(int width, int height, byte[] pixels)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        int expected = width * height * 4;
        if (pixels.Length != expected)
            throw new ArgumentException($"Expected {expected} bytes (BGRA32, {width}x{height}) but got {pixels.Length}.", nameof(pixels));

        Width = width;
        Height = height;
        Pixels = pixels;
    }

    /// <summary>
    /// Returns a new, bilinearly-resampled CapturedImage at the given size. Game UI text is often only
    /// ~9-11px tall in a native capture, which is small for OCR; upscaling before recognition is expected
    /// to be part of the real preprocessing pipeline, not just a debugging aid.
    /// </summary>
    public CapturedImage Resize(int newWidth, int newHeight)
    {
        if (newWidth <= 0) throw new ArgumentOutOfRangeException(nameof(newWidth));
        if (newHeight <= 0) throw new ArgumentOutOfRangeException(nameof(newHeight));

        var result = new byte[newWidth * newHeight * 4];
        double xRatio = (double)Width / newWidth;
        double yRatio = (double)Height / newHeight;

        for (int dy = 0; dy < newHeight; dy++)
        {
            double srcY = (dy + 0.5) * yRatio - 0.5;
            int y0 = Math.Clamp((int)Math.Floor(srcY), 0, Height - 1);
            int y1 = Math.Clamp(y0 + 1, 0, Height - 1);
            double fy = srcY - y0;

            for (int dx = 0; dx < newWidth; dx++)
            {
                double srcX = (dx + 0.5) * xRatio - 0.5;
                int x0 = Math.Clamp((int)Math.Floor(srcX), 0, Width - 1);
                int x1 = Math.Clamp(x0 + 1, 0, Width - 1);
                double fx = srcX - x0;

                int dstOffset = (dy * newWidth + dx) * 4;
                for (int channel = 0; channel < 4; channel++)
                {
                    double top = Lerp(GetPixelChannel(x0, y0, channel), GetPixelChannel(x1, y0, channel), fx);
                    double bottom = Lerp(GetPixelChannel(x0, y1, channel), GetPixelChannel(x1, y1, channel), fx);
                    result[dstOffset + channel] = (byte)Math.Round(Lerp(top, bottom, fy));
                }
            }
        }
        return new CapturedImage(newWidth, newHeight, result);
    }

    private byte GetPixelChannel(int x, int y, int channel) => Pixels[(y * Width + x) * 4 + channel];

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    /// <summary>Returns a new CapturedImage containing only the pixels within <paramref name="region"/>.</summary>
    public CapturedImage Crop(Rect region)
    {
        if (region.X < 0 || region.Y < 0 || region.Right > Width || region.Bottom > Height || region.Width <= 0 || region.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(region), region, $"Region must lie within {Width}x{Height}.");

        var result = new byte[region.Width * region.Height * 4];
        int srcStride = Width * 4;
        int dstStride = region.Width * 4;
        for (int row = 0; row < region.Height; row++)
        {
            int srcOffset = (region.Y + row) * srcStride + region.X * 4;
            int dstOffset = row * dstStride;
            Buffer.BlockCopy(Pixels, srcOffset, result, dstOffset, dstStride);
        }
        return new CapturedImage(region.Width, region.Height, result);
    }
}
