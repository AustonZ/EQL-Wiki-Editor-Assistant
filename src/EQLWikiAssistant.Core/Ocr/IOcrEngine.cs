namespace EQLWikiAssistant.Core.Ocr;

/// <summary>
/// Recognizes text in a locally-decoded image. Implementations must not send image data over the network — see
/// the hard "screenshots and OCR never leave the local machine" constraint.
///
/// Two implementations exist and they are not interchangeable: <c>EQLWikiAssistant.Ocr.RapidOcrEngine</c> (general
/// OCR, for the full-frame pass) and <c>Core.Glyphs.GlyphOcrEngine</c> (exact atlas matching, for window crops).
/// <paramref name="intent"/> says which job the call is doing; <c>RoutingOcrEngine</c> composes the two so the
/// pipeline still passes one engine around.
/// </summary>
public interface IOcrEngine
{
    Task<IReadOnlyList<OcrLine>> RecognizeAsync(
        CapturedImage image, OcrIntent intent = OcrIntent.FullFrame, CancellationToken cancellationToken = default);
}
