using EQLWikiAssistant.Core.Ocr;

namespace EQLWikiAssistant.Core.Glyphs;

/// <summary>
/// An <see cref="IOcrEngine"/> that reads the game's UI font by exact atlas matching rather than recognition.
/// See <see cref="GlyphReader"/> for the method and <see cref="GlyphAtlas"/> for why it is exact.
///
/// Lives in <c>Core</c> rather than the <c>Ocr</c> project, unlike <c>RapidOcrEngine</c>, because it needs
/// nothing Windows-specific — no Skia, no ONNX runtime, no WinRT. Keeping it portable means it is exercised by
/// plain <c>net10.0</c> tests with no capture or model files involved.
/// </summary>
public sealed class GlyphOcrEngine : IOcrEngine
{
    private readonly GlyphAtlas _atlas;

    public GlyphOcrEngine(GlyphAtlas? atlas = null) => _atlas = atlas ?? GlyphAtlas.Bundled;

    public Task<IReadOnlyList<OcrLine>> RecognizeAsync(
        CapturedImage image, OcrIntent intent = OcrIntent.FullFrame, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        cancellationToken.ThrowIfCancellationRequested();

        var region = new Rect(0, 0, image.Width, image.Height);
        return Task.FromResult(GlyphReader.Read(image, region, _atlas));
    }
}

/// <summary>
/// Sends each recognition call to whichever engine suits its <see cref="OcrIntent"/>, so the pipeline keeps
/// passing a single <see cref="IOcrEngine"/> around instead of threading two of them through every call site.
/// </summary>
public sealed class RoutingOcrEngine(IOcrEngine fullFrame, IOcrEngine windowCrop) : IOcrEngine
{
    public Task<IReadOnlyList<OcrLine>> RecognizeAsync(
        CapturedImage image, OcrIntent intent = OcrIntent.FullFrame, CancellationToken cancellationToken = default) =>
        (intent == OcrIntent.WindowCrop ? windowCrop : fullFrame)
            .RecognizeAsync(image, intent, cancellationToken);
}
