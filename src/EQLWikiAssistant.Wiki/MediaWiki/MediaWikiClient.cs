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
    /// absent one is grounds for being blocked, and being blocked mid-session is a confusing failure.</summary>
    public const string UserAgent = "EQLWikiAssistant/1.0 (https://github.com/; EverQuest Legends Wiki editor assistant)";

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
    public static MediaWikiClient Create(Uri endpoint)
    {
        var handler = new HttpClientHandler { CookieContainer = new CookieContainer(), UseCookies = true };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        return new MediaWikiClient(http, endpoint, ownsHttpClient: true);
    }

    /// <summary>True once <see cref="LoginAsync"/> has succeeded in this session.</summary>
    public bool IsLoggedIn { get; private set; }

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
            ["summary"] = summary,
            ["token"] = _csrfToken,
            // assert=user turns a silently-expired session into a loud failure rather than an anonymous edit.
            ["assert"] = "user",
        };
        foreach ((string key, string value) in guard) parameters[key] = value;

        using JsonDocument response = await PostAsync(parameters, cancellationToken).ConfigureAwait(false);
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
    /// than anywhere else in the codebase: an ordinary editor on this wiki cannot delete a file, so a wrong overwrite
    /// is permanent and takes the original with it. The warning comes back as an exception naming what the wiki
    /// objected to.
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

        using var form = new MultipartFormDataContent
        {
            { new StringContent("upload"), "action" },
            { new StringContent("json"), "format" },
            { new StringContent("2"), "formatversion" },   // see AddFormat for why
            { new StringContent(fileName), "filename" },
            { new StringContent(comment), "comment" },
            { new StringContent(description), "text" },
            { new StringContent(_csrfToken), "token" },
            { new StringContent("user"), "assert" },
        };

        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        form.Add(file, "file", fileName);

        JsonDocument response;
        try
        {
            using HttpResponseMessage message = await _http
                .PostAsync(_endpoint, form, cancellationToken).ConfigureAwait(false);
            response = await ReadAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsUnreachable(ex, cancellationToken))
        {
            throw Unavailable(ex);
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
