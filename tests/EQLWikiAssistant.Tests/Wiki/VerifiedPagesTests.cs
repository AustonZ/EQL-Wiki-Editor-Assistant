using EQLWikiAssistant.Wiki.MediaWiki;

namespace EQLWikiAssistant.Tests.Wiki;

/// <summary>
/// Reading the wiki's "Verified for EQLegends" list. The tool only ever reports this — verification covers a whole
/// page, including the parts this tool never reads, so it has no standing to claim one.
/// </summary>
public class VerifiedPagesTests : IDisposable
{
    private readonly string _cache = Path.Combine(Path.GetTempPath(), $"verified-{Guid.NewGuid():N}.json");

    public void Dispose() => File.Delete(_cache);

    /// <summary>A real excerpt: underscored titles, literal apostrophes and colons, and several namespaces.</summary>
    private const string RealList =
        "\nMain_Page\nSpecial:ZoneViewer\nCategory:Spells\n" +
        "User:Todlo/A_Young_Troll's_Guide_to_Race_Relations\n" +
        "Elementalkin:_Air_Summon\nVoid-Touched_Potential\nDragon_Bone_Bracelet\nFile:Item_6037.png\n";

    private sealed class FakeWiki : IMediaWikiClient
    {
        public string? Content { get; set; }
        public int Fetches { get; private set; }
        public bool Fail { get; set; }
        public bool Unavailable { get; set; }

        public Task<WikiPage?> FetchPageAsync(string title, CancellationToken cancellationToken = default)
        {
            Fetches++;
            if (Unavailable) throw new WikiUnavailableException("eqlwiki.com could not be reached.");
            if (Fail) throw new MediaWikiException("badtitle", "that list page is unreadable");
            return Task.FromResult(Content is null
                ? null
                : new WikiPage(title, Content, 179774, DateTimeOffset.UnixEpoch));
        }

