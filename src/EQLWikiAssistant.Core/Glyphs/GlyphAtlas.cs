using System.Text;

namespace EQLWikiAssistant.Core.Glyphs;

/// <summary>One labelled glyph shape. <see cref="Labels"/> is a list because two characters can be genuinely
/// pixel-identical in this font — measured, not hypothetical: lowercase 'l' and uppercase 'I' are both a bare
/// 2x9 vertical bar with no serif or crossbar to separate them.</summary>
public sealed record AtlasEntry(IReadOnlyList<string> Labels, int BaselineOffset, GlyphBitmap Bitmap, int Advance = 0)
{
    public bool IsAmbiguous => Labels.Count > 1;

    /// <summary>Whether this glyph's cell width is known. Advances are learned from real text rather than from
    /// the sheet (whose characters are all space-separated by design), so a glyph that never appeared immediately
    /// before another one has none — in which case the reader falls back to a gap threshold.</summary>
    public bool HasAdvance => Advance > 0;
}

/// <summary>
/// The labelled glyph shapes of the game's UI font: the lookup table that turns an exactly-matched bitmap back
/// into a character.
///
/// Built by <c>GlyphSpike atlas</c> from the in-game Notes Window sheet the user typed (a-z, A-Z, digits and
/// punctuation, each character separated by a space so it segments cleanly), and verified against real item
/// windows — the same 'A' is byte-identical in both, so one atlas serves the whole UI.
///
/// <b>Ambiguity is recorded, never resolved here.</b> A shape that two characters share maps to both labels, and
/// it is the caller's job to choose using context. Guessing at this layer would be the exact failure mode this
/// project is built to avoid: silently emitting a plausible wrong character into a public wiki edit.
///
/// The file format is deliberately plain text, one entry per line, with the bitmap written as one digit per
/// pixel — so the atlas diffs readably in git and a reviewer can see a glyph change rather than a blob.
/// </summary>
public sealed class GlyphAtlas
{
    public const string FormatVersion = "1";

    public IReadOnlyList<AtlasEntry> Entries { get; }

    private readonly Dictionary<(GlyphBitmap, int), AtlasEntry> _byShape;

    public GlyphAtlas(IReadOnlyList<AtlasEntry> entries)
    {
        Entries = entries;
        _byShape = entries.ToDictionary(e => (e.Bitmap, e.BaselineOffset));
    }

    public IReadOnlyList<AtlasEntry> Ambiguous => [.. Entries.Where(e => e.IsAmbiguous)];

