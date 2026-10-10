namespace EQLWikiEditorAssistant.Core.Imaging;

/// <summary>
/// A portable, decoded bitmap: tightly-packed top-down BGRA32 pixels (4 bytes/pixel, stride = Width * 4).
/// Deliberately not System.Drawing.Bitmap or a WinRT SoftwareBitmap, so Core stays free of Windows APIs.
/// Produced by EQLWikiEditorAssistant.Capture (from a live window capture) or by test/tooling code (from a
/// screenshot file on disk); consumed by the window locator, the glyph reader and icon cropping/comparison.
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
