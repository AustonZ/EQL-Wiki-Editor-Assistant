using EQLWikiEditorAssistant.Core.Imaging;
using EQLWikiEditorAssistant.Core.Glyphs;
using EQLWikiEditorAssistant.Core.Text;

namespace EQLWikiEditorAssistant.Core.Locate;

/// <summary>
/// Finds item detail windows in a full screenshot. <see cref="DescriptionTabFinder"/> finds each window's
/// "Description" tab by its pixels (every real item window has exactly one — see the plan's window-vs-tooltip rule; a
/// tooltip has none, so it's never mistaken for a window). From each tab, <see cref="WindowBoundsFinder"/>
/// traces the window's actual pixel bounds; if those bounds aren't clean (the edge is inconsistent — something
/// else is drawn over part of the window), the window is reported as occluded and is **not** parsed — no
/// partial/guessed data, just "found something here, but it's obstructed." For a window with clean bounds, the
/// image is cropped to it and read by the glyph reader to get its actual field data.
///
/// The tabs used to come from a text-recognition model run over the whole frame, which cost a capture 1.6-1.9 GB and
/// 2.5-4 s (user, 2026-10-09). Switching changed no window: every rectangle and every occlusion verdict across the 54
/// samples was compared before and after and came out identical.
///
/// Before that, an earlier design tried to find each window purely by clustering recognized lines by text
/// proximity. That could not reliably tell "this window's own content" apart from "a different, adjacent dark
/// window" on real screenshots where an item window sits directly against another dark UI panel (the character
/// sheet, a bag window) with no lighter gap between them — pixel darkness alone doesn't carry that information.
/// Tracing the window's own border and requiring it to be self-consistent does.
/// </summary>
public static class ItemWindowLocator
{
    public static IReadOnlyList<LocatedWindow> Locate(
        CapturedImage image, GlyphTextReader reader, CancellationToken cancellationToken = default)
    {
        var windows = new List<LocatedWindow>();
        foreach (DescriptionTab anchor in DescriptionTabFinder.Find(image))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Another skin: found, and named, but not traced or read — its borders and its textured background are
            // beyond the bounds tracing and the glyph reader alike (see DescriptionTabFinder.TexturedSpread). Marked
            // occluded too, so everything that skips a window it cannot read skips this one; the pipeline asks about
            // the skin first, so the user is told the real reason.
            if (anchor.Textured)
            {
                windows.Add(new LocatedWindow(anchor.Label, Array.Empty<TextLine>(), HasLoreTab: false,
                    PossiblyOccluded: true, DrawnIn: anchor.Font, InOtherSkin: true));
                continue;
            }

            Rect? bounds = WindowBoundsFinder.TryFindBounds(image, anchor.Label);
            if (bounds is null)
            {
                // Found a real item window (it has a Description tab) but couldn't establish clean bounds for
                // it — something is drawn over part of it. Report it as occluded with no data, rather than
                // guessing; the caller should tell the user to rearrange windows and recapture.
                windows.Add(new LocatedWindow(anchor.Label, Array.Empty<TextLine>(), HasLoreTab: false, PossiblyOccluded: true));
                continue;
            }

            CapturedImage crop = image.Crop(bounds.Value);
            IReadOnlyList<TextLine> lines = reader.Read(crop);
            TextLine? loreTab = lines.FirstOrDefault(l => IsLoreTabLabel(l.Text));
            ItemWindowTab activeTab = loreTab is not null && IsActiveTabLabel(crop, loreTab.BoundingBox)
                ? ItemWindowTab.Lore
                : ItemWindowTab.Description;

            windows.Add(new LocatedWindow(
                bounds.Value, lines, loreTab is not null, PossiblyOccluded: false, activeTab, DrawnIn(lines)));
        }

        return windows.OrderBy(w => w.Bounds.Y).ThenBy(w => w.Bounds.X).ToList();
    }

    /// <summary>The font a window was drawn in, from the lines that could tell: their one shared answer, or null
    /// when none could or they disagree. Every item window can tell — its own "Description" tab label holds an r,
    /// and the two fonts draw different r's — so null in practice means the window's text could not be read.
    /// A disagreement would mean a misread rather than a window in two fonts, so it claims nothing.</summary>
    public static UiFont? DrawnIn(IReadOnlyList<TextLine> lines)
    {
        List<UiFont> seen = [.. lines.Where(l => l.DrawnIn is not null).Select(l => l.DrawnIn!.Value).Distinct()];
        return seen.Count == 1 ? seen[0] : null;
    }

    /// <summary>"Lore" tab label — short, so a tight edit-distance budget. Only ever matches a line whose
    /// *entire* text is close to "Lore" (e.g. the tab label itself), not a substring — so it doesn't false-hit
    /// on flag text like "Lore Equipped, No Trade".</summary>
    private static bool IsLoreTabLabel(string text) =>
        EditDistance.IsCloseMatch(text.Trim(), "Lore", maxDistance: 1);

    /// <summary>True if a tab label is drawn in the active-tab colour. The game renders the selected tab's text
    /// yellow (measured: 191,191,4 / 159,159,6 / 255,255,0 — red≈green with blue near zero) and unselected tabs
    /// in neutral white/grey, so this is a direct pixel read rather than an inference.
    ///
    /// It lives here rather than in the parser because it is a question about pixels, and Locate is the layer
    /// holding the image — the parser only ever sees recognized text, which carries no colour at all.</summary>
    private static bool IsActiveTabLabel(CapturedImage crop, Rect labelBox)
    {
        const int GlyphMinChannel = 100;  // ignore the near-black background around the glyphs
        const int YellowBlueMaxRatio = 3; // blue is a small fraction of red on the yellow; equal on white

        int yellow = 0, neutral = 0;
        for (int y = Math.Max(0, labelBox.Y); y < Math.Min(crop.Height, labelBox.Bottom); y++)
        {
            for (int x = Math.Max(0, labelBox.X); x < Math.Min(crop.Width, labelBox.Right); x++)
            {
                int i = (y * crop.Width + x) * 4;
                int b = crop.Pixels[i], g = crop.Pixels[i + 1], r = crop.Pixels[i + 2];
                if (Math.Max(r, g) < GlyphMinChannel) continue;

                if (b * YellowBlueMaxRatio < r) yellow++;
                else neutral++;
            }
        }
        return yellow > neutral;
    }
}
