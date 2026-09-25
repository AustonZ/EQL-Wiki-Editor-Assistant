using EQLWikiAssistant.Wiki.Analysis;
using EQLWikiAssistant.Wiki.Wikitext;

namespace EQLWikiAssistant.Tests.Wiki;

/// <summary>
/// Every rule here exists because a measured number of real pages break it. The counts in the comments are from 744
/// live item pages and are the reason each rule was kept.
/// </summary>
public class ComplianceCheckerTests
{
    private static IReadOnlyList<ComplianceFinding> Check(string fixture)
    {
        string wikitext = WikiFixtures.Load(fixture);
        return ComplianceChecker.Check(ItemPageDocument.Parse(wikitext)!, wikitext);
    }

    private static IReadOnlyList<ComplianceFinding> CheckText(string wikitext) =>
        ComplianceChecker.Check(ItemPageDocument.Parse(wikitext)!, wikitext);

    /// <summary>A well-formed page raises nothing. Worth pinning, or every other assertion here is meaningless.
    /// Earring of Bashing already carries {{Classic Era}}.</summary>
    [Fact]
    public void ACompliantPageRaisesNothing() =>
        Assert.Empty(Check("Earring of Bashing"));

    /// <summary>3 of 744 pages. MediaWiki renders only the last, so the earlier copies are invisible content — a
    /// defect in what the page says, which is why the tool fixes it rather than reporting it.</summary>
    [Fact]
    public void ADuplicateParameterIsReportedAndFixable()
    {
        ComplianceFinding finding = Assert.Single(
            Check("Shimmering Ruby Stiletto").Where(f => f.Rule == ComplianceChecker.DuplicateParameterRule));

        Assert.True(finding.ToolWillFix);
        Assert.Contains("|notes=", finding.Detail);
    }

    /// <summary>3 of 744 pages, one of them writing `recipe` for `recipes`. The template ignores the name entirely,
    /// so the content does not render — but renaming it is a human's call, since the closest known name may not be
    /// what the author meant.</summary>
    [Fact]
    public void AnUnrecognizedParameterIsReportedButNotRenamed()
    {
        ComplianceFinding finding = Assert.Single(CheckText(
            "<onlyinclude>{{Itempage\n|itemname = X\n|lucy_img_ID = 1\n|statsblock = \nClass: ALL<br>\n" +
            "|recipe = something\n}}</onlyinclude>{{Classic Era}}")
            .Where(f => f.Rule == ComplianceChecker.UnknownParameterRule));

        Assert.False(finding.ToolWillFix);
        Assert.Contains("|recipe=", finding.Detail);
        Assert.Contains("does not appear on the page", finding.Detail);
    }

    /// <summary>35 of 744 pages. Always removed when the tool edits: by then either lore has been captured, or the
    /// item has no lore tab and so has none.</summary>
    [Fact]
    public void TheLorePlaceholderIsReportedAndFixable()
    {
        ComplianceFinding finding = Assert.Single(
            Check("Blackened Alloy Longsword").Where(f => f.Rule == ComplianceChecker.LorePlaceholderRule));

        Assert.True(finding.ToolWillFix);
    }

    /// <summary>4 of 744 pages. Without the wrapper the item box cannot be transcluded elsewhere, but repairing it
    /// means deciding what it should enclose.</summary>
    [Fact]
    public void AMissingOnlyIncludeWrapperIsReportedNotFixed()
    {
        ComplianceFinding finding = Assert.Single(CheckText(
            "{{Classic Era}}\n{{Itempage\n|itemname = X\n|lucy_img_ID = 1\n|statsblock = \nClass: ALL<br>\n}}")
            .Where(f => f.Rule == ComplianceChecker.OnlyIncludeRule));

        Assert.False(finding.ToolWillFix);
    }

    /// <summary>232 of 744 pages have no banner. The era is how the wiki records that an item has actually been seen
    /// in game (user, 2026-09-25), so a capture is itself the confirmation and the tool sets it.</summary>
    [Fact]
    public void AMissingEraTemplateIsFixedNotJustReported()
    {
        ComplianceFinding finding = Assert.Single(
            Check("Bladestopper").Where(f => f.Rule == ComplianceChecker.EraTemplateRule));

        Assert.True(finding.ToolWillFix);
        Assert.Contains("Classic Era", finding.Detail);
    }

