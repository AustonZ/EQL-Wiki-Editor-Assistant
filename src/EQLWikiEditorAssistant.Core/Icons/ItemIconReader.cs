using EQLWikiEditorAssistant.Core.Locate;
using EQLWikiEditorAssistant.Core.Imaging;

namespace EQLWikiEditorAssistant.Core.Icons;

/// <summary>
/// Finds and fingerprints the item icon inside a located window.
///
/// **The icon is always in the same place and always the same size** (user, 2026-10-10): the game draws the 40x40
/// artwork at 1.1x, 44x44, at a fixed spot left of the item's name. So it is read from that fixed cell, every pixel
/// of it, with nothing traced or searched for.
/// </summary>
public static class ItemIconReader
{
    /// <summary>
    /// The icon's cell, relative to the window's own top-left: the 40x40 artwork as the game draws it, 44x44.
    ///
    /// Measured across 119 icons in the sample set: the ink starts at y 54 and ends at y 97 (44 rows), starts at x 12,
    /// and reaches x 55 on an icon that uses its full width (`Spiderling Silk`), with the item's name starting just
    /// beyond. Like every other constant derived from this UI's pixels, it belongs to one UI scale and skin.
    /// </summary>
    public static readonly Rect IconCell = new(X: 12, Y: 54, Width: 44, Height: 44);

    /// <summary>
    /// Fingerprints the icon of a located window, or returns false when there is nothing usable to fingerprint.
    ///
    /// Returning false is an ordinary outcome, and must not produce a confident answer: a Lore-tab capture has no
    /// icon at all, an occluded window has no reliable bounds, and a blank cell carries nothing to compare. An icon
    /// check that cannot see the icon has to stay silent rather than report a mismatch.
    /// </summary>
    public static bool TryRead(CapturedImage image, LocatedWindow window, out IconFingerprint fingerprint) =>
        TryRead(image, window, out fingerprint, out _);

    /// <param name="whyNot">Why no fingerprint was produced, when none was, so the user can be told which.</param>
    public static bool TryRead(
        CapturedImage image, LocatedWindow window, out IconFingerprint fingerprint, out string? whyNot)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(window);
        fingerprint = new IconFingerprint([]);
        whyNot = null;

        // A Lore capture shows prose where the icon would be; an occluded window's bounds are only the tab's own
        // box. Measuring either would be measuring the wrong pixels.
        if (window.PossiblyOccluded || window.ActiveTab != ItemWindowTab.Description)
        {
            whyNot = window.PossiblyOccluded
                ? "the window is partly covered, so the icon could not be located"
                : "this is a Lore capture, which shows no icon";
            return false;
        }

        var cell = new Rect(window.Bounds.X + IconCell.X, window.Bounds.Y + IconCell.Y, IconCell.Width, IconCell.Height);
        if (!IconHasher.TryFingerprint(image, cell, out fingerprint))
        {
            whyNot = "the icon's cell lies outside the screenshot";
            return false;
        }

        if (fingerprint.IsComparable) return true;

        whyNot = $"the icon's cell is almost uniform (contrast {fingerprint.Contrast:F3} against the " +
                 $"{IconFingerprint.MinimumContrast:F2} needed), so there is nothing to compare";
        fingerprint = new IconFingerprint([]);
        return false;
    }
}
