using EQLWikiEditorAssistant.Core.Items;

namespace EQLWikiEditorAssistant.Tests.Items;

/// <summary>Labels are matched exactly (2026-10-10): the glyph reader reads the game's text exactly, so a near miss
/// is a label the lexicon lacks, not a misread to bend into a known one.</summary>
public class FieldLabelLexiconTests
{
    [Theory]
    [InlineData("Ornamentation", true)]
    [InlineData("SV. Magic", true)]
    [InlineData(" AC ", true)]
    [InlineData("Omamentation", false)]  // the old recognizer's "rn"->"m" misread, once corrected silently
    [InlineData("ac", false)]
    [InlineData("Some Unrelated Field", false)]
    public void OnlyAnExactLabelIsKnown(string text, bool known) =>
        Assert.Equal(known, FieldLabelLexicon.IsKnownLabel(text));

    [Fact]
    public void APrefixLabelIsStrippedWithItsSeparator()
    {
        Assert.True(FieldLabelLexicon.TryMatchPrefixLabel(
            "Class: WAR PAL RNG", ["Class", "Race"], out string label, out string remainder));
        Assert.Equal("Class", label);
        Assert.Equal("WAR PAL RNG", remainder);
    }

    /// <summary>The game writes some labels with no colon at all.</summary>
    [Fact]
    public void ALabelNeedsNoColon()
    {
        Assert.True(FieldLabelLexicon.TryMatchPrefixLabel(
            "Focus Effect Reagent Conservation II", ["Focus Effect", "Click Effect"], out string label, out string remainder));
        Assert.Equal("Focus Effect", label);
        Assert.Equal("Reagent Conservation II", remainder);
    }

    [Theory]
    [InlineData("Classs: WAR")]          // a near miss is not the label
    [InlineData(".Class: WAR")]          // nor is the label after something else
    [InlineData("Classic Era")]          // and a label must end where a word does
    [InlineData("Totally unrelated text")]
    public void ANearMissIsNotALabel(string text) =>
        Assert.False(FieldLabelLexicon.TryMatchPrefixLabel(text, ["Class"], out _, out _));

    [Fact]
    public void TheLongestLabelWins()
    {
        Assert.True(FieldLabelLexicon.TryMatchPrefixLabel(
            "Cooldown Group: 12", ["Cooldown", "Cooldown Group"], out string label, out string remainder));
        Assert.Equal("Cooldown Group", label);
        Assert.Equal("12", remainder);
    }
}
