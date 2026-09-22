using EQLWikiAssistant.Core.Ocr;

namespace EQLWikiAssistant.Core.Locate;

/// <summary>
/// One item detail window found in a screenshot, as a cluster of OCR lines rather than a pixel-perfect
/// bounding box (see ItemWindowLocator for why). <see cref="Bounds"/> is the union of its member lines'
/// bounding boxes, not the window's true chrome edges — it will run a little inside the actual border/icon.
/// </summary>
public sealed record LocatedWindow(
    Rect Bounds,
    IReadOnlyList<OcrLine> Lines,
    bool HasLoreTab,
    bool PossiblyOccluded);
