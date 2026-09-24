using EQLWikiAssistant.Core.Ocr;

namespace EQLWikiAssistant.Core.Glyphs;

/// <summary>One exact atlas hit in an image.</summary>
public sealed record GlyphMatch(AtlasEntry Entry, int Left, int Top, int Baseline)
{
    public int Right => Left + Entry.Bitmap.Width - 1;
    public int Bottom => Top + Entry.Bitmap.Height - 1;
}

/// <summary>
/// Reads text out of a region by matching the glyph atlas exactly, with no recognition step and no guessing.
///
/// <b>Every candidate calibrates itself, which is what makes this immune to window chrome.</b> The obvious design
/// — segment the region into text bands, measure each band's background and peak, then match within it — was
/// built first and fails on real item windows: a band that merges with the item icon, a divider rule or the tier
/// bar takes its calibration from the chrome instead of the text, and the whole row becomes unreadable. Measured
/// on one real window, clean text bands are 9-13px tall while merged ones run to 22, 40, 44 and 65px.
///
/// Instead, a candidate position is checked against a glyph bitmap by *solving* for the two unknowns from the
/// candidate's own pixels: the background is read off the ring of pixels just outside the glyph (which must be
/// uniform), and the peak is solved from the brightest level the bitmap actually uses. Every remaining pixel then
/// has exactly one permitted intensity. That is a very strong constraint — a glyph with three or four distinct
/// levels pins both unknowns and then verifies dozens of pixels against them — so chrome does not accidentally
/// satisfy it, and text reads identically whether it sits on a window's 16-grey content area, the title bar's
/// black, or beside a bright icon.
///
/// Matching is also position-free in the vertical direction: every row is tried as a candidate baseline rather
/// than relying on a band's computed one. Nothing about a line has to be inferred before its glyphs are known.
/// </summary>
public static class GlyphReader
{
    /// <summary>
    /// Background columns between two glyphs before a space is emitted, used only for glyphs whose cell width
    /// isn't known — see <see cref="AtlasEntry.Advance"/> and the remarks on <see cref="IsSpace"/>.
    /// </summary>
    public const int WordGapColumns = 3;

    /// <summary>
    /// Least extra width a space contributes beyond the preceding glyph's own cell.
    ///
    /// Measured, not chosen: bucketing all 26,000-odd adjacent glyph pairs in the corpus by how far past the
    /// preceding cell the next glyph starts gives a cleanly bimodal distribution, with <b>nothing at all at 2</b>:
    /// <code>
    ///   0: 23641   1: 217   |   3: 2650   4: 93   5: 8   7: 16   8: 2   10: 3   11: 11
    ///   \___ no space ___/       \________________ space ________________/
    /// </code>
    /// That empty bucket is what makes a threshold safe here, and is precisely what a bare pixel-gap rule never
    /// had — gaps for spaced and unspaced pairs overlap outright at 3px.
    /// </summary>
    public const int MinSpaceAdvance = 3;

    /// <summary>Most a glyph's cell may exceed its own ink before the learned value is disbelieved.
    ///
    /// Learning takes the tightest spacing seen, which is the no-space case <i>only if</i> the glyph is ever
    /// followed directly by another. Some never are: ':' is always followed by a space in this UI, and so are the
    /// class codes, so their "tightest" observation still contains a space and the learned cell comes out far too
    /// wide — wide enough that the reader then suppresses the very spaces that produced it. That is how
    /// "Class: RNG" read as "Class:RNG", which in turn merged "Time: Instant" into one word and let the
    /// <c>l</c>/<c>I</c> rule resolve it as "lnstant". Measured across the atlas, real side bearings are only 0
    /// or 1 (25 glyphs and 40 glyphs respectively) with a single 2, so anything wider is evidence of an
    /// always-spaced glyph and <see cref="ClampAdvance"/> replaces it.</summary>
    public const int MaxSideBearing = 2;

