using EQLWikiAssistant.Core.Ocr;

namespace EQLWikiAssistant.Core.Locate;

/// <summary>
/// One item window found in a screenshot. <see cref="Bounds"/> is the window's real pixel extent, traced by
/// <see cref="WindowBoundsFinder"/> — not a text bounding box. When <see cref="PossiblyOccluded"/> is true,
/// bounds couldn't be established cleanly (something is drawn over part of the window): <see cref="Lines"/> is
/// empty and <see cref="Bounds"/> is just the "Description" tab's own small box, useful only for telling the
/// user roughly where the obstructed window is, not for cropping/parsing.
/// </summary>
public sealed record LocatedWindow(
    Rect Bounds,
    IReadOnlyList<OcrLine> Lines,
    bool HasLoreTab,
    bool PossiblyOccluded);
