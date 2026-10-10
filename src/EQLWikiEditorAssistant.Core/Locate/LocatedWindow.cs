using EQLWikiEditorAssistant.Core.Imaging;
using EQLWikiEditorAssistant.Core.Glyphs;

namespace EQLWikiEditorAssistant.Core.Locate;

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
///
/// <see cref="DrawnIn"/> is the UI font the window's text was drawn in, when its lines say — null when no line
/// carried a font-specific character, or when they disagreed. Like the active tab, it is read from pixels, which is
/// why it is decided here rather than by the parser.
///
/// <see cref="InOtherSkin"/> means the window is drawn in one of the game's skins other than `default_modern`, which
/// the Assistant can find but not read (user, 2026-10-09). It is also marked <see cref="PossiblyOccluded"/>, so
/// everything that skips a window it cannot read skips this one: it has no lines, and <see cref="Bounds"/> is only its
/// tab's label. Whatever tells the user why must ask this first, since moving the window would not help.
/// </summary>
public sealed record LocatedWindow(
    Rect Bounds,
    IReadOnlyList<TextLine> Lines,
    bool HasLoreTab,
    bool PossiblyOccluded,
    ItemWindowTab ActiveTab = ItemWindowTab.Description,
    UiFont? DrawnIn = null,
    bool InOtherSkin = false);