    private static readonly Lazy<GlyphAtlas> Default = new(() =>
    {
        using Stream stream = typeof(GlyphAtlas).Assembly
            .GetManifestResourceStream("EQLWikiAssistant.Core.Glyphs.eql-ui-font.atlas")
            ?? throw new InvalidOperationException("The bundled glyph atlas is missing from this assembly.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    });

    /// <summary>The atlas shipped inside this assembly. Embedded rather than written beside the executable so it
    /// cannot go missing at runtime — the RapidOCR models are a loose file dependency and that has already cost a
    /// real failure once.</summary>
    public static GlyphAtlas Bundled => Default.Value;

    /// <summary>The characters a shape could be, or empty if the atlas has never seen it. Empty is a valid,
    /// expected answer — non-text shapes (the item icon, the tier bar, checkboxes) hit it constantly, and an
    /// unmatched shape must degrade to "nothing recognized here", never to a guess.</summary>
    public IReadOnlyList<string> Lookup(GlyphBitmap bitmap, int baselineOffset) =>
        _byShape.TryGetValue((bitmap, baselineOffset), out AtlasEntry? entry) ? entry.Labels : [];

    /// <summary>Builds an atlas from segmented glyphs and the text they are known to spell.
    ///
    /// The bands come from a sheet whose contents are known in advance, so labelling is positional rather than a
    /// manual pass — and the count check below is what makes that safe: if a band's glyph count doesn't match the
    /// string it should spell, the segmentation is wrong and every label after that point would be silently
    /// shifted, which is far worse than refusing.</summary>
    /// <summary>Finds the sheet's character rows among everything else segmented from the screenshot: the run of
    /// consecutive bands whose glyph counts match <paramref name="expectedRows"/> exactly.
    ///
    /// This exists so the builder doesn't have to be handed precise pixel coordinates. The sheet is typed into an
    /// in-game window that cannot be reopened in the same place twice, and the next capture will be for a new or
    /// differently-sized font rather than a redo of this one, so a hardcoded region would be wrong every time it
    /// mattered. Matching on the count sequence is specific enough to be safe — a run of bands holding 26, 26, 12,
    /// 12, 7 and 7 glyphs is not something the rest of a screenshot produces by accident — and it still refuses
    /// rather than guessing if nothing matches.</summary>
    public static int FindSheetStart(IReadOnlyList<TextBand> bands, IReadOnlyList<string> expectedRows)
    {
        for (int start = 0; start + expectedRows.Count <= bands.Count; start++)
        {
            bool matches = true;
            for (int row = 0; row < expectedRows.Count && matches; row++)
                matches = bands[start + row].Glyphs.Count() == expectedRows[row].Length;
            if (matches) return start;
        }
        return -1;
    }

    public static GlyphAtlas FromLabelledBands(IReadOnlyList<TextBand> bands, IReadOnlyList<string> expectedRows)
    {
        int start = FindSheetStart(bands, expectedRows);
        if (start < 0)
            throw new ArgumentException(
                $"No run of {expectedRows.Count} consecutive bands has the expected glyph counts " +
                $"[{string.Join(", ", expectedRows.Select(r => r.Length))}]. Segmented: " +
                $"[{string.Join(", ", bands.Select(b => b.Glyphs.Count()))}].");

        var byShape = new Dictionary<(GlyphBitmap, int), List<string>>();
        for (int row = 0; row < expectedRows.Count; row++)
        {
            List<GlyphBox> glyphs = [.. bands[start + row].Glyphs.OrderBy(g => g.X)];
            string expected = expectedRows[row];

            for (int i = 0; i < glyphs.Count; i++)
            {
                var key = (glyphs[i].Bitmap, glyphs[i].BaselineOffset);
                if (!byShape.TryGetValue(key, out List<string>? labels)) byShape[key] = labels = [];
                string label = expected[i].ToString();
                if (!labels.Contains(label)) labels.Add(label);
            }
        }

        return new GlyphAtlas([.. byShape.Select(kv => new AtlasEntry(kv.Value, kv.Key.Item2, kv.Key.Item1))
            .OrderBy(e => e.Labels[0], StringComparer.Ordinal)]);
    }

    public string Save()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# EQL UI glyph atlas, format {FormatVersion}");
        sb.AppendLine("# labels<TAB>baselineOffset<TAB>width<TAB>height<TAB>levels<TAB>advance");
        sb.AppendLine("# levels is one ramp level 0-6 per pixel, row-major. advance is the glyph's cell width, 0 if unknown.");
        sb.AppendLine("# Multiple labels on one line means those characters are pixel-identical in this font.");
        foreach (AtlasEntry entry in Entries)
            sb.AppendLine(string.Join('\t',
                string.Concat(entry.Labels), entry.BaselineOffset,
                entry.Bitmap.Width, entry.Bitmap.Height, entry.Bitmap.Encode(), entry.Advance));
        return sb.ToString();
    }

    public static GlyphAtlas Parse(string text)
    {
        var entries = new List<AtlasEntry>();
        foreach (string line in text.Split('\n'))
        {
            string trimmed = line.TrimEnd('\r');
            if (trimmed.Length == 0) continue;

            // A line is an entry if it has the five tab-separated fields, and a comment otherwise. Testing for a
            // leading '#' instead is wrong here, because '#' is itself a glyph label: that rule silently dropped
            // the '#' entry on load, and the only symptom was one unreadable character in an otherwise perfect
            // round-trip. Comment text deliberately contains no tabs.
            string[] parts = trimmed.Split('\t');
            if (parts.Length is not (5 or 6)) continue;

            IReadOnlyList<string> labels = [.. parts[0].Select(c => c.ToString())];
            entries.Add(new AtlasEntry(
                labels,
                int.Parse(parts[1]),
                GlyphBitmap.Decode(int.Parse(parts[2]), int.Parse(parts[3]), parts[4]),
                parts.Length == 6 ? int.Parse(parts[5]) : 0));
        }
        return new GlyphAtlas(entries);
    }
}