    /// <summary>A legacy banner is corrected too — 176 sampled pages say `Velious Era`, inherited from the
    /// Project1999 import. If the item was just seen in game, it is Classic Era whatever the page says.</summary>
    [Theory]
    [InlineData("{{Velious Era}}")]
    [InlineData("{{Kunark Era}}")]
    [InlineData("{{Chardok Revamp Era}}")]
    public void ALegacyEraBannerIsCorrected(string banner)
    {
        ComplianceFinding finding = Assert.Single(
            CheckText(Page(banner)).Where(f => f.Rule == ComplianceChecker.EraTemplateRule));

        Assert.True(finding.ToolWillFix);
        Assert.Contains("Classic Era", finding.Detail);
    }

    [Theory]
    [InlineData("{{Classic Era}}")]
    [InlineData("{{ Classic Era }}")]
    public void TheCurrentEraRaisesNothing(string banner) =>
        Assert.DoesNotContain(
            CheckText(Page(banner)), f => f.Rule == ComplianceChecker.EraTemplateRule);

    private static string Page(string banner) =>
        $"{banner}\n<onlyinclude>{{{{Itempage\n|itemname = X\n|lucy_img_ID = 1\n|statsblock = \nClass: ALL<br>\n}}}}</onlyinclude>";

    [Fact]
    public void AMissingRequiredParameterIsReported()
    {
        ComplianceFinding finding = Assert.Single(CheckText(
            "{{Classic Era}}\n<onlyinclude>{{Itempage\n|itemname = X\n|statsblock = \nClass: ALL<br>\n}}</onlyinclude>")
            .Where(f => f.Rule == ComplianceChecker.MissingParameterRule));

        Assert.Contains("lucy_img_ID", finding.Detail);
        Assert.False(finding.ToolWillFix);
    }

    /// <summary>Removing a duplicate takes the whole `|name = value` run — splicing out only the value would leave a
    /// stray `|notes =` behind — and keeps the last occurrence, the one MediaWiki renders.</summary>
    [Fact]
    public void WithoutDuplicateParameters_KeepsTheOneMediaWikiUses()
    {
        string original = WikiFixtures.Load("Shimmering Ruby Stiletto");
        ItemPageDocument cleaned = ItemPageDocument.Parse(original)!.WithoutDuplicateParameters();

        // The surviving value is the real one, and only one |notes= remains.
        Assert.Equal("Backstab DMG: 10", cleaned.Notes);
        Assert.Single(cleaned.Template.Parameters, p => p.Name == "notes");

        // Everything else is untouched, including the leading [[File:...]] outside <onlyinclude>.
        Assert.Equal("Shimmering Ruby Stiletto", cleaned.ItemName);
        Assert.Contains("[[File:Shimmering Ruby Stiletto.png|thumb]]", cleaned.Wikitext);
        Assert.Contains("[[Befallen]]", cleaned.Wikitext);
        Assert.Empty(ComplianceChecker.Check(cleaned, cleaned.Wikitext)
            .Where(f => f.Rule == ComplianceChecker.DuplicateParameterRule));
    }

    [Fact]
    public void WithoutDuplicateParameters_IsANoOpWhenThereAreNone()
    {
        string original = WikiFixtures.Load("Earring of Bashing");

        Assert.Equal(original, ItemPageDocument.Parse(original)!.WithoutDuplicateParameters().Wikitext);
    }

    /// <summary>Every fixture must survive the cleanup byte for byte unless it genuinely has a duplicate — the same
    /// guarantee the rest of the wikitext layer gives.</summary>
    [Theory]
    [MemberData(nameof(Pages))]
    public void WithoutDuplicateParameters_TouchesOnlyPagesWithDuplicates(string title)
    {
        string original = WikiFixtures.Load(title);
        ItemPageDocument document = ItemPageDocument.Parse(original)!;
        bool hasDuplicate = document.Template.Parameters
            .Where(p => p.Name is not null)
            .GroupBy(p => p.Name!, StringComparer.Ordinal)
            .Any(g => g.Count() > 1);

        string cleaned = document.WithoutDuplicateParameters().Wikitext;

        if (hasDuplicate) Assert.NotEqual(original, cleaned);
        else Assert.Equal(original, cleaned);
    }

    public static TheoryData<string> Pages => WikiFixtures.AllTitles();
}