        public Task LoginAsync(BotCredentials credentials, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<EditResult> EditAsync(
            string title, string newWikitext, string summary, DateTimeOffset baseTimestamp,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The verified list is never written by this tool.");
    }

    private async Task<(VerifiedPages Pages, FakeWiki Wiki)> LoadedAsync(string content = RealList)
    {
        var wiki = new FakeWiki { Content = content };
        var pages = new VerifiedPages(wiki, _cache);
        await pages.RefreshAsync();
        return (pages, wiki);
    }

    [Fact]
    public async Task ReadsTheListAndAnswersForEachTitle()
    {
        (VerifiedPages pages, _) = await LoadedAsync();

        Assert.True(pages.IsVerified("Dragon Bone Bracelet"));
        Assert.True(pages.IsVerified("Void-Touched Potential"));
        Assert.False(pages.IsVerified("Staff of Forbidden Rites"));
    }

    /// <summary>Spaces are the only difference between a page title as the API returns it and the list's own form —
    /// nothing is percent-encoded, so an apostrophe or a colon compares literally.</summary>
    [Theory]
    [InlineData("Main Page", true)]
    [InlineData("Special:ZoneViewer", true)]
    [InlineData("Elementalkin: Air Summon", true)]
    [InlineData("User:Todlo/A Young Troll's Guide to Race Relations", true)]
    [InlineData("File:Item 6037.png", true)]
    [InlineData("Some Other Item", false)]
    public async Task MatchesTitlesTheWayTheWikiDoes(string title, bool expected)
    {
        (VerifiedPages pages, _) = await LoadedAsync();

        Assert.Equal(expected, pages.IsVerified(title));
    }

    /// <summary>Copied from the toast's own script so the two cannot disagree.</summary>
    [Theory]
    [InlineData("  Ruby Crown  ", "Ruby_Crown")]
    [InlineData(":Category:Spells", "Category:Spells")]
    [InlineData("Already_Underscored", "Already_Underscored")]
    public void NormalizeMatchesTheWikisOwnRule(string input, string expected) =>
        Assert.Equal(expected, VerifiedPages.Normalize(input));

    [Fact]
    public async Task SkipsBlankLinesAndComments()
    {
        (VerifiedPages pages, _) = await LoadedAsync("\n# a comment\n\nRuby_Crown\n   \n");

        Assert.Equal(1, pages.Count);
        Assert.True(pages.IsVerified("Ruby Crown"));
    }

    /// <summary>
    /// **The safety rule.** A list that could never be read answers null, and the caller says nothing — a false
    /// "this page is unverified" would send the user to re-verify a page that is already done.
    /// </summary>
    [Fact]
    public async Task SaysNothingAtAllWhenTheListHasNeverBeenRead()
    {
        var wiki = new FakeWiki { Fail = true };
        var pages = new VerifiedPages(wiki, _cache);

        await pages.RefreshAsync();

        Assert.False(pages.IsKnown);
        Assert.Null(pages.IsVerified("Dragon Bone Bracelet"));
    }

    /// <summary>An error *about this page* is swallowed: whether somebody ticked a box on a web page is not a reason
    /// to fail a capture, and whatever was already known stays.</summary>
    [Fact]
    public async Task KeepsWhatItHasWhenTheListItselfCannotBeRead()
    {
        (VerifiedPages pages, FakeWiki wiki) = await LoadedAsync();
        wiki.Fail = true;

        await Task.Delay(1);
        await pages.RefreshAsync();

        Assert.True(pages.IsVerified("Dragon Bone Bracelet"));
    }

    /// <summary>
    /// **An unreachable wiki propagates.** This runs first in a capture, so it is the tool's earliest and cheapest
    /// notice that the wiki is gone — and the capture that would follow is pointless anyway, since every page fetch
    /// would fail the same way (user, 2026-09-29).
    /// </summary>
    [Fact]
    public async Task AnUnreachableWikiPropagatesRatherThanBeingSwallowed()
    {
        var wiki = new FakeWiki { Unavailable = true };
        var pages = new VerifiedPages(wiki, _cache);

        await Assert.ThrowsAsync<WikiUnavailableException>(() => pages.RefreshAsync());
    }

    /// <summary>The TTL is what keeps this to one small request per session rather than one per capture.</summary>
    [Fact]
    public async Task DoesNotRefetchWithinTheTtl()
    {
        (VerifiedPages pages, FakeWiki wiki) = await LoadedAsync();
        Assert.Equal(1, wiki.Fetches);

        await pages.RefreshAsync();

        Assert.Equal(1, wiki.Fetches);
    }

    /// <summary>The cache exists so the first capture of a session can answer before any request completes — and,
    /// on the ledger-skip path, with no request at all.</summary>
    [Fact]
    public async Task ACachedListIsAvailableImmediatelyOnTheNextStart()
    {
        await LoadedAsync();

        var restarted = new VerifiedPages(new FakeWiki { Fail = true }, _cache);

        Assert.True(restarted.IsKnown);
        Assert.True(restarted.IsVerified("Dragon Bone Bracelet"));
        Assert.Equal(179774, restarted.RevisionId);
    }

    /// <summary>A corrupt cache costs one fetch, never a crash — the ledger's own rule.</summary>
    [Fact]
    public void ACorruptCacheLoadsAsNothingKnown()
    {
        File.WriteAllText(_cache, "{ this is not json");

        Assert.False(new VerifiedPages(new FakeWiki(), _cache).IsKnown);
    }

    /// <summary>A missing list page is not an answer either. If somebody renames it, the tool must go quiet rather
    /// than declare every page unverified.</summary>
    [Fact]
    public async Task AMissingListPageLeavesNothingKnown()
    {
        var pages = new VerifiedPages(new FakeWiki { Content = null }, _cache);

        await pages.RefreshAsync();

        Assert.False(pages.IsKnown);
        Assert.Null(pages.IsVerified("Dragon Bone Bracelet"));
    }
}
