using EQLWikiAssistant.Wiki.MediaWiki;

namespace EQLWikiAssistant.Tests.Wiki;

/// <summary>
/// The lookup runs against a fake client that records what it asked for — which is the point of
/// <see cref="IMediaWikiClient"/> existing. Both real item names used here are from the corpus, and the
/// grave/apostrophe pair is a confusion that occurs on the live wiki in both directions.
/// </summary>
public class ItemPageLookupTests
{
    [Fact]
    public async Task AnExactMatchIsTheOrdinaryCase()
    {
        var client = new FakeClient { ["Water Flask"] = "{{Itempage}}" };

        ItemPageLookupResult result = await ItemPageLookup.FindAsync(client, "Water Flask");

        Assert.Equal(LookupOutcome.Found, result.Outcome);
        Assert.False(result.TreatAsNew);
        Assert.Null(result.Warning);
        Assert.Equal(["Water Flask"], client.Requested);
    }

    /// <summary>A name with no quote characters costs exactly one request — no speculative variant queries.</summary>
    [Fact]
    public async Task AMissWithNoQuotesCostsOneRequest()
    {
        var client = new FakeClient();

        ItemPageLookupResult result = await ItemPageLookup.FindAsync(client, "Water Flask");

        Assert.Equal(LookupOutcome.NotFound, result.Outcome);
        Assert.True(result.TreatAsNew);
        Assert.Equal(["Water Flask"], client.Requested);
    }

    /// <summary>The case that matters: the captured name uses a grave, the wiki page an apostrophe. The user cannot
    /// rename a page, so their remedy is to create a correct one and redirect the old — which means a missed
    /// candidate becomes a duplicate nobody can delete.</summary>
    [Fact]
    public async Task AGraveVersusApostropheMissIsOfferedAsACandidate()
    {
        var client = new FakeClient { ["Kavruul's Mystic Pouch"] = "{{Itempage}}" };

        ItemPageLookupResult result = await ItemPageLookup.FindAsync(client, "Kavruul`s Mystic Pouch");

        Assert.Equal(LookupOutcome.FoundMisnamedCandidate, result.Outcome);
        Assert.Equal("Kavruul's Mystic Pouch", result.Page!.Title);
        Assert.Contains("different quote character", result.Warning);
        Assert.Contains("move it", result.Warning);
    }

    [Fact]
    public async Task TheConfusionIsDetectedInBothDirections()
    {
        var client = new FakeClient { ["Engraved Di`Zok Deathbringer"] = "{{Itempage}}" };

        ItemPageLookupResult result = await ItemPageLookup.FindAsync(client, "Engraved Di'Zok Deathbringer");

        Assert.Equal(LookupOutcome.FoundMisnamedCandidate, result.Outcome);
        Assert.Equal("Engraved Di`Zok Deathbringer", result.Page!.Title);
    }

    /// <summary>A candidate is information for the user, not a page to edit. The item still counts as new, because
    /// acting on the wrong page is worse than treating a real item as unlisted.</summary>
    [Fact]
    public async Task AMisnamedCandidateStillCountsAsNew()
    {
        var client = new FakeClient { ["Kilva`s Skin of Flame"] = "{{Itempage}}" };

        ItemPageLookupResult result = await ItemPageLookup.FindAsync(client, "Kilva's Skin of Flame");

        Assert.True(result.TreatAsNew);
    }

    /// <summary>The correct name wins even when a variant also exists, and without querying the variant at all.</summary>
    [Fact]
    public async Task TheCorrectNameIsPreferredOverAVariant()
    {
        var client = new FakeClient
        {
            ["Kilva's Skin of Flame"] = "{{Itempage}}",
            ["Kilva`s Skin of Flame"] = "{{Itempage}}",
        };

        ItemPageLookupResult result = await ItemPageLookup.FindAsync(client, "Kilva's Skin of Flame");

        Assert.Equal(LookupOutcome.Found, result.Outcome);
        Assert.Equal(["Kilva's Skin of Flame"], client.Requested);
    }

