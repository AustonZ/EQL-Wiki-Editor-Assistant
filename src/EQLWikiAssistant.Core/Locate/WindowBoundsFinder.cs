using EQLWikiAssistant.Core.Ocr;

namespace EQLWikiAssistant.Core.Locate;

/// <summary>
/// Finds an item window's bounds by tracing the **content area's own outline** — the thin neutral-grey line the
/// game draws around the Description/Lore tab contents — outward from a known interior point (the tab's OCR box).
/// Returns null rather than guessing when that line can't be found consistently, which the caller treats as
/// "occluded, don't parse".
///
/// **Why the grey line and not a brightness transition.** Two earlier designs failed, and the reasons are the
/// design: (1) clustering OCR lines by text proximity can't tell a window's own content from an adjacent dark
/// window's; (2) tracing the edge of the near-black interior — "scan out until it stops being dark" — works only
/// while whatever is *outside* the window is brighter than the window. It isn't, often: the player's own 3D
/// character model standing behind the window measures ~33-75, and another dark UI panel measures about the same
/// as the interior, so those scans tunnelled straight through the real edge and ran to the scan limit. The grey
/// outline is immune to all of that, because it's the window drawing itself rather than a contrast accident.
///
/// **Measured colour profile** (via <c>tools/LocateSpike --probe x,y,dx,dy,count</c>, which dumps raw pixel RGB
/// along a ray — use it before changing any constant here):
/// <list type="bullet">
/// <item>Window interior: R=G=B≈16, widened to roughly 10-25 by JPEG noise.</item>
/// <item>Content outline: a 1px neutral grey line, 50-62 on every edge of every real sample, and essentially
/// constant along its own length (e.g. 20 consecutive pixels all reading 59-60).</item>
/// <item>Outer window frame, just outside that line: 0-8, i.e. *darker* than the interior.</item>
/// <item>World background: 150-170. Text: up to 255, with anti-aliased edges that pass through the grey range.</item>
/// </list>
///
/// **Brightness alone can't identify the line** — anti-aliased text edges hit the same 50-62 band, and so does the
/// character model. What separates them is that the outline is a *long, uniform, straight line*: a glyph edge is
/// a few pixels, a value-box outline is a few tens, the content outline runs the full width/height of the window.
/// So every candidate pixel is confirmed by <see cref="VerifyLineRun"/>, which requires a long run of same-
/// brightness line pixels perpendicular to the scan direction. That check is also what implements "if the border
/// is broken at any point, treat it as occluded": something drawn over part of an edge breaks the run.
///
/// **The top edge is deliberately different.** There is no grey line at the window's outer top — the title bar is
/// pure black and simply meets the world — and the parser needs the title bar inside the crop (it reconciles the
/// title-bar name against the content-area name). So the top alone keeps the older brightness-transition scan,
/// which has been unanimous across every real sample; the edges that the dark-neighbour problem actually broke
/// (left/right/bottom) are the ones now traced from the outline.
///
/// Every threshold here was measured against real screenshots, not derived on paper. Re-tune only with
/// <c>tools/LocateSpike</c> against the full real-sample set.
/// </summary>
public static class WindowBoundsFinder
{
    // --- Content-outline tracing (left/right/bottom) ---
    private const int InteriorMaxChannel = 30;      // interior ≈16; headroom for JPEG noise
    private const int LineMinChannel = 38;          // outline measured 50-62 …
    private const int LineMaxChannel = 110;         // … with generous headroom either side
    private const int LineMaxColourSpread = 16;     // the outline is neutral grey (R≈G≈B)
    private const int LineBrightnessTolerance = 16; // how close a neighbour must be to be "the same line"
    private const int LineVerifyHalfRun = 60;       // px sampled each way along a candidate line
    private const double LineVerifyMinMatch = 0.85; // fraction of those that must match
    private const int LineClusterGapPx = 10;        // chrome lines this close together are one stack

    // Just outside the window's outline sits its outer frame: a ~2px run measuring 0, i.e. *darker* than the ~16
    // interior. Requiring it beyond a candidate is what separates the content area's real boundary from the
    // internal divider rules the window also draws (e.g. the dropdown outline by the "Modified" row), which are
    // identical grey lines but have more window beyond them, not frame. It's also why this survives a dark
    // neighbour: the frame belongs to the window, so it's there whatever is behind it.
    //
    // Tested on the **minimum** channel, not the maximum. JPEG bleed from whatever is adjacent lifts individual
    // channels of this thin run unevenly — against a red element below one real window the frame reads (11,0,0)
    // then (34,0,0), which a max-channel test rejects — but the bleed never lifts all three at once, so the
    // minimum stays at 0 while the interior's neutral grey keeps a minimum of ~16.
    private const int FrameMaxMinChannel = 8;
    private const int FrameMinRun = 2;
    private const int FrameSearchDistance = 25;

