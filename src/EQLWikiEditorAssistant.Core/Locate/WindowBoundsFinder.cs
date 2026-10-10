using EQLWikiEditorAssistant.Core.Imaging;

namespace EQLWikiEditorAssistant.Core.Locate;

/// <summary>
/// Finds an item window's bounds by tracing the **content area's own outline** — the thin neutral-grey line the
/// game draws around the Description/Lore tab contents — outward from a known interior point (the tab label's box).
/// Returns null rather than guessing when that line can't be found consistently, which the caller treats as
/// "occluded, don't parse".
///
/// **Why the grey line and not a brightness transition.** Two earlier designs failed, and the reasons are the
/// design: (1) clustering recognized lines by text proximity can't tell a window's own content from an adjacent dark
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
/// **The top edge uses a different piece of the window's chrome.** There is no grey outline at the window's outer
/// top, and the parser needs the title bar inside the crop (it reconciles the title-bar name against the
/// content-area name), so the top is placed from the *title bar's own pure-black band* instead — see
/// <see cref="ScanToTitleBarBottom"/>.
///
/// Every threshold here was measured against real screenshots, not derived on paper. Re-tune only with
/// <c>tools/LocateSpike</c> against the full real-sample set — <c>--probe</c> for raw pixel values, and
/// <c>EQLWIKI_LOCATE_DIAG=1</c> to print what every individual probe answered, which is how the agreement
/// thresholds below were set.
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

    // --- Top edge: a fixed height up from the title bar's bottom ---
    //
    // **The title bar is always 16 rows** (user, 2026-10-10, replacing a rule that confirmed the bar's shape). The
    // first pure-black row above the content area is the bar's bottom, and the window's top is 15 rows above it,
    // clamped to the top of the screen, where the game lets up to 2 rows be clipped. Nothing above the bar is
    // looked at, so nothing above it can mislead: black chrome a little above with world between, a HUD panel flush
    // on top, or another window's title bar flush across the whole width (`Speckled Molded Mushroom`, user,
    // 2026-10-09, reported covered when it was not).
    //
    // What it replaced, from 2026-10-01: a band of pure black about 16 rows tall that had to *end*, with columns
    // abstaining when it did not. Every case it guarded is above the bar, and its one refusal — a merged band
    // across the whole width, "nothing to measure" — was exactly the false occlusion above. Measured across 147
    // located windows in the sample set, 103 of 109 at the time read exactly 16; the rest were a covered window, the
    // screen-edge clip, and four the old scan read a pixel high.
    private const int TitleBarSearchDistance = 60;  // content interior up to the black band
    private const int TitleBarHeight = 16;

    private const int ProbeCount = 11;
    private const int AgreementTolerancePx = 6;

    // Two agreement thresholds, because the two signals are not equally clean — measured across the real sample
    // set with EQLWIKI_LOCATE_DIAG=1, which prints every probe's answer:
    //  - Outline-traced edges score 64-100% on clean windows and 55-67% on the one genuinely part-covered edge
    //    in the set. Those overlap, so **agreement alone cannot decide occlusion** — a threshold strict enough
    //    to reject the covered edge (0.7) also rejects a legitimately clean window touching a neighbour and one
    //    sitting at the screen edge. So this stays permissive and <see cref="IsRectangleClosed"/> does the real
    //    work of rejecting a partly-covered window.
    //  - The title bar's bottom is clean: every column reads the same row.
    private const double MinOutlineAgreementFraction = 0.45;
    private const double MinTopAgreementFraction = 0.4;
    // Used for the scan that runs *before* the window's width is known (the bottom outline). Once left and right
    // are traced, the top scan spans those instead.
    private const int SafeHorizontalProbeHalfWidth = 100;

    // Inset from the traced content outline when probing the title bar, so no column lands on the window's own
    // frame. The outline sits a few px inside the outer frame, so this only has to clear the tracing tolerance.
    private const int TitleBarProbeInset = 12;

    // **No size ceiling, and no limit on how far an edge scan travels short of the frame** (bug found by the user,
    // 2026-10-07: a window resized to 1074px wide was reported "partly covered"). Item windows can be resized, and
    // the 600x700 ceiling and 700px scan that used to sit here refused any that were. The ceiling was a backstop for
    // an occluder flush along a window's entire side, where every probe agrees on the same oversized answer — but
    // measured, it never fired: all 119 windows in the corpus trace to byte-identical rectangles with and without it.
    // IsRectangleClosed and Parse's title-vs-content name check remain the defences for that case.

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

        if (!TryConsensusTitleBarBottom(image, left + TitleBarProbeInset, right - TitleBarProbeInset,
                interiorY, out int titleBarBottom))
            return null;
        int top = Math.Max(0, titleBarBottom - TitleBarHeight + 1);

        int width = right - left, height = bottom - top;
        if (width < 50 || height < 50) return null;
        if (!IsRectangleClosed(image, left, right, bottom, interiorY)) return null;

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
        const int interiorSearchDistance = 24;

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

        // A fixed clearance isn't enough on its own: the content's first line of text can start within a few px
        // of the outline (a real Lore-tab capture has it 4px below), so the clearance lands inside a glyph and
        // every interior test downstream fails. Step down to the first row that's actually interior.
        int candidate = (lastLine ?? anchorBottom) + clearanceBelowChrome;
        for (int i = 0; i < interiorSearchDistance; i++)
        {
            if (candidate + i >= image.Height) break;
            if (IsInterior(image, cx, candidate + i)) return candidate + i;
        }
        return candidate;
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
            if (ScanForOutline(image, x, fromY + dy, dx: 0, dy, Math.Max(image.Width, image.Height), requireFrameBeyond: true) is { } v) found.Add(v);
        }
        return TryGetConsensus(found, MinOutlineAgreementFraction, out consensus);
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
            if (ScanForOutline(image, fromX + dx, y, dx, dy: 0, Math.Max(image.Width, image.Height), requireFrameBeyond: true) is { } v) found.Add(v);
        }
        return TryGetConsensus(found, MinOutlineAgreementFraction, out consensus);
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
                return requireFrameBeyond ? OutermostOutline(image, nx, ny, dx, dy) : dx != 0 ? nx : ny;
        }
        return null;
    }

    /// <summary>
    /// The last outline before the frame, starting from one already confirmed.
    ///
    /// **The Lore tab draws two outlines 3px apart** (bug found by the user, 2026-10-07, on `Tarnished Ancient Tiara`):
    /// the tab's own, which is the content outline every Description window has, and inside it one around the lore's
    /// text area. Measured on the real capture, left to right: frame at 1547-1548, interior, the tab outline at 1552,
    /// interior, the text area's at 1555; at the bottom 658 and 652. Both have the frame within
    /// <see cref="FrameSearchDistance"/>, so stopping at the first cropped every Lore capture 3px short on the left and
    /// right and 6px at the bottom — narrower than the Description capture stacked above it. A Description window has
    /// nothing between its outline and the frame, so this changes nothing there.
    /// </summary>
    private static int OutermostOutline(CapturedImage image, int x, int y, int dx, int dy)
    {
        int best = dx != 0 ? x : y;
        for (int step = 1; step <= LineClusterGapPx; step++)
        {
            int nx = x + dx * step, ny = y + dy * step;
            if (nx < 0 || nx >= image.Width || ny < 0 || ny >= image.Height) break;
            if (IsFrameBlack(image, nx, ny)) break;

            if (IsOutlinePixel(image, nx, ny) && VerifyLineRun(image, nx, ny, dx, dy) &&
                HasFrameBeyond(image, nx, ny, dx, dy))
                best = dx != 0 ? nx : ny;
        }

        return best;
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

            if (IsFrameBlack(image, nx, ny))
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

    /// <summary>Confirms the traced edges actually form a closed rectangle, by checking the outline is present at
    /// the corners rather than only where the probes happened to cross it.
    ///
    /// This is what rejects a partly-covered window, and it does the job that probe agreement can't: on the real
    /// sample set, a clean window's edge agreement (64-100%) overlaps a covered edge's (55-67%), so no threshold
    /// separates them. Closure does, because a covered edge's consensus lands on the *occluding* window's
    /// outline — and this window's own bottom outline then doesn't reach that corner, since the neighbour's
    /// interior is there instead. Geometry that can't close is exactly "the border is broken somewhere".</summary>
    private static bool IsRectangleClosed(CapturedImage image, int left, int right, int bottom, int interiorY)
    {
        const int cornerInset = 10;
        const int tolerance = 3;

        // The bottom outline must span the full traced width …
        if (!HasOutlinePixelNear(image, left + cornerInset, bottom, dx: 0, dy: 1, tolerance)) return false;
        if (!HasOutlinePixelNear(image, right - cornerInset, bottom, dx: 0, dy: 1, tolerance)) return false;

        // … and both side outlines must run the height of the content area, not just where a probe crossed them.
        foreach (int y in new[] { interiorY + cornerInset, bottom - cornerInset })
        {
            if (!HasOutlinePixelNear(image, left, y, dx: 1, dy: 0, tolerance)) return false;
            if (!HasOutlinePixelNear(image, right, y, dx: 1, dy: 0, tolerance)) return false;
        }
        return true;
    }

    /// <summary>True if an outline pixel sits within <paramref name="tolerance"/> of (x,y) along (dx,dy) — the
    /// slack absorbs the averaging in <see cref="TryGetConsensus"/>, which can land an edge a pixel or two off
    /// the actual line.</summary>
    private static bool HasOutlinePixelNear(CapturedImage image, int x, int y, int dx, int dy, int tolerance)
    {
        for (int d = -tolerance; d <= tolerance; d++)
        {
            int nx = x + dx * d, ny = y + dy * d;
            if (nx < 0 || nx >= image.Width || ny < 0 || ny >= image.Height) continue;
            if (IsOutlinePixel(image, nx, ny)) return true;
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

    /// <summary>Probes ProbeCount columns and returns the consensus Y of the title bar's bottom row.</summary>
    private static bool TryConsensusTitleBarBottom(
        CapturedImage image, int xRangeStart, int xRangeEnd, int fromY, out int consensus)
    {
        var found = new List<int>();
        for (int i = 0; i < ProbeCount; i++)
        {
            int x = xRangeStart + (xRangeEnd - xRangeStart) * i / Math.Max(1, ProbeCount - 1);
            if (x < 0 || x >= image.Width) continue;
            if (ScanToTitleBarBottom(image, x, fromY) is { } v) found.Add(v);
        }
        return TryGetConsensus(found, MinTopAgreementFraction, out consensus);
    }

    /// <summary>The first pure-black row going up from inside the content area — the title bar's bottom — or null
    /// when there is none within reach.</summary>
    private static int? ScanToTitleBarBottom(CapturedImage image, int x, int fromY)
    {
        int y = fromY;
        int searched = 0;
        while (!IsPureBlack(image, x, y))
        {
            if (++searched > TitleBarSearchDistance || --y < 0) return null;
        }
        return y;
    }

    /// <summary>The largest same-value cluster (within tolerance), if at least MinAgreementFraction of the
    /// samples belong to it — otherwise no consensus, i.e. the probes disagree and the edge looks broken.
    /// Plurality rather than median: a wide title or a partly-covered edge can split the probes into groups, and
    /// the median would side with whichever happens to sit mid-list rather than the correct one. The threshold is
    /// deliberately under 50% because on a busy real window even the correct cluster can be a minority.</summary>
    private static bool TryGetConsensus(List<int> values, double minAgreementFraction, out int consensus)
    {
        consensus = 0;
        if (values.Count == 0) return false;
        if (Environment.GetEnvironmentVariable("EQLWIKI_LOCATE_DIAG") == "1")
            Console.Error.WriteLine($"DIAG consensus over [{string.Join(",", values)}]");

        int bestCount = 0, bestValue = values[0];
        foreach (int candidate in values)
        {
            int count = values.Count(v => Math.Abs(v - candidate) <= AgreementTolerancePx);
            if (count > bestCount) { bestCount = count; bestValue = candidate; }
        }

        if ((double)bestCount / values.Count < minAgreementFraction) return false;

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

    /// <summary>The window's outer frame, tested on the minimum channel so channel bleed from a bright neighbour
    /// doesn't hide it — see <see cref="FrameMaxMinChannel"/>.</summary>
    private static bool IsFrameBlack(CapturedImage image, int x, int y) => MinChannel(image, x, y) <= FrameMaxMinChannel;

    /// <summary>Genuinely black, tested on the *maximum* channel. The title bar band needs this stricter test
    /// rather than <see cref="IsFrameBlack"/>: the active tab's label is yellow, `(191,191,4)`, whose minimum
    /// channel is 4 — so a minimum-channel test reads bright yellow text as black and latches the top edge onto
    /// the tab label instead of the title bar.</summary>
    private static bool IsPureBlack(CapturedImage image, int x, int y) => MaxChannel(image, x, y) <= FrameMaxMinChannel;


    private static bool IsOutlinePixel(CapturedImage image, int x, int y)
    {
        int i = (y * image.Width + x) * 4;
        int b = image.Pixels[i], g = image.Pixels[i + 1], r = image.Pixels[i + 2];
        int max = Math.Max(r, Math.Max(g, b));
        int min = Math.Min(r, Math.Min(g, b));
        return max >= LineMinChannel && max <= LineMaxChannel && max - min <= LineMaxColourSpread;
    }
}
