using EQLWikiAssistant.Wiki.Wikitext;

namespace EQLWikiAssistant.Tests.Wiki;

public class PageTitleTests
{
    /// <summary>`#` is the one that turns up on real items — a "Cell Key #5" cannot have a page at its own name.
    /// The rest are MediaWiki markup or HTML and are illegal for the same reason.</summary>
    [Theory]
    [InlineData("Cell Key #5", '#')]
    [InlineData("Key [Master]", '[')]
    [InlineData("Scroll {Sealed}", '{')]
    [InlineData("A|B", '|')]
    [InlineData("A<B", '<')]
    public void FindProblem_RejectsCharactersMediaWikiForbidsInATitle(string name, char offender)
    {
        TitleProblem? problem = PageTitle.FindProblem(name);

        Assert.NotNull(problem);
        Assert.Contains(offender, problem.OffendingCharacters);
        Assert.False(PageTitle.IsUsableAsTitle(name));
    }

    /// <summary>The tool reports and never substitutes. The wiki's own fix for the real case was
    /// "Cell Key #5" -> "Cell Key No. 5", and No. 5 / Number 5 / 5 / dropping the # are all defensible — the choice
    /// is permanent, becomes the URL, and a wrong guess creates a page nobody can delete.</summary>
    [Fact]
    public void FindProblem_ExplainsTheChoiceWithoutMakingIt()
    {
        TitleProblem problem = PageTitle.FindProblem("Cell Key #5")!;

        Assert.Contains("Choose the page name yourself", problem.Explanation);
        Assert.Contains("may already exist", problem.Explanation);
    }

    /// <summary>Characters that look risky but are legal in a MediaWiki title, so they must not be rejected. The
    /// apostrophe and grave both occur in real item names and the two are deliberately distinct.</summary>
    [Theory]
    [InlineData("Water Flask")]
    [InlineData("Kilva's Skin of Flame")]
    [InlineData("Kavruul`s Mystic Pouch")]
    [InlineData("Staff of Elemental Mastery: Earth")]
    [InlineData("Salil's Writ Pg. 153 (Right)")]
    [InlineData("CLASS 6 Steel Silver Tip Arrow")]
    [InlineData("Rough Hickory Recurve Bow +2")]
    [InlineData("100' of Waterproofed Rope")]
    [InlineData("Iksar Skull with an 'X'")]
    [InlineData("Ry`Gorr Invasion Plans")]
    public void FindProblem_AcceptsNamesThatAreLegalTitles(string name) =>
        Assert.True(PageTitle.IsUsableAsTitle(name), $"'{name}' should be usable as a page title.");

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void FindProblem_RejectsAnEmptyName(string name) =>
        Assert.NotNull(PageTitle.FindProblem(name));

    [Fact]
    public void Compare_ExactMatchIsWhatTheTemplateWants() =>
        Assert.Equal(TitleMatch.Exact, PageTitle.Compare("Water Flask", "Water Flask"));

    /// <summary>MediaWiki treats underscores as spaces in titles, so these are the same page.</summary>
    [Fact]
    public void Compare_TreatsUnderscoresAsSpaces() =>
        Assert.Equal(TitleMatch.Exact, PageTitle.Compare("Water Flask", "Water_Flask"));

    /// <summary>One in-game item, several wiki pages — a real convention on this wiki, so it is reported as its own
    /// outcome rather than as a defect. All five are real title/itemname pairs.</summary>
    [Theory]
    [InlineData("Rough Ashwood Recurve Bow", "Rough Ashwood Recurve Bow (Hemp)")]
    [InlineData("Imbued Dwarven Chain Cloak", "Imbued Dwarven Chain Cloak (Bristlebane)")]
    [InlineData("Tailoring", "Tailoring (Item)")]
    [InlineData("A Sealed Letter", "A Sealed Letter (Thex Dagger Quest)")]
    [InlineData("Essence of Barbarian", "Essence of Barbarian (Wormwood)")]
    public void Compare_RecognizesTheDisambiguatedTitleConvention(string itemName, string title) =>
        Assert.Equal(TitleMatch.DisambiguatedTitle, PageTitle.Compare(itemName, title));

    /// <summary>Both are real divergences found on the wiki. The second is the grave-vs-apostrophe confusion, where
    /// the title uses a grave and the itemname an apostrophe — exactly the distinction glyph matching preserves,
    /// which is what lets the tool notice it at all.</summary>
    [Theory]
    [InlineData("Orb of Mastery", "Kurrat's Magician Epic Guide")]
    [InlineData("Engraved Di'Zok Deathbringer", "Engraved Di`Zok Deathbringer")]
    [InlineData("Sparkling Sapphire", "A Sparkling Sapphire")]
    [InlineData("Storm Giant Steak", "Storm Giant Steaks")]
    public void Compare_ReportsARealDivergence(string itemName, string title) =>
        Assert.Equal(TitleMatch.Divergent, PageTitle.Compare(itemName, title));

    /// <summary>A page with no itemname at all cannot be said to match; that is a defect, not an exact match to
    /// nothing.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Compare_TreatsAMissingItemnameAsDivergent(string? itemName) =>
        Assert.Equal(TitleMatch.Divergent, PageTitle.Compare(itemName, "Water Flask"));

    /// <summary>A qualifier has to actually be a parenthesised suffix; a title that merely starts with the same
    /// letters is a divergence.</summary>
    [Fact]
    public void Compare_DoesNotMistakeAPrefixForADisambiguator() =>
        Assert.Equal(TitleMatch.Divergent, PageTitle.Compare("Water Flask", "Water Flask of Doom"));

    /// <summary>Every fixture page should be Exact, since they are ordinary item pages — a cheap check that the
    /// comparison is not accidentally strict.</summary>
    [Theory]
    [MemberData(nameof(Pages))]
    public void Compare_EveryFixturePageMatchesItsOwnTitle(string title)
    {
        ItemPageDocument document = ItemPageDocument.Parse(WikiFixtures.Load(title))!;

        Assert.Equal(TitleMatch.Exact, PageTitle.Compare(document.ItemName, title));
    }

    public static TheoryData<string> Pages => WikiFixtures.AllTitles();
}