    // --- Top edge only: the original brightness-transition scan (see the class doc for why) ---
    private const int DarkPixelMaxChannel = 90;     // "not yet out of the window" for the upward scan
    private const int SustainedBrightRunThreshold = 26; // shorter bright runs are the window's own text

    private const int MaxScanDistance = 700;
    private const int ProbeCount = 11;
    private const int AgreementTolerancePx = 6;
    private const double MinAgreementFraction = 0.4;
    private const int SafeHorizontalProbeHalfWidth = 100;

    // Sanity ceilings, generous over the largest real window measured (~550x655). These catch the case where an
    // occluder is adjacent along an *entire* side, so every probe agrees on the same wrong, oversized answer and
    // consensus has no disagreement to notice.
    private const int MaxPlausibleWidth = 600;
    private const int MaxPlausibleHeight = 700;

    /// <summary>Returns the window's pixel bounds, or null if its outline can't be traced consistently (treat as
    /// occluded — don't parse it).</summary>
    public static Rect? TryFindBounds(CapturedImage image, Rect anchorBox)
    {
        int cx = anchorBox.X + anchorBox.Width / 2;

        int interiorY = FindContentInteriorBelowAnchor(image, cx, anchorBox.Bottom);
        if (interiorY >= image.Height || !IsInterior(image, cx, interiorY)) return null;

        if (!TryConsensusOutlineVertical(image, cx - SafeHorizontalProbeHalfWidth, cx + SafeHorizontalProbeHalfWidth,
                interiorY, dy: 1, out int bottom))
            return null;

        if (!TryConsensusOutlineHorizontal(image, interiorY, bottom - 4, cx, dx: -1, out int left)) return null;
        if (!TryConsensusOutlineHorizontal(image, interiorY, bottom - 4, cx, dx: 1, out int right)) return null;

        if (!TryConsensusBrightnessTop(image, cx - SafeHorizontalProbeHalfWidth, cx + SafeHorizontalProbeHalfWidth,
                interiorY, out int top))
            return null;

        int width = right - left, height = bottom - top;
        if (width < 50 || height < 50) return null;
        if (width > MaxPlausibleWidth || height > MaxPlausibleHeight) return null;

        return new Rect(left, top, width + 1, height + 1);
    }

    /// <summary>Returns a Y that is inside the content area, starting from the tab label's bottom. Both real tab
    /// states have to work here:
    /// <list type="bullet">
    /// <item><b>Description is the active tab</b> — it merges into the content area (standard tab UI), so there
    /// is no chrome line below the label and the label is already inside the content area.</item>
    /// <item><b>Description is inactive</b> (the Lore tab is selected) — it's drawn as its own raised box, so a
    /// short stack of chrome lines sits below it: the tab box's own bottom edge, then the content area's top
    /// outline, only a few px apart. Both must be stepped past — stopping between them would make the content
    /// area's top outline itself look like the window's bottom on the very next downward scan.</item>
    /// </list>
    /// </summary>
    private static int FindContentInteriorBelowAnchor(CapturedImage image, int cx, int anchorBottom)
    {
        const int chromeSearchDistance = 40;
        const int clearanceBelowChrome = 4;

        int? lastLine = null;
        int y = anchorBottom;
        for (int guard = 0; guard < 8; guard++)
        {
            int searchDistance = lastLine is null ? chromeSearchDistance : LineClusterGapPx;
            // No frame check here: these are the window's *internal* tab-chrome lines, which by definition have
            // more window beyond them, not frame.
            if (ScanForOutline(image, cx, y + 1, dx: 0, dy: 1, searchDistance, requireFrameBeyond: false) is not { } line) break;
            lastLine = line;
            y = line;
        }

        return (lastLine ?? anchorBottom) + clearanceBelowChrome;
    }