    /// <summary>Replaces a learned cell that is too wide to be real with the glyph's own ink width.
    ///
    /// Clamping to the ink width rather than to ink+1 matters, and 'Z' is the case that proves it. 'Z' only ever
    /// appears in class lists, always followed by a space, so it learned a cell of 10 against 7px of ink; with
    /// ink+1 the "WIZ MAG" pair then lands 2 past the cell — inside the empty bucket of the distribution above,
    /// which the threshold reads as no space, and the class list merges into "WIZMAG". Clamping to the ink width
    /// is safe in both directions: a genuinely tight follower lands at 0 or 1 (no space) and a spaced one at 3 or
    /// more (space), which is exactly the separation the measurement shows.</summary>
    public static int ClampAdvance(int learned, int inkWidth) =>
        learned > inkWidth + MaxSideBearing ? inkWidth : learned;

    /// <summary>
    /// Whether two adjacent glyphs are separated by a space.
    ///
    /// <b>The gap between them cannot answer this, which cost a full corpus run to establish.</b> Glyphs sit on
    /// per-glyph cells, so a narrow glyph leaves a wide gap with no space present: two adjacent '1's (ink 4px on
    /// a 7px cell) sit 3 background columns apart, exactly as far as a real space sets some other pairs. Reading
    /// the gap at a threshold of 3 turned every "11" into "1 1"; raising it to 4 merged hundreds of real spaces
    /// instead — 387 silently-wrong fields against 50. The ranges genuinely overlap.
    ///
    /// What does separate them is the *cell*: with no space, the next glyph's ink starts one advance along, so
    /// anything beyond that is a space. Advances are learned from real windows by <c>GlyphSpike advances</c>,
    /// because the sheet the atlas is built from deliberately spaces every character out and so cannot show them.
    /// A glyph with no learned advance falls back to the gap threshold.
    /// </summary>
    public static bool IsSpace(GlyphMatch previous, GlyphMatch next) =>
        previous.Entry.HasAdvance
            ? next.Left - previous.Left >= previous.Entry.Advance + MinSpaceAdvance
            : next.Left - previous.Right - 1 >= WordGapColumns;

    /// <summary>Permitted deviation, in raw intensity, between a pixel and the value its ramp level demands once
    /// background and peak are solved. The game's own rounding is not reproducible exactly from the outside, so
    /// this is not zero; it is deliberately tight enough that a wrong glyph cannot slip through.</summary>
    public const int IntensityTolerance = 2;

    /// <summary>Least separation between solved background and peak for a match to be believed.
    ///
    /// Without this, self-calibration has a degenerate solution and finds it: a flat-coloured piece of UI art
    /// solves to background 149 and peak 150, and with a one-unit span *every* ramp level rounds to the same
    /// intensity, so the whole bitmap "matches" inside the tolerance. That is how a scroll-bar arrow read as '!'
    /// and 'j'. Real text is nowhere near: the narrowest span measured in any colour is 192 (title-bar grey on
    /// black), against 239 for white on a content area, so this threshold rejects the degenerate case by a wide
    /// margin without coming close to legitimate text.</summary>
    public const int MinContrast = 64;

    public static IReadOnlyList<OcrLine> Read(CapturedImage image, Rect region, GlyphAtlas atlas)
    {
        List<GlyphMatch> matches = FindMatches(image, region, atlas);
        List<GlyphMatch> accepted = ResolveOverlaps(matches);
        return BuildLines(accepted);
    }

    /// <summary>Learns each glyph's cell width from real text: the tightest spacing observed to a following glyph
    /// is, by definition, the no-space case. Accumulates into <paramref name="advances"/> so several windows can
    /// be pooled — no single window contains every character next to another one.
    ///
    /// Only plausible spacings are counted. A pair that overlaps or sits absurdly far apart is either a stray
    /// match or a genuine space, and letting either set the minimum would teach a too-narrow cell, which then
    /// invents spaces inside every word using that glyph.</summary>
    public static void LearnAdvances(
        CapturedImage image, Rect region, GlyphAtlas atlas, Dictionary<string, int> advances,
        List<(string Label, int Distance)>? observations = null)
    {
        const int minPlausible = 2, maxPlausible = 14;

        List<GlyphMatch> accepted = ResolveOverlaps(FindMatches(image, region, atlas));
        foreach (var line in accepted.GroupBy(m => m.Baseline))
        {
            List<GlyphMatch> ordered = [.. line.OrderBy(m => m.Left)];
            for (int i = 1; i < ordered.Count; i++)
            {
                int advance = ordered[i].Left - ordered[i - 1].Left;
                if (advance < minPlausible || advance > maxPlausible) continue;

                string key = string.Concat(ordered[i - 1].Entry.Labels);
                advances[key] = advances.TryGetValue(key, out int best) ? Math.Min(best, advance) : advance;
                observations?.Add((key, advance));
            }
        }
    }

