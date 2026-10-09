using EQLWikiEditorAssistant.Core.Glyphs;

namespace EQLWikiEditorAssistant.Tests.Glyphs;

/// <summary>Plain-string and plain-arithmetic tests for the two decisions the glyph reader makes that are not
/// pure pixel comparison — resolving the one ambiguous glyph, and deciding where the spaces are. Both were got
/// wrong at first in ways that only a corpus run exposed, so they are pinned here where they run in a second.</summary>
public class GlyphReaderTests
{
    private const char Bar = '\u0001';

    /// <summary>'l' and 'I' are the same pixels in this font, so the reader picks from the word around them.
    /// These are the real words it has to get right, taken from the corpus.</summary>
    [Theory]
    [InlineData("B\u0001adestopper", "Bladestopper")]      // mid-word, lowercase neighbours
    [InlineData("Hea\u0001ing", "Healing")]
    [InlineData("Shie\u0001d", "Shield")]
    [InlineData("C\u0001ick Effect:", "Click Effect:")]
    [InlineData("Exa\u0001tation:", "Exaltation:")]
    [InlineData("MED\u0001UM", "MEDIUM")]                  // mid-word, all-uppercase neighbours
    [InlineData("G\u0001ANT", "GIANT")]
    [InlineData("\u0001mproved", "Improved")]              // word-initial in Title Case
    [InlineData("\u0001nstant", "Instant")]
    [InlineData("\u0001do\u0001", "Idol")]                 // both rules in one word
    [InlineData("\u0001\u0001\u0001usion:", "Illusion:")]  // capital I then two lowercase l's
    [InlineData("\u0001\u0001\u0001", "III")]              // nothing but bars: a roman numeral
    [InlineData("\u0001V", "IV")]
    [InlineData("Cast Time: \u0001nstant", "Cast Time: Instant")]
    [InlineData("Ha\u0001as 10\u0001b Meat Pie", "Halas 10lb Meat Pie")] // a digit keeps the bar mid-word
    public void ResolveAmbiguous_UsesTheWordAroundTheBar(string text, string expected) =>
        Assert.Equal(expected, GlyphReader.ResolveAmbiguous(text));

    /// <summary>
    /// Where the word cannot decide, the dictionary does (user, 2026-10-09). Every case is one the old rule got wrong
    /// on real text — lore, quest dialogue or a page title — measured by <c>GlyphSpike bars</c>.
    /// </summary>
    [Theory]
    [InlineData("recover \u0001ost items", "recover lost items")] // the lore word that started this
    [InlineData("the \u0001ight of \u0001ong ago", "the light of long ago")]
    [InlineData("\u0001eve\u0001 50", "level 50")]
    [InlineData("(\u0001ost)", "(lost)")]                       // leading punctuation does not hide the start
    [InlineData("we\u0001\u0001-\u0001it", "well-lit")]          // nor does a hyphen
    [InlineData("Cast-\u0001ron", "Cast-Iron")]
    [InlineData("A\u0001\u0001 who enter", "All who enter")]      // a capital, then only bars
    [InlineData("\u0001'\u0001\u0001 go", "I'll go")]               // the 'll of a contraction
    [InlineData("we'\u0001\u0001", "we'll")]
    [InlineData("\u0001\u0001\u0001-Fitting", "Ill-Fitting")]       // all bars, inside a lowercase token
    [InlineData("a \u0001amb's wool", "a lamb's wool")]           // "iamb" is dropped from the list
    [InlineData("at \u0001v\u0001 50 (/\u0001oc 12, 40)", "at lvl 50 (/loc 12, 40)")] // the game's own words
    [InlineData("a \u0001izardman's ta\u0001e", "a lizardman's tale")]
    public void ResolveAmbiguous_AsksTheDictionaryWhereTheWordCannotSay(string text, string expected) =>
        Assert.Equal(expected, GlyphReader.ResolveAmbiguous(text));