    /// <summary>Probes ProbeCount columns and returns the consensus Y of the first confirmed outline each one
    /// finds scanning vertically.</summary>
    private static bool TryConsensusOutlineVertical(CapturedImage image, int xRangeStart, int xRangeEnd, int fromY, int dy, out int consensus)
    {
        var found = new List<int>();
        for (int i = 0; i < ProbeCount; i++)
        {
            int x = xRangeStart + (xRangeEnd - xRangeStart) * i / Math.Max(1, ProbeCount - 1);
            if (x < 0 || x >= image.Width || !IsInterior(image, x, fromY)) continue;
            if (ScanForOutline(image, x, fromY + dy, dx: 0, dy, MaxScanDistance, requireFrameBeyond: true) is { } v) found.Add(v);
        }
        return TryGetConsensus(found, out consensus);
    }

    /// <summary>Probes ProbeCount rows spread through the content area and returns the consensus X of the first
    /// confirmed outline each one finds scanning horizontally.</summary>
    private static bool TryConsensusOutlineHorizontal(CapturedImage image, int yRangeStart, int yRangeEnd, int fromX, int dx, out int consensus)
    {
        var found = new List<int>();
        for (int i = 0; i < ProbeCount; i++)
        {
            int y = yRangeStart + (yRangeEnd - yRangeStart) * i / Math.Max(1, ProbeCount - 1);
            if (y < 0 || y >= image.Height || !IsInterior(image, fromX, y)) continue;
            if (ScanForOutline(image, fromX + dx, y, dx, dy: 0, MaxScanDistance, requireFrameBeyond: true) is { } v) found.Add(v);
        }
        return TryGetConsensus(found, out consensus);
    }

    /// <summary>Steps outward looking for the first pixel that looks like the outline and is confirmed by
    /// <see cref="VerifyLineRun"/> — and, when <paramref name="requireFrameBeyond"/>, by the window's outer frame
    /// lying just past it. Returns the coordinate along the scan axis, or null if none is found.</summary>
    private static int? ScanForOutline(CapturedImage image, int x, int y, int dx, int dy, int maxSteps, bool requireFrameBeyond)
    {
        for (int step = 0; step < maxSteps; step++)
        {
            int nx = x + dx * step, ny = y + dy * step;
            if (nx < 0 || nx >= image.Width || ny < 0 || ny >= image.Height) return null;
            if (IsOutlinePixel(image, nx, ny)
                && VerifyLineRun(image, nx, ny, dx, dy)
                && (!requireFrameBeyond || HasFrameBeyond(image, nx, ny, dx, dy)))
                return dx != 0 ? nx : ny;
        }
        return null;
    }

    /// <summary>True if the window's outer frame (a short run of near-black, darker than the interior) lies
    /// within a short distance beyond a candidate outline — see <see cref="FrameMaxChannel"/> for why this is
    /// what distinguishes a real boundary from an internal divider rule.</summary>
    private static bool HasFrameBeyond(CapturedImage image, int x, int y, int dx, int dy)
    {
        int run = 0;
        for (int step = 1; step <= FrameSearchDistance; step++)
        {
            int nx = x + dx * step, ny = y + dy * step;
            if (nx < 0 || nx >= image.Width || ny < 0 || ny >= image.Height) return false;

            if (MinChannel(image, nx, ny) <= FrameMaxMinChannel)
            {
                if (++run >= FrameMinRun) return true;
            }
            else
            {
                run = 0;
            }
        }
        return false;
    }

    /// <summary>Confirms a candidate is part of a real outline rather than a bright glyph edge or a patch of
    /// whatever is behind the window, by requiring a long run of same-brightness outline pixels *perpendicular*
    /// to the scan direction — which is the direction the line itself runs. A glyph edge spans a few px and a
    /// stat value-box outline a few tens, so neither survives this; the content outline runs the window's full
    /// width/height. A broken run is also exactly what "the border is obscured here" looks like.</summary>
    private static bool VerifyLineRun(CapturedImage image, int x, int y, int dx, int dy)
    {
        int pdx = dy != 0 ? 1 : 0; // the line runs across the scan direction
        int pdy = dx != 0 ? 1 : 0;
        int reference = MaxChannel(image, x, y);

        int matches = 0, total = 0;
        for (int side = -1; side <= 1; side += 2)
        {
            for (int k = 1; k <= LineVerifyHalfRun; k++)
            {
                int nx = x + pdx * k * side, ny = y + pdy * k * side;
                if (nx < 0 || nx >= image.Width || ny < 0 || ny >= image.Height) break;
                total++;
                if (IsOutlinePixel(image, nx, ny) && Math.Abs(MaxChannel(image, nx, ny) - reference) <= LineBrightnessTolerance)
                    matches++;
            }
        }

        return total >= LineVerifyHalfRun && (double)matches / total >= LineVerifyMinMatch;
    }

