using EQLWikiAssistant.Core.Locate;
using EQLWikiAssistant.Core.Ocr;

namespace EQLWikiAssistant.Core.Icons;

/// <summary>
/// Finds and fingerprints the item icon inside a located window.
///
/// **The icon has no frame.** The game draws the sprite with transparency straight onto the window's 16-grey
/// background, to the left of the item's name — there is no box, no border, nothing to trace. So the icon is found
/// by cropping the strip it occupies and taking the ink's own bounding box, rather than by locating an edge the way
/// <see cref="WindowBoundsFinder"/> does for the window itself.
///
/// The strip was measured across the whole 43-screenshot sample set (`LocateSpike --icon`): the sprite's ink sits
/// within window-relative x 12..52 and y 52..100 on every window that has one, and the item's name and flag rows
/// begin at x ≈ 56. <see cref="IconStrip"/> is that region with a little margin, chosen to contain the icon on
/// every sample while never reaching the text.
/// </summary>
public static class ItemIconReader
{
    /// <summary>Where the icon lives, relative to the window's own top-left. Measured, not assumed — see the type
    /// comment. Like every other constant derived from this UI's pixels, it is scale- and skin-dependent (see the
    /// plan's note on UI scale being out of scope for v1).</summary>
    public static readonly Rect IconStrip = new(X: 10, Y: 50, Width: 45, Height: 52);

    /// <summary>
    /// Fingerprints the icon of a located window, or returns false when there is nothing usable to fingerprint.
    ///
    /// Returning false is an ordinary outcome with several real causes, all of which must not produce a confident
    /// answer: a Lore-tab capture has no icon at all (and no name row either, so the strip would catch prose); an
    /// occluded window has no reliable bounds to measure from; and a few windows simply have too little ink in the
    /// strip to say anything. An icon check that cannot see the icon has to stay silent rather than report a
    /// mismatch — a false "wrong icon" would send the user hunting for a problem that is not there.
    /// </summary>
    public static bool TryRead(CapturedImage image, LocatedWindow window, out IconFingerprint fingerprint)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(window);
        fingerprint = new IconFingerprint([], 0, 0);

        // A Lore capture shows prose where the icon would be; an occluded window's bounds are only the tab's own
        // box. Measuring either would be measuring the wrong pixels.
        if (window.PossiblyOccluded || window.ActiveTab != ItemWindowTab.Description) return false;

        var region = new Rect(
            window.Bounds.X + IconStrip.X,
            window.Bounds.Y + IconStrip.Y,
            IconStrip.Width,
            IconStrip.Height);

        if (!IconHasher.TryFingerprint(image, region, out fingerprint)) return false;

        // A near-black sprite carries too little variation to tell one icon from another — `Nightmare Hide` is
        // almost entirely black with a faint outline, and comparing it produced a confident mismatch against its
        // own correct icon. Refusing to judge is the right answer for a check whose only job is to flag.
        if (fingerprint.IsComparable) return true;

        fingerprint = new IconFingerprint([], 0, 0);
        return false;
    }
}
