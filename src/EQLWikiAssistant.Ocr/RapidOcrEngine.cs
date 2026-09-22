using EQLWikiAssistant.Core.Ocr;
using RapidOcrNet;
using SkiaSharp;

namespace EQLWikiAssistant.Ocr;

/// <summary>
/// IOcrEngine backed by RapidOCR (PaddleOCR PP-OCRv5 models via ONNX Runtime) — fully local, no network calls,
/// models are bundled with the NuGet package. Evaluated as an alternative to WindowsOcrEngine specifically for
/// accuracy on the game's small (~9-11px) UI text; see the plan's milestone 1 writeup for the comparison.
/// </summary>
public sealed class RapidOcrEngine : IOcrEngine, IDisposable
{
    private readonly RapidOcr _ocr;

    public RapidOcrEngine()
    {
        _ocr = new RapidOcr();
        _ocr.InitModels();
    }

    public Task<IReadOnlyList<OcrLine>> RecognizeAsync(CapturedImage image, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        cancellationToken.ThrowIfCancellationRequested();

        using SKBitmap bitmap = ToSKBitmap(image);

        // RapidOcrOptions.Default.ImgResize (1024) downsamples anything larger before detection — fine for a
        // single cropped item window (a few hundred px), but on a full ~2560x1440 screenshot it shrinks our
        // already-tiny UI text below a usable threshold and the detector finds almost nothing (confirmed:
        // "Description" tab anchors go from reliably found to entirely missing). Match it to the image's own
        // size (with the same 1024 floor as the default, so small crops behave exactly as before) so a full-frame
        // locate pass and a small per-window crop both get a detector resolution that doesn't shrink the text.
        int imgResize = Math.Max(1024, Math.Max(image.Width, image.Height));
        RapidOcrOptions options = RapidOcrOptions.Default with { ImgResize = imgResize };
        OcrResult result = _ocr.Detect(bitmap, options);

        var lines = new List<OcrLine>(result.TextBlocks.Length);
        foreach (TextBlock block in result.TextBlocks)
        {
            Rect bounds = BoundsOf(block.BoxPoints);
            // RapidOCR returns line-level text without per-word boxes; treat the whole block as one "word" too.
            lines.Add(new OcrLine(block.Text, bounds, new[] { new OcrWord(block.Text, bounds) }));
        }
        return Task.FromResult<IReadOnlyList<OcrLine>>(lines);
    }

    private static SKBitmap ToSKBitmap(CapturedImage image)
    {
        var info = new SKImageInfo(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var bitmap = new SKBitmap(info);
        System.Runtime.InteropServices.Marshal.Copy(image.Pixels, 0, bitmap.GetPixels(), image.Pixels.Length);
        return bitmap;
    }

    private static Rect BoundsOf(SKPointI[] points)
    {
        int minX = points.Min(p => p.X), minY = points.Min(p => p.Y);
        int maxX = points.Max(p => p.X), maxY = points.Max(p => p.Y);
        return new Rect(minX, minY, maxX - minX, maxY - minY);
    }

    public void Dispose() => _ocr.Dispose();
}
