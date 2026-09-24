using EQLWikiAssistant.Core.Glyphs;
using EQLWikiAssistant.Core.Ocr;
using EQLWikiAssistant.TestSupport;

// Milestone 2d / stage 2 spike tool: the measuring instrument and atlas builder for the glyph-matching work, in
// the same spirit as LocateSpike's --probe (which is how the window-chrome colour profile was measured rather
// than guessed). Keep using this rather than recreating an ad hoc version.
//
//   GlyphSpike dump <image> <x,y,w,h> [--raw]   2D intensity map of a region, so a glyph's actual pixels can be
//                                               read directly instead of inferred from OCR output
//   GlyphSpike segment <image> [x,y,w,h]        text bands / runs / glyph boxes the segmenter finds
//   GlyphSpike cluster <image> [x,y,w,h]        cluster glyphs by exact equality and render each distinct shape
//   GlyphSpike atlas <image> [x,y,w,h] --out f  build the labelled atlas from the Notes Window glyph sheet
//
// The whole approach rests on the font being a deterministic bitmap blit: see GlyphRamp for the measurements.

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: GlyphSpike <dump|segment|cluster|atlas|verify> <imagePath> [args]");
    return 1;
}

string command = args[0];
CapturedImage image = await ImageFile.LoadAsync(args[1]);
Rect region = args.Length > 2 && TryParseRect(args[2], out Rect parsed)
    ? parsed
    : new Rect(0, 0, image.Width, image.Height);

switch (command)
{
    case "dump": return Dump(image, args);
    case "segment": return Segment(image, region, args[1]);
    case "cluster": return Cluster(image, region, args[1]);
    case "atlas": return Atlas(image, region, args);
    case "verify": return Verify(image, region, args);
    default:
        Console.Error.WriteLine($"unknown command '{command}'");
        return 1;
}

static int Dump(CapturedImage image, string[] args)
{
    if (args.Length < 3 || !TryParseRect(args[2], out Rect r))
    {
        Console.Error.WriteLine("usage: GlyphSpike dump <imagePath> <x,y,w,h> [--raw]");
        return 1;
    }
    bool raw = args.Contains("--raw");

    Console.WriteLine($"{args[1]} ({image.Width}x{image.Height}) region {r.X},{r.Y} {r.Width}x{r.Height}");
    Console.WriteLine();

    Console.Write("      ");
    for (int col = 0; col < r.Width; col++) Console.Write(raw ? $"{(r.X + col) % 10,4}" : $"{(r.X + col) % 10}");
    Console.WriteLine();

    for (int row = 0; row < r.Height; row++)
    {
        Console.Write($"{r.Y + row,5} ");
        for (int col = 0; col < r.Width; col++)
        {
            int v = GlyphRamp.Intensity(image.Pixels, image.Width, image.Height, r.X + col, r.Y + row);
            Console.Write(raw ? $"{v,4}" : Symbol(v));
        }
        Console.WriteLine();
    }
    return 0;
}

static int Segment(CapturedImage image, Rect region, string path)
{
    SegmentResult result = GlyphSegmenter.Segment(image, region);
    PrintHeader(path, image, region, result);

    foreach (TextBand band in result.Bands)
    {
        int glyphs = band.Glyphs.Count();
        Console.WriteLine($"  band y={band.Top}-{band.Bottom} (h={band.Bottom - band.Top + 1}) " +
                          $"baseline={band.Baseline}  {band.Runs.Count} run(s), {glyphs} glyph(s)");
        foreach (GlyphRun run in band.Runs)
            Console.WriteLine($"    run x={run.Left}-{run.Right} mask={run.Mask} peak={run.Peak} " +
                              $"glyphs=[{string.Join(", ", run.Glyphs.Select(g => $"{g.X}+{g.Bitmap.Width}x{g.Bitmap.Height}@{g.BaselineOffset}"))}]");
    }
    return 0;
}

