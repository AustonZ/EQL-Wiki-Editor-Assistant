using System.Net;
using System.Text;
using EQLWikiAssistant.Wiki.MediaWiki;

namespace EQLWikiAssistant.Tests.Wiki;

/// <summary>
/// The API client against a stub handler. No network: these pin the protocol decisions that are easy to get wrong
/// and expensive to debug against a live wiki — that an API error arriving as HTTP 200 still fails, that an edit
/// carries the conflict guard, and that an edit without a login is refused rather than made anonymously.
/// </summary>
public class MediaWikiClientTests
{
    private static readonly Uri Endpoint = new("https://eqlwiki.com/api.php");

    [Fact]
    public async Task FetchPageAsync_ReadsTheWikitextAndTheConflictGuardValues()
    {
        var handler = new StubHandler(Json("""
            {"query":{"pages":[{"title":"Water Flask","revisions":[
              {"revid":4213,"timestamp":"2026-09-01T12:34:56Z","slots":{"main":{"content":"{{Itempage}}"}}}]}]}}
            """));
        using var client = new MediaWikiClient(new HttpClient(handler), Endpoint);

        WikiPage? page = await client.FetchPageAsync("Water Flask");

        Assert.NotNull(page);
        Assert.Equal("Water Flask", page.Title);
        Assert.Equal("{{Itempage}}", page.Wikitext);
        Assert.Equal(4213, page.RevisionId);
        Assert.Equal(DateTimeOffset.Parse("2026-09-01T12:34:56Z"), page.Timestamp);
    }

    /// <summary>A missing page means "this item has no wiki page yet", which the pipeline handles. Throwing would
    /// turn an ordinary branch into an error path.</summary>
    [Fact]
    public async Task FetchPageAsync_ReturnsNullForAMissingPage()
    {
        var handler = new StubHandler(Json("""{"query":{"pages":[{"title":"Nonexistent","missing":true}]}}"""));
        using var client = new MediaWikiClient(new HttpClient(handler), Endpoint);

        Assert.Null(await client.FetchPageAsync("Nonexistent"));
    }

    /// <summary>A redirect resolves to a different title, and an edit has to target the page the text came from.</summary>
    [Fact]
    public async Task FetchPageAsync_ReportsTheResolvedTitle()
    {
        var handler = new StubHandler(Json("""
            {"query":{"redirects":[{"from":"Water flask","to":"Water Flask"}],"pages":[{"title":"Water Flask","revisions":[
              {"revid":1,"timestamp":"2026-09-01T00:00:00Z","slots":{"main":{"content":"x"}}}]}]}}
            """));
        using var client = new MediaWikiClient(new HttpClient(handler), Endpoint);

        Assert.Equal("Water Flask", (await client.FetchPageAsync("Water flask"))!.Title);
    }

    /// <summary>MediaWiki reports errors with HTTP 200 and an "error" object, so the status code proves nothing.
    /// Unwrapping it centrally is what gives every call site the wiki's own error code.</summary>
    [Fact]
    public async Task AnApiErrorFailsEvenThoughTheStatusIs200()
    {
        var handler = new StubHandler(Json("""{"error":{"code":"readapidenied","info":"You need read permission."}}"""));
        using var client = new MediaWikiClient(new HttpClient(handler), Endpoint);

        MediaWikiException ex = await Assert.ThrowsAsync<MediaWikiException>(() => client.FetchPageAsync("X"));
        Assert.Equal("readapidenied", ex.Code);
        Assert.Contains("read permission", ex.Message);
    }

    /// <summary>Asking what a credential may do costs one read. Discovering it by attempting an edit costs a
    /// permanent revision in some page's history, which on this wiki an ordinary editor cannot delete.</summary>
    [Fact]
    public async Task GetUserInfoAsync_ReportsTheRightsTheBotPasswordGranted()
    {
        var handler = new StubHandler(Json("""
            {"query":{"userinfo":{"id":7,"name":"Editor","rights":["read","edit","createpage"]}}}
            """));
        using var client = new MediaWikiClient(new HttpClient(handler), Endpoint);

        UserInfo info = await client.GetUserInfoAsync();

        Assert.Equal("Editor", info.Name);
        Assert.False(info.IsAnonymous);
        Assert.True(info.CanEdit);
        Assert.True(info.CanCreate);
    }

