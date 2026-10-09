using System.Net;
using EQLWikiEditorAssistant.Pipeline;

namespace EQLWikiEditorAssistant.Tests.Pipeline;

public class ReleaseCheckTests
{
    private static string Release(string tag, bool prerelease = false, bool draft = false,
        string url = "https://github.com/AustonZ/EQL-Wiki-Editor-Assistant/releases/tag/") =>
        $$"""{"tag_name":"{{tag}}","prerelease":{{(prerelease ? "true" : "false")}},"draft":{{(draft ? "true" : "false")}},"html_url":"{{url}}{{tag}}"}""";

    private static string Feed(params string[] releases) => "[" + string.Join(",", releases) + "]";

    /// <summary>The specification's own precedence example, plus the double-digit case a string comparison gets
    /// wrong (<c>alpha.10</c> after <c>alpha.2</c>).</summary>
    [Fact]
    public void VersionsOrderAsSemanticVersioningSays()
    {
        string[] ascending =
        [
            "1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.2", "1.0.0-alpha.10", "1.0.0-alpha.beta", "1.0.0-beta",
            "1.0.0-beta.2", "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0", "1.0.1", "1.1.0", "2.0.0",
        ];
        for (int i = 1; i < ascending.Length; i++)
        {
            Assert.True(SemanticVersion.TryParse(ascending[i - 1], out SemanticVersion lower));
            Assert.True(SemanticVersion.TryParse(ascending[i], out SemanticVersion higher));
            Assert.True(lower.CompareTo(higher) < 0, $"{ascending[i - 1]} should precede {ascending[i]}");
            Assert.True(higher.CompareTo(lower) > 0, $"{ascending[i]} should follow {ascending[i - 1]}");
        }
    }

    /// <summary>The app's own build version carries the commit after a <c>+</c>; it must not count.</summary>
    [Fact]
    public void BuildMetadataIsIgnored()
    {
        Assert.True(SemanticVersion.TryParse("1.0.0-alpha.1+3334f45-modified", out SemanticVersion built));
        Assert.True(SemanticVersion.TryParse("1.0.0-alpha.1", out SemanticVersion plain));
        Assert.Equal(0, built.CompareTo(plain));
        Assert.Equal("1.0.0-alpha.1", built.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("1.0")]
    [InlineData("1.0.0-")]
    [InlineData("1.0.0-alpha..1")]
    [InlineData("one.two.three")]
    [InlineData("latest")]
    public void TextThatIsNotAVersionIsRefused(string text) => Assert.False(SemanticVersion.TryParse(text, out _));

    [Fact]
    public void ATesterHearsAboutTheNextPrerelease()
    {
        AvailableRelease? found = ReleaseCheck.FindNewer(
            Feed(Release("v1.0.0-alpha.2", prerelease: true), Release("v1.0.0-alpha.1", prerelease: true)),
            "1.0.0-alpha.1");

        Assert.NotNull(found);
        Assert.Equal("1.0.0-alpha.2", found.Version);
        Assert.Equal("https://github.com/AustonZ/EQL-Wiki-Editor-Assistant/releases/tag/v1.0.0-alpha.2",
            found.Page.AbsoluteUri);
    }

    /// <summary>The control for the test above: the same feed says nothing to someone already on it.</summary>
    [Fact]
    public void NothingIsReportedWhenTheNewestIsTheOneRunning()
    {
        Assert.Null(ReleaseCheck.FindNewer(
            Feed(Release("v1.0.0-alpha.2", prerelease: true), Release("v1.0.0-alpha.1", prerelease: true)),
            "1.0.0-alpha.2"));
    }

    /// <summary>The newest wins wherever it sits in the list, and an older release is never offered.</summary>
    [Fact]
    public void TheNewestIsChosenAndAnOlderOneNeverOffered()
    {
        string feed = Feed(
            Release("v1.0.0-alpha.3", prerelease: true), Release("v1.0.0-beta.1", prerelease: true),
            Release("v0.9.0"), Release("v1.0.0-alpha.10", prerelease: true));

        Assert.Equal("1.0.0-beta.1", ReleaseCheck.FindNewer(feed, "1.0.0-alpha.2")?.Version);
        Assert.Null(ReleaseCheck.FindNewer(feed, "1.0.0-rc.1"));
    }

    /// <summary>Someone on a stable release has not asked to test anything, whether GitHub flags the release as a
    /// pre-release or only its tag says so.</summary>
    [Fact]
    public void AStableUserIsNotOfferedAPrerelease()
    {
        string feed = Feed(Release("v1.1.0-beta.1", prerelease: true), Release("v1.1.0-rc.1"), Release("v1.0.1"));

        Assert.Equal("1.0.1", ReleaseCheck.FindNewer(feed, "1.0.0")?.Version);
    }

    [Fact]
    public void ADraftNeverCounts()
    {
        Assert.Null(ReleaseCheck.FindNewer(Feed(Release("v1.0.0-alpha.2", prerelease: true, draft: true)),
            "1.0.0-alpha.1"));
    }

    /// <summary>The link opens a browser, so it has to be a GitHub page and nothing else.</summary>
    [Theory]
    [InlineData("http://github.com/x/")]
    [InlineData("https://example.com/x/")]
    [InlineData("javascript:alert(1)//")]
    public void APageThatIsNotGitHubIsIgnored(string url)
    {
        Assert.Null(ReleaseCheck.FindNewer(Feed(Release("v2.0.0", url: url)), "1.0.0"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"message":"API rate limit exceeded"}""")]
    [InlineData("""[{"tag_name":"nightly","prerelease":false,"draft":false,"html_url":"https://github.com/x"}]""")]
    public void AnythingUnreadableIsNothingToReport(string json)
    {
        Assert.Null(ReleaseCheck.FindNewer(json, "1.0.0-alpha.1"));
    }

    [Fact]
    public async Task AFailedRequestIsNothingToReportRatherThanAnError()
    {
        using var http = new HttpClient(new Respond(_ => throw new HttpRequestException("offline")));
        Assert.Null(await ReleaseCheck.FindNewerAsync(http, "1.0.0-alpha.1"));

        using var limited = new HttpClient(new Respond(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)));
        Assert.Null(await ReleaseCheck.FindNewerAsync(limited, "1.0.0-alpha.1"));
    }

    /// <summary>What goes out is the feed address and nothing else: no query beyond paging, no body.</summary>
    [Fact]
    public async Task TheRequestCarriesNothingButTheFeedAddress()
    {
        HttpRequestMessage? sent = null;
        using var http = new HttpClient(new Respond(request =>
        {
            sent = request;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Feed(Release("v1.0.0-alpha.2", prerelease: true))),
            };
        }));

        AvailableRelease? found = await ReleaseCheck.FindNewerAsync(http, "1.0.0-alpha.1");

        Assert.Equal("1.0.0-alpha.2", found?.Version);
        Assert.NotNull(sent);
        Assert.Equal(HttpMethod.Get, sent.Method);
        Assert.Equal(ReleaseCheck.Feed, sent.RequestUri);
        Assert.Null(sent.Content);
    }

    private sealed class Respond(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }
}
