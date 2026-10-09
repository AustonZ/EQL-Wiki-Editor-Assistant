using EQLWikiEditorAssistant.Core.Ocr;

namespace EQLWikiEditorAssistant.Core.Glyphs;

/// <summary>One glyph found in an image, already normalized. <see cref="BaselineOffset"/> is part of a glyph's
/// identity, not decoration: trimmed to its own ink, a period and an apostrophe can be the same little blob, and
/// only where they sit relative to the baseline tells them apart.</summary>
public sealed record GlyphBox(GlyphBitmap Bitmap, int X, int Y, int BaselineOffset);

/// <summary>A maximal group of glyphs with no word-sized gap between them — roughly a word. <see cref="Mask"/> is
/// which colour channels its text uses, which is how runs of different colours on one line are told apart.</summary>
public sealed record GlyphRun(int Left, int Right, int Mask, int Peak, IReadOnlyList<GlyphBox> Glyphs);

/// <summary>A line of text: a maximal group of consecutive rows containing ink. Background and peak are measured
/// per band (and, for peak, per colour within it) rather than per region — see <see cref="GlyphSegmenter"/>.</summary>
public sealed record TextBand(int Top, int Bottom, int Background, int Baseline, IReadOnlyList<GlyphRun> Runs)
{
    public IEnumerable<GlyphBox> Glyphs => Runs.SelectMany(r => r.Glyphs);
}

public sealed record SegmentResult(int Background, IReadOnlyList<TextBand> Bands, int OffRampPixels)
{
    public IEnumerable<GlyphBox> Glyphs => Bands.SelectMany(b => b.Glyphs);
}

/// <summary>
/// Cuts a region of a capture into text bands, runs and individual glyphs, normalized per <see cref="GlyphRamp"/>.
///
/// Every threshold here was measured with <c>GlyphSpike dump</c> (which prints a region's raw intensities, in the
/// same spirit as <c>LocateSpike --probe</c>) against real captures — measure before changing one:
/// <list type="bullet">
/// <item>the background inside a window's content area reads exactly 16, and the title bar exactly 0;</item>
/// <item>glyphs within a word frequently <b>touch</b> — in a real "ALL" the 'A' ends at x=869 and the 'L' begins
/// at x=870 with no gap — so gap-splitting alone cannot separate them. That is fine for <i>building</i> the
/// atlas, because the in-game Notes Window sheet deliberately spaces every character out, and it is exactly why
/// the engine will match greedily against the atlas rather than pre-segmenting;</item>
/// <item>word gaps measure 4-5 background columns, so a 2-column threshold separates words without splitting a
/// word whose glyphs happen not to touch.</item>
/// </list>
///
/// <b>Background is measured per band and peak per colour within a band</b>, which took two corrections to get
/// right and both were caught by the off-ramp counter rather than by inspection:
/// <list type="bullet">
/// <item>Peak cannot come from a single run. A 2px-wide 'i' or 'l' tops out at ramp level 4 (measured 191 where
/// white's true peak is 255) because its stem is never fully covered, so calibrating on its own maximum
/// mis-normalizes the whole glyph. Peak is therefore the brightest pixel of the <i>same colour</i> anywhere in
/// the band, which any real line of text supplies from its wider letters.</item>
/// <item>Peak cannot come from the band either without splitting by colour, because one line routinely carries
/// two: a white "Focus Effect:" label peaking at 255 sits on the same row as a magenta effect name peaking at
/// 224. Runs are grouped by which channels their brightest pixel uses (white = all three, yellow = R+G,
/// magenta = R+B, green = G alone).</item>
/// </list>
/// The off-ramp count is the check on all of this: captures are lossless, so it should be exactly 0. Anything
/// else means the model no longer describes the image and the caller should refuse to build an atlas from it
/// rather than quietly rounding to the nearest level.
/// </summary>
public static class GlyphSegmenter
{
    /// <summary>Background columns needed to end a run. Measured: 0-1 inside a word, 4-5 between words.</summary>
    public const int WordGapColumns = 2;


