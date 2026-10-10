using EQLWikiEditorAssistant.Core.Ocr;

namespace EQLWikiEditorAssistant.Ocr;

/// <summary>
/// A <see cref="RapidOcrEngine"/> loaded for each recognition and released straight after it — what the app uses, so
/// the memory a capture needs is given back between captures.
///
/// **ONNX Runtime keeps what it allocates** (user, 2026-10-09, after seeing the app's working set pass 2 GB). Its memory
/// pool grows to fit the largest input it has run and never shrinks, and the app gives it a whole 2560x1440 frame on
/// every capture. Measured over 153 real frames with one long-lived engine: about 1.75 GB working set and 2.5 GB private,
/// reached within two captures and flat after that, with the .NET heap at 30 MB — not a leak, but held for the life of
/// the app, beside the game. Loaded per capture: about 0.25 GB between captures and 1.1 GB at a capture's peak (it no
/// longer accumulates across different frames), at the same speed, for **about 140 ms to load the models** against a
/// recognition of 2.5-4 s. The output is byte-identical, boxes included, across 3,336 lines.
///
/// Turning the pool off (<c>SessionOptions.EnableCpuMemArena</c>) was measured too: about 0.5 GB between captures, but
/// every capture about 40% slower. Not worth it beside this, which costs nothing noticeable.
///
/// The accuracy corpus and the spike tools keep one <see cref="RapidOcrEngine"/> for a whole run, since they recognize
/// many frames in a row and then exit.
/// </summary>
public sealed class OnDemandRapidOcrEngine : IOcrEngine
{
    public async Task<IReadOnlyList<OcrLine>> RecognizeAsync(
        CapturedImage image, OcrIntent intent = OcrIntent.FullFrame, CancellationToken cancellationToken = default)
    {
        using var engine = new RapidOcrEngine();
        return await engine.RecognizeAsync(image, intent, cancellationToken).ConfigureAwait(false);
    }
}
