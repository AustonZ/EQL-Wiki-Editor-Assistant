using EQLWikiAssistant.Core.Ocr;

namespace EQLWikiAssistant.Core.Locate;

/// <summary>
/// Finds an item window's true pixel bounds by tracing the edge of its near-black interior from a known
/// interior point (the "Description" tab's OCR box), and refuses to guess when that edge isn't clean —
/// replaces an earlier OCR-line-clustering approach to locate that could not reliably tell "this window's own
/// content" apart from "a different, adjacent dark window" (see the plan's milestone 2 writeup for why: pure
/// text-proximity and even a pixel darkness check both failed on real screenshots where an item window sits
/// directly against another dark UI panel, e.g. the character sheet, with no lighter gap between them).
///
/// The fix is to stop asking "is this pixel dark" along a single ray and instead ask "does the edge agree with
/// itself": every edge (top/bottom/left/right) is found by probing several rows/columns, not one, and taking
/// the consensus. This does two jobs at once: (1) a window's own straight border is consistent all along a
/// side, so something else (another window, a bag panel) covering part of that side changes what a probe finds
/// there — probes disagree, which is what a person means by "the border is broken"; (2) a single probe can
/// also be thrown off by something harmless *inside* the window (its own bright text, its icon) — a few such
/// outliers get outvoted by the rest rather than corrupting the result. If there's no consensus, this returns
/// null: no bounds, no attempt to parse — the caller should tell the user to rearrange windows and recapture,
/// not salvage partial data.
///
/// A single scan also has to tolerate the window's own content on the way to the true edge (see
/// SustainedBrightRunThreshold) — a window is full of bright text (stat lines, flags, class lists, the item's
/// own name) that a plain "stop at the first non-dark pixel" scan trips over almost immediately, nowhere near
/// the true edge. But tolerating too much also risks quietly tolerating straight through a brief, genuine gap
/// into a *different*, adjacent dark window — which is exactly why this can't be a single ray per edge; the
/// multi-probe consensus is what catches that, even when any individual probe might be fooled.
///
/// **Known residual gap**: this only catches occlusion that disturbs a large enough share of an edge's probes.
/// A real sample (see the plan's milestone 2 writeup) has another window covering only the left ~20% of this
/// window's own title text ("Lustrous " out of "Lustrous Russet Bracer +6") — too small a minority to break
/// consensus, so bounds come back clean while the title itself is truncated. Geometry alone can't close this;
/// the parser MUST additionally reconcile the title-bar name against the content-area name (every real item
/// window repeats its own name a few lines into the content — see the plan) and treat a mismatch as suspect.
/// Do not treat "bounds found" as "definitely not occluded" when writing that code.
/// </summary>
public static class WindowBoundsFinder
{
    // A pixel this dark or darker is "window interior" — see the plan's milestone 2 writeup for the measured
    // values behind this number: real interior samples were ~16, real backgrounds/other UI were ~150+.
    private const int DarkPixelMaxChannel = 90;

    private const int MaxScanDistance = 700; // don't chase an edge further than any real window could be
    private const int ProbeCount = 11; // rows/columns sampled per edge
    private const int AgreementTolerancePx = 6; // how close probes must land to "agree"
    private const double MinAgreementFraction = 0.4; // fraction of probes that must land in the largest cluster

    // No single line of this UI's text runs bright for anywhere near this many consecutive pixels along a scan
    // line, so tolerating a run shorter than this (and resuming once dark again) safely skips over individual
    // text rows/glyphs while still stopping at a real, sustained exit into background or different UI.
    private const int SustainedBrightRunThreshold = 26;

    // A conservative half-width, safely inside even the narrowest real window we've measured, used to probe
    // top/bottom without first needing to know the window's true left/right extent.
    private const int SafeHorizontalProbeHalfWidth = 100;

    // Sanity ceilings, generous over the largest real window measured so far (~550px wide, ~655px tall — a
    // heavily augmented weapon with every exaltation slot filled). These catch a failure mode the consensus
    // check alone can't: when an occluding panel (e.g. the character sheet) is adjacent along the *entire*
    // side, not just part of it, every probe agrees on the same wrong, oversized answer — there's no
    // disagreement for the consensus check to notice. An implausibly large result is itself the signal.
    private const int MaxPlausibleWidth = 600;
    private const int MaxPlausibleHeight = 700;

    /// <summary>Returns the window's pixel bounds, or null if the edges aren't clean/consistent (treat as
    /// occluded — don't parse it).</summary>
    public static Rect? TryFindBounds(CapturedImage image, Rect anchorBox)
    {
        int cx = anchorBox.X + anchorBox.Width / 2;
        // Not anchorBox's own vertical center: "Description" is rendered as bright (active-tab yellow) text,
        // so sampling inside its own tight OCR box risks landing on a letter's own bright pixels rather than
        // the dark background around it. A few px below the text sits in the tab's dark interior instead.
        int safeRowY = anchorBox.Bottom + 4;
        if (!IsDark(image, cx, safeRowY)) return null;

        // Top/bottom: probe columns in a safe, anchor-relative range (not yet dependent on knowing left/right).
        if (!TryConsensusVerticalEdge(image, cx - SafeHorizontalProbeHalfWidth, cx + SafeHorizontalProbeHalfWidth, safeRowY, dy: -1, out int top))
            return null;
        if (!TryConsensusVerticalEdge(image, cx - SafeHorizontalProbeHalfWidth, cx + SafeHorizontalProbeHalfWidth, safeRowY, dy: 1, out int bottom))
            return null;

        // Left/right: now that the height is known, probe rows spread across it.
        if (!TryConsensusHorizontalEdge(image, top, bottom, cx, dx: -1, out int leftX))
            return null;
        if (!TryConsensusHorizontalEdge(image, top, bottom, cx, dx: 1, out int rightX))
            return null;

        int width = rightX - leftX, height = bottom - top;
        if (width < 50 || height < 50) return null; // sanity floor, avoids a degenerate sliver
        if (width > MaxPlausibleWidth || height > MaxPlausibleHeight) return null; // sanity ceiling, see above

        return new Rect(leftX, top, rightX - leftX + 1, bottom - top + 1);
    }

