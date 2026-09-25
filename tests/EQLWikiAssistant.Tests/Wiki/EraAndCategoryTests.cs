using EQLWikiAssistant.Wiki.Mapping;
using EQLWikiAssistant.Wiki.Wikitext;

namespace EQLWikiAssistant.Tests.Wiki;

/// <summary>
/// The era banner is how the wiki records that an item has actually been seen in game (user, 2026-09-25), so a
/// capture is itself the confirmation and the tool sets it outright — including over a legacy banner inherited from
/// the Project1999 import.
/// </summary>
public class EraTemplateTests
{
    [Fact]
    public void APageAlreadyOnTheCurrentEraIsUntouched()
    {
        string original = WikiFixtures.Load("Earring of Bashing");

        Assert.Equal(original, ItemPageDocument.Parse(original)!.WithEraTemplate("Classic").Wikitext);
    }

    /// <summary>Bladestopper has no banner at all — 232 of 744 sampled pages are like it.</summary>
    [Fact]
    public void AMissingBannerIsAdded()
    {
        ItemPageDocument page = ItemPageDocument.Parse(WikiFixtures.Load("Bladestopper"))!;
        Assert.Null(page.CurrentEra);

        ItemPageDocument fixedPage = page.WithEraTemplate("Classic");

        Assert.StartsWith("{{Classic Era}}\n", fixedPage.Wikitext);
        Assert.True(fixedPage.HasEraTemplate("Classic"));
        // The rest of the page survives, including the human's own commentary and the trailing categories.
        Assert.Contains("This charged source of Rune", fixedPage.Wikitext);
        Assert.Contains("[[Category: Instant Click Equipment]]", fixedPage.Wikitext);
        Assert.Equal("Bladestopper", fixedPage.ItemName);
    }

    /// <summary>176 sampled pages say `Velious Era`. If the item was just seen in game, it is Classic whatever the
    /// page inherited.</summary>
    [Theory]
    [InlineData("{{Velious Era}}")]
    [InlineData("{{Kunark Era}}")]
    [InlineData("{{Chardok Revamp Era}}")]
    public void ALegacyBannerIsReplaced(string banner)
    {
        ItemPageDocument page = ItemPageDocument.Parse(
            $"{banner}\n<onlyinclude>{{{{Itempage\n|itemname = X\n|statsblock = \nClass: ALL<br>\n}}}}</onlyinclude>")!;

        ItemPageDocument fixedPage = page.WithEraTemplate("Classic");

        Assert.True(fixedPage.HasEraTemplate("Classic"));
        Assert.DoesNotContain(banner, fixedPage.Wikitext);
        Assert.Equal("X", fixedPage.ItemName);
    }

    /// <summary>Two banners is itself a defect; the result carries exactly one.</summary>
    [Fact]
    public void DuplicateBannersCollapseToOne()
    {
        ItemPageDocument page = ItemPageDocument.Parse(
            "{{Velious Era}}\n{{Classic Era}}\n<onlyinclude>{{Itempage\n|itemname = X\n|statsblock = \n}}</onlyinclude>")!;

        ItemPageDocument fixedPage = page.WithEraTemplate("Classic");

        Assert.True(fixedPage.HasEraTemplate("Classic"));
        Assert.Equal(1, fixedPage.Wikitext.Split("Era}}").Length - 1);
    }

    /// <summary>Replacing a banner must not leave the blank line it sat on behind.</summary>
    [Fact]
    public void ReplacingABannerLeavesNoBlankLine()
    {
        ItemPageDocument page = ItemPageDocument.Parse(
            "{{Velious Era}}\n<onlyinclude>{{Itempage\n|itemname = X\n|statsblock = \n}}</onlyinclude>")!;

        Assert.Equal(
            "{{Classic Era}}\n<onlyinclude>{{Itempage\n|itemname = X\n|statsblock = \n}}</onlyinclude>",
            page.WithEraTemplate("Classic").Wikitext);
    }
}

