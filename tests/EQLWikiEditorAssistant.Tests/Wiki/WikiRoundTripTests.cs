using EQLWikiEditorAssistant.Wiki.Wikitext;

namespace EQLWikiEditorAssistant.Tests.Wiki;

/// <summary>
/// The repo's hardest wikitext constraint, as a test: **untouched parts of a page must survive byte-for-byte.**
///
/// Rewriting a parameter with the value it already has must be an exact no-op. That single property is what makes
/// every real edit safe, because an edit is the same splice with a different string — so if this holds for all ten
/// fixture pages it holds for any edit to them, and if it fails anywhere the tool cannot be trusted to touch a
/// public wiki at all.
///
/// The same check runs against a much larger live sample via <c>dotnet run --project tools/WikiSpike -- roundtrip
/// 500</c>, which reported 0 failures over 414 real item pages (2026-09-24). These fixtures are the subset that
/// runs offline in the ordinary test pass.
/// </summary>
public class WikiRoundTripTests
{
    public static TheoryData<string> Pages => WikiFixtures.AllTitles();

    [Theory]
    [MemberData(nameof(Pages))]
    public void RewritingEveryParameterWithItsOwnValueChangesNothing(string title)
    {
        string original = WikiFixtures.Load(title);
        ItemPageDocument? document = ItemPageDocument.Parse(original);
        Assert.NotNull(document);

        // Distinct names, not every occurrence: on a page with a duplicated parameter, WithParameter edits the one
        // MediaWiki uses (the last), so feeding it the *first* occurrence's value is a genuine change, not a
        // round trip. That is the tool behaving correctly.
        foreach (string name in document.Template.Parameters
                     .Where(p => p.Name is not null)
                     .Select(p => p.Name!)
                     .Distinct(StringComparer.Ordinal))
        {
            string rewritten = document.WithParameter(name, document.GetParameter(name)!).Wikitext;
            Assert.True(rewritten == original, $"Rewriting |{name}= with its own value changed '{title}'.");
        }
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public void SplittingAndRenderingTheStatsBlockIsExact(string title)
    {
        ItemPageDocument document = ItemPageDocument.Parse(WikiFixtures.Load(title))!;
        TemplateParameter statsblock = document.Template.Find("statsblock")!;

        Assert.Equal(statsblock.RawValue, StatsBlock.Parse(statsblock.RawValue).Render());
    }

    /// <summary>Round-tripping the block through the document — the path a real edit takes — is exact too. This is
    /// a stronger claim than the one above: it also pins that <see cref="ItemPageDocument.WithStatsBlock"/> does
    /// not re-apply whitespace around a value that already carries its own.</summary>
    [Theory]
    [MemberData(nameof(Pages))]
    public void WritingTheStatsBlockBackUnchangedIsExact(string title)
    {
        string original = WikiFixtures.Load(title);
        ItemPageDocument document = ItemPageDocument.Parse(original)!;

        Assert.Equal(original, document.WithStatsBlock(document.ReadStatsBlock()!).Wikitext);
    }

    /// <summary>Every fixture must at least yield the two parameters the tool identifies an item by; a page where
    /// those are missing is one the pipeline has to refuse rather than half-process.</summary>
    [Theory]
    [MemberData(nameof(Pages))]
    public void EveryFixtureHasTheFieldsThePipelineNeeds(string title)
    {
        ItemPageDocument document = ItemPageDocument.Parse(WikiFixtures.Load(title))!;

        Assert.False(string.IsNullOrWhiteSpace(document.ItemName));
        Assert.NotNull(document.ReadStatsBlock());
    }

    /// <summary>No fixture line should be unreadable. Measured across 414 live pages the count is 0, so a non-zero
    /// count here is a grammar regression rather than a known gap being re-measured — the same ratchet discipline
    /// the screenshot corpus uses.</summary>
    [Theory]
    [MemberData(nameof(Pages))]
    public void NoStatsBlockLineIsUnreadable(string title)
    {
        StatsBlock block = ItemPageDocument.Parse(WikiFixtures.Load(title))!.ReadStatsBlock()!;

        string[] unreadable = [.. block.Lines.Where(l => l.Kind == StatsLineKind.Unparsed).Select(l => l.Text)];
        Assert.True(unreadable.Length == 0, $"'{title}' has unreadable statsblock lines: {string.Join(" | ", unreadable)}");
    }
}