    /// <summary>Probes for a top (dy=-1) or bottom (dy=1) edge at ProbeCount columns spread between
    /// <paramref name="xRangeStart"/> and <paramref name="xRangeEnd"/>, each scanning vertically from
    /// <paramref name="fixedY"/>, and returns the consensus Y.</summary>
    private static bool TryConsensusVerticalEdge(CapturedImage image, int xRangeStart, int xRangeEnd, int fixedY, int dy, out int consensus)
    {
        var found = new List<int>();
        for (int i = 0; i < ProbeCount; i++)
        {
            int x = xRangeStart + (xRangeEnd - xRangeStart) * i / Math.Max(1, ProbeCount - 1);
            if (!IsDark(image, x, fixedY)) continue;
            if (ScanEdge(image, x, fixedY, dx: 0, dy, MaxScanDistance) is { } v) found.Add(v);
        }
        return TryGetConsensus(found, out consensus);
    }

    /// <summary>Probes for a left (dx=-1) or right (dx=1) edge at ProbeCount rows spread between
    /// <paramref name="yRangeStart"/> and <paramref name="yRangeEnd"/>, each scanning horizontally from
    /// <paramref name="fixedX"/>, and returns the consensus X.</summary>
    private static bool TryConsensusHorizontalEdge(CapturedImage image, int yRangeStart, int yRangeEnd, int fixedX, int dx, out int consensus)
    {
        var found = new List<int>();
        for (int i = 0; i < ProbeCount; i++)
        {
            int y = yRangeStart + (yRangeEnd - yRangeStart) * i / Math.Max(1, ProbeCount - 1);
            if (!IsDark(image, fixedX, y)) continue;
            if (ScanEdge(image, fixedX, y, dx, dy: 0, MaxScanDistance) is { } v) found.Add(v);
        }
        return TryGetConsensus(found, out consensus);
    }

    /// <summary>Steps from (x,y) in direction (dx,dy), tolerating brief non-dark runs (a text glyph/line) but
    /// stopping at a sustained one (SustainedBrightRunThreshold+ consecutive non-dark pixels — a real exit).
    /// Returns the last confirmed-dark coordinate before that sustained run, or null if it ran past
    /// MaxScanDistance without ever finding one (suspicious — either an unrealistically huge window, or
    /// dark-on-dark occlusion masking the true edge).</summary>
    private static int? ScanEdge(CapturedImage image, int x, int y, int dx, int dy, int maxSteps)
    {
        if (!IsDark(image, x, y)) return null;
        int lastDarkX = x, lastDarkY = y;
        int brightRun = 0;

        for (int step = 1; step <= maxSteps; step++)
        {
            int nx = x + dx * step, ny = y + dy * step;
            if (nx < 0 || nx >= image.Width || ny < 0 || ny >= image.Height)
                return dx != 0 ? lastDarkX : lastDarkY;

            if (IsDark(image, nx, ny))
            {
                brightRun = 0;
                lastDarkX = nx;
                lastDarkY = ny;
            }
            else
            {
                brightRun++;
                if (brightRun >= SustainedBrightRunThreshold)
                    return dx != 0 ? lastDarkX : lastDarkY;
            }
        }
        return null;
    }

    /// <summary>The largest same-value cluster (within tolerance), if at least MinAgreementFraction of the
    /// samples belong to it — otherwise no consensus (the probes disagree, i.e. the edge looks
    /// broken/occluded). Plurality rather than median: a wide title (e.g. "Bloodstar Pendant +6 (Augmented)")
    /// can span most of the probed range, so the *majority* of probes legitimately land on the same
    /// text-obstructed value while the minority that dodge the text reach the true edge somewhere else —
    /// median would side with whichever group happens to sit in the middle of the sorted list, which isn't
    /// reliably the correct one. The threshold is deliberately well under 50%: on a busy real window even the
    /// single largest cluster can be a minority of all probes (title text, tab label, icon, and stray outliers
    /// from adjacent UI can each claim a few), so requiring a plain majority routinely finds no winner at all
    /// on perfectly good windows.</summary>
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

        var agreeing = values.Where(v => Math.Abs(v - bestValue) <= AgreementTolerancePx).ToList();
        consensus = (int)Math.Round(agreeing.Average());
        return true;
    }

    private static bool IsDark(CapturedImage image, int x, int y)
    {
        int i = (y * image.Width + x) * 4;
        byte b = image.Pixels[i], g = image.Pixels[i + 1], r = image.Pixels[i + 2];
        return Math.Max(r, Math.Max(g, b)) <= DarkPixelMaxChannel;
    }
}
