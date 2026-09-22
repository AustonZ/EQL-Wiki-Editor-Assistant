using System.Runtime.InteropServices.WindowsRuntime;
using EQLWikiAssistant.Core.Ocr;
using Windows.Graphics.Imaging;
using WinRtOcr = Windows.Media.Ocr;

namespace EQLWikiAssistant.Ocr;

/// <summary>
/// IOcrEngine backed by the OS-provided Windows.Media.Ocr engine: fully local, no network calls, requires
/// an OCR language pack for the user's profile language (Settings -> Time & Language -> Language -> add
/// "Optical character recognition" for the relevant language, e.g. English).
/// </summary>
public sealed class WindowsOcrEngine : IOcrEngine
{
    private readonly WinRtOcr.OcrEngine _engine;

    public WindowsOcrEngine()
    {
        _engine = WinRtOcr.OcrEngine.TryCreateFromUserProfileLanguages()
            ?? throw new InvalidOperationException(
                "No OCR language pack is installed for this Windows user profile. " +
                "Install one via Settings -> Time & Language -> Language & region -> (language) -> Options -> " +
                "\"Optical character recognition\".");
    }

    public async Task<IReadOnlyList<OcrLine>> RecognizeAsync(CapturedImage image, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);

        using var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, image.Width, image.Height, BitmapAlphaMode.Ignore);
        bitmap.CopyFromBuffer(image.Pixels.AsBuffer());

        cancellationToken.ThrowIfCancellationRequested();
        WinRtOcr.OcrResult result = await _engine.RecognizeAsync(bitmap);

        var lines = new List<OcrLine>(result.Lines.Count);
        foreach (var line in result.Lines)
        {
            var words = new List<OcrWord>(line.Words.Count);
            Rect lineBounds = default;
            bool first = true;
            foreach (var word in line.Words)
            {
                Rect wordBounds = ToRect(word.BoundingRect);
                words.Add(new OcrWord(word.Text, wordBounds));
                lineBounds = first ? wordBounds : Union(lineBounds, wordBounds);
                first = false;
            }
            lines.Add(new OcrLine(line.Text, lineBounds, words));
        }
        return lines;
    }

    private static Rect ToRect(global::Windows.Foundation.Rect r) =>
        new((int)Math.Round(r.X), (int)Math.Round(r.Y), (int)Math.Round(r.Width), (int)Math.Round(r.Height));

    private static Rect Union(Rect a, Rect b)
    {
        int x = Math.Min(a.X, b.X);
        int y = Math.Min(a.Y, b.Y);
        int right = Math.Max(a.Right, b.Right);
        int bottom = Math.Max(a.Bottom, b.Bottom);
        return new Rect(x, y, right - x, bottom - y);
    }
}
