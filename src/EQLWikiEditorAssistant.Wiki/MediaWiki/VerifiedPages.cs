using System.Text.Json;

namespace EQLWikiEditorAssistant.Wiki.MediaWiki;

/// <summary>
/// Which pages the wiki's editors have marked "Verified for EQLegends".
///
/// The wiki shows a toast on every unverified page, and an editor clears it by typing "Verified" into it. Behind that
/// sits an ordinary main-namespace page, <c>VerifiedPages</c>, holding one title per line — 1,873 of them when this
/// was written. This reads that page.
///
/// **Read from the page, not from the API the toast's own extension exposes.** `EQLClientData` provides an
/// `action=eqlmetadata`, but it self-describes as *"internal or unstable, and you should not use it"* and is POST
/// only. The list page is stable public content and is what the toast itself reads.
///
/// **The tool only ever reports what this says, and never writes to it** — verification attests that a *whole page*
/// is accurate, including the drops, quests and recipes this tool never looks at, so it has no standing to claim one.
/// Writing is a v2 question; for the record the toast does it with a plain `action=edit` and `appendtext`.
///
/// **Not knowing is silence, never a warning.** If the list has never been read and the wiki is unreachable,
/// <see cref="IsVerified"/> returns null and the caller says nothing — a false "this page is unverified" would send
/// the user to re-verify a page that is already done, which is worse than saying nothing at all. Same rule the icon
/// check follows.
/// </summary>
public sealed class VerifiedPages
{
    /// <summary>The wiki page holding the list. Named by <c>wgEQLVerifiedPagesTitle</c> on every rendered page.</summary>
    public const string ListPageTitle = "VerifiedPages";

    /// <summary>How long a loaded list is trusted before a capture refreshes it. Matches the wiki's own client TTL
    /// for related data (<c>wgEQLEraStatusClientTtlSeconds</c> is 300), and the cost of being stale is only a warning
    /// that lingers one session too long.</summary>
    public static readonly TimeSpan RefreshAfter = TimeSpan.FromMinutes(5);

    private readonly IMediaWikiClient _wiki;
    private readonly string _cacheFile;
    private readonly TimeProvider _time;

    private HashSet<string>? _titles;
    private DateTimeOffset _loadedAt;

    public VerifiedPages(IMediaWikiClient wiki, string cacheFile, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(wiki);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheFile);

        _wiki = wiki;
        _cacheFile = cacheFile;
        _time = time ?? TimeProvider.System;
        LoadCache();
    }

    /// <summary>The revision of the list this copy came from, or null if none has ever been read.</summary>
    public long? RevisionId { get; private set; }

    /// <summary>Whether anything is known at all. False only before the first successful read.</summary>
    public bool IsKnown => _titles is not null;

    public int Count => _titles?.Count ?? 0;

    /// <summary>
    /// Whether this page is marked verified — or null when the list has never been read.
    /// </summary>
    public bool? IsVerified(string pageTitle)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pageTitle);
        return _titles?.Contains(Normalize(pageTitle));
    }

    /// <summary>
    /// The wiki's own title normalization, copied from the toast's script so the two agree exactly: trim, spaces to
    /// underscores, leading colons dropped. Nothing is percent-encoded — apostrophes and colons are literal in the
    /// list, and comparison is case-sensitive after MediaWiki's own first-letter rule.
    /// </summary>
    public static string Normalize(string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        return title.Trim().Replace(' ', '_').TrimStart(':');
    }

    /// <summary>
    /// Re-reads the list if the copy in hand is older than <see cref="RefreshAfter"/>.
    ///
    /// **An unreachable wiki propagates** (<see cref="WikiUnavailableException"/>), because the capture that follows
    /// would fail on every page anyway — and since this runs first, it is the tool's earliest and cheapest notice
    /// that the wiki is gone (user, 2026-09-29). An error *about this page* is a different matter and is swallowed:
    /// whether somebody has ticked a box on a web page is not a reason to fail an item.
    /// </summary>
    public Task RefreshAsync(CancellationToken cancellationToken = default) => RefreshAsync(false, cancellationToken);

    /// <summary>Makes the next <see cref="RefreshAsync(CancellationToken)"/> re-read the list whatever its age — for
    /// when the user has gone to the wiki to verify a page. The copy in hand still answers until then.</summary>
    public void MarkStale() => _loadedAt = DateTimeOffset.MinValue;

    /// <summary>
    /// Re-reads the list, now when <paramref name="evenIfRecent"/> is set — for a refresh the user asked for.
    ///
    /// **The age limit is for captures, not for a deliberate refresh** (bug found by the user, 2026-10-07). They marked
    /// a page verified on the wiki and pressed "Refresh wiki data", and the badge stayed: the list read a minute earlier
    /// was still inside its five minutes, so nothing re-read it. A capture repeated every few seconds is what the limit
    /// spares the wiki from; a refresh is one request the user expects to see the result of.
    /// </summary>
    public async Task RefreshAsync(bool evenIfRecent, CancellationToken cancellationToken = default)
    {
        if (!evenIfRecent && _titles is not null && _time.GetUtcNow() - _loadedAt < RefreshAfter) return;

        try
        {
            WikiPage? page = await _wiki.FetchPageAsync(ListPageTitle, cancellationToken).ConfigureAwait(false);
            if (page is null) return;

            _titles = Parse(page.Wikitext);
            RevisionId = page.RevisionId;
            _loadedAt = _time.GetUtcNow();
            SaveCache();
        }
        catch (Exception ex) when (ex is MediaWikiException or HttpRequestException or TaskCanceledException)
        {
            // Keep the cached copy. Nothing here is worth failing a capture over.
        }
    }

    /// <summary>Blank lines and <c>#</c> comments are skipped, matching the wiki's own parser.</summary>
    internal static HashSet<string> Parse(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var titles = new HashSet<string>(StringComparer.Ordinal);
        foreach (string line in content.Split('\n'))
        {
            string normalized = Normalize(line);
            if (normalized.Length == 0 || normalized[0] == '#') continue;
            titles.Add(normalized);
        }

        return titles;
    }

    private void LoadCache()
    {
        try
        {
            if (!File.Exists(_cacheFile)) return;

            CacheFile? cached = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(_cacheFile), JsonOptions);
            if (cached?.Titles is null) return;

            _titles = new HashSet<string>(cached.Titles, StringComparer.Ordinal);
            RevisionId = cached.RevisionId;

            // Deliberately not restoring the fetch time: a cache read at startup should still refresh once. It exists
            // so the first capture of a session can answer offline, not to postpone asking.
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A corrupt cache costs one fetch, exactly like the ledger's own rule.
        }
    }

    private void SaveCache()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_cacheFile))!);
            File.WriteAllText(
                _cacheFile,
                JsonSerializer.Serialize(
                    new CacheFile { RevisionId = RevisionId, Titles = [.. _titles!] }, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not being able to cache is a slower next start, not a failure.
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private sealed class CacheFile
    {
        public long? RevisionId { get; set; }
        public List<string> Titles { get; set; } = [];
    }
}