    public static SegmentResult Segment(CapturedImage image, Rect region)
    {
        int regionBackground = DetectBackground(image, region);
        int offRamp = 0;
        var bands = new List<TextBand>();

        foreach ((int top, int bottom) in FindBands(image, region, regionBackground))
        {
            int background = DetectBackground(image, region with { Y = top, Height = bottom - top + 1 });

            // Two passes over the band's runs: the first only to learn each run's colour and the brightest pixel
            // of that colour anywhere in the band, because a run cannot be normalized until its colour's true
            // peak is known (see the class remarks — a narrow 'i' never reaches it on its own).
            var extents = FindRuns(image, region, background, top, bottom);
            var peakByMask = new Dictionary<int, int>();
            var masks = new List<int>(extents.Count);
            foreach ((int left, int right) in extents)
            {
                (int mask, int localPeak) = ColourOf(image, background, left, right, top, bottom);
                masks.Add(mask);
                peakByMask[mask] = Math.Max(peakByMask.GetValueOrDefault(mask), localPeak);
            }

            var runs = new List<GlyphRun>();
            for (int r = 0; r < extents.Count; r++)
            {
                (int left, int right) = extents[r];
                int peak = peakByMask[masks[r]];
                var glyphs = new List<GlyphBox>();
                foreach ((int gLeft, int gRight) in FindGlyphColumns(image, background, left, right, top, bottom))
                    glyphs.Add(Extract(image, background, peak, gLeft, gRight, top, bottom, ref offRamp));

                if (glyphs.Count > 0) runs.Add(new GlyphRun(left, right, masks[r], peak, glyphs));
            }

            if (runs.Count == 0) continue;
            bands.Add(new TextBand(top, bottom, background, 0, runs));
        }

        return new SegmentResult(regionBackground, AssignBaselines(bands), offRamp);
    }

    /// <summary>
    /// Fixes each band's baseline — the row where most glyphs' ink ends — then restates every glyph's vertical
    /// position relative to it.
    ///
    /// <b>Ties are broken towards the lower row, and that is load-bearing.</b> On a line of letters the mode is
    /// unambiguous, but the sheet's <c>&lt;&gt;?|:~</c> row has only '?' and ':' actually sitting on the
    /// baseline while '&lt;' and '&gt;' float above it — a 2-2 tie. Breaking it towards the higher row put that
    /// band's baseline 2px up, so the ':' went into the atlas with a bogus offset and never matched a real
    /// "Race:", which then read as "Race." — a silent character substitution, exactly the failure class this
    /// engine exists to remove. Descenders are the only thing that sits lower than the baseline and they are
    /// always a minority of a line, so they cannot win a tie against it.
    ///
    /// A global "text is on a 16px grid, so take the modal baseline phase across all bands" rule was tried here
    /// and is <i>wrong</i>: the pitch is 16px through the stat block but 23px down the exaltation rows, so one
    /// phase for a whole window mis-places the baseline for every section that doesn't share it.
    /// </summary>
    private static List<TextBand> AssignBaselines(List<TextBand> bands) =>
        [.. bands.Select(band =>
        {
            int baseline = Mode(band.Glyphs.Select(g => g.Y + g.Bitmap.Height - 1));
            return band with
            {
                Baseline = baseline,
                Runs = [.. band.Runs.Select(r => r with
                {
                    Glyphs = [.. r.Glyphs.Select(g => g with { BaselineOffset = g.Y + g.Bitmap.Height - 1 - baseline })],
                })],
            };
        })];

    /// <summary>The modal intensity in the region. Text covers a small fraction of any real region, so the most
    /// common value is the background — no threshold to choose, and it adapts to the title bar's 0 and the
    /// content area's 16 without being told which one it is looking at.</summary>
    public static int DetectBackground(CapturedImage image, Rect region)
    {
        var histogram = new int[256];
        for (int y = region.Y; y < region.Bottom; y++)
            for (int x = region.X; x < region.Right; x++)
                histogram[GlyphRamp.Intensity(image.Pixels, image.Width, image.Height, x, y)]++;

        int best = 0;
        for (int v = 1; v < histogram.Length; v++)
            if (histogram[v] > histogram[best]) best = v;
        return best;
    }

    private static bool IsInk(CapturedImage image, int background, int x, int y) =>
        GlyphRamp.Intensity(image.Pixels, image.Width, image.Height, x, y) > background + GlyphRamp.InkThreshold;