    /// <summary>Any pixel brighter than this is ink. The two backgrounds a window uses measure exactly 16 (the
    /// content area) and 0 (the title bar), while the dimmest real ink step measured in any colour is 38, so this
    /// separates them with room to spare and without needing to know which background it is looking at.</summary>
    public const int InkFloor = 24;

    /// <summary>Every exact atlas hit in the region, including ones that overlap each other.
    ///
    /// Anchored on each glyph's own brightest pixel rather than swept over every position: a full sweep is
    /// ~90 entries x every pixel, which measured at 11.8s for one window — far too slow for a corpus run, let
    /// alone a hotkey. Ink is a small fraction of a window, and a glyph's brightest pixel must land on ink, so
    /// walking ink pixels and back-solving the implied top-left visits orders of magnitude fewer candidates and
    /// cannot miss a match that the sweep would have found.</summary>
    public static List<GlyphMatch> FindMatches(CapturedImage image, Rect region, GlyphAtlas atlas)
    {
        var matches = new List<GlyphMatch>();
        var anchors = atlas.Entries.Select(BrightestPixel).ToList();

        for (int y = region.Y; y < region.Bottom; y++)
            for (int x = region.X; x < region.Right; x++)
            {
                if (GlyphRamp.Intensity(image.Pixels, image.Width, image.Height, x, y) <= InkFloor) continue;

                for (int i = 0; i < atlas.Entries.Count; i++)
                {
                    AtlasEntry entry = atlas.Entries[i];
                    (int anchorX, int anchorY) = anchors[i];
                    int left = x - anchorX;
                    int top = y - anchorY;
                    if (left < region.X || top < region.Y
                        || left + entry.Bitmap.Width > region.Right
                        || top + entry.Bitmap.Height > region.Bottom) continue;

                    if (Matches(image, left, top, entry.Bitmap))
                        matches.Add(new GlyphMatch(entry, left, top, top + entry.Bitmap.Height - 1 - entry.BaselineOffset));
                }
            }

        return matches;
    }

    private static (int X, int Y) BrightestPixel(AtlasEntry entry)
    {
        byte brightest = 0;
        (int X, int Y) at = (0, 0);
        for (int y = 0; y < entry.Bitmap.Height; y++)
            for (int x = 0; x < entry.Bitmap.Width; x++)
                if (entry.Bitmap[x, y] > brightest)
                {
                    brightest = entry.Bitmap[x, y];
                    at = (x, y);
                }
        return at;
    }

    /// <summary>
    /// Exact match of one bitmap at one position, solving background and peak from the candidate itself.
    ///
    /// The ring of pixels immediately above and below the glyph must all share one intensity: that is the
    /// background, and requiring it uniform also stops a bare stem from matching inside a taller letter that
    /// happens to contain one. The peak then follows from the brightest ramp level the bitmap uses, and every
    /// pixel is checked against the single intensity its own level now demands.
    /// </summary>
    public static bool Matches(CapturedImage image, int left, int top, GlyphBitmap bitmap)
    {
        if (!TrySolveBackground(image, left, top, bitmap, out int background)) return false;
        if (!TrySolvePeak(image, left, top, bitmap, background, out int peak)) return false;

        for (int y = 0; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++)
            {
                int expected = Expected(bitmap[x, y], background, peak);
                int actual = GlyphRamp.Intensity(image.Pixels, image.Width, image.Height, left + x, top + y);
                if (Math.Abs(actual - expected) > IntensityTolerance) return false;
            }

        return true;
    }

