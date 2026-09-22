using EQLWikiAssistant.Core.Ocr;
using EQLWikiAssistant.Core.Text;

namespace EQLWikiAssistant.Core.Locate;

/// <summary>
/// Finds item detail windows in a full screenshot's worth of OCR lines. Every real item window's content is
/// already recognized as text by a whole-screenshot OCR pass (see the plan's milestone 2 writeup for why
/// running OCR on the full frame, not just hand-picked crops, works once the detector's resolution is set to
/// match the image), so locating a window is mostly a matter of clustering already-recognized lines.
///
/// Algorithm: each "Description" tab line seeds one window's cluster (every real item window has exactly one —
/// see the plan's window-vs-tooltip rule, confirmed against real screenshots). Unassigned lines are repeatedly
/// pulled into the nearest reachable anchor's cluster, where "reachable" requires two things:
///  1. The line sits within a realistic item-window size envelope measured from that anchor (not from the
///     cluster's own drifting bounds) — this is what actually stops the cluster from swallowing a neighboring
///     window or an unrelated UI panel. Tried anchor-agnostic pairwise clustering first; it does not work,
///     because two *different* item windows sitting close together are both dark-background UI, so the gap
///     between them can look just as "dark" as the gap between two columns of the *same* window. An
///     anchor-relative size cap is the only cheap thing that reliably tells "still this window" from "drifted
///     into the next one," since we independently know real windows are on the order of a few hundred px.
///  2. The line is pixel-bridged (see HasDarkBridge) to some line already in that cluster, i.e. the space
///     between them is the window's own near-black interior, not a lighter color (game world showing through,
///     or a UI panel with a different background). Confirmed empirically: item window interiors sample at
///     ~RGB(16,16,16); a gap that should NOT bridge samples at ~RGB(150-170,120-140,70-90) — a wide margin.
///
/// When a line is reachable from more than one anchor (two windows close enough that their envelopes overlap),
/// it's assigned to whichever anchor it's closest to.
///
/// Keeping only clusters seeded by a real "Description" tab gets tooltip exclusion for free: a tooltip has no
/// such tab, so it never seeds a cluster and its lines are simply never claimed.
/// </summary>
public static class ItemWindowLocator
{
    // Per-window size envelope, measured from the anchor (Description tab) line — generous over every real
    // window we've measured (largest so far: ~550px wide, ~530px of content below the tab row).
    private const int EnvelopeHorizontalHalfWidth = 350;
    private const int EnvelopeAboveAnchor = 60; // room for the title bar sitting just above the tab row
    private const int EnvelopeBelowAnchor = 750;

    // Cheap pre-filter before the (more expensive) pixel-bridge check.
    private const int MaxVerticalGap = 40;
    private const int MaxHorizontalGap = 220;

    // How far above the Description tab row the window's title-bar (item name) line is expected to sit.
    private const int TitleBarMaxGap = 40;

    // A pixel this dark or darker is "window interior" (or its border) — see the class doc for the measured
    // values behind this number; real interior samples were ~16, real gaps were ~150+.
    private const int DarkPixelMaxChannel = 90;

