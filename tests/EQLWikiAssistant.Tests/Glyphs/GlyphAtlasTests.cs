using EQLWikiAssistant.Core.Glyphs;

namespace EQLWikiAssistant.Tests.Glyphs;

/// <summary>Plain-data tests for the glyph atlas and its normalization — no image or capture needed, so these run
/// on every <c>dotnet test</c> even on a fresh clone where <c>samples/</c> is absent.</summary>
public class GlyphAtlasTests
{
    /// <summary>The three ramps measured in real captures — white on a window's content area, the dimmer grey of
    /// a title bar over black, and magenta effect text — must all normalize to the same seven levels. This is the
    /// claim that lets one colour-blind atlas read the whole UI, so it is worth pinning rather than assuming.</summary>
    [Theory]
    [InlineData(16, 255, new[] { 16, 64, 112, 159, 191, 223, 255 })]   // white on content area
    [InlineData(0, 192, new[] { 0, 38, 77, 115, 141, 166, 192 })]      // title bar grey on black
    [InlineData(16, 224, new[] { 16, 58, 100, 141, 169, 196, 224 })]   // magenta effect name
    public void Quantize_EveryMeasuredRamp_MapsToTheSameLevels(int background, int peak, int[] measured)
    {
        for (byte level = 0; level < measured.Length; level++)
        {
            byte actual = GlyphRamp.Quantize(measured[level], background, peak, out bool offRamp);
            Assert.False(offRamp, $"{measured[level]} (bg {background}, peak {peak}) should sit on the ramp");
            Assert.Equal(level, actual);
        }
    }

    [Fact]
    public void Quantize_ValueOffTheRamp_IsReported()
    {
        // Captures are lossless, so an off-ramp value means the model no longer describes the image. It must be
        // surfaced, never rounded away: silently snapping to the nearest level is how a wrong glyph gets read
        // confidently.
        GlyphRamp.Quantize(90, 16, 255, out bool offRamp);
        Assert.True(offRamp);
    }

