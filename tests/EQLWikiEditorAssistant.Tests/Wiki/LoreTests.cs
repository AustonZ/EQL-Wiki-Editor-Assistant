using EQLWikiEditorAssistant.Core.Items;
using EQLWikiEditorAssistant.Wiki.Analysis;
using EQLWikiEditorAssistant.Wiki.Wikitext;

namespace EQLWikiEditorAssistant.Tests.Wiki;

/// <summary>
/// Lore is the one v1 field that lives nested inside another parameter, and the one the tool deliberately declines
/// to overwrite. Both of those are asserted here, because both are easy to "simplify" into something worse.
/// </summary>
public class LoreTests
{
    private static ParsedItem Captured(string name = "Earring of Bashing", string? lore = null) =>
        new(name, 0, false, [], [], [], [], [], [], [], null, lore, []);

    private static ItemPageDocument Page(string notes) =>
        ItemPageDocument.Parse(
            "{{Classic Era}}\n<onlyinclude>{{Itempage\n|itemname = Thing\n|lucy_img_ID = 1\n" +
            "|statsblock = \nClass: ALL<br>\n|notes = " + notes + "\n}}</onlyinclude>")!;

    // --- writing ----------------------------------------------------------------------------------------

    /// <summary>The wrapper's value is spliced in place, so a human's own commentary elsewhere in `notes` survives
    /// byte for byte — the same guarantee every other edit in this codebase gives.</summary>
    [Fact]
    public void ExistingLoreIsReplacedInPlaceAndTheRestOfNotesSurvives()
    {
        ItemPageDocument page = Page("{{Item Lore|Old words.}}<br>\nDropped by the guy in the place.");

        string edited = page.WithLore("New words.").Wikitext;

        Assert.Contains("{{Item Lore|New words.}}<br>\nDropped by the guy in the place.", edited);
        Assert.DoesNotContain("Old words.", edited);
    }

    /// <summary>A page with no wrapper gets one at the front of `notes`, which is where the
    /// <c>{{Item Lore Missing}}</c> placeholder it replaces also sat.</summary>
    [Fact]
    public void LoreIsAddedInFrontOfWhateverAHumanWrote()
    {
        ItemPageDocument page = Page("Dropped by the guy in the place.");

        string edited = page.WithLore("New words.").Wikitext;

        Assert.Contains("{{Item Lore|New words.}}<br>\nDropped by the guy in the place.", edited);
    }

    [Fact]
    public void AnEmptyNotesParameterBecomesTheLoreCall()
    {
        string edited = Page("").WithLore("New words.").Wikitext;

        Assert.Contains("|notes = {{Item Lore|New words.}}", edited);
    }

    /// <summary>The minimal-edit rule's third case: no parameter means no line to change, and inventing a position
    /// is a judgement this codebase consistently refuses to make.</summary>
    [Fact]
    public void APageWithoutANotesParameterRefusesRatherThanInventingOne()
    {
        ItemPageDocument page = ItemPageDocument.Parse(
            "{{Classic Era}}\n<onlyinclude>{{Itempage\n|itemname = Thing\n|statsblock = \nClass: ALL<br>\n}}</onlyinclude>")!;

        Assert.Throws<InvalidOperationException>(() => page.WithLore("New words."));
    }

    /// <summary>A pipe would start a second template parameter and a brace pair would open or close a template, so
    /// such text is refused rather than written — it would restructure somebody's page silently.</summary>
    [Theory]
    [InlineData("A tale of two|three cities.")]
    [InlineData("It bore the mark {{of the maker}}.")]
    public void TextThatWouldRestructureTheTemplateIsRefused(string lore)
    {
        Assert.False(ItemPageDocument.CanBeWrittenAsLore(lore));
        Assert.Throws<ArgumentException>(() => Page("").WithLore(lore));
    }

    [Fact]
    public void OrdinaryProseIsWritable() =>
        Assert.True(ItemPageDocument.CanBeWrittenAsLore("A trophy of the first bashing — it's heavy, isn't it?"));

    // --- comparing --------------------------------------------------------------------------------------

    /// <summary>The whole point of the two-capture flow: a page with no lore gets the captured text.</summary>
    [Fact]
    public void LoreMissingFromThePageIsAdded()
    {
        ItemPageDocument page = Page("Dropped by the guy in the place.");
        ItemPageAnalysis analysis = ItemPageAnalyzer.Analyze(Captured(lore: "A trophy."), page, "Thing");

        FieldFinding finding = analysis.Find(ItemPageAnalyzer.LoreField)!;
        Assert.Equal(FieldVerdict.MissingOnWiki, finding.Verdict);
        Assert.Contains("{{Item Lore|A trophy.}}", ItemPageEditor.BuildEdit(page, analysis).NewWikitext);
    }

