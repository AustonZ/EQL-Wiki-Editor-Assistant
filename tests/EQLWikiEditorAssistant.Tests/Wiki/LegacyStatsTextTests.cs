using EQLWikiEditorAssistant.Core.Items;
using EQLWikiEditorAssistant.Wiki.Analysis;
using EQLWikiEditorAssistant.Wiki.Wikitext;

namespace EQLWikiEditorAssistant.Tests.Wiki;

/// <summary>
/// The legacy statsblock vocabulary, recognized by name (user, 2026-10-12): Project1999-era flags, removed by the data
/// pass, and the classic item window's own lines, moved to notes. Every token here is a real one, from the census of
/// every item page on the wiki (`WikiSpike flagcensus`), so the lists are pinned against what pages actually say.
/// </summary>
public class LegacyStatsTextTests
{
    [Theory]
    [InlineData("MAGIC ITEM")]
    [InlineData("LORE ITEM")]
    [InlineData("NO DROP")]
    [InlineData("QUEST ITEM")]
    [InlineData("EXPENDABLE")]
    [InlineData("TEMPORARY")]
    [InlineData("NO TRADE")]
    [InlineData("NODROP")]
    [InlineData("NO RENT")]
    [InlineData("NORENT")]
    [InlineData("LORE EQUIPPED")]
    [InlineData("ARTIFACT")]
    // EQL's current flags in capitals: a contributor keeping the legacy look.
    [InlineData("QUEST")]
    [InlineData("PLACEABLE")]
    [InlineData("ATTUNABLE")]
    [InlineData("NO STORAGE")]
    [InlineData("FREE STORAGE")]
    [InlineData("NO PET")]
    [InlineData("BANK ONLY")]
    [InlineData("MARKETPLACE")]
    [InlineData("NO TRADE QUEST ITEM")]
    // Single-spaced, so one token to the grammar: split against the list, longest flag first.
    [InlineData("MAGIC ITEM LORE ITEM")]
    [InlineData("LORE ITEM NO DROP QUEST ITEM")]
    [InlineData("MAGIC ITEM LORE EQUIPPED TEMPORARY")]
    [InlineData("MAGIC ITEM LORE ITEM ARTIFACT NO TRADE")]
    [InlineData("NODROP NORENT")]
    public void LegacyFlagsAreRecognized(string token) => Assert.True(LegacyStatsText.IsLegacyFlags(token));

    [Theory]
    [InlineData("No Trade")]                       // a current flag: case is the whole difference
    [InlineData("Lore Equipped")]
    [InlineData("MNK BRD ROG SHM")]                // a class list that lost its `Class:` label
    [InlineData("PENDING LORE")]
    [InlineData("ATTUNEABLE")]
    [InlineData("NO TRADE LORE EQUPPED PLACEABLE QUEST")]
    [InlineData("SV FIRE +5 SV COLD +5")]
    [InlineData("MAGIC ITEMS")]                    // a near miss is not a match
    public void OtherTextIsNotALegacyFlag(string token) => Assert.False(LegacyStatsText.IsLegacyFlags(token));

    [Theory]
    [InlineData("This is a meal!")]
    [InlineData("This is a meal.")]
    [InlineData("This is a Meal")]
    [InlineData("This is a snack!")]
    [InlineData("This is a hearty meal!")]
    [InlineData("This is a banquet size meal.")]
    [InlineData("This is a banquet sized meal!")]
    [InlineData("This is a banquet-sized meal!")]
    [InlineData("This is a Banquet")]
    [InlineData("This is a feast!")]
    [InlineData("This meal is a feast!")]
    [InlineData("This mealis a feast!")]
    [InlineData("This is a enduring meal!")]
    [InlineData("This is an enduring meal")]
    [InlineData("This is a miraculous meal!")]
    [InlineData("This is a whistle wetter.")]
    [InlineData("This is a drink")]
    [InlineData("This is a refreshing drink!")]
    [InlineData("This is a lasting drink")]
    [InlineData("This is a flowing drink!")]
    [InlineData("This is a enduring drink!")]
    [InlineData("This is a miraculous drink.")]
    [InlineData("THIS IS A SNACK")]
    [InlineData("The Note is rolled up.")]
    [InlineData("This note is rolled up.")]
    [InlineData("The Book is closed.")]
    [InlineData("The letter is sealed.")]
    [InlineData("The compass points East.")]
    [InlineData("This item can be used in tradeskills.")]
    [InlineData("This item can be used in tradeskills")]
    [InlineData("(This item acts as a light source.)")]
    public void TheClassicWindowsOwnTextIsRecognized(string token) => Assert.True(LegacyStatsText.IsWindowText(token));

    [Theory]
    [InlineData("Required level of 46.")]         // may still be data: for a human (user, 2026-10-12)
    [InlineData("Recommended level of 10.")]
    [InlineData("These are not stackable")]
    [InlineData("This is a meal and a half!")]    // only the whole line, not a prefix
    [InlineData("This clicky requires that you have Cat's Eye Agate.")]
    [InlineData("MAGIC ITEM")]
    public void OtherTextIsNotWindowText(string token) => Assert.False(LegacyStatsText.IsWindowText(token));

    // ---- placing a line in notes ----

    private static string Notes(string notes) =>
        "<onlyinclude>{{Itempage\n|itemname = Thing\n|statsblock = \nClass: ALL<br>\n|notes = " + notes +
        "|merchant_value = 1p\n}}</onlyinclude>";

