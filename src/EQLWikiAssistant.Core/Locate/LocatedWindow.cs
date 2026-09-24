using EQLWikiAssistant.Core.Ocr;

namespace EQLWikiAssistant.Core.Locate;

/// <summary>Which of the window's tabs is currently showing. The two lay their contents out completely
/// differently — Description has the stat block, Lore has free prose and no content-area name row at all — so the
/// parser has to be told which it is looking at.</summary>
public enum ItemWindowTab
{
    Description,
    Lore,
}

/// <summary>
/// One item window found in a screenshot. <see cref="Bounds"/> is the window's real pixel extent, traced by
/// <see cref="WindowBoundsFinder"/> — not a text bounding box. When <see cref="PossiblyOccluded"/> is true,
/// bounds couldn't be established cleanly (something is drawn over part of the window): <see cref="Lines"/> is
/// empty and <see cref="Bounds"/> is just the "Description" tab's own small box, useful only for telling the
/// user roughly where the obstructed window is, not for cropping/parsing.
///
/// <see cref="HasLoreTab"/> means the window *offers* a Lore tab (it drives the two-capture lore flow);
/// <see cref="ActiveTab"/> says which tab is actually on screen in this capture.
/// </summary>
public sealed record LocatedWindow(
    Rect Bounds,
    IReadOnlyList<OcrLine> Lines,
    bool HasLoreTab,
    bool PossiblyOccluded,
    ItemWindowTab ActiveTab = ItemWindowTab.Description);