    private static int Expected(byte level, int background, int peak) =>
        background + (int)Math.Round((peak - background) * (GlyphRamp.Fifteenths[level] / 15.0));

    /// <summary>
    /// The background, taken from the glyph's <b>own</b> level-0 pixels — the counter of an 'o', the corners of
    /// almost any shape — which must all share one intensity.
    ///
    /// Reading it from a ring of pixels around the glyph instead is the obvious approach and is wrong: the game
    /// draws divider rules flush beneath a line of text, so a descender's bottom ring lands on the rule rather
    /// than on background. Measured on a real "Modified" row, the rule sits at intensity 50 one pixel under the
    /// 'p' descenders of "Bladestopper", and requiring a uniform ring silently dropped exactly those two glyphs —
    /// the word came back as "Bladesto" and "er". A glyph's own interior is both more reliable and more
    /// constraining, since it is where the background genuinely shows through.
    ///
    /// Only a shape with no level-0 pixels at all — the bare 'l'/'I' bar, '-', '|' — needs the ring, and for
    /// those it must be uniform background. That strictness is what keeps a window's own frame from reading as a
    /// column of '|' and '!': the frame runs the full height of the window, so a candidate bar cut out of it has
    /// more frame above and below rather than background. Relaxing the ring for these (taking the darker side)
    /// was tried and immediately produced exactly that.
    /// </summary>
    private static bool TrySolveBackground(CapturedImage image, int left, int top, GlyphBitmap bitmap, out int background)
    {
        background = -1;
        for (int y = 0; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++)
            {
                if (bitmap[x, y] != 0) continue;
                int intensity = GlyphRamp.Intensity(image.Pixels, image.Width, image.Height, left + x, top + y);
                if (background < 0) background = intensity;
                else if (Math.Abs(intensity - background) > IntensityTolerance) return false;
            }

        if (background >= 0) return true;

        for (int x = 0; x < bitmap.Width; x++)
            foreach (int y in (int[])[top - 1, top + bitmap.Height])
            {
                if (y < 0 || y >= image.Height) continue;
                int intensity = GlyphRamp.Intensity(image.Pixels, image.Width, image.Height, left + x, y);
                if (background < 0) background = intensity;
                else if (Math.Abs(intensity - background) > IntensityTolerance) return false;
            }

        return background >= 0;
    }

    /// <summary>The peak, from the brightest ramp level the bitmap uses. Solving from the brightest level keeps
    /// the arithmetic well-conditioned: a dim level would divide by a small number and let a large error in the
    /// implied peak look acceptable.</summary>
    private static bool TrySolvePeak(
        CapturedImage image, int left, int top, GlyphBitmap bitmap, int background, out int peak)
    {
        peak = background;
        byte brightest = 0;
        int brightestX = -1, brightestY = -1;
        for (int y = 0; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++)
                if (bitmap[x, y] > brightest)
                {
                    brightest = bitmap[x, y];
                    brightestX = x;
                    brightestY = y;
                }

        if (brightest == 0) return false; // an all-background bitmap would match anywhere

        int intensity = GlyphRamp.Intensity(image.Pixels, image.Width, image.Height, left + brightestX, top + brightestY);
        peak = background + (int)Math.Round((intensity - background) * 15.0 / GlyphRamp.Fifteenths[brightest]);
        return peak - background >= MinContrast;
    }

    /// <summary>Keeps the largest non-overlapping set of matches, largest first.
    ///
    /// Overlaps are real and expected: a '.' genuinely matches the lower dot of a ':', and a bare stem matches
    /// inside wider letters. Preferring the larger shape resolves both. Doing it globally rather than per line
    /// also settles conflicts between two candidate baselines a pixel apart, without having to decide which rows
    /// are text before the text is known.</summary>
    public static List<GlyphMatch> ResolveOverlaps(List<GlyphMatch> matches)
    {
        var accepted = new List<GlyphMatch>();
        foreach (GlyphMatch match in matches
            .OrderByDescending(m => m.Entry.Bitmap.Width * m.Entry.Bitmap.Height)
            .ThenByDescending(m => m.Entry.Bitmap.InkWeight)
            .ThenBy(m => m.Left).ThenBy(m => m.Top))
        {
            if (accepted.Any(a => Overlaps(a, match))) continue;
            accepted.Add(match);
        }
        return accepted;
    }