    /// <summary>
    /// **Lore that differs is reported, never overwritten** — a narrower rule than every other field follows, and
    /// deliberately so: lore is a paragraph, where one misread word would pass review invisibly, and the page's copy
    /// may carry wikilinks the item window cannot show.
    /// </summary>
    [Fact]
    public void LoreThatDiffersIsReportedRatherThanOverwritten()
    {
        ItemPageDocument page = Page("{{Item Lore|A trophy of the [[First Bashing]].}}");
        ItemPageAnalysis analysis = ItemPageAnalyzer.Analyze(Captured(lore: "A trophy of the First Bashing."), page, "Thing");

        FieldFinding finding = analysis.Find(ItemPageAnalyzer.LoreField)!;
        Assert.Equal(FieldVerdict.NeedsReview, finding.Verdict);
        Assert.True(finding.Blocks);
        Assert.Contains("A trophy of the [[First Bashing]].", ItemPageEditor.BuildEdit(page, analysis).NewWikitext);
    }

    /// <summary>The game wraps lore to fit its window and the parser rejoins those rows with single spaces, so a
    /// page that breaks the same sentence differently must not read as a difference.</summary>
    [Fact]
    public void LineBreaksInThePagesLoreAreNotADifference()
    {
        ItemPageDocument page = Page("{{Item Lore|A trophy of the\nfirst bashing.}}");
        ItemPageAnalysis analysis = ItemPageAnalyzer.Analyze(
            Captured(lore: "A trophy of the first bashing."), page, "Thing");

        Assert.Equal(FieldVerdict.Matches, analysis.Find(ItemPageAnalyzer.LoreField)!.Verdict);
    }

    /// <summary>Punctuation is content, not formatting — this is prose, and the comparison that decides whether a
    /// public wiki gets rewritten must not be tolerant.</summary>
    [Fact]
    public void PunctuationIsADifference()
    {
        ItemPageDocument page = Page("{{Item Lore|A trophy of the first bashing!}}");
        ItemPageAnalysis analysis = ItemPageAnalyzer.Analyze(
            Captured(lore: "A trophy of the first bashing."), page, "Thing");

        Assert.Equal(FieldVerdict.NeedsReview, analysis.Find(ItemPageAnalyzer.LoreField)!.Verdict);
    }

    /// <summary>
    /// Read in Arial, l and I are the same pixels, so a misread between exactly those two must not report a page's
    /// correct lore as different (user, 2026-10-09). The page is never overwritten either way; this only stops a false
    /// warning.
    /// </summary>
    [Fact]
    public void InArialLAndIAreTheSameLetter()
    {
        ItemPageDocument page = Page("{{Item Lore|Few recover lost items.}}");
        ItemPageAnalysis analysis = ItemPageAnalyzer.Analyze(
            Captured(lore: "Few recover Iost items."), page, "Thing", loreMayConfuseLAndI: true);

        Assert.Equal(FieldVerdict.Matches, analysis.Find(ItemPageAnalyzer.LoreField)!.Verdict);
    }

    /// <summary>The control: read in a font with no ambiguity, the same two texts really do differ, and any other
    /// letter differs in Arial too.</summary>
    [Fact]
    public void OnlyLAndIAreConfusedAndOnlyInArial()
    {
        ItemPageDocument page = Page("{{Item Lore|Few recover lost items.}}");

        Assert.Equal(FieldVerdict.NeedsReview, ItemPageAnalyzer.Analyze(
            Captured(lore: "Few recover Iost items."), page, "Thing").Find(ItemPageAnalyzer.LoreField)!.Verdict);
        Assert.Equal(FieldVerdict.NeedsReview, ItemPageAnalyzer.Analyze(
            Captured(lore: "Few recover most items."), page, "Thing", loreMayConfuseLAndI: true)
            .Find(ItemPageAnalyzer.LoreField)!.Verdict);
    }

    /// <summary>A Description-only capture has no lore, and that absence must not read as "the page's lore is
    /// wrong" — it means the second capture has not happened yet.</summary>
    [Fact]
    public void NoCapturedLoreIsNotAFinding()
    {
        ItemPageAnalysis analysis = ItemPageAnalyzer.Analyze(
            Captured(), Page("{{Item Lore|A trophy.}}"), "Thing");

        Assert.Null(analysis.Find(ItemPageAnalyzer.LoreField));
    }
}
