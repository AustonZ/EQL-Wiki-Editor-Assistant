namespace EQLWikiAssistant.Core.Ocr;

/// <summary>
/// Recognizes text in a locally-decoded image. Implementations must not send image data over the network — see
/// the hard "screenshots and OCR never leave the local machine" constraint. The only implementation is
/// <c>EQLWikiAssistant.Ocr.RapidOcrEngine</c>; this port still earns its place because <c>Core</c> can't
/// reference the Windows-only Ocr project, and it keeps the engine swappable without touching the pipeline.
/// </summary>
public interface IOcrEngine
{
    Task<IReadOnlyList<OcrLine>> RecognizeAsync(CapturedImage image, CancellationToken cancellationToken = default);
}
