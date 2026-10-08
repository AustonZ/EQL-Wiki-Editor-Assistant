using EQLWikiAssistant.Wiki.MediaWiki;

namespace EQLWikiAssistant.Tests.Wiki;

/// <summary>
/// Resolving an effect's link target, for the one case where the tool chooses one: a line it writes itself.
///
/// The asserts are on request *counts* as much as on return values, for the same reason the ledger's are — the
/// point of only asking about missing lines is that an ordinary check costs nothing extra, and only a counting
/// fake can state that.
/// </summary>
public class EffectPageLookupTests
{
    [Fact]
    public async Task AnEffectWithItsOwnQualifiedPageResolvesToIt()
    {
        var client = new FakeTitles("Firestrike (Effect)");

        IReadOnlyDictionary<string, string> targets =
            await EffectPageLookup.ResolveLinkTargetsAsync(client, ["Firestrike"]);

        Assert.Equal("Firestrike (Effect)", targets["Firestrike"]);
    }

    /// <summary>The common case by far: three such pages exist on the whole wiki, so most effects resolve to
    /// nothing and the caller links the name itself.</summary>
    [Fact]
    public async Task AnEffectWithNoQualifiedPageResolvesToNothing()
    {
        var client = new FakeTitles("Firestrike (Effect)");

        Assert.Empty(await EffectPageLookup.ResolveLinkTargetsAsync(client, ["Burn"]));
    }

    /// <summary>The bare page is not an answer, which is the whole point of the qualifier: `Firestrike` exists too
    /// and is the *player spell*, with different damage and a different cast time.</summary>
    [Fact]
    public async Task TheBarePageIsNotTakenAsTheEffectsPage()
    {
        var client = new FakeTitles("Firestrike");

        Assert.Empty(await EffectPageLookup.ResolveLinkTargetsAsync(client, ["Firestrike"]));
    }

    /// <summary>No names, no request. An item with no effects is most items, and a page that already carries its
    /// effect line resolves nothing either.</summary>
    [Fact]
    public async Task NoNamesCostsNoRequest()
    {
        var client = new FakeTitles();

        Assert.Empty(await EffectPageLookup.ResolveLinkTargetsAsync(client, []));
        Assert.Equal(0, client.Calls);
    }

    /// <summary>Several effects are one batched request, not one each.</summary>
    [Fact]
    public async Task SeveralEffectsAreAskedAboutTogether()
    {
        var client = new FakeTitles("Firestrike (Effect)", "Fungus Spores (Effect)");

        IReadOnlyDictionary<string, string> targets = await EffectPageLookup
            .ResolveLinkTargetsAsync(client, ["Firestrike", "Fungus Spores", "Burn"]);

        Assert.Equal(2, targets.Count);
        Assert.Equal(1, client.Calls);
    }

    /// <summary>The link is written the way the wiki spells the title, not the way the request did — MediaWiki
    /// normalizes a title, so comparing what came back is what keeps the two in step.</summary>
    [Fact]
    public async Task TheTitleIsTakenAsTheWikiSpellsIt()
    {
        var client = new FakeTitles("Nature's Melody (Effect)");

        IReadOnlyDictionary<string, string> targets =
            await EffectPageLookup.ResolveLinkTargetsAsync(client, ["nature's melody"]);

        Assert.Equal("Nature's Melody (Effect)", targets["nature's melody"]);
    }

    private sealed class FakeTitles(params string[] existing) : IMediaWikiClient
    {
        public int Calls { get; private set; }

        public Task<IReadOnlySet<string>> ExistingTitlesAsync(
            IReadOnlyList<string> titles, CancellationToken cancellationToken = default)
        {
            Calls++;
            IReadOnlySet<string> found = existing
                .Where(e => titles.Any(t => string.Equals(t, e, StringComparison.OrdinalIgnoreCase)))
                .ToHashSet(StringComparer.Ordinal);
            return Task.FromResult(found);
        }

        public Task<WikiPage?> FetchPageAsync(string title, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task LoginAsync(BotCredentials credentials, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<EditResult> EditAsync(
            string title, string newWikitext, string summary, DateTimeOffset baseTimestamp,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<EditResult> CreatePageAsync(
            string title, string wikitext, string summary, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<RenderedPage> RenderAsync(
            string title, string wikitext, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<UploadResult> UploadFileAsync(
            string fileName, byte[] content, string description, string comment,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
