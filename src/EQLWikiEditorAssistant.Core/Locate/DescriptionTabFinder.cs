using EQLWikiEditorAssistant.Core.Glyphs;
using EQLWikiEditorAssistant.Core.Imaging;

namespace EQLWikiEditorAssistant.Core.Locate;

/// <summary>An item window's "Description" tab label, found in a frame: the label's ink box, in frame coordinates, the
/// UI font that drew it, and whether it sits on a texture — which means the window is in one of the game's other skins
/// (see <see cref="DescriptionTabFinder.TexturedSpread"/>).</summary>
public sealed record DescriptionTab(Rect Label, UiFont Font, bool Textured = false);

/// <summary>
/// Finds every item window's "Description" tab label in a whole frame by matching the label's own pixels, with no
/// text-recognition model (user, 2026-10-09). Every item window has exactly one such tab and a hover tooltip has none,
/// which is why the label has been the anchor from the start; what changed is how it is found.
///
/// **Why not the model that used to find it.** A text-recognition model over the whole frame existed only for this, and it was the whole
/// of a capture's memory peak (1.6-1.9 GB) and most of its time (2.5-4 s). This search takes tens of milliseconds and
/// next to no memory. On every sample it finds exactly the tabs the model found: 148 of 148 across the game's three skins
/// and both UI fonts, with no false matches (measured with a prototype, 2026-10-09; see the plan).
///
/// **The template is the label as the atlas draws it**, glyph by glyph at the learned advances, one per UI font — the
/// fonts draw different r's, so which template matched is also which font drew the window. A test proves each template
/// is the real label pixel for pixel.
///
/// **The match is the blend model, not exact equality,** because the game's other two skins draw text over a texture.
/// The text is the same glyphs on the same ramp in every skin, blended over whatever background is under each pixel:
/// a partly covered pixel is that pixel's background plus its coverage times (peak - background). So:
/// <list type="bullet">
/// <item>every fully covered pixel must equal the peak;</item>
/// <item>the uncovered pixels inside the label's box give the background's range there;</item>
/// <item>every partly covered pixel must fall inside the range its coverage allows over that background, with
/// <see cref="Slack"/> levels to spare, and at most <see cref="MaxMisses"/> may fall outside it — the texture under a
/// letter can be a shade brighter than any background left visible beside it.</item>
/// </list>
/// On the flat background of `default_modern` this reduces to the exact match it was measured against. Finding a
/// window in another skin is what lets the Assistant say which skin it is in rather than failing silently; reading one
/// is not supported (see the plan).
///
/// **Only the whole label is looked for** (user, 2026-10-09). A window covered far enough to hide part of its tab is
/// not found at all, where the old model's fuzzy match reported it as occluded; that much overlap is obvious to the
/// user, and a partial template would also match "inscription" in lore or chat.
///
/// **Compared in the green channel**, not the brightest one the glyph reader uses. The blend happens per colour
/// channel: the selected tab's label is yellow, which has no blue, and the light skin's texture is not neutral, so the
/// brightest channel of the background can be one the text never touches, and the prediction then starts from the wrong
/// base. Green is at the peak in both the white and the yellow label. Measured: the brightest channel found 7 of the
/// other skins' 16 tabs, green 13, then the slack 15 and the misses all 16.
/// </summary>
public static class DescriptionTabFinder
{
    public const string Label = "Description";

    /// <summary>Levels of slack either side of a partly covered pixel's allowed range.</summary>
    public const int Slack = 4;

    /// <summary>Partly covered pixels allowed outside their range, of about 380 in the label.</summary>
    public const int MaxMisses = 3;

    /// <summary>The least the peak may stand above the brightest background pixel. Without it a flat patch solves the
    /// match trivially — the same degenerate case the glyph reader guards against, with the same floor.</summary>
    public const int MinContrast = 64;

    /// <summary>
    /// A label whose background pixels span more than this is on a texture, so its window is in one of the game's other
    /// skins, which the Assistant can find but not read (see the plan). Measured in the green channel: **exactly 0 on
    /// all 131 tabs in `default_modern`**, whose background is one flat grey, and 40-73 on all 16 in `default` and
    /// `default_light`. The threshold sits between with room on both sides.
    /// </summary>
    public const int TexturedSpread = 20;

