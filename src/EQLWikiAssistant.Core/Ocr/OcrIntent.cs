namespace EQLWikiAssistant.Core.Ocr;

/// <summary>
/// What a recognition call is for, so the pipeline can be served by the engine that suits it.
///
/// The two passes are genuinely different problems, and the same engine is not best at both:
/// <list type="bullet">
/// <item><see cref="FullFrame"/> scans a whole 2560x1440 screenshot that includes the 3D world, to find each
/// window's "Description" tab. That is open-ended recognition and general OCR is the right tool.</item>
/// <item><see cref="WindowCrop"/> reads inside a located window, where the background is a known constant and the
/// font is a fixed bitmap blit. That is exact template matching, and treating it as recognition is what produced
/// every remaining extraction error — dropped digits, `rn`-&gt;`m`, a grave read as an apostrophe.</item>
/// </list>
/// </summary>
public enum OcrIntent
{
    FullFrame,
    WindowCrop,
}
