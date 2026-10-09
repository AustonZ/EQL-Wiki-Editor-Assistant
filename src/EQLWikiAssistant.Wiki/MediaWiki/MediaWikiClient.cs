using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace EQLWikiAssistant.Wiki.MediaWiki;

/// <summary>
/// Talks to a MediaWiki <c>api.php</c>. Targets eqlwiki.com (MediaWiki 1.45.3, <c>api.php</c> at the site root —
/// there is no script path, so the endpoint is <c>https://eqlwiki.com/api.php</c>).
///
/// **Cookies are mandatory and easy to get wrong.** MediaWiki's login is session-based: the login token, the
/// session it belongs to and the later CSRF token are all tied together by cookies, so an <see cref="HttpClient"/>
/// built without a <see cref="CookieContainer"/> fails at the second step with a <c>badtoken</c> that looks like a
/// token-handling bug. <see cref="Create"/> builds a correctly configured client; the constructor takes one so
/// tests can substitute a fake handler and assert on request counts.
///
/// **Reads are anonymous, writes are not.** Fetching wikitext needs no login, which keeps the common path — check
/// an item, find it already correct — free of credentials entirely. Only <see cref="EditAsync"/> requires
/// <see cref="LoginAsync"/> to have run.
/// </summary>
public sealed class MediaWikiClient : IMediaWikiClient, IDisposable
{
    /// <summary>MediaWiki's API etiquette asks for a descriptive User-Agent with a contact route; a generic or
    /// absent one is grounds for being blocked, and being blocked mid-session is a confusing failure. This bare form
    /// is what the dev tools send; the app sends <see cref="UserAgentFor"/> its version, so the wiki's admins can
    /// tell releases apart.</summary>
    public const string UserAgent = "EQLWikiEditorAssistant (" + ProjectUrl + ")";

    private const string ProjectUrl = "https://github.com/AustonZ/EQLWiki-EditorAssistant";

    /// <summary>The User-Agent naming a release, e.g. <c>EQLWikiEditorAssistant/1.0.0-alpha.1 (https://...)</c>.</summary>
    public static string UserAgentFor(string version) => $"EQLWikiEditorAssistant/{version} ({ProjectUrl})";

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Uri _endpoint;
    private string? _csrfToken;

    public MediaWikiClient(HttpClient http, Uri endpoint, bool ownsHttpClient = false)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _ownsHttp = ownsHttpClient;

