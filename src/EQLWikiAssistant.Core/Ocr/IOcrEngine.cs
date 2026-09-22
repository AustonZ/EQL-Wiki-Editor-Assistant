namespace EQLWikiAssistant.Core.Ocr;

/// <summary>
/// Recognizes text in a locally-decoded image. Implementations must not send image data over the network
/// (see the hard "OCR stays 100% local" constraint) — the default implementation (EQLWikiAssistant.Ocr)
/// wraps Windows.Media.Ocr; this interface exists so it can be swapped (e.g. for Tesseract) without touching
/// the shared pipeline.
/// </summary>
public interface IOcrEngine
{
    Task<IReadOnlyList<OcrLine>> RecognizeAsync(CapturedImage image, CancellationToken cancellationToken = default);
}