    private static List<(int Top, int Bottom)> FindBands(CapturedImage image, Rect region, int background)
    {
        var bands = new List<(int, int)>();
        int? start = null;
        for (int y = region.Y; y < region.Bottom; y++)
        {
            bool hasInk = false;
            for (int x = region.X; x < region.Right && !hasInk; x++) hasInk = IsInk(image, background, x, y);

            if (hasInk) start ??= y;
            else if (start is int s)
            {
                bands.Add((s, y - 1));
                start = null;
            }
        }
        if (start is int last) bands.Add((last, region.Bottom - 1));
        return bands;
    }

    private static List<(int Left, int Right)> FindRuns(
        CapturedImage image, Rect region, int background, int top, int bottom)
    {
        var runs = new List<(int, int)>();
        int? start = null;
        int gap = 0;
        for (int x = region.X; x < region.Right; x++)
        {
            bool hasInk = false;
            for (int y = top; y <= bottom && !hasInk; y++) hasInk = IsInk(image, background, x, y);

            if (hasInk)
            {
                start ??= x;
                gap = 0;
            }
            else if (start is int s && ++gap >= WordGapColumns)
            {
                runs.Add((s, x - gap));
                start = null;
            }
        }
        if (start is int last) runs.Add((last, region.Right - 1));
        return runs;
    }

    private static List<(int Left, int Right)> FindGlyphColumns(
        CapturedImage image, int background, int left, int right, int top, int bottom)
    {
        var columns = new List<(int, int)>();
        int? start = null;
        for (int x = left; x <= right; x++)
        {
            bool hasInk = false;
            for (int y = top; y <= bottom && !hasInk; y++) hasInk = IsInk(image, background, x, y);

            if (hasInk) start ??= x;
            else if (start is int s)
            {
                columns.Add((s, x - 1));
                start = null;
            }
        }
        if (start is int last) columns.Add((last, right));
        return columns;
    }

    /// <summary>A run's text colour, as the set of channels its brightest pixel uses, plus that pixel's
    /// intensity. The brightest pixel is the one to read the colour from: a partly-covered pixel of white text
    /// still has all three channels raised, just less, so any pixel identifies the channel set — but only the
    /// brightest is guaranteed not to have been dimmed below the ink threshold on a weakly-used channel.</summary>
    private static (int Mask, int Peak) ColourOf(
        CapturedImage image, int background, int left, int right, int top, int bottom)
    {
        int peak = 0, peakIndex = -1;
        for (int y = top; y <= bottom; y++)
            for (int x = left; x <= right; x++)
            {
                int intensity = GlyphRamp.Intensity(image.Pixels, image.Width, image.Height, x, y);
                if (intensity <= peak) continue;
                peak = intensity;
                peakIndex = (y * image.Width + x) * 4;
            }

        if (peakIndex < 0) return (0, 0);

        int mask = 0;
        for (int channel = 0; channel < 3; channel++)
            if (image.Pixels[peakIndex + channel] > background + GlyphRamp.InkThreshold)
                mask |= 1 << channel;
        return (mask, peak);
    }

    private static GlyphBox Extract(
        CapturedImage image, int background, int peak,
        int left, int right, int top, int bottom, ref int offRamp)
    {
        // Trim to the glyph's own ink rows, so a glyph's height doesn't depend on what else happened to share its
        // band. The vertical position that trimming discards is preserved by the caller as BaselineOffset.
        int inkTop = bottom, inkBottom = top;
        for (int y = top; y <= bottom; y++)
            for (int x = left; x <= right; x++)
                if (IsInk(image, background, x, y))
                {
                    inkTop = Math.Min(inkTop, y);
                    inkBottom = Math.Max(inkBottom, y);
                }

        int width = right - left + 1;
        int height = inkBottom - inkTop + 1;
        var levels = new byte[width * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int intensity = GlyphRamp.Intensity(image.Pixels, image.Width, image.Height, left + x, inkTop + y);
                levels[y * width + x] = GlyphRamp.Quantize(intensity, background, peak, out bool off);
                if (off) offRamp++;
            }

        return new GlyphBox(new GlyphBitmap(width, height, levels), left, inkTop, 0);
    }

    /// <summary>Most common value, ties broken towards the <b>larger</b> value — see
    /// <see cref="AssignBaselines"/> for why that direction is not arbitrary.</summary>
    private static int Mode(IEnumerable<int> values)
    {
        var counts = new Dictionary<int, int>();
        foreach (int v in values) counts[v] = counts.GetValueOrDefault(v) + 1;
        return counts.OrderByDescending(kv => kv.Value).ThenByDescending(kv => kv.Key).First().Key;
    }
}