    [Fact]
    public void Bitmap_EqualityIsExactAndStructural()
    {
        var a = new GlyphBitmap(2, 2, [0, 3, 6, 0]);
        var b = new GlyphBitmap(2, 2, [0, 3, 6, 0]);
        var differentPixel = new GlyphBitmap(2, 2, [0, 3, 5, 0]);
        var differentShape = new GlyphBitmap(4, 1, [0, 3, 6, 0]);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, differentPixel);
        Assert.NotEqual(a, differentShape);
    }

    [Fact]
    public void Bitmap_EncodeDecode_RoundTrips()
    {
        var original = new GlyphBitmap(3, 2, [0, 1, 2, 3, 4, 5]);
        Assert.Equal(original, GlyphBitmap.Decode(3, 2, original.Encode()));
    }

    [Fact]
    public void Atlas_SaveParse_RoundTripsIncludingTheCommentCharacter()
    {
        // '#' is both a glyph in this font and the atlas file's comment marker. Parsing comments by a leading '#'
        // silently dropped the '#' entry on load, and the only symptom was one unreadable character in an
        // otherwise perfect round-trip — so the format is parsed by field count instead.
        var atlas = new GlyphAtlas([
            new AtlasEntry(["#"], 0, new GlyphBitmap(2, 2, [1, 2, 3, 4])),
            new AtlasEntry(["l", "I"], 0, new GlyphBitmap(1, 2, [6, 6])),
        ]);

        GlyphAtlas parsed = GlyphAtlas.Parse(atlas.Save());

        Assert.Equal(2, parsed.Entries.Count);
        Assert.Equal(["#"], parsed.Lookup(new GlyphBitmap(2, 2, [1, 2, 3, 4]), 0));
        Assert.Equal(["l", "I"], parsed.Lookup(new GlyphBitmap(1, 2, [6, 6]), 0));
    }

    [Fact]
    public void Atlas_UnknownShape_ReturnsNoLabels()
    {
        // The expected answer for the item icon, the tier bar and every other non-text shape. An unmatched shape
        // must degrade to "nothing recognized here", never to the nearest guess.
        var atlas = new GlyphAtlas([new AtlasEntry(["a"], 0, new GlyphBitmap(1, 1, [6]))]);
        Assert.Empty(atlas.Lookup(new GlyphBitmap(1, 1, [3]), 0));
    }

    [Fact]
    public void Atlas_AmbiguousShape_KeepsEveryLabel()
    {
        // Lowercase 'l' and uppercase 'I' are the same 2x9 bar in this font. The atlas records both and leaves the
        // choice to the caller's context rather than picking one, which would be a silent substitution.
        var shape = new GlyphBitmap(1, 2, [6, 6]);
        var atlas = new GlyphAtlas([new AtlasEntry(["l", "I"], 0, shape)]);

        Assert.Single(atlas.Ambiguous);
        Assert.Equal(["l", "I"], atlas.Lookup(shape, 0));
    }

    [Fact]
    public void FromLabelledBands_GlyphCountMismatch_Throws()
    {
        // Labelling is positional against known text, so a miscount would shift every label after it and bake
        // wrong characters into the atlas. Refusing is the only safe response.
        var band = Band(Glyph(0), Glyph(1));
        Assert.Throws<ArgumentException>(() => GlyphAtlas.FromLabelledBands([band], ["abc"]));
    }

    [Fact]
    public void FromLabelledBands_IdenticalShapes_CollapseToOneAmbiguousEntry()
    {
        var band = Band(Glyph(0), Glyph(10));
        GlyphAtlas atlas = GlyphAtlas.FromLabelledBands([band], ["lI"]);

        Assert.Single(atlas.Entries);
        Assert.Equal(["l", "I"], atlas.Entries[0].Labels);
    }

    /// <summary>Guards the tracked atlas artifact itself. It is generated from a screenshot that isn't in the
    /// repo, so without this a regeneration that quietly lost or merged glyphs would go unnoticed until the
    /// reader started producing wrong characters.</summary>
    [Fact]
    public void BundledAtlas_CoversTheWholeSheetWithOneKnownAmbiguity()
    {
        GlyphAtlas atlas = GlyphAtlas.Bundled;

        // 88 characters typed on the sheet, 87 distinct shapes: 'l' and 'I' are the same bare vertical bar.
        Assert.Equal(87, atlas.Entries.Count);
        Assert.Equal(88, atlas.Entries.Sum(e => e.Labels.Count));

        AtlasEntry ambiguous = Assert.Single(atlas.Ambiguous);
        Assert.Equal(["l", "I"], ambiguous.Labels);

        foreach (char expected in "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789")
            Assert.Contains(atlas.Entries, e => e.Labels.Contains(expected.ToString()));

        // Characters the item windows actually use, beyond letters and digits.
        foreach (char expected in ":.,%+-/()`")
            Assert.Contains(atlas.Entries, e => e.Labels.Contains(expected.ToString()));
    }

    /// <summary>The sheet the atlas was built from has a backtick but no apostrophe, and real item names use
    /// both ("Kilva's Skin of Flame" against "Kavruul`s Mystic Pouch"). Pinned as a known gap so it is visible
    /// rather than showing up as an unreadable character mid-name; delete this test when the sheet is recaptured
    /// with the missing quote characters.</summary>
    [Fact]
    public void BundledAtlas_KnownGap_HasNoQuoteCharacters()
    {
        GlyphAtlas atlas = GlyphAtlas.Bundled;
        Assert.DoesNotContain(atlas.Entries, e => e.Labels.Contains("'"));
        Assert.DoesNotContain(atlas.Entries, e => e.Labels.Contains("\""));
    }

    private static GlyphBox Glyph(int x) => new(new GlyphBitmap(1, 2, [6, 6]), x, 0, 0);

    private static TextBand Band(params GlyphBox[] glyphs) =>
        new(0, 1, Background: 16, Baseline: 1, [new GlyphRun(0, 10, Mask: 7, Peak: 255, glyphs)]);
}