    public static IReadOnlyList<LocatedWindow> Locate(CapturedImage image, IReadOnlyList<OcrLine> lines)
    {
        List<int> anchorIndices = lines
            .Select((l, i) => (l, i))
            .Where(p => IsDescriptionTab(p.l.Text))
            .Select(p => p.i)
            .ToList();
        if (anchorIndices.Count == 0) return Array.Empty<LocatedWindow>();

        int n = lines.Count, k = anchorIndices.Count;
        var owner = new int[n]; // index into anchorIndices, or -1 if unclaimed
        Array.Fill(owner, -1);
        for (int a = 0; a < k; a++) owner[anchorIndices[a]] = a;

        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int i = 0; i < n; i++)
            {
                if (owner[i] != -1) continue;

                int bestAnchor = -1;
                long bestDistance = long.MaxValue;
                for (int a = 0; a < k; a++)
                {
                    Rect anchorBox = lines[anchorIndices[a]].BoundingBox;
                    if (!WithinEnvelope(anchorBox, lines[i].BoundingBox)) continue;
                    if (!LinkedToCluster(image, lines, owner, a, lines[i].BoundingBox)) continue;

                    long dist = ChebyshevDistance(anchorBox, lines[i].BoundingBox);
                    if (dist < bestDistance) { bestDistance = dist; bestAnchor = a; }
                }

                if (bestAnchor != -1)
                {
                    owner[i] = bestAnchor;
                    changed = true;
                }
            }
        }

        var windows = new List<LocatedWindow>();
        for (int a = 0; a < k; a++)
        {
            List<OcrLine> groupLines = Enumerable.Range(0, n).Where(i => owner[i] == a).Select(i => lines[i]).ToList();
            OcrLine anchor = lines[anchorIndices[a]];

            Rect bounds = UnionBounds(groupLines.Select(l => l.BoundingBox));
            bool hasLoreTab = groupLines.Any(l => l != anchor && IsLoreTab(l.Text, l.BoundingBox, anchor.BoundingBox));
            bool possiblyOccluded = !HasTitleLineAbove(groupLines, anchor.BoundingBox);

            List<OcrLine> ordered = groupLines.OrderBy(l => l.BoundingBox.Y).ThenBy(l => l.BoundingBox.X).ToList();
            windows.Add(new LocatedWindow(bounds, ordered, hasLoreTab, possiblyOccluded));
        }

        return windows.OrderBy(w => w.Bounds.Y).ThenBy(w => w.Bounds.X).ToList();
    }

    private static bool WithinEnvelope(Rect anchorBox, Rect candidate) =>
        candidate.X >= anchorBox.X - EnvelopeHorizontalHalfWidth &&
        candidate.Right <= anchorBox.Right + EnvelopeHorizontalHalfWidth &&
        candidate.Y >= anchorBox.Y - EnvelopeAboveAnchor &&
        candidate.Bottom <= anchorBox.Bottom + EnvelopeBelowAnchor;

    private static bool LinkedToCluster(CapturedImage image, IReadOnlyList<OcrLine> lines, int[] owner, int anchor, Rect candidate)
    {
        for (int j = 0; j < lines.Count; j++)
        {
            if (owner[j] != anchor) continue;
            if (IsLinked(image, candidate, lines[j].BoundingBox)) return true;
        }
        return false;
    }

    private static long ChebyshevDistance(Rect a, Rect b)
    {
        int dx = Math.Max(0, Math.Max(a.X - b.Right, b.X - a.Right));
        int dy = Math.Max(0, Math.Max(a.Y - b.Bottom, b.Y - a.Bottom));
        return Math.Max(dx, dy);
    }

    private static bool IsLinked(CapturedImage image, Rect a, Rect b)
    {
        int hGap = HorizontalGap(a, b);
        int vGap = VerticalGap(a, b);
        if (hGap > MaxHorizontalGap || vGap > MaxVerticalGap) return false;

        // Touching/overlapping boxes (e.g. two text rows whose OCR boxes bleed into each other by a couple px)
        // have no real gap to bridge-check — checking anyway risks sampling on top of a glyph's own bright
        // pixels and producing a false "not dark" (a real bug, caught by testing: two adjacent, legitimately
        // connected lines were getting split apart because the sample path landed inside one box's own text
        // instead of in empty space).
        if (hGap == 0 && vGap == 0) return true;

        return HasDarkBridge(image, a, b);
    }

    /// <summary>Samples pixels along the empty gap between the two boxes (never inside either box's own
    /// interior/text); true only if every sample is dark enough to be window interior (DarkPixelMaxChannel).</summary>
    private static bool HasDarkBridge(CapturedImage image, Rect a, Rect b)
    {
        int x0, y0, x1, y1;

        if (a.Right <= b.X || b.Right <= a.X) // separated left-right: bridge is a horizontal strip
        {
            (Rect left, Rect right) = a.Right <= b.X ? (a, b) : (b, a);
            x0 = left.Right;
            x1 = right.X;
            int overlapStart = Math.Max(a.Y, b.Y), overlapEnd = Math.Min(a.Bottom, b.Bottom);
            y0 = y1 = overlapEnd > overlapStart
                ? (overlapStart + overlapEnd) / 2
                : ((a.Y + a.Height / 2) + (b.Y + b.Height / 2)) / 2;
        }
        else if (a.Bottom <= b.Y || b.Bottom <= a.Y) // separated top-bottom: bridge is a vertical strip
        {
            (Rect top, Rect bottom) = a.Bottom <= b.Y ? (a, b) : (b, a);
            y0 = top.Bottom;
            y1 = bottom.Y;
            int overlapStart = Math.Max(a.X, b.X), overlapEnd = Math.Min(a.Right, b.Right);
            x0 = x1 = overlapEnd > overlapStart
                ? (overlapStart + overlapEnd) / 2
                : ((a.X + a.Width / 2) + (b.X + b.Width / 2)) / 2;
        }
        else
        {
            return true; // boxes overlap in both axes — IsLinked's gap==0 shortcut should already have caught this
        }

        int steps = Math.Max(1, Math.Max(Math.Abs(x1 - x0), Math.Abs(y1 - y0)) / 4);
        for (int i = 0; i <= steps; i++)
        {
            double t = (double)i / steps;
            int x = Math.Clamp((int)Math.Round(x0 + (x1 - x0) * t), 0, image.Width - 1);
            int y = Math.Clamp((int)Math.Round(y0 + (y1 - y0) * t), 0, image.Height - 1);
            if (!IsDarkPixel(image, x, y)) return false;
        }
        return true;
    }

    private static bool IsDarkPixel(CapturedImage image, int x, int y)
    {
        int i = (y * image.Width + x) * 4;
        byte b = image.Pixels[i], g = image.Pixels[i + 1], r = image.Pixels[i + 2];
        return Math.Max(r, Math.Max(g, b)) <= DarkPixelMaxChannel;
    }

    private static int HorizontalGap(Rect a, Rect b)
    {
        if (a.Right < b.X) return b.X - a.Right;
        if (b.Right < a.X) return a.X - b.Right;
        return 0;
    }

    private static int VerticalGap(Rect a, Rect b)
    {
        if (a.Bottom < b.Y) return b.Y - a.Bottom;
        if (b.Bottom < a.Y) return a.Y - b.Bottom;
        return 0;
    }

    /// <summary>"Description" tab label, tolerant of OCR noise (e.g. "Descripbon").</summary>
    public static bool IsDescriptionTab(string text) =>
        EditDistance.IsCloseMatch(text.Trim(), "Description", maxDistance: 3);

    /// <summary>"Lore" tab label — short, so a tight edit-distance budget — positioned to the right of, and
    /// roughly level with, the Description tab it sits next to.</summary>
    private static bool IsLoreTab(string text, Rect box, Rect descriptionBox) =>
        EditDistance.IsCloseMatch(text.Trim(), "Lore", maxDistance: 1) &&
        box.X > descriptionBox.X &&
        VerticalGap(box, descriptionBox) <= MaxVerticalGap;

    private static bool HasTitleLineAbove(IReadOnlyList<OcrLine> groupLines, Rect anchorBox) =>
        groupLines.Any(l =>
            l.BoundingBox.Bottom <= anchorBox.Bottom &&
            l.BoundingBox.Y < anchorBox.Y &&
            VerticalGap(l.BoundingBox, anchorBox) <= TitleBarMaxGap &&
            HorizontalGap(l.BoundingBox, anchorBox) <= MaxHorizontalGap);

    private static Rect UnionBounds(IEnumerable<Rect> boxes)
    {
        using var e = boxes.GetEnumerator();
        e.MoveNext();
        Rect r = e.Current;
        int minX = r.X, minY = r.Y, maxX = r.Right, maxY = r.Bottom;
        while (e.MoveNext())
        {
            r = e.Current;
            minX = Math.Min(minX, r.X);
            minY = Math.Min(minY, r.Y);
            maxX = Math.Max(maxX, r.Right);
            maxY = Math.Max(maxY, r.Bottom);
        }
        return new Rect(minX, minY, maxX - minX, maxY - minY);
    }
}
