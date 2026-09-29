using EQLWikiAssistant.Core.Items;
using EQLWikiAssistant.Core.Locate;
using EQLWikiAssistant.Core.Ocr;
using EQLWikiAssistant.Wiki.Analysis;
using EQLWikiAssistant.Wiki.Mapping;
using EQLWikiAssistant.Wiki.Wikitext;

namespace EQLWikiAssistant.Tests.Wiki;

/// <summary>
/// Pet illusion: a property only three items in the game have, which the game encodes as a bare <c>Effect:</c> line
/// — the user's read is that it was a hack around a legacy system. The tool normalizes that spelling, records the
/// appearance, and puts the page in the category that exists to collect those three items.
/// </summary>
public class PetIllusionTests
{
    private static OcrLine L(string text, int x, int y) => new(text, new Rect(x, y, 10, 10), []);

    private static ParsedItem Parse(string effectLine) =>
        ItemParser.Parse(
        [
            L("Guise of the Deceived", 96, 0),
            L("Description", 170, 18),
            L("Guise of the Deceived", 62, 49),
            L("No Trade", 61, 64),
            L("Class: ALL", 61, 78),
            L("Race: ALL", 60, 95),
            L("Size:", 10, 191), L("SMALL", 82, 191),
            L(effectLine, 10, 240),
        ]);

    // --- reading it -------------------------------------------------------------------------------------

    /// <summary>The casting time is not part of what the wiki records (user, 2026-09-28): the value is the
    /// appearance alone.</summary>
    [Theory]
    [InlineData("Effect: Pet Illusion: Dark Elf (Casting Time: 6.0)", "Dark Elf")]
    [InlineData("Effect: Pet Illusion: Murderbee (Casting Time: 5.0)", "Murderbee")]
    [InlineData("Effect: Pet Illusion: Dark Elf", "Dark Elf")]
    [InlineData("Effect: Pet Illusion: Storm Giant (Casting Time 6.0)", "Storm Giant")]
    public void APetIllusionBecomesItsOwnFieldHoldingTheAppearance(string line, string appearance)
    {
        ParsedItem item = Parse(line);

        Assert.Equal(appearance, item.Stats.Single(s => s.Key == ItemParser.PetIllusionLabel).Value);
        Assert.DoesNotContain(item.Stats, s => s.Key == "Effect");
    }

    /// <summary>
    /// **A bare `Effect:` line that is not a pet illusion is left exactly as it is.** Only three items in the game
    /// carry this property today, and inventing a reading for the next unfamiliar shape is how a tool writes
    /// something nobody sanctioned — it surfaces as an unmapped field instead, for the user to decide.
    /// </summary>
    [Fact]
    public void AnUnfamiliarBareEffectLineIsLeftAloneAndReported()
    {
        ParsedItem item = Parse("Effect: Something Nobody Has Seen (Casting Time: 2.0)");

        Assert.Equal(
            "Something Nobody Has Seen (Casting Time: 2.0)",
            item.Stats.Single(s => s.Key == "Effect").Value);
        Assert.Null(WikiMapping.Default.FindStat("Effect"));
    }

    // --- writing it -------------------------------------------------------------------------------------

    private static ItemPageDocument Page(string statsblock, string categories = "") =>
        ItemPageDocument.Parse(
            "{{Classic Era}}\n<onlyinclude>{{Itempage\n|itemname = Guise of the Deceived\n|lucy_img_ID = 1\n" +
            "|statsblock = \n" + statsblock + "\n}}</onlyinclude>" + categories)!;

    [Fact]
    public void ThePetIllusionIsWrittenToTheStatsblock()
    {
        ParsedItem item = Parse("Effect: Pet Illusion: Dark Elf (Casting Time: 6.0)");
        ItemPageDocument page = Page("Class: ALL<br>");

        ProposedEdit edit = ItemPageEditor.BuildEdit(page, ItemPageAnalyzer.Analyze(item, page, "Guise of the Deceived"));

        Assert.Contains("Pet Illusion: Dark Elf<br>", edit.NewWikitext);
    }

    /// <summary>The category is how anyone finds the three items in the game that have this property at all.</summary>
    [Fact]
    public void ThePageJoinsThePetIllusionCategory()
    {
        ParsedItem item = Parse("Effect: Pet Illusion: Dark Elf (Casting Time: 6.0)");
        ItemPageDocument page = Page("Class: ALL<br>", "\n\n[[Category:Neck]]");

        ProposedEdit edit = ItemPageEditor.BuildEdit(page, ItemPageAnalyzer.Analyze(item, page, "Guise of the Deceived"));

        Assert.Contains("[[Category:Pet Illusion Items]]", edit.NewWikitext);
        // Added, never replacing — the page's own categories are not the tool's to judge.
        Assert.Contains("[[Category:Neck]]", edit.NewWikitext);
    }

    [Fact]
    public void APageAlreadyInTheCategoryIsNotChanged()
    {
        ParsedItem item = Parse("Effect: Pet Illusion: Dark Elf (Casting Time: 6.0)");
        ItemPageDocument page = Page("Pet Illusion: Dark Elf<br>\nClass: ALL<br>", "\n\n[[Category:Pet Illusion Items]]");

        ProposedEdit edit = ItemPageEditor.BuildEdit(page, ItemPageAnalyzer.Analyze(item, page, "Guise of the Deceived"));

        Assert.Equal(1, edit.NewWikitext.Split("[[Category:Pet Illusion Items]]").Length - 1);
    }

    /// <summary>An item without the property gets nothing — a category rule that fires on everything is worse than
    /// none.</summary>
    [Fact]
    public void AnItemWithoutThePropertyGetsNoCategory()
    {
        ParsedItem item = Parse("AC: 10");
        ItemPageDocument page = Page("Class: ALL<br>");

        ProposedEdit edit = ItemPageEditor.BuildEdit(page, ItemPageAnalyzer.Analyze(item, page, "Guise of the Deceived"));

        Assert.DoesNotContain("Pet Illusion", edit.NewWikitext);
    }

    /// <summary>
    /// **Class and slot categories are deliberately not proposed yet**, though `CategoryRules` can derive them.
    /// Adding the class set would put sixteen new lines on any page missing them — a large unasked-for edit on a
    /// page the tool is otherwise only correcting one value on. A property category is different in kind: nothing
    /// can carry it without the tool having read the property first.
    /// </summary>
    [Fact]
    public void ClassAndSlotCategoriesAreNotProposed()
    {
        ParsedItem item = Parse("AC: 10");
        ItemPageDocument page = Page("Class: WAR<br>\nSlot: NECK<br>");

        ProposedEdit edit = ItemPageEditor.BuildEdit(page, ItemPageAnalyzer.Analyze(item, page, "Guise of the Deceived"));

        Assert.DoesNotContain("[[Category:Warrior Equipment]]", edit.NewWikitext);
        Assert.DoesNotContain("[[Category:Neck]]", edit.NewWikitext);
    }
}