    [Theory]
    // After the lore and before a human's text, each on a line of its own.
    [InlineData("{{Item Lore|Old.}}<br>\nA note.\n", "{{Item Lore|Old.}}<br>\nThis is a meal!<br>\nA note.\n")]
    // Lore and nothing else, with or without a break: the line ends before the parameter's own line end, so the
    // next parameter stays on its own line.
    [InlineData("{{Item Lore|Old.}}\n", "{{Item Lore|Old.}}<br>\nThis is a meal!\n")]
    [InlineData("{{Item Lore|Old.}}<br>\n", "{{Item Lore|Old.}}<br>\nThis is a meal!\n")]
    // No lore: the line opens the notes.
    [InlineData("A note.\n", "This is a meal!<br>\nA note.\n")]
    // Empty notes: the line is the notes.
    [InlineData("\n", "This is a meal!\n")]
    // Already there, in any case: not added twice.
    [InlineData("this is a meal!\n", "this is a meal!\n")]
    public void ALineGoesIntoTheNotesAfterTheLore(string before, string after)
    {
        ItemPageDocument document = ItemPageDocument.Parse(Notes(before))!.WithNotesLine("This is a meal!");

        Assert.Contains("|notes = " + after + "|merchant_value = 1p\n", document.Wikitext);
    }

    // ---- the data pass ----

    private static ParsedItem Captured(params string[] flags) =>
        new("Thing", 0, false, flags, ["ALL"], [], [], [], [], [], null, null, []);

    private static ProposedEdit Edit(string statsblock, string notes, params string[] flags)
    {
        ItemPageDocument page = ItemPageDocument.Parse(
            "<onlyinclude>{{Itempage\n|itemname = Thing\n|statsblock = \n" + statsblock + "|notes = " + notes + "}}</onlyinclude>")!;
        return ItemPageEditor.BuildEdit(page, ItemPageAnalyzer.Analyze(Captured(flags), page, "Thing"));
    }

    /// <summary>The old blueprint's `EXPENDABLE  Charges: 10`, on 79 pages: the flag goes and the field stays where
    /// it is. Reading only the first flags line used to leave it there whatever the rewrite did.</summary>
    [Fact]
    public void ALegacyFlagOpeningAFieldLineGoesAndTheFieldStays()
    {
        ProposedEdit edit = Edit("MAGIC ITEM  LORE ITEM<br>\nEXPENDABLE  Charges: 10<br>\nClass: ALL<br>\n", "\n", "No Trade");

        Assert.Contains("No Trade<br>\nCharges: 10<br>\nClass: ALL<br>", edit.NewWikitext);
        Assert.DoesNotContain("EXPENDABLE", edit.NewWikitext);
        Assert.DoesNotContain("MAGIC ITEM", edit.NewWikitext);
    }

    /// <summary>A food line on a line of its own, below the flags: it moves to notes, the flags line is rewritten,
    /// and its own line goes.</summary>
    [Fact]
    public void AFoodLineOnItsOwnLineMovesAndItsLineGoes()
    {
        ProposedEdit edit = Edit(
            "LORE ITEM<br>\nThis is a hearty meal!<br>\nClass: ALL<br>\n", "{{Item Lore|Rich.}}\n", "Quest");

        Assert.Contains("|statsblock = \nQuest<br>\nClass: ALL<br>", edit.NewWikitext);
        Assert.Contains("|notes = {{Item Lore|Rich.}}<br>\nThis is a hearty meal!\n}}", edit.NewWikitext);
    }

    /// <summary>Two lines of window text keep the page's order in notes, both after the lore.</summary>
    [Fact]
    public void SeveralLinesKeepThePagesOrder()
    {
        ProposedEdit edit = Edit(
            "The Book is closed.  This item can be used in tradeskills.<br>\nClass: ALL<br>\n", "Read it.\n");

        Assert.Contains("|notes = The Book is closed.<br>\nThis item can be used in tradeskills.<br>\nRead it.\n", edit.NewWikitext);
        Assert.DoesNotContain("closed.<br>\nClass", edit.NewWikitext);
        Assert.Equal("moved flags-line text to notes", edit.Summary[(edit.Summary.LastIndexOf(';') + 2)..]);
    }

    /// <summary>Unrecognized text anywhere among the flag lines holds the whole rewrite, recognized text included:
    /// moving the food line while leaving the line it shares would be half an edit.</summary>
    [Fact]
    public void UnrecognizedTextHoldsTheWholeRewrite()
    {
        ProposedEdit edit = Edit("MNK BRD ROG SHM<br>\nThis is a meal!<br>\nClass: ALL<br>\n", "\n", "Quest");

        Assert.Contains("MNK BRD ROG SHM<br>\nThis is a meal!<br>", edit.NewWikitext);
        Assert.DoesNotContain("|notes = This", edit.NewWikitext);
    }

    /// <summary>Attunable beats a captured No Trade (user, 2026-09-24), and legacy flags beside it still go.</summary>
    [Fact]
    public void AttunableIsKeptWhileTheLegacyFlagsBesideItGo()
    {
        ProposedEdit edit = Edit("MAGIC ITEM  Attunable<br>\nClass: ALL<br>\n", "\n", "No Trade");

        Assert.Contains("|statsblock = \nAttunable<br>\n", edit.NewWikitext);
    }
}
