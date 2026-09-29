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

    /// <summary>Without a blueprint order there is still no basis for a position, so it still refuses. Supplying the
    /// order is what supplies the judgement.</summary>
    [Fact]
    public void WithParameter_RefusesToInventAMissingParameterWithNoOrderToPlaceItBy()
    {
        ItemPageDocument document = ItemPageDocument.Parse(WikiFixtures.Load("Cloak of Scales"))!;

        Assert.Throws<InvalidOperationException>(() => document.WithParameter("merchant_value", "1p"));
    }

    private static readonly string[] BlueprintOrder =
        ["itemname", "lucy_img_ID", "statsblock", "focus_effect", "merchant_value", "notes", "dropsfrom"];

    [Fact]
    public void WithParameter_WritesAMissingParameterInBlueprintOrder()
    {
        ItemPageDocument document = ItemPageDocument.Parse(
            "{{Itempage\n|itemname    = A\n|statsblock  = x\n|dropsfrom = y\n}}")!;

        Assert.Equal(
            "{{Itempage\n|itemname    = A\n|statsblock  = x\n|merchant_value = 1p\n|dropsfrom = y\n}}",
            document.WithParameter("merchant_value", "1p", BlueprintOrder).Wikitext);
    }

    /// <summary>The separator is copied from the anchor rather than chosen, so a call written on one line stays on
    /// one line — the tool must not impose a layout on a page that did not have it.</summary>
    [Fact]
    public void WithParameter_KeepsASingleLineCallOnOneLine()
    {
        ItemPageDocument document = ItemPageDocument.Parse("{{Itempage|itemname=A|statsblock=x|dropsfrom=y}}")!;

        Assert.Equal(
            "{{Itempage|itemname=A|statsblock=x|merchant_value = 1p|dropsfrom=y}}",
            document.WithParameter("merchant_value", "1p", BlueprintOrder).Wikitext);
    }

    /// <summary>Nothing the blueprint places earlier is present, so the first parameter it places later is the
    /// anchor and the new one goes in front of it.</summary>
    [Fact]
    public void WithParameter_GoesBeforeALaterParameterWhenNothingEarlierIsPresent()
    {
        ItemPageDocument document = ItemPageDocument.Parse("{{Itempage\n|notes = n\n|dropsfrom = y\n}}")!;

        Assert.Equal(
            "{{Itempage\n|itemname = A\n|notes = n\n|dropsfrom = y\n}}",
            document.WithParameter("itemname", "A", BlueprintOrder).Wikitext);
    }

    /// <summary>A name the blueprint does not know has no order to respect, so it goes at the end — the one position
    /// that cannot be wrong about an order it has no place in.</summary>
    [Fact]
    public void WithParameter_PutsAnUnknownParameterAtTheEnd()
    {
        ItemPageDocument document = ItemPageDocument.Parse("{{Itempage\n|itemname = A\n|statsblock = x\n}}")!;

        Assert.Equal(
            "{{Itempage\n|itemname = A\n|statsblock = x\n|invented = z\n}}",
            document.WithParameter("invented", "z", BlueprintOrder).Wikitext);
    }

    /// <summary>The page outside the template call is untouched by an insertion, same as by a replacement.</summary>
    [Fact]
    public void WithParameter_InsertingLeavesTheRestOfThePageAlone()
    {
        string original = WikiFixtures.Load("Cloak of Scales");
        ItemPageDocument document = ItemPageDocument.Parse(original)!;

        string edited = document.WithParameter("merchant_value", "1p", BlueprintOrder).Wikitext;

        Assert.Equal(original.Length + "|merchant_value = 1p\n".Length, edited.Length);
        Assert.Equal(original, edited.Replace("|merchant_value = 1p\n", ""));
    }

    /// <summary>A page with no <c>notes</c> at all can still receive lore: it is the same missing-parameter case.</summary>
    [Fact]
    public void WithLore_CreatesTheNotesParameterWhenThePageHasNone()
    {
        ItemPageDocument document = ItemPageDocument.Parse(
            "{{Itempage\n|itemname = A\n|statsblock = x\n|dropsfrom = y\n}}")!;

        string edited = document.WithLore("A tale of old.", BlueprintOrder).Wikitext;

        Assert.Contains("|notes = {{Item Lore|A tale of old.}}", edited);
        Assert.True(
            edited.IndexOf("|statsblock", StringComparison.Ordinal) <
            edited.IndexOf("|notes", StringComparison.Ordinal));
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

    /// <summary>
    /// The real page that exposed this (user, 2026-09-29). `Dragon Bone Bracelet` is a legacy import with no
    /// `merchant_value` at all — the common case, since legacy EverQuest gave a player no easy way to learn a value.
    /// Kept verbatim because the whole bug was about where the parameter lands on a page whose own order is not the
    /// blueprint's: this one opens with `|notes=`, which the blueprint puts sixth.
    /// </summary>
    [Fact]
    public void WithParameter_WritesMerchantValueIntoARealLegacyPage()
    {
        const string page =
            "{{Classic Era}}\n<onlyinclude>{{Itempage\n|notes       = \n" +
            "|itemname    = Dragon Bone Bracelet\n|lucy_img_ID = 505\n|statsblock  = \n" +
            "Attunable<br>\nSlot: WRIST<br>\nAC: 4<br>\nSTR: +7  AGI: +7<br>\n" +
            "WT: 0.1  Size: SMALL<br>\nClass: WAR PAL RNG SHD MNK BRD ROG BST BER<br>\nRace: ALL<br>\n" +
            "|dropsfrom = \n\n[[Dreadlands]]\n\n* [[Gorenaire]]\n\n}}</onlyinclude>\n\n[[Category:Wrist]]";

        string edited = ItemPageDocument.Parse(page)!
            .WithParameter("merchant_value", "1p 2g 3s", BlueprintOrder).Wikitext;

        Assert.Contains("Race: ALL<br>\n|merchant_value = 1p 2g 3s\n|dropsfrom = ", edited);

        // Everything else survives byte for byte, including the aligned padding this edit deliberately does not
        // match and the dropsfrom list and category outside the call.
        Assert.Equal(page, edited.Replace("|merchant_value = 1p 2g 3s\n", ""));
    }
}
