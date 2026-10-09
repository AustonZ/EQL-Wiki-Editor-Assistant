using EQLWikiEditorAssistant.Wiki.Wikitext;

namespace EQLWikiEditorAssistant.Tests.Wiki;

/// <summary>
/// The statsblock grammar, tested on lines taken verbatim from real pages rather than from the template blueprint —
/// the blueprint describes an idealized block and the pages are a mix of a Project1999-era import and hand edits.
/// </summary>
public class StatsBlockTests
{
    /// <summary>Compares what these tests mean — the label and the value — rather than the whole record, which now
    /// also carries the value's offset so that an in-place edit is possible.</summary>
    private static void AssertField(string label, string value, StatsField field)
    {
        Assert.Equal(label, field.Label);
        Assert.Equal(value, field.Value);
    }

    [Fact]
    public void Parse_SplitsALineIntoLabelValuePairs()
    {
        StatsBlockLine line = StatsBlock.Parse("WT: 0.1  Size: TINY<br>").Lines[0];

        Assert.Equal(StatsLineKind.Fields, line.Kind);
        Assert.Collection(line.Fields,
            f => AssertField("WT", "0.1", f),
            f => AssertField("Size", "TINY", f));
    }

    /// <summary>Most pages separate two fields with a double space; several use a single one. Both are real.</summary>
    [Fact]
    public void Parse_HandlesSingleSpacedStatRuns()
    {
        StatsBlockLine line = StatsBlock.Parse("STR: +10 WIS: +10 INT: +10 HP: +50 MANA: +50<br>").Lines[0];

        Assert.Equal(
            new[] { "STR", "WIS", "INT", "HP", "MANA" },
            line.Fields.Select(f => f.Label));
        Assert.All(line.Fields, f => Assert.StartsWith("+", f.Value));
    }

    [Fact]
    public void Parse_KeepsSpacesInsideALabel()
    {
        StatsBlockLine line = StatsBlock.Parse("SV FIRE: +5  SV DISEASE: +5<br>").Lines[0];

        Assert.Equal(["SV FIRE", "SV DISEASE"], line.Fields.Select(f => f.Label));
    }

    /// <summary>The value of an Effect line contains two more colons, both inside constructs that nest. A
    /// depth-blind split cuts this line into nonsense.</summary>
    [Fact]
    public void Parse_DoesNotSplitOnAColonInsideParenthesesOrALink()
    {
        StatsBlockLine line = StatsBlock.Parse(
            "Effect: [[Alter Plane: Sky]] (Any Slot/Can Equip, Casting Time: Instant, Cooldown: 120 seconds) at Level 45<br>")
            .Lines[0];

        StatsField only = Assert.Single(line.Fields);
        Assert.Equal("Effect", only.Label);
        Assert.Equal(
            "[[Alter Plane: Sky]] (Any Slot/Can Equip, Casting Time: Instant, Cooldown: 120 seconds) at Level 45",
            only.Value);
    }

    /// <summary>A label may contain a single space but never two, which is what stops "EXPENDABLE  Charges" from
    /// reading as one label — the only place that rule is load-bearing, and the reason it exists.</summary>
    [Fact]
    public void Parse_SeparatesALeadingFlagFromAFieldOnTheSameLine()
    {
        StatsBlockLine line = StatsBlock.Parse("EXPENDABLE  Charges: 10<br>").Lines[0];

        Assert.Equal(["EXPENDABLE"], line.Flags);
        AssertField("Charges", "10", Assert.Single(line.Fields));
    }

    [Theory]
    [InlineData("MAGIC ITEM  LORE ITEM  NO DROP", new[] { "MAGIC ITEM", "LORE ITEM", "NO DROP" })]
    [InlineData("Lore Equipped, No Trade", new[] { "Lore Equipped", "No Trade" })]
    [InlineData("Lore Equipped, No Trade, Quest, Placeable", new[] { "Lore Equipped", "No Trade", "Quest", "Placeable" })]
    [InlineData("Attunable", new[] { "Attunable" })]
    [InlineData("This is a meal!", new[] { "This is a meal!" })]
    public void Parse_ReadsBothFlagDialects(string text, string[] expected)
    {
        StatsBlockLine line = StatsBlock.Parse(text + "<br>").Lines[0];

        Assert.Equal(StatsLineKind.Flags, line.Kind);
        Assert.Equal(expected, line.Flags);
    }

    /// <summary>Three sampled pages write the flags with single spaces, which no separator rule can split without a
    /// flag vocabulary. One unrecognized token is the documented, deliberate outcome — the mapping layer resolves
    /// flag spellings, and inventing a split here would be a silent guess.</summary>
    [Fact]
    public void Parse_LeavesSingleSpacedFlagsAsOneToken()
    {
        StatsBlockLine line = StatsBlock.Parse("MAGIC ITEM LORE ITEM NO TRADE<br>").Lines[0];

        Assert.Equal(["MAGIC ITEM LORE ITEM NO TRADE"], line.Flags);
    }