    private static readonly Lazy<IReadOnlyList<LabelTemplate>> BundledTemplates = new(() =>
        [.. Enum.GetValues<UiFont>().Select(font => LabelTemplate.Render(GlyphAtlas.Bundled, Label, font))]);

    /// <summary>Every "Description" label in the frame, top to bottom then left to right.</summary>
    public static IReadOnlyList<DescriptionTab> Find(CapturedImage image) => Find(image, BundledTemplates.Value);

    public static IReadOnlyList<DescriptionTab> Find(CapturedImage image, IReadOnlyList<LabelTemplate> templates)
    {
        ArgumentNullException.ThrowIfNull(image);
        byte[] green = GreenChannel(image);

        var found = new List<DescriptionTab>();
        foreach (LabelTemplate template in templates)
            foreach (Rect label in template.FindAll(green, image.Width, image.Height))
                // A label matches one position, but nothing rules out a tolerant match a pixel to one side as well.
                if (!found.Any(f => Overlaps(f.Label, label)))
                    found.Add(new DescriptionTab(label, template.Font,
                        template.BackgroundSpread(green, image.Width, label) > TexturedSpread));

        return [.. found.OrderBy(t => t.Label.Y).ThenBy(t => t.Label.X)];
    }

    private static byte[] GreenChannel(CapturedImage image)
    {
        var green = new byte[image.Width * image.Height];
        byte[] pixels = image.Pixels;
        for (int i = 0, j = 1; i < green.Length; i++, j += 4) green[i] = pixels[j]; // BGRA
        return green;
    }

    private static bool Overlaps(Rect a, Rect b) => a.X < b.Right && b.X < a.Right && a.Y < b.Bottom && b.Y < a.Bottom;
}

/// <summary>
/// A run of text as the game draws it, in coverage levels: the glyphs from the atlas laid out at their learned
/// advances on a shared baseline, cropped to the ink. What <see cref="DescriptionTabFinder"/> searches for.
/// </summary>
public sealed class LabelTemplate
{
    public UiFont Font { get; }
    public int Width { get; }
    public int Height { get; }

    /// <summary>Coverage in fifteenths (0, 3, 6, 9, 11, 13 or 15) per pixel, row-major.</summary>
    public IReadOnlyList<int> Fifteenths => _fifteenths;

    private readonly int[] _fifteenths;
    private readonly int[] _full, _none, _part; // pixel offsets (y * Width + x) by kind

    private LabelTemplate(UiFont font, int width, int height, int[] fifteenths)
    {
        Font = font;
        Width = width;
        Height = height;
        _fifteenths = fifteenths;
        _full = [.. Enumerable.Range(0, fifteenths.Length).Where(i => fifteenths[i] == 15)];
        _none = [.. Enumerable.Range(0, fifteenths.Length).Where(i => fifteenths[i] == 0)];
        _part = [.. Enumerable.Range(0, fifteenths.Length).Where(i => fifteenths[i] is > 0 and < 15)];
    }

