namespace EQLWikiAssistant.Core.Ocr;

/// <summary>One word recognized by OCR, with its location in the source image.</summary>
public sealed record OcrWord(string Text, Rect BoundingBox);

/// <summary>
/// One line recognized by OCR (as grouped by the engine), with its location in the source image and the
/// words that make it up. Line grouping/reading order is engine-dependent; downstream parsing should treat
/// it as a best-effort hint, not a guarantee (see Core's tolerant-parsing requirement).
/// </summary>
public sealed record OcrLine(string Text, Rect BoundingBox, IReadOnlyList<OcrWord> Words);