    /// <summary>A title-illegal character cannot be queried, so this is reported as its own outcome rather than as
    /// "not found" — the page very likely exists under a name a human chose, and creating a second is the wrong
    /// move.</summary>
    [Fact]
    public async Task AnUnusableNameIsNotReportedAsNotFound()
    {
        var client = new FakeClient();

        ItemPageLookupResult result = await ItemPageLookup.FindAsync(client, "Cell Key #5");

        Assert.Equal(LookupOutcome.NameUnusable, result.Outcome);
        Assert.NotNull(result.TitleProblem);
        Assert.Contains('#', result.TitleProblem.OffendingCharacters);
        Assert.Empty(client.Requested); // never even asked — the query would error
        Assert.Contains("cannot be a wiki page title", result.Warning);
    }

    [Theory]
    [InlineData("Water Flask", 0)]                      // no quotes, no variants, no extra requests
    [InlineData("Kilva's Skin of Flame", 3)]            // one quote -> the three other forms
    [InlineData("Ry`Gorr's Plans", 8)]                  // two quotes -> 15 forms, capped at MaxVariants
    public void QuoteVariants_GrowsOnlyWithActualQuotes(string name, int expected) =>
        Assert.Equal(expected, ItemPageLookup.QuoteVariants(name).Count);

    [Fact]
    public void QuoteVariants_NeverIncludesTheNameItself() =>
        Assert.DoesNotContain("Kilva's Skin of Flame", ItemPageLookup.QuoteVariants("Kilva's Skin of Flame"));

    /// <summary>The cap is only safe because the ordering is. Four quote characters over two positions is sixteen
    /// combinations — more than the cap — so an arbitrary order could discard the plausible apostrophe/grave swaps
    /// in favour of curly-quote forms nobody has ever observed. Every all-plain variant must come first.</summary>
    [Fact]
    public void QuoteVariants_TriesThePlausibleFormsBeforeTheCapCanDiscardThem()
    {
        string[] firstThree = [.. ItemPageLookup.QuoteVariants("Ry`Gorr's Plans").Take(3).Order(StringComparer.Ordinal)];

        // The three other ways to spell it using only the apostrophe and the grave.
        Assert.Equal(
            ["Ry'Gorr's Plans", "Ry'Gorr`s Plans", "Ry`Gorr`s Plans"],
            firstThree);
    }

    /// <summary>Records every title asked for, so a test can assert the lookup is not making speculative requests.</summary>
    private sealed class FakeClient : IMediaWikiClient
    {
        public Task<IReadOnlySet<string>> ExistingTitlesAsync(
            IReadOnlyList<string> titles, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());

        private readonly Dictionary<string, string> _pages = new(StringComparer.Ordinal);

        public List<string> Requested { get; } = [];

        public string this[string title] { set => _pages[title] = value; }

        public Task<WikiPage?> FetchPageAsync(string title, CancellationToken cancellationToken = default)
        {
            Requested.Add(title);
            return Task.FromResult(_pages.TryGetValue(title, out string? text)
                ? new WikiPage(title, text, 1, DateTimeOffset.UnixEpoch)
                : null);
        }

        public Task LoginAsync(BotCredentials credentials, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("A lookup must never need credentials — reads are anonymous.");

        public Task<EditResult> EditAsync(
            string title, string newWikitext, string summary, DateTimeOffset baseTimestamp,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("A lookup must never write.");

        public Task<EditResult> CreatePageAsync(
            string title, string wikitext, string summary,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("A lookup must never write.");

        // This fake exists to answer reads; nothing here uploads.
        public Task<RenderedPage> RenderAsync(
            string title, string wikitext, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<UploadResult> UploadFileAsync(
            string fileName, byte[] content, string description, string comment,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
