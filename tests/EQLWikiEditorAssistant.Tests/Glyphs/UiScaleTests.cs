using EQLWikiEditorAssistant.Core.Glyphs;
using EQLWikiEditorAssistant.Core.Icons;
using EQLWikiEditorAssistant.Core.Imaging;
using EQLWikiEditorAssistant.Core.Items;
using EQLWikiEditorAssistant.Core.Locate;

namespace EQLWikiEditorAssistant.Tests.Glyphs;

/// <summary>
/// The game's UI scales above 1.00 (2026-10-13; see <see cref="UiScales"/>). A scale is the 1.00 window with every
/// distance times N, plus its own glyph atlas and its own "Description" template. These pin the parts that run with no
/// samples on disk; the tracing has its own test in <c>WindowTopEdgeTests</c>.
/// </summary>
public class UiScaleTests
{
    public static TheoryData<int> AboveOne => [2, 3, 4];

    public static TheoryData<int> Every => [1, 2, 3, 4];

    /// <summary>Each scale's atlas is the whole sheet with every shape its own. Above 1.00 Arial's l and I are distinct
    /// shapes — only at 1.00 is the I a bare bar — so there is nothing to guess between them.</summary>
    [Theory]
    [MemberData(nameof(AboveOne))]
    public void EachScaleHasTheWholeSheetWithNoAmbiguity(int scale)
    {
        GlyphAtlas atlas = GlyphAtlas.BundledAt(scale);

        Assert.Equal(90, atlas.Entries.Count);
        Assert.Empty(atlas.Ambiguous);
        Assert.All(atlas.Entries, e => Assert.Null(e.Font));
        Assert.Equal(90, atlas.Entries.Select(e => e.Bitmap).Distinct().Count());
        foreach (char expected in "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789:.,%+-/()'`\"")
            Assert.Single(atlas.Entries, e => e.Labels.Contains(expected.ToString()));
    }

    /// <summary>
    /// **Punctuation sits on the same baseline as the letters at every scale.** At 2.00 the sheet's `!@#$%^&amp;*()_+`
    /// row has `@ ( ) _` descending the same 5 rows and `! # % &amp;` on the baseline, and the segmenter broke that tie
    /// downward, putting the whole row 5 rows off: a '(' beside letters then read as a row of its own. The atlas builder
    /// now takes each row's baseline from the characters known to rest on it.
    /// </summary>
    [Theory]
    [MemberData(nameof(Every))]
    public void PunctuationSitsOnTheLettersBaseline(int scale)
    {
        GlyphAtlas atlas = GlyphAtlas.BundledAt(scale);
        int Offset(string label) => atlas.Entries.First(e => e.Labels.Contains(label)).BaselineOffset;

        foreach (string onBaseline in new[] { "!", "#", "%", "&", ".", ":", "?", "a", "1" })
            Assert.Equal(0, Offset(onBaseline));
        foreach (string descends in new[] { "(", ")", "_", "@", ",", "g" })
            Assert.True(Offset(descends) > 0, $"'{descends}' should descend below the baseline at {scale}.00");
        foreach (string floats in new[] { "^", "*", "'", "\"" })
            Assert.True(Offset(floats) < 0, $"'{floats}' should sit above the baseline at {scale}.00");
    }

    /// <summary>Every scale has a template for each font it knows, laid out from the measured label above 1.00, and only
    /// Arial is known there: the other font's glyph sheet exists only at 1.00.</summary>
    [Theory]
    [MemberData(nameof(Every))]
    public void EachScaleHasADescriptionTemplateForEachFontItKnows(int scale)
    {
        Assert.Equal(scale == 1 ? Enum.GetValues<UiFont>() : [UiFont.Arial], UiScales.FontsAt(scale));

        foreach (UiFont font in UiScales.FontsAt(scale))
        {
            IReadOnlyList<int>? lefts = DescriptionTabFinder.MeasuredLayouts.GetValueOrDefault(scale);
            LabelTemplate template = LabelTemplate.Render(
                GlyphAtlas.BundledAt(scale), DescriptionTabFinder.Label, font, scale, lefts);

            Assert.Equal(scale, template.Scale);
            if (lefts is not null)
                Assert.Equal(lefts[^1] + GlyphAtlas.BundledAt(scale).Entries.Single(e => e.Labels.Contains("n")).Bitmap.Width,
                    template.Width);
        }
    }

    /// <summary>A measured layout that does not have one position per character is refused rather than drawn short.</summary>
    [Fact]
    public void AMeasuredLayoutMustPlaceEveryCharacter() =>
        Assert.Throws<ArgumentException>(() =>
            LabelTemplate.Render(GlyphAtlas.BundledAt(2), DescriptionTabFinder.Label, UiFont.Arial, 2, [0, 17, 31]));