/// <summary>
/// Both tables come from the Item Page Blueprint rather than from sampling pages — a census would have recorded
/// whatever existing pages happen to do, and several are incomplete (`Golden Efreeti Boots` says `Class: ALL` but
/// lists only 14 of the 16 class categories, predating Beastlord and Berserker).
/// </summary>
public class CategoryRulesTests
{
    [Fact]
    public void ClassCodesBecomeEquipmentCategories()
    {
        IReadOnlyList<string> categories = CategoryRules.Derive(
            ["WAR", "SHD", "SHM", "BST", "BER"], [], out IReadOnlyList<string> unrecognized);

        Assert.Equal(
            ["Beastlord Equipment", "Berserker Equipment", "Shadow Knight Equipment", "Shaman Equipment", "Warrior Equipment"],
            categories);
        Assert.Empty(unrecognized);
    }

    [Fact]
    public void AllExpandsToEveryClass()
    {
        IReadOnlyList<string> categories = CategoryRules.Derive([CategoryRules.AllClasses], [], out _);

        Assert.Equal(16, categories.Count);
        Assert.Contains("Bard Equipment", categories);
        Assert.Contains("Wizard Equipment", categories);
    }

    /// <summary>The documented quirk: the slot is `FINGER`, the category is `Fingers`.</summary>
    [Theory]
    [InlineData("FINGER", "Fingers")]
    [InlineData("PRIMARY", "Primary")]
    [InlineData("EAR", "Ear")]
    [InlineData("SHOULDERS", "Shoulders")]
    public void SlotsBecomeTheirOwnCategories(string wikiSlot, string expected) =>
        Assert.Equal([expected], CategoryRules.Derive([], [wikiSlot], out _));

    /// <summary>`ANY` is a real slot in the blueprint with no category of its own, so it contributes none — and is
    /// not reported as unrecognized either, which would be a false alarm.</summary>
    [Fact]
    public void TheAnySlotContributesNoCategoryAndIsNotAnError()
    {
        Assert.Empty(CategoryRules.Derive([], ["ANY"], out IReadOnlyList<string> unrecognized));
        Assert.Empty(unrecognized);
    }

    /// <summary>A class or slot with no category surfaces rather than silently shortening the list — the same rule
    /// as an unmapped stat, and for the same reason: a new one is how a game patch announces itself.</summary>
    [Fact]
    public void AnUnknownClassOrSlotIsReported()
    {
        CategoryRules.Derive(["WAR", "XYZ"], ["NOSE"], out IReadOnlyList<string> unrecognized);

        Assert.Equal(["XYZ", "NOSE"], unrecognized);
    }

    /// <summary>The tool may only add categories it derives. Everything else a page carries — zone names, quest and
    /// focus groupings, fashion entries — belongs to somebody else and is left alone.</summary>
    [Theory]
    [InlineData("Warrior Equipment", true)]
    [InlineData("Fingers", true)]
    [InlineData("Ear", true)]
    [InlineData("Nagafen's Lair", false)]
    [InlineData("Quest Items", false)]
    [InlineData("Focus Items", false)]
    [InlineData("Instant Click Equipment", false)]
    [InlineData("Fashion: Plate", false)]
    [InlineData("Inventory Items", false)]
    public void OnlyDerivedCategoriesAreOwnedByTheTool(string category, bool expected) =>
        Assert.Equal(expected, CategoryRules.IsDerivable(category));

    /// <summary>Earring of Bashing's real categories, as a sanity check that the derivation matches a page a human
    /// maintained: Class WAR SHD SHM BST BER plus Slot EAR.</summary>
    [Fact]
    public void DerivationMatchesARealPage()
    {
        IReadOnlyList<string> categories = CategoryRules.Derive(
            ["WAR", "SHD", "SHM", "BST", "BER"], ["EAR"], out _);

        string pageText = WikiFixtures.Load("Earring of Bashing");
        foreach (string category in categories)
            Assert.Contains($"[[Category:{category}]]", pageText);
    }
}