    /// <summary>The window's outer top edge has no outline to trace (the title bar is pure black and simply meets
    /// the world), and the title bar has to stay in the crop for the parser's title-vs-content name check, so
    /// this edge keeps the original scan: step upward tolerating the window's own text and stop at a sustained
    /// bright run.</summary>
    private static bool TryConsensusBrightnessTop(CapturedImage image, int xRangeStart, int xRangeEnd, int fromY, out int consensus)
    {
        var found = new List<int>();
        for (int i = 0; i < ProbeCount; i++)
        {
            int x = xRangeStart + (xRangeEnd - xRangeStart) * i / Math.Max(1, ProbeCount - 1);
            if (x < 0 || x >= image.Width || !IsDarkish(image, x, fromY)) continue;
            if (ScanToBrightness(image, x, fromY, dy: -1, MaxScanDistance) is { } v) found.Add(v);
        }
        return TryGetConsensus(found, out consensus);
    }

    /// <summary>Steps vertically, tolerating brief bright runs (a line of the window's own text) but stopping at
    /// a sustained one, and returns the last confirmed in-window coordinate.</summary>
    private static int? ScanToBrightness(CapturedImage image, int x, int y, int dy, int maxSteps)
    {
        if (!IsDarkish(image, x, y)) return null;
        int lastDarkY = y;
        int brightRun = 0;

        for (int step = 1; step <= maxSteps; step++)
        {
            int ny = y + dy * step;
            if (ny < 0 || ny >= image.Height) return lastDarkY;

            if (IsDarkish(image, x, ny))
            {
                brightRun = 0;
                lastDarkY = ny;
            }
            else if (++brightRun >= SustainedBrightRunThreshold)
            {
                return lastDarkY;
            }
        }
        return null;
    }

    /// <summary>The largest same-value cluster (within tolerance), if at least MinAgreementFraction of the
    /// samples belong to it — otherwise no consensus, i.e. the probes disagree and the edge looks broken.
    /// Plurality rather than median: a wide title or a partly-covered edge can split the probes into groups, and
    /// the median would side with whichever happens to sit mid-list rather than the correct one. The threshold is
    /// deliberately under 50% because on a busy real window even the correct cluster can be a minority.</summary>
    private static bool TryGetConsensus(List<int> values, out int consensus)
    {
        consensus = 0;
        if (values.Count == 0) return false;

        int bestCount = 0, bestValue = values[0];
        foreach (int candidate in values)
        {
            int count = values.Count(v => Math.Abs(v - candidate) <= AgreementTolerancePx);
            if (count > bestCount) { bestCount = count; bestValue = candidate; }
        }

        if ((double)bestCount / values.Count < MinAgreementFraction) return false;

        consensus = (int)Math.Round(values.Where(v => Math.Abs(v - bestValue) <= AgreementTolerancePx).Average());
        return true;
    }

    private static int MaxChannel(CapturedImage image, int x, int y)
    {
        int i = (y * image.Width + x) * 4;
        return Math.Max(image.Pixels[i + 2], Math.Max(image.Pixels[i + 1], image.Pixels[i]));
    }

    private static int MinChannel(CapturedImage image, int x, int y)
    {
        int i = (y * image.Width + x) * 4;
        return Math.Min(image.Pixels[i + 2], Math.Min(image.Pixels[i + 1], image.Pixels[i]));
    }

    private static bool IsInterior(CapturedImage image, int x, int y) => MaxChannel(image, x, y) <= InteriorMaxChannel;

    private static bool IsDarkish(CapturedImage image, int x, int y) => MaxChannel(image, x, y) <= DarkPixelMaxChannel;

    private static bool IsOutlinePixel(CapturedImage image, int x, int y)
    {
        int i = (y * image.Width + x) * 4;
        int b = image.Pixels[i], g = image.Pixels[i + 1], r = image.Pixels[i + 2];
        int max = Math.Max(r, Math.Max(g, b));
        int min = Math.Min(r, Math.Min(g, b));
        return max >= LineMinChannel && max <= LineMaxChannel && max - min <= LineMaxColourSpread;
    }
}
