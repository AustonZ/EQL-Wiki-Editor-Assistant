using EQLWikiAssistant.Wiki.Wikitext;

namespace EQLWikiAssistant.Tests.Wiki;

/// <summary>Reading and surgically editing a real item page. Every fixture used here is a page from eqlwiki.com,
/// not a hand-written approximation — see <c>Fixtures/README.md</c>.</summary>
public class ItemPageDocumentTests
{
    [Fact]
    public void Parse_ReadsTheV1Fields()
    {
        ItemPageDocument document = ItemPageDocument.Parse(WikiFixtures.Load("Earring of Bashing"))!;

        Assert.Equal("Earring of Bashing", document.ItemName);
        Assert.Equal("752", document.IconId);
        Assert.Equal("For those that like to bash", document.Lore);
        Assert.False(document.HasLoreMissingPlaceholder);
        Assert.Empty(document.Warnings);
    }

    [Fact]
    public void Parse_ReadsFocusEffectAndMerchantValueWhenPresent()
    {
        Assert.Equal("Enhancement Haste II", ItemPageDocument.Parse(WikiFixtures.Load("Golden Efreeti Boots"))!.FocusEffect);
        Assert.Equal("1s", ItemPageDocument.Parse(WikiFixtures.Load("Water Flask"))!.MerchantValue);
        Assert.Null(ItemPageDocument.Parse(WikiFixtures.Load("Cloak of Scales"))!.FocusEffect);
    }

    [Fact]
    public void Parse_ReturnsNullForAPageThatIsNotAnItemPage()
    {
        Assert.Null(ItemPageDocument.Parse("{{Namedmobpage|name=Lord Nagafen}}\n[[Category:Nagafen's Lair]]"));
        Assert.Null(ItemPageDocument.Parse("#REDIRECT [[Water Flask]]"));
        Assert.Null(ItemPageDocument.Parse(""));
    }

    /// <summary>Real page, real defect: an empty |notes= near the top and the real one further down. MediaWiki
    /// renders the last, so the tool must read the last — and must still say the page needs cleaning up.</summary>
    [Fact]
    public void Parse_WarnsAboutDuplicateParametersAndReadsTheOneMediaWikiUses()
    {
        ItemPageDocument document = ItemPageDocument.Parse(WikiFixtures.Load("Shimmering Ruby Stiletto"))!;

        Assert.Equal("Backstab DMG: 10", document.Notes);
        Assert.Contains(document.Warnings, w => w.Contains("|notes=") && w.Contains("more than once"));
    }

    [Fact]
    public void ReadStatsBlock_ParsesARealBlock()
    {
        StatsBlock block = ItemPageDocument.Parse(WikiFixtures.Load("Earring of Bashing"))!.ReadStatsBlock()!;

        Assert.Equal("EAR", block.Find("Slot")!.Value);
        Assert.Equal("5", block.Find("AC")!.Value);
        Assert.Equal("+8", block.Find("STR")!.Value);
        Assert.Equal("WAR SHD SHM BST BER", block.Find("Class")!.Value);
    }

    /// <summary>The surgical-edit guarantee: changing one parameter changes exactly that parameter's value, and the
    /// rest of the page — including 21 KB of unrelated soldby tables — comes back identical.</summary>
    [Fact]
    public void WithParameter_TouchesOnlyTheTargetedValue()
    {
        string original = WikiFixtures.Load("Water Flask");
        ItemPageDocument document = ItemPageDocument.Parse(original)!;

        string edited = document.WithParameter("merchant_value", "2s").Wikitext;

        Assert.Equal("2s", ItemPageDocument.Parse(edited)!.MerchantValue);
        Assert.Equal(original.Replace("|merchant_value = 1s", "|merchant_value = 2s"), edited);
    }

    /// <summary>Alignment padding around a value is cosmetic to MediaWiki and load-bearing to a human reading the
    /// diff, so a replacement keeps it.</summary>
    [Fact]
    public void WithParameter_PreservesTheWhitespaceAroundTheOldValue()
    {
        ItemPageDocument document = ItemPageDocument.Parse("{{Itempage\n|itemname    = Old\n|statsblock  = x\n}}")!;

        Assert.Equal("{{Itempage\n|itemname    = New\n|statsblock  = x\n}}", document.WithParameter("itemname", "New").Wikitext);
    }