    private static bool Overlaps(GlyphMatch a, GlyphMatch b) =>
        a.Left <= b.Right && b.Left <= a.Right && a.Top <= b.Bottom && b.Top <= a.Bottom;

    /// <summary>Gap, in background columns, that separates the item window's two stat *columns* rather than two
    /// words. Measured on a real "Size: MEDIUM  AC: 43" row: word gaps run 4-5px while column gaps run 19-67px,
    /// so this sits well clear of both.</summary>
    public const int ColumnGapColumns = 10;

    /// <summary>A lone glyph is only believed if it carries at least this much ink. The tiniest atlas shapes —
    /// '.', '\'', ',' — are a handful of pixels, which is little enough evidence that window chrome occasionally
    /// satisfies even an exact match; a real isolated value like a stat's "7" is far heavier (measured: every
    /// punctuation shape is 7-52, every digit 81-145). Glyphs sitting alongside others are exempt, because their
    /// neighbours are the corroboration.</summary>
    public const int LoneGlyphMinInk = 20;

    /// <summary>
    /// Groups accepted matches into <see cref="OcrLine"/>s.
    ///
    /// One line per *column segment*, not per baseline: the stat block puts two label/value pairs on one row
    /// ("Size: MEDIUM   AC: 43") separated by a wide gap, and emitting that as a single string would make
    /// <c>ItemParser</c> read the value as "MEDIUM AC: 43". Splitting on the column gap reproduces exactly the
    /// fragment shape <c>RapidOcrEngine</c> produces, which the parser's row-grouping already handles, so no
    /// parser change is needed to switch engines.
    /// </summary>
    /// <summary>Baselines this close belong to the same visual row. The stat block's two columns are not always
    /// rendered on exactly the same baseline, and grouping on the exact value emitted the right-hand column
    /// before the left-hand one — reversing reading order for the parser downstream.</summary>
    public const int BaselineTolerance = 2;

    private static List<OcrLine> BuildLines(List<GlyphMatch> accepted)
    {
        var lines = new List<OcrLine>();
        foreach (List<GlyphMatch> row in ClusterRows(accepted))
        {
            List<GlyphMatch> ordered = [.. row.OrderBy(m => m.Left)];

            var segment = new List<GlyphMatch>();
            for (int i = 0; i < ordered.Count; i++)
            {
                if (i > 0 && ordered[i].Left - ordered[i - 1].Right - 1 >= ColumnGapColumns)
                {
                    Emit(lines, segment);
                    segment = [];
                }
                segment.Add(ordered[i]);
            }
            Emit(lines, segment);
        }
        return lines;
    }

    /// <summary>Groups matches into visual rows, merging baselines within <see cref="BaselineTolerance"/>, and
    /// returns them top to bottom so the emitted lines are in reading order.</summary>
    private static List<List<GlyphMatch>> ClusterRows(List<GlyphMatch> accepted)
    {
        var rows = new List<List<GlyphMatch>>();
        List<GlyphMatch> current = [];
        int anchor = int.MinValue;

        foreach (GlyphMatch match in accepted.OrderBy(m => m.Baseline).ThenBy(m => m.Left))
        {
            if (current.Count > 0 && match.Baseline - anchor > BaselineTolerance)
            {
                rows.Add(current);
                current = [];
            }
            if (current.Count == 0) anchor = match.Baseline;
            current.Add(match);
        }

        if (current.Count > 0) rows.Add(current);
        return rows;
    }