    /// <summary>
    /// The text as <paramref name="font"/> draws it. Each character must have exactly one atlas entry in that font with
    /// a learned advance, or this throws: a template guessed at the spacing would match nothing and say nothing, and
    /// a missing window is the failure this exists to prevent.
    /// </summary>
    public static LabelTemplate Render(GlyphAtlas atlas, string text, UiFont font)
    {
        ArgumentNullException.ThrowIfNull(atlas);
        ArgumentException.ThrowIfNullOrEmpty(text);

        var placed = new List<(AtlasEntry Entry, int Left, int Top)>();
        int left = 0;
        for (int i = 0; i < text.Length; i++)
        {
            string ch = text[i].ToString();
            AtlasEntry[] candidates = [.. atlas.Entries.Where(e => e.Labels.Contains(ch) && (e.Font is null || e.Font == font))];
            if (candidates.Length != 1)
                throw new InvalidOperationException(
                    $"The atlas has {candidates.Length} entries for '{ch}' in {font}; a label template needs exactly one.");
            AtlasEntry entry = candidates[0];
            // Glyphs sit on one baseline, at row 0 here; a glyph's bottom row is BaselineOffset below it.
            placed.Add((entry, left, -(entry.Bitmap.Height - 1 - entry.BaselineOffset)));
            if (i < text.Length - 1)
            {
                if (!entry.HasAdvance)
                    throw new InvalidOperationException($"'{ch}' in {font} has no learned advance, so the label cannot be laid out.");
                left += entry.Advance;
            }
        }

        int top = placed.Min(p => p.Top), bottom = placed.Max(p => p.Top + p.Entry.Bitmap.Height);
        int width = placed.Max(p => p.Left + p.Entry.Bitmap.Width), height = bottom - top;
        var fifteenths = new int[width * height];
        foreach ((AtlasEntry entry, int x0, int y0) in placed)
        {
            GlyphBitmap bitmap = entry.Bitmap;
            for (int y = 0; y < bitmap.Height; y++)
                for (int x = 0; x < bitmap.Width; x++)
                {
                    int i = (y0 - top + y) * width + x0 + x;
                    fifteenths[i] = Math.Max(fifteenths[i], GlyphRamp.Fifteenths[bitmap[x, y]]);
                }
        }
        return new LabelTemplate(font, width, height, fifteenths);
    }

    /// <summary>Every position where the template matches under the blend model; see <see cref="DescriptionTabFinder"/>.</summary>
    internal IEnumerable<Rect> FindAll(byte[] plane, int width, int height)
    {
        if (width < Width || height < Height) return [];
        Offsets offsets = OffsetsFor(width);
        var found = new System.Collections.Concurrent.ConcurrentBag<Rect>();
        Parallel.For(0, height - Height + 1, y =>
        {
            for (int x = 0; x <= width - Width; x++)
                if (MatchesAt(plane, y * width + x, offsets)) found.Add(new Rect(x, y, Width, Height));
        });
        return found;
    }

    /// <summary>How far apart the background pixels inside the label are, where it was found.</summary>
    internal int BackgroundSpread(byte[] plane, int stride, Rect at)
    {
        int low = 255, high = 0;
        foreach (int offset in OffsetsFor(stride).None)
        {
            int v = plane[at.Y * stride + at.X + offset];
            if (v < low) low = v;
            if (v > high) high = v;
        }
        return high - low;
    }

    /// <summary>Each kind of pixel as an offset into a frame of the given width, worked out once per frame rather than
    /// at each of its millions of positions.</summary>
    private sealed record Offsets(int[] Full, int[] None, int[] Part, int[] PartFifteenths);

    private Offsets OffsetsFor(int stride)
    {
        int[] Of(int[] pixels) => [.. pixels.Select(p => p / Width * stride + p % Width)];
        return new Offsets(Of(_full), Of(_none), Of(_part), [.. _part.Select(p => _fifteenths[p])]);
    }

    /// <summary>Whether the template matches with its top-left corner at <paramref name="origin"/>. The fully covered
    /// pixels are tested first: they must all equal one value, which rejects almost every position at the first or
    /// second pixel.</summary>
    private static bool MatchesAt(byte[] plane, int origin, Offsets offsets)
    {
        int peak = plane[origin + offsets.Full[0]];
        foreach (int offset in offsets.Full)
            if (Math.Abs(plane[origin + offset] - peak) > 1) return false;

        int low = 255, high = 0;
        foreach (int offset in offsets.None)
        {
            int v = plane[origin + offset];
            if (v < low) low = v;
            if (v > high) high = v;
        }
        if (peak - high < DescriptionTabFinder.MinContrast) return false;

        int misses = 0;
        for (int i = 0; i < offsets.Part.Length; i++)
        {
            double coverage = offsets.PartFifteenths[i] / 15.0;
            int v = plane[origin + offsets.Part[i]];
            if ((v < low + coverage * (peak - low) - DescriptionTabFinder.Slack ||
                 v > high + coverage * (peak - high) + DescriptionTabFinder.Slack) &&
                ++misses > DescriptionTabFinder.MaxMisses)
                return false;
        }
        return true;
    }
}