    /// <summary>
    /// Single-spaced lines whose value is a word, not a number — the hardest case in the corpus, and the one a
    /// label census caught after the unparsed count had already read zero. All four lines are verbatim from real
    /// pages, and all four were split wrongly by an earlier version that allowed three-word labels.
    /// </summary>
    [Theory]
    [InlineData("Skill: Archery Atk Delay: 0", "Skill", "Archery", "Atk Delay", "0")]
    [InlineData("Skill: 1H Slashing Atk Delay: 25", "Skill", "1H Slashing", "Atk Delay", "25")]
    [InlineData("Skill: 1H Blunt Atk Delay: 26", "Skill", "1H Blunt", "Atk Delay", "26")]
    [InlineData("DMG: 11 Fire DMG: 3", "DMG", "11", "Fire DMG", "3")]
    public void Parse_SplitsSingleSpacedLinesAtTheRightWord(
        string text, string firstLabel, string firstValue, string secondLabel, string secondValue)
    {
        StatsBlockLine line = StatsBlock.Parse(text + "<br>").Lines[0];

        Assert.Collection(line.Fields,
            f => AssertField(firstLabel, firstValue, f),
            f => AssertField(secondLabel, secondValue, f));
    }

    /// <summary>"Size: MEDIUM WT: 3.0" is the one line a word cap alone cannot fix, since "MEDIUM WT" is only two
    /// words. What gives it away is that claiming it leaves "Size" with no value, and no real page has an empty
    /// value — so the emptiness is the signal to give the word back.</summary>
    [Fact]
    public void Parse_WillNotLeaveTheFieldBeforeItEmpty()
    {
        StatsBlockLine line = StatsBlock.Parse("Size: MEDIUM WT: 3.0<br>").Lines[0];

        Assert.Collection(line.Fields,
            f => AssertField("Size", "MEDIUM", f),
            f => AssertField("WT", "3.0", f));
    }

    /// <summary>A page that simply omits its colons has nothing to parse. Reporting the whole line as one
    /// unrecognized token is right; inventing "SV FIRE: +5" from it would be a guess at a page defect.</summary>
    [Fact]
    public void Parse_DoesNotInventFieldsForALineMissingItsColons()
    {
        StatsBlockLine line = StatsBlock.Parse("SV FIRE +5 SV COLD +5 <br>").Lines[0];

        Assert.Empty(line.Fields);
        Assert.Equal(["SV FIRE +5 SV COLD +5"], line.Flags);
    }

    [Fact]
    public void Parse_RecordsTheBreakTagVerbatim()
    {
        StatsBlock block = StatsBlock.Parse("AC: 5<br>\nWT: 0.1<br/>\n");

        Assert.Equal("<br>", block.Lines[0].Break);
        Assert.Equal("<br/>", block.Lines[1].Break);
    }

    /// <summary>The guarantee the whole design rests on. Real pages are inconsistent about alignment, single vs
    /// double spaces, a stray space before the break and blank lines mid-block; all of it is meaningless to
    /// MediaWiki and all of it must survive, because reformatting it turns a one-value fix into an unreviewable
    /// whole-page diff.</summary>
    [Theory]
    [InlineData("\nMAGIC ITEM  LORE ITEM  <br>\nSlot: SECONDARY<br>\nAC: 25<br>\n")]
    [InlineData("\nAC: 15 <br>\n\nWT: 1.0  Size: SMALL<br>\n")]
    [InlineData("")]
    [InlineData("no break tag at all")]
    [InlineData("\n\n\n")]
    public void Render_ReturnsTheInputByteForByte(string raw) =>
        Assert.Equal(raw, StatsBlock.Parse(raw).Render());

    [Fact]
    public void ReplaceLine_ChangesOneLineAndNothingElse()
    {
        StatsBlock block = StatsBlock.Parse("\nAC: 25<br>\nWT: 7.3  Size: MEDIUM<br>\n");

        StatsBlock edited = block.ReplaceLine(1, "AC: 43<br>\n");

        Assert.Equal("\nAC: 43<br>\nWT: 7.3  Size: MEDIUM<br>\n", edited.Render());
        Assert.Equal("\nAC: 25<br>\nWT: 7.3  Size: MEDIUM<br>\n", block.Render());
    }

    [Fact]
    public void Find_LocatesAFieldAcrossLines()
    {
        StatsBlock block = StatsBlock.Parse("\nSlot: EAR<br>\nWT: 0.1  Size: TINY<br>\nRace: ALL<br>\n");

        Assert.Equal("TINY", block.Find("Size")!.Value);
        Assert.Equal("ALL", block.Find("race")!.Value);
        Assert.Null(block.Find("AC"));
    }

    [Fact]
    public void Parse_NeverThrowsOnMalformedInput()
    {
        foreach (string raw in new[] { "<br", "Label:", ": orphan value", "((unbalanced", "[[unclosed|link", "::::" })
            Assert.Equal(raw, StatsBlock.Parse(raw).Render());
    }
}