static int Cluster(CapturedImage image, Rect region, string path)
{
    SegmentResult result = GlyphSegmenter.Segment(image, region);
    PrintHeader(path, image, region, result);

    // Exact structural equality, no distance metric and no threshold — the font is a deterministic blit, so two
    // renderings of one character are byte-identical. A character that produced more than one cluster would mean
    // that assumption is wrong, which is the single most important thing this command exists to reveal.
    var clusters = new Dictionary<(GlyphBitmap Bitmap, int Baseline), List<GlyphBox>>();
    foreach (GlyphBox glyph in result.Glyphs)
    {
        var key = (glyph.Bitmap, glyph.BaselineOffset);
        if (!clusters.TryGetValue(key, out List<GlyphBox>? members))
            clusters[key] = members = [];
        members.Add(glyph);
    }

    Console.WriteLine($"  {result.Glyphs.Count()} glyph(s) -> {clusters.Count} distinct shape(s)");
    Console.WriteLine();

    int index = 0;
    foreach (var (key, members) in clusters.OrderByDescending(c => c.Value.Count).ThenByDescending(c => c.Key.Bitmap.InkWeight))
    {
        Console.WriteLine($"[{index++,3}] {key.Bitmap.Width}x{key.Bitmap.Height} baseline{key.Baseline:+0;-0;+0}  " +
                          $"x{members.Count}  at {string.Join(" ", members.Take(8).Select(m => $"{m.X},{m.Y}"))}" +
                          (members.Count > 8 ? " ..." : ""));
        Console.Write(key.Bitmap.Render());
        Console.WriteLine();
    }
    return 0;
}

/// <summary>Rows of the in-game Notes Window sheet, in the order the user typed them, each character separated by
/// a space so it segments cleanly. Labelling is positional against these strings rather than a manual pass — the
/// atlas builder refuses if any row's glyph count disagrees, which is what makes that safe.</summary>
static string[] SheetRows() =>
[
    "abcdefghijklmnopqrstuvwxyz",
    "ABCDEFGHIJKLMNOPQRSTUVWXYZ",
    "1234567890-=",
    "!@#$%^&*()_+",
    ",./\\;`",
    "<>?|:~",
];

static int Atlas(CapturedImage image, Rect region, string[] args)
{
    SegmentResult result = GlyphSegmenter.Segment(image, region);
    PrintHeader(args[1], image, region, result);

    if (result.OffRampPixels > 0) return 1;

    GlyphAtlas atlas;
    try
    {
        atlas = GlyphAtlas.FromLabelledBands(result.Bands, SheetRows());
    }
    catch (ArgumentException ex)
    {
        Console.Error.WriteLine($"  !! {ex.Message}");
        Console.Error.WriteLine("  The region must cover exactly the sheet's character rows, in order, and nothing else.");
        return 1;
    }

    int characters = SheetRows().Sum(r => r.Length);
    Console.WriteLine($"  {characters} character(s) -> {atlas.Entries.Count} distinct shape(s)");
    foreach (AtlasEntry entry in atlas.Ambiguous)
    {
        Console.WriteLine($"  ambiguous: {string.Join(" = ", entry.Labels)}  ({entry.Bitmap.Width}x{entry.Bitmap.Height}) " +
                          "- pixel-identical in this font, so only context can separate them");
        Console.Write(entry.Bitmap.Render());
    }

    int outIndex = Array.IndexOf(args, "--out");
    if (outIndex >= 0 && outIndex + 1 < args.Length)
    {
        File.WriteAllText(args[outIndex + 1], atlas.Save());
        Console.WriteLine($"  wrote {args[outIndex + 1]}");
    }
    else
    {
        Console.WriteLine("  (no --out given; not written)");
    }
    return 0;
}