    /// <summary>A bot password grants a subset of the account's rights, so "logged in" does not imply "may edit".</summary>
    [Fact]
    public async Task GetUserInfoAsync_ReportsAGrantThatCannotEdit()
    {
        var handler = new StubHandler(Json("""{"query":{"userinfo":{"id":7,"name":"Editor","rights":["read"]}}}"""));
        using var client = new MediaWikiClient(new HttpClient(handler), Endpoint);

        UserInfo info = await client.GetUserInfoAsync();

        Assert.False(info.CanEdit);
        Assert.False(info.CanCreate);
    }

    /// <summary>An unauthenticated session reports itself anonymous and names an IP address. Treating that as a
    /// successful login is how anonymous edits happen by accident.</summary>
    [Fact]
    public async Task GetUserInfoAsync_DetectsAnAnonymousSession()
    {
        var handler = new StubHandler(Json("""
            {"query":{"userinfo":{"id":0,"name":"203.0.113.7","anon":true,"rights":["read","edit"]}}}
            """));
        using var client = new MediaWikiClient(new HttpClient(handler), Endpoint);

        Assert.True((await client.GetUserInfoAsync()).IsAnonymous);
    }

    [Fact]
    public async Task EditAsync_RefusesWithoutALogin()
    {
        using var client = new MediaWikiClient(new HttpClient(new StubHandler(Json("{}"))), Endpoint);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.EditAsync("Sandbox", "text", "summary", DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task LoginAsync_FetchesATokenThenPostsTheCredentials()
    {
        var handler = new StubHandler(
            Json("""{"query":{"tokens":{"logintoken":"LOGIN+\\"}}}"""),
            Json("""{"login":{"result":"Success","lgusername":"Editor"}}"""));
        using var client = new MediaWikiClient(new HttpClient(handler), Endpoint);

        await client.LoginAsync(new BotCredentials("Editor@assistant", "secret"));

        Assert.True(client.IsLoggedIn);
        Assert.Contains("type=login", handler.Requests[0]);
        Assert.Contains("lgtoken=LOGIN%2B%5C", handler.Requests[1]);
        Assert.Contains("lgname=Editor%40assistant", handler.Requests[1]);
    }

    [Fact]
    public async Task LoginAsync_ReportsTheWikisOwnReasonOnFailure()
    {
        var handler = new StubHandler(
            Json("""{"query":{"tokens":{"logintoken":"t"}}}"""),
            Json("""{"login":{"result":"Failed","reason":"Incorrect username or password entered."}}"""));
        using var client = new MediaWikiClient(new HttpClient(handler), Endpoint);

        MediaWikiException ex = await Assert.ThrowsAsync<MediaWikiException>(
            () => client.LoginAsync(new BotCredentials("Editor@assistant", "wrong")));

        Assert.Equal("Failed", ex.Code);
        Assert.Contains("Incorrect username or password", ex.Message);
        Assert.False(client.IsLoggedIn);
    }

    /// <summary>Saving a new bot password must not leave the old session writing. Forgetting it makes the next write
    /// refuse before sending anything, which is what sends the app's write gate back to log in with the new one.</summary>
    [Fact]
    public async Task ForgetSession_MakesTheNextWriteLogInAgain()
    {
        var handler = new StubHandler(
            Json("""{"query":{"tokens":{"logintoken":"t"}}}"""),
            Json("""{"login":{"result":"Success"}}"""));
        using var client = new MediaWikiClient(new HttpClient(handler), Endpoint);
        await client.LoginAsync(new BotCredentials("Editor@assistant", "old"));

        client.ForgetSession();

        Assert.False(client.IsLoggedIn);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.EditAsync("Sandbox", "text", "summary", DateTimeOffset.UtcNow));
        Assert.Equal(2, handler.Requests.Count);
    }

    /// <summary>basetimestamp is the difference between a patch and a revert: without it, an edit saved by somebody
    /// else between our read and our write is silently overwritten.</summary>
    [Fact]
    public async Task EditAsync_SendsTheConflictGuardAndTheSessionAssertion()
    {
        var handler = new StubHandler(
            Json("""{"query":{"tokens":{"logintoken":"t"}}}"""),
            Json("""{"login":{"result":"Success"}}"""),
            Json("""{"query":{"tokens":{"csrftoken":"CSRF+\\"}}}"""),
            Json("""{"edit":{"result":"Success","newrevid":4300}}"""));
        using var client = new MediaWikiClient(new HttpClient(handler), Endpoint);
        await client.LoginAsync(new BotCredentials("Editor@assistant", "secret"));

        EditResult result = await client.EditAsync(
            "Sandbox", "new text", "why", DateTimeOffset.Parse("2026-09-01T12:34:56Z"));

        Assert.Equal(4300, result.NewRevisionId);
        Assert.False(result.NoChange);

        string edit = handler.Requests[^1];
        Assert.Contains("basetimestamp=2026-09-01T12%3A34%3A56Z", edit);
        Assert.Contains("assert=user", edit);
        Assert.Contains("token=CSRF%2B%5C", edit);
        Assert.Contains("summary=why", edit);
    }

    [Fact]
    public async Task EditAsync_DistinguishesANoChangeSave()
    {
        var handler = new StubHandler(
            Json("""{"query":{"tokens":{"logintoken":"t"}}}"""),
            Json("""{"login":{"result":"Success"}}"""),
            Json("""{"query":{"tokens":{"csrftoken":"c"}}}"""),
            Json("""{"edit":{"result":"Success","nochange":true}}"""));
        using var client = new MediaWikiClient(new HttpClient(handler), Endpoint);
        await client.LoginAsync(new BotCredentials("Editor@assistant", "secret"));

        EditResult result = await client.EditAsync("Sandbox", "same", "s", DateTimeOffset.UtcNow);

        Assert.True(result.NoChange);
        Assert.Null(result.NewRevisionId);
    }

    [Fact]
    public async Task EditAsync_SurfacesAnEditConflict()
    {
        var handler = new StubHandler(
            Json("""{"query":{"tokens":{"logintoken":"t"}}}"""),
            Json("""{"login":{"result":"Success"}}"""),
            Json("""{"query":{"tokens":{"csrftoken":"c"}}}"""),
            Json("""{"error":{"code":"editconflict","info":"Edit conflict."}}"""));
        using var client = new MediaWikiClient(new HttpClient(handler), Endpoint);
        await client.LoginAsync(new BotCredentials("Editor@assistant", "secret"));

        MediaWikiException ex = await Assert.ThrowsAsync<MediaWikiException>(
            () => client.EditAsync("Sandbox", "text", "s", DateTimeOffset.UtcNow));

        Assert.Equal("editconflict", ex.Code);
    }

    // --- a session that expired underneath us ------------------------------------------------------------

    /// <summary>
    /// **The session can die after the login gate has passed** (user, 2026-10-02: "not logged in" on a save, with
    /// the credential saved and a login already done). `assert=user` is what turns that into a loud failure instead
    /// of an anonymous edit; this is the tool answering that failure by logging in again and sending the edit once
    /// more, rather than handing the user an error they can only fix by restarting it.
    ///
    /// Safe to resend precisely because of what the refusal means: MediaWiki checks `assert` before it edits, so
    /// nothing was written.
    /// </summary>
    [Fact]
    public async Task AnEditRefusedBecauseTheSessionExpiredLogsInAgainAndSucceeds()
    {
        var handler = new StubHandler(
            Json("""{"query":{"tokens":{"logintoken":"t"}}}"""),
            Json("""{"login":{"result":"Success"}}"""),
            Json("""{"query":{"tokens":{"csrftoken":"first"}}}"""),
            Json("""{"error":{"code":"assertuserfailed","info":"You are not logged in."}}"""),
            // the renewal: a login token, the login itself, then a token belonging to the new session
            Json("""{"query":{"tokens":{"logintoken":"t2"}}}"""),
            Json("""{"login":{"result":"Success"}}"""),
            Json("""{"query":{"tokens":{"csrftoken":"second"}}}"""),
            Json("""{"edit":{"result":"Success","newrevid":4301}}"""));
        using var client = new MediaWikiClient(new HttpClient(handler), Endpoint);
        await client.LoginAsync(new BotCredentials("Editor@assistant", "secret"));

        int renewals = 0;
        client.ReestablishSession = async ct =>
        {
            renewals++;
            await client.LoginAsync(new BotCredentials("Editor@assistant", "secret"), ct);
            return true;
        };

        EditResult result = await client.EditAsync("Sandbox", "text", "why", DateTimeOffset.UtcNow);

        Assert.Equal(4301, result.NewRevisionId);
        Assert.Equal(1, renewals);
        Assert.True(client.IsLoggedIn);
        // The retry must carry the *new* session's token — a CSRF token belongs to the session that issued it.
        Assert.Contains("token=second", handler.Requests[^1]);
    }

    /// <summary>
    /// The negative control, and it is the half that matters even with no way to log in again: the client stops
    /// believing it has a session. The app's write gate short-circuits on <see cref="MediaWikiClient.IsLoggedIn"/>,
    /// so leaving it true is what made one expiry fail every subsequent write until the tool was restarted.
    /// </summary>
    [Fact]
    public async Task AnExpiredSessionIsForgottenEvenWhenItCannotBeRenewed()
    {
        var handler = new StubHandler(
            Json("""{"query":{"tokens":{"logintoken":"t"}}}"""),
            Json("""{"login":{"result":"Success"}}"""),
            Json("""{"query":{"tokens":{"csrftoken":"c"}}}"""),
            Json("""{"error":{"code":"assertuserfailed","info":"You are not logged in."}}"""));
        using var client = new MediaWikiClient(new HttpClient(handler), Endpoint);
        await client.LoginAsync(new BotCredentials("Editor@assistant", "secret"));
        Assert.True(client.IsLoggedIn);

        MediaWikiException ex = await Assert.ThrowsAsync<MediaWikiException>(
            () => client.EditAsync("Sandbox", "text", "s", DateTimeOffset.UtcNow));

        Assert.Equal("assertuserfailed", ex.Code);
        Assert.False(client.IsLoggedIn);
    }

    /// <summary>
    /// **Only a lost session is retried.** Every other refusal — a conflict, a protected page — means the request
    /// itself was wrong or beaten to it, and resending one of those could duplicate real work. The stub answers
    /// four requests and fails the test if a fifth is made, so a retry here cannot pass unnoticed.
    /// </summary>
    [Fact]
    public async Task AnEditConflictIsNotRetried()
    {
        var handler = new StubHandler(
            Json("""{"query":{"tokens":{"logintoken":"t"}}}"""),
            Json("""{"login":{"result":"Success"}}"""),
            Json("""{"query":{"tokens":{"csrftoken":"c"}}}"""),
            Json("""{"error":{"code":"editconflict","info":"Edit conflict."}}"""));
        using var client = new MediaWikiClient(new HttpClient(handler), Endpoint);
        await client.LoginAsync(new BotCredentials("Editor@assistant", "secret"));

        int renewals = 0;
        client.ReestablishSession = _ => { renewals++; return Task.FromResult(true); };

        await Assert.ThrowsAsync<MediaWikiException>(
            () => client.EditAsync("Sandbox", "text", "s", DateTimeOffset.UtcNow));

        Assert.Equal(0, renewals);
        Assert.Equal(4, handler.Requests.Count);
        Assert.True(client.IsLoggedIn);
    }

    /// <summary>
    /// The upload path builds its own request — multipart, a different API module — so it needs its own proof. Its
    /// form cannot be sent twice, which is why the retry rebuilds it.
    /// </summary>
    [Fact]
    public async Task AnUploadRefusedBecauseTheSessionExpiredLogsInAgainAndSucceeds()
    {
        var handler = new StubHandler(
            Json("""{"query":{"tokens":{"logintoken":"t"}}}"""),
            Json("""{"login":{"result":"Success"}}"""),
            Json("""{"query":{"tokens":{"csrftoken":"first"}}}"""),
            Json("""{"error":{"code":"assertuserfailed","info":"You are not logged in."}}"""),
            Json("""{"query":{"tokens":{"logintoken":"t2"}}}"""),
            Json("""{"login":{"result":"Success"}}"""),
            Json("""{"query":{"tokens":{"csrftoken":"second"}}}"""),
            Json("""{"upload":{"result":"Success","imageinfo":{"url":"https://eqlwiki.com/images/Item_5797.png"}}}"""));
        using var client = new MediaWikiClient(new HttpClient(handler), Endpoint);
        await client.LoginAsync(new BotCredentials("Editor@assistant", "secret"));
        client.ReestablishSession = async ct =>
        {
            await client.LoginAsync(new BotCredentials("Editor@assistant", "secret"), ct);
            return true;
        };

        UploadResult result = await client.UploadFileAsync(
            "Item_5797.png", [1, 2, 3, 4], "An item icon.", "Uploading an item icon");

        Assert.Equal("Item_5797.png", result.FileName);
        Assert.Equal("https://eqlwiki.com/images/Item_5797.png", result.Url);
        Assert.Contains("second", handler.Requests[^1], StringComparison.Ordinal);
    }

    /// <summary>
    /// A renewal that fails leaves the original refusal in place rather than reporting the login's problem over it:
    /// what the caller attempted was the write. The next write asks the gate, which has the better message, because
    /// the session has been forgotten by then.
    /// </summary>
    [Fact]
    public async Task AFailedRenewalLeavesTheOriginalRefusal()
    {
        var handler = new StubHandler(
            Json("""{"query":{"tokens":{"logintoken":"t"}}}"""),
            Json("""{"login":{"result":"Success"}}"""),
            Json("""{"query":{"tokens":{"csrftoken":"c"}}}"""),
            Json("""{"error":{"code":"assertuserfailed","info":"You are not logged in."}}"""));
        using var client = new MediaWikiClient(new HttpClient(handler), Endpoint);
        await client.LoginAsync(new BotCredentials("Editor@assistant", "secret"));
        client.ReestablishSession = _ => Task.FromResult(false);

        MediaWikiException ex = await Assert.ThrowsAsync<MediaWikiException>(
            () => client.EditAsync("Sandbox", "text", "s", DateTimeOffset.UtcNow));

        Assert.Equal("assertuserfailed", ex.Code);
        Assert.False(client.IsLoggedIn);
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    /// <summary>Returns canned responses in order and records what was asked for — the query string for a GET, the
    /// form body for a POST, so an assertion can look at either without caring which verb was used.</summary>
    // --- the wiki being unavailable ---------------------------------------------------------------------

    /// <summary>
    /// A transport failure is <see cref="WikiUnavailableException"/>, not a page-level error — the distinction the
    /// pipeline uses to decide between aborting the frame and failing one window (user, 2026-09-29).
    /// </summary>
    [Fact]
    public async Task ATransportFailureIsReportedAsTheWikiBeingUnavailable()
    {
        using var client = new MediaWikiClient(
            new HttpClient(new ThrowingHandler(new HttpRequestException("No such host is known."))), Endpoint);

        WikiUnavailableException ex =
            await Assert.ThrowsAsync<WikiUnavailableException>(() => client.FetchPageAsync("Water Flask"));

        Assert.Contains("eqlwiki.com", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>An HTTP error from the server is the same thing — a 503 is exactly the outage this guards.</summary>
    [Fact]
    public async Task AServerErrorIsReportedAsTheWikiBeingUnavailable()
    {
        var handler = new StubHandler(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));
        using var client = new MediaWikiClient(new HttpClient(handler), Endpoint);

        await Assert.ThrowsAsync<WikiUnavailableException>(() => client.FetchPageAsync("Water Flask"));
    }

    /// <summary>MediaWiki's own maintenance mode describes the site, not the request.</summary>
    [Fact]
    public async Task ReadOnlyModeIsReportedAsTheWikiBeingUnavailable()
    {
        var handler = new StubHandler(Json("""{"error":{"code":"readonly","info":"The wiki is in read-only mode."}}"""));
        using var client = new MediaWikiClient(new HttpClient(handler), Endpoint);

        await Assert.ThrowsAsync<WikiUnavailableException>(() => client.FetchPageAsync("Water Flask"));
    }

    /// <summary>An error about the request stays a plain <see cref="MediaWikiException"/>, so the pipeline still
    /// treats it as one window's problem rather than an outage.</summary>
    [Fact]
    public async Task AnErrorAboutTheRequestStaysAnOrdinaryWikiError()
    {
        var handler = new StubHandler(Json("""{"error":{"code":"protectedpage","info":"That page is protected."}}"""));
        using var client = new MediaWikiClient(new HttpClient(handler), Endpoint);

        MediaWikiException ex =
            await Assert.ThrowsAsync<MediaWikiException>(() => client.FetchPageAsync("Water Flask"));

        Assert.Equal("protectedpage", ex.Code);
    }

    /// <summary>
    /// **A cancellation the caller asked for is not an outage.** `HttpClient` reports its own timeout as a
    /// `TaskCanceledException` too, so the two are indistinguishable by type — the token is what tells them apart,
    /// and a user who cancels must not be told the wiki is down.
    /// </summary>
    [Fact]
    public async Task ACancellationTheCallerAskedForIsNotAnOutage()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        using var client = new MediaWikiClient(
            new HttpClient(new ThrowingHandler(new TaskCanceledException())), Endpoint);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.FetchPageAsync("Water Flask", cancelled.Token));
    }

    /// <summary>Whereas the same exception with nobody having cancelled is `HttpClient`'s timeout — an outage.</summary>
    [Fact]
    public async Task ATimeoutWithNoCancellationIsAnOutage()
    {
        using var client = new MediaWikiClient(
            new HttpClient(new ThrowingHandler(new TaskCanceledException())), Endpoint);

        await Assert.ThrowsAsync<WikiUnavailableException>(() => client.FetchPageAsync("Water Flask"));
    }

    private sealed class ThrowingHandler(Exception thrown) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) => throw thrown;
    }

    private sealed class StubHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private int _next;

        public List<string> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.Content is null
                ? request.RequestUri!.Query
                : await request.Content.ReadAsStringAsync(cancellationToken));

            return _next < responses.Length
                ? responses[_next++]
                : throw new InvalidOperationException($"The client made {Requests.Count} requests; only {responses.Length} were stubbed.");
        }
    }
}
