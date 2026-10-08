using System.Windows.Media;
using System.Windows.Media.Imaging;
using EQLWikiAssistant.Core.Ocr;

namespace EQLWikiAssistant.App;

/// <summary>Turns a captured or decoded image into something WPF can draw. Shared by the review screen and the item
/// list's icons, so both show the same pixels the same way.</summary>
internal static class Bitmaps
{
    /// <summary>The capture is tightly packed top-down BGRA32, which is exactly <c>Bgra32</c>'s layout, so this is a
    /// copy rather than a conversion.</summary>
    public static BitmapSource From(CapturedImage image, int magnify = 1)
    {
        BitmapSource bitmap = BitmapSource.Create(
            image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null,
            image.Pixels, image.Width * 4);
        bitmap.Freeze();

        if (magnify <= 1) return bitmap;

        // Nearest-neighbour, so a magnified icon shows the artwork rather than a blurred guess at it — the user is
        // being asked to compare two sprites, and interpolation would invent detail in both.
        var scaled = new TransformedBitmap(bitmap, new ScaleTransform(magnify, magnify));
        scaled.Freeze();
        return scaled;
    }
}