/// <summary>Reads a real region back using the atlas, by exact pixel match — the Stage 2 acceptance gate.
///
/// This is deliberately *not* the eventual engine: it is the smallest thing that answers "is the atlas built from
/// the Notes Window actually the same font real item windows are drawn in, and does it cover them?" Glyphs inside
/// a word touch, so the reader walks each run left to right taking the widest exact match at each position rather
/// than relying on gaps. Anything unmatched is reported as '?' and counted — never guessed at.</summary>
static int Verify(CapturedImage image, Rect region, string[] args)
{
    int atlasIndex = Array.IndexOf(args, "--atlas");
    if (atlasIndex < 0 || atlasIndex + 1 >= args.Length)
    {
        Console.Error.WriteLine("usage: GlyphSpike verify <imagePath> [x,y,w,h] --atlas <file>");
        return 1;
    }
    GlyphAtlas atlas = GlyphAtlas.Parse(File.ReadAllText(args[atlasIndex + 1]));

    SegmentResult result = GlyphSegmenter.Segment(image, region);
    PrintHeader(args[1], image, region, result);

    int matched = 0, unmatched = 0;
    var seen = new HashSet<string>();

    foreach (TextBand band in result.Bands)
    {
        var line = new System.Text.StringBuilder();
        foreach (GlyphRun run in band.Runs)
        {
            int x = run.Left;
            while (x <= run.Right)
            {
                // Atlas bitmaps are trimmed to their own ink, but the font leaves a sidebearing between letters,
                // so advancing by exactly the matched width lands on a blank column. Skip blank columns rather
                // than counting them as failures.
                bool blank = true;
                for (int y = band.Top; y <= band.Bottom && blank; y++) blank = !IsInkAt(image, band.Background, x, y);
                if (blank) { x++; continue; }

                // Prefer the largest match, by width then height then ink. Width alone is not enough: a ':' and a
                // '.' are the same 2px wide, and a '.' matches the colon's lower dot on its own (there is genuine
                // background above and below it), so a width-only rule silently read every "Race:" as "Race.".
                AtlasEntry? best = null;
                foreach (AtlasEntry entry in atlas.Entries)
                {
                    if (best is not null && Rank(entry).CompareTo(Rank(best)) <= 0) continue;
                    int top = band.Baseline + entry.BaselineOffset - entry.Bitmap.Height + 1;
                    if (MatchesAt(image, x, top, entry.Bitmap, band.Background, run.Peak)) best = entry;
                }

                if (best is null)
                {
                    line.Append('?');
                    unmatched++;
                    x++;
                }
                else
                {
                    line.Append(best.IsAmbiguous ? $"[{string.Concat(best.Labels)}]" : best.Labels[0]);
                    foreach (string label in best.Labels) seen.Add(label);
                    matched++;
                    x += best.Bitmap.Width;
                }
            }
            line.Append(' ');
        }
        Console.WriteLine($"  y={band.Top,4}  {line}");
    }

    int total = matched + unmatched;
    Console.WriteLine();
    Console.WriteLine($"  matched {matched}/{total} glyph position(s) ({(total == 0 ? 0 : 100.0 * matched / total):F1}%), " +
                      $"{seen.Count} of {atlas.Entries.Count} atlas shape(s) seen");
    return 0;
}

/// <summary>Exact match of an atlas bitmap against the image at a position, normalizing on the fly with the
/// band's background and the run's peak. Also requires the glyph to be bounded by background (or the image edge)
/// above and below, so a bare stem can't match inside a taller letter that happens to contain one.</summary>
static bool MatchesAt(CapturedImage image, int left, int top, GlyphBitmap bitmap, int background, int peak)
{
    if (left < 0 || top < 0 || left + bitmap.Width > image.Width || top + bitmap.Height > image.Height) return false;

    for (int y = 0; y < bitmap.Height; y++)
        for (int x = 0; x < bitmap.Width; x++)
        {
            int intensity = GlyphRamp.Intensity(image.Pixels, image.Width, image.Height, left + x, top + y);
            if (GlyphRamp.Quantize(intensity, background, peak, out bool offRamp) != bitmap[x, y] || offRamp)
                return false;
        }

    for (int x = 0; x < bitmap.Width; x++)
    {
        if (IsInkAt(image, background, left + x, top - 1)) return false;
        if (IsInkAt(image, background, left + x, top + bitmap.Height)) return false;
    }
    return true;
}

static ValueTuple<int, int, int> Rank(AtlasEntry entry) => (entry.Bitmap.Width, entry.Bitmap.Height, entry.Bitmap.InkWeight);

static bool IsInkAt(CapturedImage image, int background, int x, int y) =>
    x >= 0 && y >= 0 && x < image.Width && y < image.Height
    && GlyphRamp.Intensity(image.Pixels, image.Width, image.Height, x, y) > background + GlyphRamp.InkThreshold;

static void PrintHeader(string path, CapturedImage image, Rect region, SegmentResult result)
{
    Console.WriteLine($"{path} ({image.Width}x{image.Height}) region {region.X},{region.Y} {region.Width}x{region.Height}");
    Console.WriteLine($"  background={result.Background}  bands={result.Bands.Count}  off-ramp pixels={result.OffRampPixels}");
    if (result.OffRampPixels > 0)
        Console.WriteLine("  !! off-ramp pixels present: the fixed-ramp assumption does not hold for this image " +
                          "(rescaled? different skin or UI scale?). Do not build an atlas from it.");
    Console.WriteLine();
}

static char Symbol(int v) => v switch
{
    < 24 => '.',    // background (measured: exactly 16 inside a window, 0 in the title bar)
    < 88 => ':',
    < 136 => '-',
    < 175 => '+',
    < 207 => '*',
    < 240 => '#',
    _ => '@',       // full coverage
};

static bool TryParseRect(string s, out Rect rect)
{
    rect = default;
    string[] parts = s.Split(',');
    if (parts.Length != 4) return false;
    if (!int.TryParse(parts[0], out int x) || !int.TryParse(parts[1], out int y)
        || !int.TryParse(parts[2], out int w) || !int.TryParse(parts[3], out int h)) return false;
    rect = new Rect(x, y, w, h);
    return true;
}