    /// <summary>
    /// A space is 2N + 1 past the cell. Measured from the spike frames: no space is never more than N past it and a space
    /// never less than 2.5N, so the threshold must sit between — 3N, the 1.00 figure scaled, would read the 5-column
    /// spaces seen at 2.00 as none.
    /// </summary>
    [Fact]
    public void ASpaceIsTwoScalesAndOnePastTheCell()
    {
        Assert.Equal(GlyphReader.MinSpaceAdvance, GlyphReader.SpaceThreshold(1));

        // A 2.00 digit: a 14-column cell. Two columns past it is no space; five past it is one.
        Assert.False(GlyphReader.IsSpace(Glyph(100, 14), Glyph(116, 14), scale: 2));
        Assert.True(GlyphReader.IsSpace(Glyph(100, 14), Glyph(119, 14), scale: 2));
        Assert.True(GlyphReader.IsSpace(Glyph(100, 14), Glyph(119, 14), scale: 1));
    }

    /// <summary>
    /// Above 1.00 a learned cell is believed up to its 1.00 cell plus one, times the scale. '1' is the case the ink-based
    /// limit got wrong: a real 14-column cell on 7px of ink at 2.00, so "1.0" read as "1 .0". A cell learned only from the
    /// glyph sheet, where every character is spaced out, still goes: the digits' 21 at 2.00 is the cell plus a space.
    /// </summary>
    [Theory]
    [InlineData(14, 7, 2, 6, 14)]   // '1' at 2.00: a real wide side bearing, kept
    [InlineData(21, 13, 2, 7, 13)]  // '4' at 2.00, learned only with a space: clamped to its ink
    [InlineData(28, 15, 4, 6, 28)]  // '1' at 4.00
    [InlineData(6, 4, 1, 99, 6)]    // 1.00 ignores the 1.00 cell and keeps its own rule
    [InlineData(14, 7, 2, 0, 7)]    // no 1.00 cell known: the ink rule, scaled
    public void AboveOneACellIsJudgedAgainstItsCellAtOne(int learned, int ink, int scale, int cellAtOne, int expected) =>
        Assert.Equal(expected, GlyphReader.ClampAdvance(learned, ink, scale, cellAtOne));

    /// <summary>The icon cell is the 1.00 cell times the scale, measured on Fresh Fish at 2.00, 3.00 and 4.00: its ink
    /// ends on the scaled cell's right and bottom edges, as it does at 1.00.</summary>
    [Theory]
    [MemberData(nameof(Every))]
    public void TheIconCellIsTheOneAt100TimesTheScale(int scale)
    {
        var window = new LocatedWindow(new Rect(100, 200, 394 * scale, 318 * scale), [], HasLoreTab: false,
            PossiblyOccluded: false, Scale: scale);

        Assert.Equal(new Rect(100 + 12 * scale, 200 + 54 * scale, 44 * scale, 44 * scale), ItemIconReader.CellIn(window));
    }

    /// <summary>The parser's one distance, the row tolerance, scales too: a value drawn 12px below its label is on the
    /// label's row at 2.00, as one drawn 6px below is at 1.00, and is a row of its own when read as 1.00.</summary>
    [Fact]
    public void TheRowToleranceScales()
    {
        TextLine[] window =
        [
            L("Fish Rolls", 120, 20), L("Description", 100, 40), L("Fish Rolls", 60, 70),
            L("Class: ALL", 60, 90), L("Race: ALL", 60, 106),
            L("Size:", 10, 140), L("SMALL", 80, 146),
        ];
        TextLine[] doubled = [.. window.Select(l => L(l.Text, l.BoundingBox.X * 2, l.BoundingBox.Y * 2))];

        Assert.Equal("SMALL", ItemParser.Parse(window).Stats.Single(s => s.Key == "Size").Value);
        Assert.Equal("SMALL", ItemParser.Parse(doubled, scale: 2).Stats.Single(s => s.Key == "Size").Value);
        Assert.DoesNotContain(ItemParser.Parse(doubled).Stats, s => s.Key == "Size" && s.Value == "SMALL");
    }

    private static TextLine L(string text, int x, int y) => new(text, new Rect(x, y, 10, 10), []);

    private static GlyphMatch Glyph(int left, int advance) =>
        new(new AtlasEntry(["1"], 0, new GlyphBitmap(7, 1, [6, 6, 6, 6, 6, 6, 6]), advance), left, 0, 0);
}
