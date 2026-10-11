using EQLWikiEditorAssistant.Wiki.Formatting;
using EQLWikiEditorAssistant.Wiki.Mapping;

namespace EQLWikiEditorAssistant.Tests.Wiki;

/// <summary>
/// The formatter sorts the categories that end a page into the Item Page Blueprint's order (user, 2026-10-11): a blank
/// line between each blueprint group the page uses, and anything the blueprint does not list last, after a blank line,
/// in the page's own order.
/// </summary>
public class CategoryOrderTests
{
    private static string Page(string categories, string inside = "") =>
        "{{Classic Era}}\n<onlyinclude>{{Itempage\n|itemname = Thing\n|lucy_img_ID = 1\n|statsblock = \nAC: 1<br>\n}}" +
        inside + "</onlyinclude>\n\n" + categories;

    private static PrettifyResult Format(string categories, string inside = "")
    {
        PrettifyResult result = ItemPagePrettifier.Format(Page(categories, inside));
        Assert.True(result.IsSafe, string.Join("; ", result.Refusals));
        return result;
    }

    [Fact]
    public void CategoriesFollowTheBlueprintsGroupsWithTheRestLast()
    {
        PrettifyResult result = Format(
            "[[Category:Vendor Sold]]\n[[Category:Quest Items]]\n[[Category:Chest]]\n[[Category:Plane of Sky]]\n" +
            "[[Category:Warrior Equipment]]\n[[Category:Cleric Equipment]]\n");

        Assert.EndsWith(
            "</onlyinclude>\n\n" +
            "[[Category:Cleric Equipment]]\n[[Category:Warrior Equipment]]\n\n" +
            "[[Category:Chest]]\n\n" +
            "[[Category:Quest Items]]\n[[Category:Vendor Sold]]\n\n" +
            "[[Category:Plane of Sky]]\n",
            result.Formatted);
    }

    /// <summary>The blueprint's `Fashion: ?` stands for any `Fashion:` category, between `Drink With Stats` and
    /// `Food`.</summary>
    [Fact]
    public void AFashionCategoryTakesThePlaceholdersPlace()
    {
        PrettifyResult result = Format("[[Category:Food]]\n[[Category:Fashion: Robes]]\n[[Category:Drink]]");

        Assert.EndsWith("[[Category:Drink]]\n[[Category:Fashion: Robes]]\n[[Category:Food]]", result.Formatted);
    }

    /// <summary>Matched the way MediaWiki matches titles, and moved exactly as written, sort key included.</summary>
    [Fact]
    public void ALinkIsMatchedLikeATitleAndMovedAsWritten()
    {
        PrettifyResult result = Format("[[Category:quest_Items]]\n[[Category:Chest|Thing]]");

        Assert.EndsWith("[[Category:Chest|Thing]]\n\n[[Category:quest_Items]]", result.Formatted);
    }

    /// <summary>A category inside `onlyinclude` is transcluded with the item; moving it would change other pages.</summary>
    [Fact]
    public void ACategoryOutsideTheEndBlockStaysAndIsReported()
    {
        PrettifyResult result = Format("[[Category:Quest Items]]\n[[Category:Chest]]", inside: "\n[[Category:Waist]]");

        Assert.Contains("}}\n[[Category:Waist]]</onlyinclude>", result.Formatted);
        Assert.EndsWith("[[Category:Chest]]\n\n[[Category:Quest Items]]", result.Formatted);
        Assert.Contains(result.Notes, n => n.Contains("1 category elsewhere", StringComparison.Ordinal));
    }

    /// <summary>Text after the categories ends the block above it, so nothing below it moves.</summary>
    [Fact]
    public void TextAmongTheCategoriesIsNotCrossed()
    {
        PrettifyResult result = Format("[[Category:Quest Items]]\n{{Delete}}\n[[Category:Food]]\n[[Category:Chest]]");

        Assert.EndsWith("[[Category:Quest Items]]\n{{Delete}}\n[[Category:Chest]]\n\n[[Category:Food]]", result.Formatted);
    }

    [Fact]
    public void FormattingTwiceChangesNothing()
    {
        PrettifyResult once = Format("[[Category:Vendor Sold]]\n\n\n[[Category:Chest]]\n[[Category:Bard Equipment]]\n");
        PrettifyResult twice = ItemPagePrettifier.Format(once.Formatted);

        Assert.Equal(once.Formatted, twice.Formatted);
    }

    /// <summary>The content check allows the order to change and nothing else: a lost category is refused.</summary>
    [Fact]
    public void TheContentCheckRefusesALostCategory()
    {
        string original = Page("[[Category:Chest]]\n[[Category:Food]]");

        Assert.True(ItemPagePrettifier.WouldVerify(original, Page("[[Category:Food]]\n\n[[Category:Chest]]")));
        Assert.False(ItemPagePrettifier.WouldVerify(original, Page("[[Category:Chest]]")));
        Assert.False(ItemPagePrettifier.WouldVerify(original, Page("[[Category:Chest]]\n[[Category:Food]]\nText")));
    }

    /// <summary>Every category the Assistant writes has a place in the order, so the categories it adds are sorted
    /// rather than left at the end. `Pet Illusion Items` joined the blueprint on 2026-10-11 (oldid 193248).</summary>
    [Fact]
    public void EveryCategoryTheAssistantDerivesIsInTheBlueprintsOrder()
    {
        IEnumerable<string> derived = CategoryRules.ClassCategories.Select(c => c.Value)
            .Concat(CategoryRules.SlotCategoryNames.Values)
            .Concat(CategoryRules.FieldCategories.Values);

        Assert.All(derived, c => Assert.NotNull(WikiMapping.Default.FindCategoryPosition(c)));
    }
}
