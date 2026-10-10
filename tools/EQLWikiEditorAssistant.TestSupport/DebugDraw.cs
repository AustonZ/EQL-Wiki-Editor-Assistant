using EQLWikiEditorAssistant.Core.Imaging;

namespace EQLWikiEditorAssistant.TestSupport;

/// <summary>Draws simple debug overlays (rectangle outlines) onto a CapturedImage — dev/test tooling only, for
/// visually verifying locate/parse results against real screenshots (e.g. via LocateSpike).</summary>
public static class DebugDraw
{
    public static void DrawRectOutline(CapturedImage image, Rect rect, byte r, byte g, byte b, int thickness = 3)
    {
        for (int t = 0; t < thickness; t++)
        {
            DrawHLine(image, rect.X, rect.Right - 1, rect.Y + t, r, g, b);
            DrawHLine(image, rect.X, rect.Right - 1, rect.Bottom - 1 - t, r, g, b);
            DrawVLine(image, rect.X + t, rect.Y, rect.Bottom - 1, r, g, b);
            DrawVLine(image, rect.Right - 1 - t, rect.Y, rect.Bottom - 1, r, g, b);
        }
    }

    private static void DrawHLine(CapturedImage image, int x0, int x1, int y, byte r, byte g, byte b)
    {
        if (y < 0 || y >= image.Height) return;
        for (int x = Math.Max(0, x0); x <= Math.Min(image.Width - 1, x1); x++)
            SetPixel(image, x, y, r, g, b);
    }

    private static void DrawVLine(CapturedImage image, int x, int y0, int y1, byte r, byte g, byte b)
    {
        if (x < 0 || x >= image.Width) return;
        for (int y = Math.Max(0, y0); y <= Math.Min(image.Height - 1, y1); y++)
            SetPixel(image, x, y, r, g, b);
    }

    private static void SetPixel(CapturedImage image, int x, int y, byte r, byte g, byte b)
    {
        int i = (y * image.Width + x) * 4;
        image.Pixels[i + 0] = b;
        image.Pixels[i + 1] = g;
        image.Pixels[i + 2] = r;
        image.Pixels[i + 3] = 255;
    }
}