    /// <summary>
    /// The controls: a word the dictionary does not know stays a capital I, because it is almost always a capitalised
    /// name; a word that is real both ways stays I; roman numerals stay roman numerals, which is exactly what "All" could
    /// otherwise be mistaken for; and a quoted 'II' is not a contraction.
    /// </summary>
    [Theory]
    [InlineData("\u0001ksar", "Iksar")]
    [InlineData("\u0001nnoruuk's", "Innoruuk's")]
    [InlineData("\u0001ron", "Iron")]
    [InlineData("\u0001ce", "Ice")]
    [InlineData("\u0001t \u0001s", "It Is")]
    [InlineData("\u0001ota", "Iota")]                             // "lota" is dropped from the list
    [InlineData("V\u0001\u0001", "VII")]
    [InlineData("X\u0001\u0001\u0001", "XIII")]
    [InlineData("Vampirism \u0001\u0001\u0001", "Vampirism III")]
    [InlineData("'\u0001\u0001'", "'II'")]
    [InlineData("A\u0001'Kabor", "Al'Kabor")]                    // lowercase elsewhere in the token decides
    [InlineData("Ku`\u0001u\u0001", "Ku`lul")]                   // a grave accent does not start a word
    public void ResolveAmbiguous_KeepsTheDefaultWhereTheDictionaryCannotHelp(string text, string expected) =>
        Assert.Equal(expected, GlyphReader.ResolveAmbiguous(text));

    /// <summary>
    /// No word in the list may be real both ways, or the reader has to guess between two words (user, 2026-10-09). The
    /// four ENABLE had are dropped; a list regenerated without dropping them, or a game word that collides, fails here.
    /// Both shapes the reader asks about are checked: a first letter l against i, and one letter followed by l's
    /// against the same letter followed by i's, from three letters up as the reader asks it ("al"/"ai" never is).
    /// </summary>
    [Fact]
    public void NoWordReadsBothWays()
    {
        IReadOnlySet<string> words = BarWords.Words;
        string[] collisions = [.. words
            .Where(w => w[0] == 'l' && words.Contains("i" + w[1..]))
            .Concat(words.Where(w => w.Length >= 3 && w[1..].All(c => c == 'l') &&
                                     words.Contains(w[0] + new string('i', w.Length - 1))))];

        Assert.Empty(collisions);
        Assert.Contains("lost", words);                       // the control: the list did load
        Assert.Contains("lvl", words);
        Assert.DoesNotContain("iamb", words);
    }

    [Fact]
    public void ResolveAmbiguous_TextWithoutTheMarker_IsUntouched() =>
        Assert.Equal("Size: MEDIUM", GlyphReader.ResolveAmbiguous("Size: MEDIUM"));

    /// <summary>A learned cell wider than the glyph's ink plus a real side bearing means the glyph was only ever
    /// seen followed by a space, so the measurement includes one. 'Z' is the case: 7px of ink, a learned cell of
    /// 10, and keeping it merged "WIZ MAG" into "WIZMAG".</summary>
    [Theory]
    [InlineData(10, 7, 7)]   // 'Z' — always followed by a space, so clamped to its ink width
    [InlineData(7, 6, 7)]    // 'a' — a real 1px side bearing, kept
    [InlineData(6, 4, 6)]    // '1' — a real 2px side bearing, kept
    [InlineData(7, 7, 7)]    // '4' — no side bearing, kept
    public void ClampAdvance_RejectsCellsTooWideToBeReal(int learned, int inkWidth, int expected) =>
        Assert.Equal(expected, GlyphReader.ClampAdvance(learned, inkWidth));

    /// <summary>Spacing is decided by the cell, not the pixel gap. Two '1's sit 3 background columns apart with no
    /// space between them, which is as far apart as a space puts some other pairs — so a gap rule cannot work,
    /// and did not: at a threshold of 3 every "11" read as "1 1", and at 4 hundreds of real spaces vanished.</summary>
    [Fact]
    public void IsSpace_UsesTheCellWidthWhenKnown()
    {
        // '1': 4px of ink on a 6px cell. A second '1' starts 7px along — one past the cell, so no space.
        Assert.False(GlyphReader.IsSpace(Digit(100, advance: 6), Digit(107, advance: 6)));

        // A glyph starting 10px along is 4 past the cell, which only a space accounts for.
        Assert.True(GlyphReader.IsSpace(Digit(100, advance: 6), Digit(110, advance: 6)));
    }

    [Fact]
    public void IsSpace_FallsBackToTheGapWhenTheCellIsUnknown()
    {
        Assert.False(GlyphReader.IsSpace(Digit(100, advance: 0), Digit(105, advance: 0)));
        Assert.True(GlyphReader.IsSpace(Digit(100, advance: 0), Digit(108, advance: 0)));
    }

    /// <summary>A 4x1 stand-in, so only Left and Advance matter to the assertions above.</summary>
    private static GlyphMatch Digit(int left, int advance) =>
        new(new AtlasEntry(["1"], 0, new GlyphBitmap(4, 1, [6, 6, 6, 6]), advance), left, 0, 0);
}
