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
/// **The top edge uses a different piece of the window's chrome.** There is no grey outline at the window's outer
/// top, and the parser needs the title bar inside the crop (it reconciles the title-bar name against the
/// content-area name), so the top is traced from the *title bar's own pure-black band* instead — see
/// <see cref="ScanToWindowTop"/>. This also started as a brightness scan ("stop at a sustained bright run") and
/// failed for exactly the same reason as the side edges: with another dark UI panel directly above a window, no
/// bright run exists and the scan ran to its limit, failing the window outright.
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

    // --- Top edge: trace the title bar's own black band (see the class doc) ---
    // Above the content area sits the title bar: a band of pure black carrying the window's title text as bright
    // glyphs. Its topmost row is the window's outer top edge. Measured identically on every real capture.
    private const int TitleBarSearchDistance = 60;  // content interior up to the black band
    private const int TitleBarMaxHeight = 60;       // sanity cap on how far the walk may run
    // A real title bar's black band measures ~16px on every capture. A band much taller has merged with adjacent
    // black chrome (another window's title bar, another dark panel), and there is then nothing marking where
    // *this* window starts — so such a probe returns nothing rather than a wrong answer, leaving consensus to the
    // columns that didn't merge. Without this, a window sitting under another dark panel had 8 of 11 probes walk
    // ~200px up into it, agree with each other, and only fail later via the overall height ceiling — losing a
    // perfectly readable window.
    private const int TitleBarMaxBandHeight = 32;
    // Consecutive glyph rows tolerated inside the band. The title's own glyphs interrupt a column, and
    // anti-aliasing means they are *not* simply "bright": a real capture reads 192, 115, 77, 38 down one stroke,
    // so the exit test can't be "black or bright, else stop" — that stops on a glyph's soft edge a few px in and
    // cuts the title bar out of the crop. A real glyph interrupts ~6 rows.
    private const int TitleBarGapMaxRun = 14;

    private const int MaxScanDistance = 700;
    private const int ProbeCount = 11;
    private const int AgreementTolerancePx = 6;

    // Two agreement thresholds, because the two signals are not equally clean — measured across the real sample
    // set with EQLWIKI_LOCATE_DIAG=1, which prints every probe's answer:
    //  - Outline-traced edges score 64-100% on clean windows and 55-67% on the one genuinely part-covered edge
    //    in the set. Those overlap, so **agreement alone cannot decide occlusion** — a threshold strict enough
    //    to reject the covered edge (0.7) also rejects a legitimately clean window touching a neighbour and one
    //    sitting at the screen edge. So this stays permissive and <see cref="IsRectangleClosed"/> does the real
    //    work of rejecting a partly-covered window.
    //  - The title-bar scan is legitimately noisier (a probe column running down a letter of the title breaks
    //    early), scoring 64-82% on clean windows.
    private const double MinOutlineAgreementFraction = 0.45;
    private const double MinTopAgreementFraction = 0.4;
    // Used for the two scans that run *before* the window's width is known (the bottom outline). Once left and
    // right are traced, the top scan spans those instead — see TryFindBounds.
    private const int SafeHorizontalProbeHalfWidth = 100;

    // Inset from the traced content outline when probing the title bar, so no column lands on the window's own
    // frame. The outline sits a few px inside the outer frame, so this only has to clear the tracing tolerance.
    private const int TitleBarProbeInset = 12;

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

        // **The narrow span first, the window's full traced width only as a fallback** (bug found by the user,
        // 2026-09-30, on `Shield of the Stalwart Seas`). The player's HP bar is a black HUD panel, and it sat
        // directly above that window's title bar with no gap at all: the two black regions were contiguous, so every
        // column under the panel correctly refused (a 41px band against a real ~16px one). The panel covered x
        // 780-1145 of a window spanning 807-1210, so the only columns that could answer were the ~65px to its right
        // — outside the ±100 span, leaving zero usable probes and a fully visible window reported as occluded. That
        // is ScanToWindowTop's abstain-and-let-the-others-decide rule working as designed and being given nowhere to
        // work: refusing a merged column only helps if an unmerged one is sampled.
        //
        // **Widening unconditionally was tried first and the corpus rejected it**, which is why this is a fallback
        // rather than a replacement. The wider span also admits columns that merge *slightly* — by less than
        // TitleBarMaxBandHeight, so they answer instead of abstaining — and since TryGetConsensus averages its
        // agreeing cluster, one such column drags the result a pixel high. On `12e-3-neck-items.png` that moved a
        // window's top from 272 to 271 (measured: background to 271, black from 272), which was enough to flip two
        // windows' reading order and score 32 fields against the wrong item. A pixel of top edge is not worth that.
        //
        // So the proven span stays the default and decides every window it can; the wide one only ever runs where
        // the alternative is refusing the window outright, where a possibly-1px-high top beats reading nothing.
        if (!TryConsensusWindowTop(image, cx - SafeHorizontalProbeHalfWidth, cx + SafeHorizontalProbeHalfWidth,
                interiorY, out int top) &&
            !TryConsensusWindowTop(image, left + TitleBarProbeInset, right - TitleBarProbeInset,
                interiorY, out top))
            return null;

        int width = right - left, height = bottom - top;
        if (width < 50 || height < 50) return null;
        if (width > MaxPlausibleWidth || height > MaxPlausibleHeight) return null;
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
            if (ScanForOutline(image, x, fromY + dy, dx: 0, dy, MaxScanDistance, requireFrameBeyond: true) is { } v) found.Add(v);
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
            if (ScanForOutline(image, fromX + dx, y, dx, dy: 0, MaxScanDistance, requireFrameBeyond: true) is { } v) found.Add(v);
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

    /// <summary>Probes ProbeCount columns and returns the consensus Y of the window's outer top.</summary>
    private static bool TryConsensusWindowTop(CapturedImage image, int xRangeStart, int xRangeEnd, int fromY, out int consensus)
    {
        var found = new List<int>();
        for (int i = 0; i < ProbeCount; i++)
        {
            int x = xRangeStart + (xRangeEnd - xRangeStart) * i / Math.Max(1, ProbeCount - 1);
            if (x < 0 || x >= image.Width) continue;
            if (ScanToWindowTop(image, x, fromY) is { } v) found.Add(v);
        }
        return TryGetConsensus(found, MinTopAgreementFraction, out consensus);
    }

    /// <summary>Walks up from the content area into the title bar's black band and returns its topmost row — the
    /// window's outer top edge.
    ///
    /// This replaced an upward brightness scan that stopped at "a sustained bright run", which silently assumed
    /// whatever sits above the window is brighter than it. Real captures break that: with another dark UI panel
    /// directly above, the scan found no bright run at all and ran to its limit, failing the whole window. Here
    /// the black band belongs to the window, so it works regardless of what's above — the exit condition is
    /// leaving the band, not finding brightness.
    ///
    /// Bright rows *inside* the band are the title's own glyphs and are tolerated; the scan stops at the first
    /// row that is neither black nor bright (e.g. the ~16 grey of an adjacent panel). Tolerating brightness is
    /// safe because only black rows move the answer, so overshooting into a bright background changes nothing.
    /// The one narrow case this can't resolve is another window's *own* black chrome butting directly against
    /// this one's, which consensus and the size ceilings are left to catch.</summary>
    private static int? ScanToWindowTop(CapturedImage image, int x, int fromY)
    {
        int y = fromY;
        int searched = 0;
        while (!IsPureBlack(image, x, y))
        {
            if (++searched > TitleBarSearchDistance || --y < 0) return null;
        }

        int bandStart = y;
        int lastBlack = y;
        int gapRun = 0;
        while (--y >= 0 && bandStart - y <= TitleBarMaxHeight)
        {
            if (IsPureBlack(image, x, y))
            {
                // Measured against the band's start, not the last black row found — measuring against the latter
                // means a continuous band drags the limit along with it and never trips.
                if (bandStart - y > TitleBarMaxBandHeight) return null;
                lastBlack = y;
                gapRun = 0;
            }
            else if (IsInterior(image, x, y))
            {
                // Interior grey. A title bar is only ever its own black plus its glyphs, so this is the window's
                // content/border below the band — we've left it. This distinction matters: with one window
                // overlapping another, the two title bars can sit only ~7px apart, which a plain "tolerate any
                // short non-black run" rule bridges straight into the neighbour's chrome. That made a fully
                // visible window come back as occluded because every probe then overran the height cap.
                break;
            }
            else if (++gapRun > TitleBarGapMaxRun)
            {
                break; // a sustained bright run — past the top of the band into whatever is outside
            }
        }
        return lastBlack;
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