    private static void Emit(List<OcrLine> lines, List<GlyphMatch> segment)
    {
        if (segment.Count == 0) return;
        if (segment.Count == 1 && segment[0].Entry.Bitmap.InkWeight < LoneGlyphMinInk) return;

        // A fragment with no letter or digit in it is chrome, not text. The title bar's decorations match '.' and
        // '_' exactly, and because the parser joins a row's fragments before reading it positionally, those turned
        // a title of "Spit" into "_ . Spit .. .. . . ." — enough to trip the title-vs-content occlusion check and
        // condemn a perfectly good capture. This is the same reasoning behind ItemParser dropping punctuation-only
        // rows, applied one level down where the evidence is better.
        if (!segment.Any(m => m.Entry.Labels.Any(l => l.Length > 0 && char.IsLetterOrDigit(l[0])))) return;

        var text = new System.Text.StringBuilder();
        for (int i = 0; i < segment.Count; i++)
        {
            if (i > 0 && IsSpace(segment[i - 1], segment[i])) text.Append(' ');
            text.Append(segment[i].Entry.Labels.Count == 1 ? segment[i].Entry.Labels[0] : AmbiguousMarker);
        }

        int left = segment.Min(m => m.Left);
        int top = segment.Min(m => m.Top);
        var bounds = new Rect(left, top, segment.Max(m => m.Right) - left + 1, segment.Max(m => m.Bottom) - top + 1);
        string resolved = ResolveAmbiguous(text.ToString());
        lines.Add(new OcrLine(resolved, bounds, [new OcrWord(resolved, bounds)]));
    }

    /// <summary>Placeholder for the one shape the font renders identically for two characters, before context
    /// decides which it is. Deliberately a character that cannot occur in game text, so a bug that leaves one
    /// unresolved is visible rather than silently plausible.</summary>
    private const char AmbiguousMarker = '\u0001';

    /// <summary>
    /// Decides whether each bare vertical bar is a lowercase 'l' or an uppercase 'I', from the case of the other
    /// letters in its word.
    ///
    /// <b>This is the one place the reader chooses rather than reports, and it is deliberate.</b> The two
    /// characters are the same pixels — there is no measurement that separates them, so "don't guess" would mean
    /// emitting both and corrupting every word containing either ("Bladestopper" as "BlIadestopper"). A human
    /// reading the screen resolves it exactly this way, from the surrounding word, and so does this:
    /// <list type="bullet">
    /// <item>word-initial, it is 'I' — this UI writes item names, labels and effect names in Title Case, so a
    /// leading bare bar is a capital ("Idol", "Improved", "IV", "Illusion");</item>
    /// <item>elsewhere it follows the word's other letters: any lowercase makes it 'l' ("Bladestopper",
    /// "Healing", "Shield"), all-uppercase makes it 'I' ("MEDIUM", "GIANT");</item>
    /// <item>a word of nothing but bars is 'I', because in this UI that is a roman numeral ("III").</item>
    /// </list>
    /// The residual risk is a lowercase word beginning with 'l' mid-sentence, which item windows do not produce.
    /// Anything this gets wrong is a *name*, where the wiki lookup's fuzzy matching is the existing backstop —
    /// unlike a digit, which has no such recourse and is never guessed at anywhere in this pipeline.
    /// </summary>
    public static string ResolveAmbiguous(string text)
    {
        if (!text.Contains(AmbiguousMarker)) return text;

        char[] resolved = text.ToCharArray();
        foreach ((int start, int end) in Words(text))
        {
            bool anyLower = false, anyUpper = false;
            for (int i = start; i < end; i++)
            {
                if (char.IsLower(text[i])) anyLower = true;
                else if (char.IsUpper(text[i])) anyUpper = true;
            }

            for (int i = start; i < end; i++)
            {
                if (text[i] != AmbiguousMarker) continue;
                bool wordInitial = i == start;
                resolved[i] = wordInitial || (!anyLower && anyUpper) || (!anyLower && !anyUpper) ? 'I' : 'l';
            }
        }
        return new string(resolved);
    }

    private static IEnumerable<(int Start, int End)> Words(string text)
    {
        int start = -1;
        for (int i = 0; i <= text.Length; i++)
        {
            bool isWord = i < text.Length && !char.IsWhiteSpace(text[i]);
            if (isWord) { if (start < 0) start = i; }
            else if (start >= 0) { yield return (start, i); start = -1; }
        }
    }
}
