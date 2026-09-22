using EQLWikiAssistant.Core.Ocr;
using EQLWikiAssistant.Core.Text;

namespace EQLWikiAssistant.Core.Locate;

/// <summary>
/// Finds item detail windows in a full screenshot. A whole-screenshot OCR pass finds each window's
/// "Description" tab (every real item window has exactly one — see the plan's window-vs-tooltip rule; a
/// tooltip has none, so it's never mistaken for a window). From each tab, <see cref="WindowBoundsFinder"/>
/// traces the window's actual pixel bounds; if those bounds aren't clean (the edge is inconsistent — something
/// else is drawn over part of the window), the window is reported as occluded and is **not** parsed — no
/// partial/guessed data, just "found something here, but it's obstructed." For a window with clean bounds, the
/// image is cropped to it and re-OCR'd (matching milestone 1's already-validated crop-based accuracy) to get
/// its actual field data, rather than reusing the whole-image pass's own line detections for that.
///
/// This replaced an earlier design that tried to find each window purely by clustering OCR lines by text
/// proximity. That could not reliably tell "this window's own content" apart from "a different, adjacent dark
/// window" on real screenshots where an item window sits directly against another dark UI panel (the character
/// sheet, a bag window) with no lighter gap between them — pixel darkness alone doesn't carry that information.
/// Tracing the window's own border and requiring it to be self-consistent does.
/// </summary>
public static class ItemWindowLocator
{
    public static async Task<IReadOnlyList<LocatedWindow>> LocateAsync(
        CapturedImage image, IOcrEngine ocrEngine, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<OcrLine> wholeImageLines = await ocrEngine.RecognizeAsync(image, cancellationToken);
        List<OcrLine> anchors = wholeImageLines.Where(l => IsDescriptionTab(l.Text)).ToList();

        var windows = new List<LocatedWindow>();
        foreach (OcrLine anchor in anchors)
        {
            Rect? bounds = WindowBoundsFinder.TryFindBounds(image, anchor.BoundingBox);
            if (bounds is null)
            {
                // Found a real item window (it has a Description tab) but couldn't establish clean bounds for
                // it — something is drawn over part of it. Report it as occluded with no data, rather than
                // guessing; the caller should tell the user to rearrange windows and recapture.
                windows.Add(new LocatedWindow(anchor.BoundingBox, Array.Empty<OcrLine>(), HasLoreTab: false, PossiblyOccluded: true));
                continue;
            }

            CapturedImage crop = image.Crop(bounds.Value);
            IReadOnlyList<OcrLine> lines = await ocrEngine.RecognizeAsync(crop, cancellationToken);
            bool hasLoreTab = lines.Any(l => IsLoreTabLabel(l.Text));
            windows.Add(new LocatedWindow(bounds.Value, lines, hasLoreTab, PossiblyOccluded: false));
        }

        return windows.OrderBy(w => w.Bounds.Y).ThenBy(w => w.Bounds.X).ToList();
    }

    /// <summary>"Description" tab label, tolerant of OCR noise (e.g. "Descripbon").</summary>
    public static bool IsDescriptionTab(string text) =>
        EditDistance.IsCloseMatch(text.Trim(), "Description", maxDistance: 3);

    /// <summary>"Lore" tab label — short, so a tight edit-distance budget. Only ever matches a line whose
    /// *entire* text is close to "Lore" (e.g. the tab label itself), not a substring — so it doesn't false-hit
    /// on flag text like "Lore Equipped, No Trade".</summary>
    private static bool IsLoreTabLabel(string text) =>
        EditDistance.IsCloseMatch(text.Trim(), "Lore", maxDistance: 1);
}