    /// <summary>
    /// A present-but-empty parameter is a different thing from an absent one: the page author wrote it and left it
    /// blank, and 343 of 662 sampled pages have at least one.
    ///
    /// Its raw value is *entirely* padding (<c>" \n"</c>), so "the whitespace before the value" and "the whitespace
    /// after it" describe the same characters — and emitting both duplicated them, silently adding a blank line to
    /// every such page. None of the original ten fixtures had an empty parameter, so only the live sweep caught it;
    /// this page was added as a fixture so the offline suite catches it next time.
    /// </summary>
    [Fact]
    public void WithParameter_DoesNotDuplicateThePaddingOfAnEmptyValue()
    {
        string original = WikiFixtures.Load("10 Dose Potion of Antiweight");
        ItemPageDocument document = ItemPageDocument.Parse(original)!;
        Assert.Equal("", document.Notes);

        Assert.Equal(original, document.WithParameter("notes", "").Wikitext);
    }

    /// <summary>...and writing a real value into that empty parameter puts it on the same line as the "=", where a
    /// human would put it.</summary>
    [Fact]
    public void WithParameter_FillsAnEmptyValueInPlace()
    {
        ItemPageDocument document = ItemPageDocument.Parse("{{Itempage\n|notes       = \n|itemname    = X\n}}")!;

        Assert.Equal(
            "{{Itempage\n|notes       = Drink item\n|itemname    = X\n}}",
            document.WithParameter("notes", "Drink item").Wikitext);
    }

    [Fact]
    public void WithParameter_RefusesToInventAMissingParameter()
    {
        ItemPageDocument document = ItemPageDocument.Parse(WikiFixtures.Load("Cloak of Scales"))!;

        Assert.Throws<InvalidOperationException>(() => document.WithParameter("merchant_value", "1p"));
    }

    [Fact]
    public void WithStatsBlock_ReplacesTheBlockAndLeavesTheRestAlone()
    {
        string original = WikiFixtures.Load("Earring of Bashing");
        ItemPageDocument document = ItemPageDocument.Parse(original)!;
        StatsBlock block = document.ReadStatsBlock()!;

        int acLine = block.Lines.ToList().FindIndex(l => l.Fields.Any(f => f.Label == "AC"));
        ItemPageDocument edited = document.WithStatsBlock(block.ReplaceLine(acLine, "AC: 6<br>\n"));

        Assert.Equal("6", edited.ReadStatsBlock()!.Find("AC")!.Value);
        Assert.Equal(original.Replace("AC: 5<br>", "AC: 6<br>"), edited.Wikitext);
    }

    [Fact]
    public void WithoutLoreMissingPlaceholder_RemovesThePlaceholderAndItsBreak()
    {
        ItemPageDocument document = ItemPageDocument.Parse(WikiFixtures.Load("Bladestopper"))!;
        Assert.True(document.HasLoreMissingPlaceholder);

        ItemPageDocument cleaned = document.WithoutLoreMissingPlaceholder();

        Assert.False(cleaned.HasLoreMissingPlaceholder);
        // The human's own commentary, which shares the parameter, has to survive intact.
        Assert.Contains("This charged source of Rune is <s>valuable for increasing hate", cleaned.Notes);
        Assert.DoesNotContain("Item Lore Missing", cleaned.Wikitext);
        // And nothing outside notes moved.
        Assert.Contains("[[Category: Instant Click Equipment]]", cleaned.Wikitext);
        Assert.Equal(document.ItemName, cleaned.ItemName);
    }

    [Fact]
    public void WithoutLoreMissingPlaceholder_IsANoOpWhenThereIsNoPlaceholder()
    {
        string original = WikiFixtures.Load("Earring of Bashing");

        Assert.Equal(original, ItemPageDocument.Parse(original)!.WithoutLoreMissingPlaceholder().Wikitext);
    }

    /// <summary>A placeholder is the absence of lore, not lore. Reading it as text would publish the literal
    /// string "Item Lore Missing" as the item's lore.</summary>
    [Fact]
    public void Lore_IsNullWhenOnlyThePlaceholderIsPresent()
    {
        ItemPageDocument document = ItemPageDocument.Parse(WikiFixtures.Load("Blackened Alloy Longsword"))!;

        Assert.True(document.HasLoreMissingPlaceholder);
        Assert.Null(document.Lore);
    }

    [Fact]
    public void Lore_TrimsTheSpacingRealPagesPutAroundTheValue() =>
        Assert.Equal("Fishbone Earring.", ItemPageDocument.Parse(WikiFixtures.Load("Fishbone Earring"))!.Lore);
}
