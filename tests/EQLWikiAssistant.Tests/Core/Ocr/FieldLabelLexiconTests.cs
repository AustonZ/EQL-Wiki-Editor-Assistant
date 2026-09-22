using EQLWikiAssistant.Core.Ocr;

namespace EQLWikiAssistant.Tests.Core.Ocr;

public class FieldLabelLexiconTests
{
    [Theory]
    [InlineData("Omamentation", "Ornamentation")] // the confirmed real "rn"->"m" OCR confusion
    [InlineData("Wom Exaltation", "Worn Exaltation")] // "Worn" only ever appears as "Worn Exaltation" in-game
    [InlineData("Womn Exaltation", "Worn Exaltation")]
    [InlineData("Ornamentation", "Ornamentation")] // already correct
    public void Correct_KnownConfusions_FixesToCanonicalLabel(string ocrLabel, string expected) =>
        Assert.Equal(expected, FieldLabelLexicon.Correct(ocrLabel));

    [Fact]
    public void Correct_UnrecognizedText_ReturnsInputUnchanged()
    {
        // Something with no close match to any known label shouldn't be forced onto the nearest one — an
        // unrecognized label may just be a field this lexicon hasn't catalogued yet, not an OCR error.
        Assert.Equal("Some Unrelated Field", FieldLabelLexicon.Correct("Some Unrelated Field"));
    }

    [Fact]
    public void Correct_ShortExactLabel_DoesNotOverCorrect()
    {
        Assert.Equal("AC", FieldLabelLexicon.Correct("AC"));
        Assert.Equal("HP", FieldLabelLexicon.Correct("HP"));
    }

    [Theory]
    [InlineData("Class: WAR CLR", "Class", true)]
    [InlineData("Classs: WAR", "Class", true)] // one extra char, still close
    [InlineData("Race: ALL", "Class", false)]
    public void StartsWithLabel_MatchesFuzzyPrefix(string text, string label, bool expected) =>
        Assert.Equal(expected, FieldLabelLexicon.StartsWithLabel(text, label));

    [Fact]
    public void TryMatchPrefixLabel_StripsLabelAndDelimiter()
    {
        bool matched = FieldLabelLexicon.TryMatchPrefixLabel(
            "Focus Effect Reagent Conservation II", ["Focus Effect", "Click Effect"], out string label, out string remainder);

        Assert.True(matched);
        Assert.Equal("Focus Effect", label);
        Assert.Equal("Reagent Conservation II", remainder);
    }

    [Fact]
    public void TryMatchPrefixLabel_NoCandidateCloseEnough_ReturnsFalse()
    {
        bool matched = FieldLabelLexicon.TryMatchPrefixLabel(
            "Totally unrelated text", ["Focus Effect", "Click Effect"], out _, out _);

        Assert.False(matched);
    }
}