        if (!_http.DefaultRequestHeaders.Contains("User-Agent"))
            _http.DefaultRequestHeaders.Add("User-Agent", UserAgent);
    }

    /// <summary>Builds a client with the cookie container and user agent MediaWiki needs.</summary>
    public static MediaWikiClient Create(Uri endpoint, string userAgent = UserAgent)
    {
        var handler = new HttpClientHandler { CookieContainer = new CookieContainer(), UseCookies = true };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.Add("User-Agent", userAgent);
        return new MediaWikiClient(http, endpoint, ownsHttpClient: true);
    }

    /// <summary>
    /// True once <see cref="LoginAsync"/> has succeeded in this session.
    ///
    /// **It records what this process did, not what the wiki still believes**, which is the distinction that cost
    /// the user a failed save (2026-10-02). A MediaWiki session expires on its own schedule and a cookie can be
    /// dropped, so a client that logged in an hour ago can be anonymous now with nothing locally having changed.
    /// Hence <see cref="ReestablishSession"/>, and hence this flag being cleared the moment the wiki says the
    /// session is gone rather than only when a login fails.
    /// </summary>
    public bool IsLoggedIn { get; private set; }

    /// <summary>
    /// Logs in again when the wiki says this session is no longer authenticated. Returns true if a fresh session
    /// was established. Left null, a lost session is simply reported, which is what it did before.
    ///
    /// **It lives here because the session does.** Every write already funnels through this class, so a repair
    /// attached here covers both commits, the creation, the upload and <c>WikiSpike</c> alike — where putting it in
    /// the pipeline would mean four copies of the same recovery, which is the duplication this codebase has paid
    /// for four times (the ledger fingerprint, the login gate, the flag dialect, the lore join).
    ///
    /// The host supplies it rather than this class reading Credential Manager itself: which credential to use, and
    /// whether to use one at all, is the composition root's decision.
    /// </summary>
    public Func<CancellationToken, Task<bool>>? ReestablishSession { get; set; }

    public async Task<WikiPage?> FetchPageAsync(string title, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        using JsonDocument response = await GetAsync(new Dictionary<string, string>
        {
            ["action"] = "query",
            ["prop"] = "revisions",
            ["rvprop"] = "content|ids|timestamp",
            ["rvslots"] = "main",
            ["rvlimit"] = "1",
            ["titles"] = title,
            ["redirects"] = "1",
        }, cancellationToken).ConfigureAwait(false);

        if (!response.RootElement.TryGetProperty("query", out JsonElement query) ||
            !query.TryGetProperty("pages", out JsonElement pages) ||
            pages.GetArrayLength() == 0)
            return null;

        JsonElement page = pages[0];
        // "missing" is how the API says the page does not exist. That is a normal answer for this tool — the item
        // simply has no page yet — so it maps to null rather than to an exception.
        if (page.TryGetProperty("missing", out _)) return null;
        if (!page.TryGetProperty("revisions", out JsonElement revisions) || revisions.GetArrayLength() == 0) return null;

        JsonElement revision = revisions[0];
        string text = revision.GetProperty("slots").GetProperty("main").GetProperty("content").GetString() ?? "";

        return new WikiPage(
            // The resolved title, which differs from the requested one when a redirect was followed — an edit has
            // to target the page the text actually came from, not the name we asked for.
            Title: page.GetProperty("title").GetString() ?? title,
            Wikitext: text,
            RevisionId: revision.GetProperty("revid").GetInt64(),
            Timestamp: revision.GetProperty("timestamp").GetDateTimeOffset());
    }

    public async Task<IReadOnlySet<string>> ExistingTitlesAsync(
        IReadOnlyList<string> titles, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(titles);

        var found = new HashSet<string>(StringComparer.Ordinal);
        if (titles.Count == 0) return found;

        // The API caps `titles` at 50 per request for an ordinary user, so ask in batches rather than letting a
        // long list come back as an error.
        foreach (string[] batch in titles.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.Ordinal)
                     .Chunk(50))
        {
            using JsonDocument response = await GetAsync(new Dictionary<string, string>
            {
                ["action"] = "query",
                ["titles"] = string.Join("|", batch),
            }, cancellationToken).ConfigureAwait(false);

            if (!response.RootElement.TryGetProperty("query", out JsonElement query) ||
                !query.TryGetProperty("pages", out JsonElement pages))
                continue;

            foreach (JsonElement page in pages.EnumerateArray())
            {
                // "missing" is a page that does not exist, "invalid" a title the wiki will not accept at all.
                // Neither is an error here: both mean there is nothing to link to.
                if (page.TryGetProperty("missing", out _) || page.TryGetProperty("invalid", out _)) continue;
                if (page.GetProperty("title").GetString() is { } title) found.Add(title);
            }
        }

        return found;
    }

    public async Task<RenderedPage> RenderAsync(
        string title, string wikitext, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(wikitext);

        // POSTed because a whole item page is far too long for a query string. It is still a read: action=parse saves
        // nothing, and with `text` it renders what it is given rather than any stored revision.
        using JsonDocument response = await PostAsync(new Dictionary<string, string>
        {
            ["action"] = "parse",
            ["title"] = title,
            ["text"] = wikitext,
            ["contentmodel"] = "wikitext",
            ["prop"] = "text|headhtml|categorieshtml",
            ["disablelimitreport"] = "1",
            ["disableeditsection"] = "1",
        }, cancellationToken).ConfigureAwait(false);

        JsonElement parse = response.RootElement.GetProperty("parse");
        return new RenderedPage(
            Title: parse.TryGetProperty("title", out JsonElement t) ? t.GetString() ?? title : title,
            HeadHtml: parse.TryGetProperty("headhtml", out JsonElement head) ? head.GetString() ?? "" : "",
            BodyHtml: parse.TryGetProperty("text", out JsonElement text) ? text.GetString() ?? "" : "",
            CategoriesHtml: parse.TryGetProperty("categorieshtml", out JsonElement cats) ? cats.GetString() ?? "" : "",
            Stylesheets: await SkinStylesheetsAsync(cancellationToken).ConfigureAwait(false));
    }

    private IReadOnlyList<string>? _skinStylesheets;

    /// <summary>
    /// The skin's stylesheets, which <c>action=parse</c> does not link: read once per session from a page view of
    /// <c>Special:BlankPage</c>, the smallest page the wiki serves with its full skin.
    ///
    /// **A preview without them is still a preview**, just an unstyled one, so a failure here is swallowed and simply
    /// not remembered — the next preview asks again. An unreachable wiki is the exception, and is reported by the parse
    /// request that follows rather than here.
    /// </summary>
    private async Task<IReadOnlyList<string>> SkinStylesheetsAsync(CancellationToken cancellationToken)
    {
        if (_skinStylesheets is not null) return _skinStylesheets;

        try
        {
            using HttpResponseMessage response = await _http
                .GetAsync(new Uri(_endpoint, "index.php?title=Special:BlankPage"), cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return [];

            string html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return _skinStylesheets = RenderedPage.SkinStylesheets(html);
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException) &&
                                   !cancellationToken.IsCancellationRequested)
        {
            return [];
        }
    }

    /// <summary>Who the wiki thinks this session is, and which rights it grants. Read-only, so it verifies a
    /// credential without leaving a revision anywhere — see <see cref="UserInfo"/> for why that matters.</summary>
    public async Task<UserInfo> GetUserInfoAsync(CancellationToken cancellationToken = default)
    {
        using JsonDocument response = await GetAsync(new Dictionary<string, string>
        {
            ["action"] = "query",
            ["meta"] = "userinfo",
            ["uiprop"] = "rights",
        }, cancellationToken).ConfigureAwait(false);

        JsonElement info = response.RootElement.GetProperty("query").GetProperty("userinfo");
        var rights = new List<string>();
        if (info.TryGetProperty("rights", out JsonElement list))
            foreach (JsonElement right in list.EnumerateArray())
                if (right.GetString() is { } value) rights.Add(value);

        return new UserInfo(
            Name: info.TryGetProperty("name", out JsonElement name) ? name.GetString() ?? "" : "",
            // The API reports an unauthenticated session by flagging it anon; the "name" is then an IP address.
            IsAnonymous: info.TryGetProperty("anon", out _),
            Rights: rights);
    }

    /// <summary>Every page title that transcludes a template, following continuations. Not on
    /// <see cref="IMediaWikiClient"/> because the pipeline never needs it: it exists so the wikitext layer can be
    /// validated against the real corpus rather than against a handful of pages somebody chose.</summary>
    public async Task<IReadOnlyList<string>> ListTransclusionsAsync(
        string templateTitle, int namespaceId = 0, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templateTitle);

        var titles = new List<string>();
        string? continuation = null;

        do
        {
            var parameters = new Dictionary<string, string>
            {
                ["action"] = "query",
                ["list"] = "embeddedin",
                ["eititle"] = templateTitle,
                ["einamespace"] = namespaceId.ToString(),
                ["eilimit"] = "500",
            };
            if (continuation is not null) parameters["eicontinue"] = continuation;

            using JsonDocument response = await GetAsync(parameters, cancellationToken).ConfigureAwait(false);
            foreach (JsonElement entry in response.RootElement.GetProperty("query").GetProperty("embeddedin").EnumerateArray())
                if (entry.GetProperty("title").GetString() is { } title) titles.Add(title);

            continuation = response.RootElement.TryGetProperty("continue", out JsonElement cont) &&
                cont.TryGetProperty("eicontinue", out JsonElement ei)
                ? ei.GetString()
                : null;
        } while (continuation is not null);

        return titles;
    }

    public async Task LoginAsync(BotCredentials credentials, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);

        string loginToken = await FetchTokenAsync("login", cancellationToken).ConfigureAwait(false);

        using JsonDocument response = await PostAsync(new Dictionary<string, string>
        {
            ["action"] = "login",
            ["lgname"] = credentials.UserName,
            ["lgpassword"] = credentials.Password,
            ["lgtoken"] = loginToken,
        }, cancellationToken).ConfigureAwait(false);

        JsonElement login = response.RootElement.GetProperty("login");
        string result = login.GetProperty("result").GetString() ?? "Unknown";
        if (!string.Equals(result, "Success", StringComparison.Ordinal))
        {
            string reason = login.TryGetProperty("reason", out JsonElement r) ? r.ToString() : result;
            throw new MediaWikiException(result, $"Login failed: {reason}");
        }

        IsLoggedIn = true;
        _csrfToken = null; // the anonymous CSRF token is not valid for the logged-in session
    }

    /// <summary>
    /// Appended, in brackets, to every edit summary and upload comment this client sends — the app's name and version,
    /// e.g. <c>Editor Assistant 1.0.0-alpha.1</c> (user, 2026-10-08). With more than one person using the
    /// Assistant, it is what lets anyone find every edit a given release made — the way to clean up after a release
    /// that shipped a bad rule — and tells other editors where an edit came from. Null sends summaries as given.
    /// </summary>
    public string? SummaryTag { get; set; }

    /// <summary>MediaWiki cuts a summary at 500 characters; the tag is the part that must survive, so the summary is
    /// what gets shortened.</summary>
    private const int SummaryCharacterLimit = 500;

    private string Tagged(string summary)
    {
        if (string.IsNullOrWhiteSpace(SummaryTag)) return summary;

        string tag = $"({SummaryTag.Trim()})";
        string text = summary.TrimEnd();
        if (text.Length == 0) return tag;

        int room = SummaryCharacterLimit - tag.Length - 1;
        if (text.Length > room) text = text[..Math.Max(0, room - 1)].TrimEnd() + "…";
        return $"{text} {tag}";
    }

    public Task<EditResult> EditAsync(
        string title,
        string newWikitext,
        string summary,
        DateTimeOffset baseTimestamp,
        CancellationToken cancellationToken = default) =>
        SaveAsync(title, newWikitext, summary, new Dictionary<string, string>
        {
            // basetimestamp is the edit-conflict guard: if somebody else saved between our fetch and this write,
            // the API refuses with "editconflict" instead of silently reverting them.
            ["basetimestamp"] = baseTimestamp.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            // Creating a page is a separate request with separate review requirements, so doing it by accident
            // here is worse than failing.
            ["nocreate"] = "1",
        }, cancellationToken);

    public Task<EditResult> CreatePageAsync(
        string title,
        string wikitext,
        string summary,
        CancellationToken cancellationToken = default) =>
        SaveAsync(title, wikitext, summary, new Dictionary<string, string>
        {
            // The mirror image of the pair above, and the reason creating is its own method: there is no base
            // revision for basetimestamp to guard, so the guard that matters is "fail if this page now exists".
            // Without it, two captures of the same new item — or somebody else creating the page in between —
            // would overwrite a page this tool never read.
            ["createonly"] = "1",
        }, cancellationToken);

    /// <summary>
    /// The one write path. Everything both callers share lives here — the session check, the CSRF token,
    /// <c>assert=user</c> and reading the wiki's answer — so the <em>only</em> difference between editing and
    /// creating is the guard each one passes in, which is the difference worth being able to see at a glance.
    /// </summary>
    private async Task<EditResult> SaveAsync(
        string title,
        string wikitext,
        string summary,
        Dictionary<string, string> guard,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(wikitext);

        if (!IsLoggedIn)
            throw new InvalidOperationException("Log in before editing — an anonymous edit would be attributed to an IP address.");

        _csrfToken ??= await FetchTokenAsync("csrf", cancellationToken).ConfigureAwait(false);

        var parameters = new Dictionary<string, string>
        {
            ["action"] = "edit",
            ["title"] = title,
            ["text"] = wikitext,
            ["summary"] = Tagged(summary),
            ["token"] = _csrfToken,
            // assert=user turns a silently-expired session into a loud failure rather than an anonymous edit.
            ["assert"] = "user",
        };
        foreach ((string key, string value) in guard) parameters[key] = value;

        using JsonDocument response = await PostRenewingALostSessionAsync(parameters, cancellationToken)
            .ConfigureAwait(false);
        JsonElement edit = response.RootElement.GetProperty("edit");

        string result = edit.TryGetProperty("result", out JsonElement r2) ? r2.GetString() ?? "" : "";
        if (!string.Equals(result, "Success", StringComparison.Ordinal))
            throw new MediaWikiException(result, $"Edit of '{title}' was not accepted: {edit}");

        bool noChange = edit.TryGetProperty("nochange", out _);
        long? newRevision = edit.TryGetProperty("newrevid", out JsonElement rev) ? rev.GetInt64() : null;
        return new EditResult(title, newRevision, noChange);
    }
    /// <summary>
    /// Uploads a new file, and refuses rather than replaces when the wiki already has one by that name.
    ///
    /// **It does not go through <see cref="SaveAsync"/>, and the reason is not shared-code squeamishness**: this is a
    /// different API module (<c>action=upload</c>) sending multipart form data rather than a URL-encoded body, with
    /// its own result shape. What it does share is the discipline — the session check, the CSRF token,
    /// <c>assert=user</c>, and a guard in the request rather than a check in this process.
    ///
    /// **That guard is `ignorewarnings` left off.** MediaWiki answers an upload over an existing file with a warning
    /// rather than an error, and `ignorewarnings=1` is what turns it into an overwrite — so omitting it makes
    /// "replace somebody's file" unreachable from this tool rather than merely unintended. This matters more here
    /// than anywhere else in the codebase: only an admin on this wiki can delete a file, so a wrong overwrite takes
    /// the original with it and needs somebody else to put right. The warning comes back as an exception naming what
    /// the wiki objected to.
    /// </summary>
    /// <param name="fileName">The target name *without* the `File:` prefix — e.g. <c>Item_5797.png</c>.</param>
    /// <param name="description">The initial wikitext of the file's own page.</param>
    public async Task<UploadResult> UploadFileAsync(
        string fileName,
        byte[] content,
        string description,
        string comment,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(comment);
        if (content.Length == 0)
            throw new ArgumentException("Refusing to upload an empty file.", nameof(content));

        if (!IsLoggedIn)
            throw new InvalidOperationException(
                "Log in before uploading — an anonymous upload would be attributed to an IP address.");

        _csrfToken ??= await FetchTokenAsync("csrf", cancellationToken).ConfigureAwait(false);

        // Built per attempt rather than once: an HttpContent cannot be sent twice, so a retry after the session is
        // renewed needs its own form — and that form has to carry the new session's token anyway.
        async Task<JsonDocument> SendAsync()
        {
            using var form = new MultipartFormDataContent
            {
                { new StringContent("upload"), "action" },
                { new StringContent("json"), "format" },
                { new StringContent("2"), "formatversion" },   // see AddFormat for why
                { new StringContent(fileName), "filename" },
                { new StringContent(Tagged(comment)), "comment" },
                { new StringContent(description), "text" },
                { new StringContent(_csrfToken!), "token" },
                { new StringContent("user"), "assert" },
            };

            var file = new ByteArrayContent(content);
            file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            form.Add(file, "file", fileName);

            try
            {
                using HttpResponseMessage message = await _http
                    .PostAsync(_endpoint, form, cancellationToken).ConfigureAwait(false);
                return await ReadAsync(message, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsUnreachable(ex, cancellationToken))
            {
                throw Unavailable(ex);
            }
        }

        JsonDocument response;
        try
        {
            response = await SendAsync().ConfigureAwait(false);
        }
        catch (MediaWikiException ex) when (IsSessionLost(ex.Code))
        {
            if (!await TryReestablishSessionAsync(cancellationToken).ConfigureAwait(false)) throw;
            response = await SendAsync().ConfigureAwait(false);
        }

        using (response)
        {
            JsonElement upload = response.RootElement.GetProperty("upload");
            string result = upload.TryGetProperty("result", out JsonElement r) ? r.GetString() ?? "" : "";

            if (!string.Equals(result, "Success", StringComparison.Ordinal))
            {
                // A refused upload is nearly always "the file already exists", which is a real answer rather than a
                // fault: the caller checks first, and this is the race. Named so the user is told which it was.
                string warnings = upload.TryGetProperty("warnings", out JsonElement w) ? w.ToString() : upload.ToString();
                throw new MediaWikiException(
                    result.Length == 0 ? "uploadfailed" : result,
                    $"Upload of '{fileName}' was not accepted: {warnings}");
            }

            string? url = upload.TryGetProperty("imageinfo", out JsonElement info)
                          && info.TryGetProperty("url", out JsonElement u)
                ? u.GetString()
                : null;

            return new UploadResult(fileName, url);
        }
    }

    /// <summary>
    /// Posts a write, and — if the wiki answers that this session is not authenticated — logs in again and sends it
    /// exactly once more.
    ///
    /// **Retrying a write is only safe because of what these particular codes mean.** MediaWiki checks
    /// <c>assert</c> and the CSRF token *before* performing the action, so a request refused for either reason
    /// changed nothing: there is no half-done edit to resend. Any other failure — an edit conflict, a protected
    /// page, a refused upload — is passed straight out, because a retry there could duplicate real work.
    /// </summary>
    private async Task<JsonDocument> PostRenewingALostSessionAsync(
        Dictionary<string, string> parameters, CancellationToken cancellationToken)
    {
        try
        {
            return await PostAsync(parameters, cancellationToken).ConfigureAwait(false);
        }
        catch (MediaWikiException ex) when (IsSessionLost(ex.Code))
        {
            if (!await TryReestablishSessionAsync(cancellationToken).ConfigureAwait(false)) throw;
            parameters["token"] = _csrfToken!;
            return await PostAsync(parameters, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Stops treating this session as logged in, so the next write logs in again first. For when the credential
    /// changes: a session opened with the old bot password must not carry on writing after the user saves a new one.
    /// Nothing is sent — the next login simply replaces the session.
    /// </summary>
    public void ForgetSession()
    {
        IsLoggedIn = false;
        _csrfToken = null;
    }

    /// <summary>
    /// Drops the session this client believed in and asks the host for a new one.
    ///
    /// **Forgetting comes first, and happens even when there is no way to log in again.** The app's write gate
    /// short-circuits on <see cref="IsLoggedIn"/>, so leaving it true after the wiki has disowned the session means
    /// the *next* write starts from the same dead state and fails the same way — which is how a single expiry
    /// turned into "not logged in" on every attempt until the tool was restarted.
    /// </summary>
    private async Task<bool> TryReestablishSessionAsync(CancellationToken cancellationToken)
    {
        ForgetSession();

        if (ReestablishSession is null) return false;
        if (!await ReestablishSession(cancellationToken).ConfigureAwait(false) || !IsLoggedIn) return false;

        // A CSRF token belongs to the session that issued it, so the new session needs its own.
        _csrfToken = await FetchTokenAsync("csrf", cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Whether this error code means "the wiki no longer considers this session logged in" rather than something
    /// about the request.
    ///
    /// <c>assertuserfailed</c> is the one that actually happens: every write sends <c>assert=user</c> precisely so
    /// an expired session fails loudly instead of editing as an IP address, and this is that failure arriving.
    /// <c>badtoken</c> is the same event seen through the CSRF token, which is session-bound and so stops being
    /// valid at the same moment. The remaining two are how other modules phrase it.
    /// </summary>
    private static bool IsSessionLost(string code) =>
        code is "assertuserfailed" or "assertnameduserfailed" or "badtoken" or "notloggedin" or "mustbeloggedin";

    private async Task<string> FetchTokenAsync(string type, CancellationToken cancellationToken)
    {
        using JsonDocument response = await GetAsync(new Dictionary<string, string>
        {
            ["action"] = "query",
            ["meta"] = "tokens",
            ["type"] = type,
        }, cancellationToken).ConfigureAwait(false);

        JsonElement tokens = response.RootElement.GetProperty("query").GetProperty("tokens");
        return tokens.GetProperty($"{type}token").GetString()
            ?? throw new MediaWikiException("notoken", $"The wiki returned no {type} token.");
    }

    private async Task<JsonDocument> GetAsync(Dictionary<string, string> parameters, CancellationToken cancellationToken)
    {
        AddFormat(parameters);
        var uri = new Uri($"{_endpoint}?{await new FormUrlEncodedContent(parameters).ReadAsStringAsync(cancellationToken).ConfigureAwait(false)}");

        try
        {
            using HttpResponseMessage response = await _http.GetAsync(uri, cancellationToken).ConfigureAwait(false);
            return await ReadAsync(response, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsUnreachable(ex, cancellationToken))
        {
            throw Unavailable(ex);
        }
    }

    private async Task<JsonDocument> PostAsync(Dictionary<string, string> parameters, CancellationToken cancellationToken)
    {
        AddFormat(parameters);

        try
        {
            using HttpResponseMessage response = await _http
                .PostAsync(_endpoint, new FormUrlEncodedContent(parameters), cancellationToken).ConfigureAwait(false);
            return await ReadAsync(response, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsUnreachable(ex, cancellationToken))
        {
            throw Unavailable(ex);
        }
    }

    /// <summary>
    /// Whether this failure means the wiki is unavailable rather than the request being wrong.
    ///
    /// **A cancellation the caller asked for is never one of these.** `HttpClient` reports its own timeout as a
    /// <see cref="TaskCanceledException"/> too, which is indistinguishable by type — the token is what tells them
    /// apart, so a user who cancels does not get told the wiki is down.
    /// </summary>
    private static bool IsUnreachable(Exception ex, CancellationToken cancellationToken) => ex switch
    {
        OperationCanceledException => !cancellationToken.IsCancellationRequested,
        HttpRequestException => true,
        MediaWikiException wiki => WikiUnavailableException.IsUnavailableCode(wiki.Code),
        _ => false,
    };

    private WikiUnavailableException Unavailable(Exception ex) => new(
        ex is MediaWikiException wiki
            ? $"The wiki is unavailable ({wiki.Code}): {wiki.Message}"
            : $"{_endpoint.Host} could not be reached: {ex.Message}",
        ex);

    private static void AddFormat(Dictionary<string, string> parameters)
    {
        parameters["format"] = "json";
        // formatversion=2 is what makes "pages" an array rather than an object keyed by page id, and gives plain
        // booleans instead of the legacy empty-string markers.
        parameters["formatversion"] = "2";
    }

    private static async Task<JsonDocument> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        response.EnsureSuccessStatusCode();

        JsonDocument document = await response.Content
            .ReadFromJsonAsync<JsonDocument>(cancellationToken).ConfigureAwait(false)
            ?? throw new MediaWikiException("emptyresponse", "The wiki returned an empty response.");

        // A MediaWiki API error arrives as HTTP 200 with an "error" object, so the status code alone proves
        // nothing. Unwrapping it here means every call site sees a typed failure with the wiki's own code.
        if (document.RootElement.TryGetProperty("error", out JsonElement error))
        {
            string code = error.TryGetProperty("code", out JsonElement c) ? c.GetString() ?? "unknown" : "unknown";
            string info = error.TryGetProperty("info", out JsonElement i) ? i.GetString() ?? "" : error.ToString();
            document.Dispose();
            throw new MediaWikiException(code, info);
        }

        return document;
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
