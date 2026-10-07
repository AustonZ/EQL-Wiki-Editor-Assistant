using System.Text.Json;

namespace EQLWikiAssistant.Wiki.MediaWiki;

/// <summary>Fetches an icon's bytes. A port so the cache can be tested for the property that matters — that it
/// makes exactly one request per icon, ever — without touching the network.</summary>
public interface IIconSource
{
    /// <summary>The PNG bytes for <c>File:item_&lt;id&gt;.png</c>, or null if the wiki has no such file.</summary>
    Task<byte[]?> DownloadAsync(string iconId, CancellationToken cancellationToken = default);
}

/// <summary>
/// An on-disk cache of wiki icon files, keyed by icon id.
///
/// **Icons are static and shared across many items**, which is the whole reason this exists: `lucy_img_ID` 584 is
/// the Water Flask's icon and also several other containers', so a naive implementation would re-download the same
/// 40x40 PNG dozens of times across a session. The cache is consulted before any network call, and there is no
/// expiry — a wiki icon file does not change, and the Settings window's "clear icon cache" covers the rare case
/// where one is replaced.
///
/// **A missing icon is cached too, with a short TTL.** Otherwise every capture of an item whose icon has never been
/// uploaded re-asks the wiki and gets the same "no such file" answer. The TTL is short because this absence is the
/// one thing here that genuinely changes — somebody uploads the file — unlike the icons themselves.
/// </summary>
public sealed class IconCache
{
    /// <summary>How long a "the wiki has no such file" answer is trusted. Short, because unlike an icon's contents
    /// this is a fact that changes the moment somebody uploads one.</summary>
    public static readonly TimeSpan MissingIconLifetime = TimeSpan.FromHours(6);

    private readonly string _directory;
    private readonly IIconSource _source;
    private readonly TimeProvider _time;

    public IconCache(string directory, IIconSource source, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = directory;
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Icon ids that were fetched from the wiki rather than served from disk, for diagnostics and for the
    /// tests that assert a shared icon is downloaded once.</summary>
    public int Downloads { get; private set; }

    /// <summary>
    /// The icon's bytes, from disk if they are there and from the wiki otherwise. Null means the wiki has no such
    /// file — which is an ordinary answer, not a failure: plenty of items have no icon uploaded yet.
    /// </summary>
    public async Task<byte[]?> GetAsync(string iconId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(iconId);

        string path = PathFor(iconId);
        if (File.Exists(path))
            return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);

        if (TryReadMissingMarker(iconId, out DateTimeOffset recordedAt) &&
            _time.GetUtcNow() - recordedAt < MissingIconLifetime)
            return null;

        Downloads++;
        byte[]? bytes = await _source.DownloadAsync(iconId, cancellationToken).ConfigureAwait(false);

        Directory.CreateDirectory(_directory);
        if (bytes is null)
        {
            await File.WriteAllTextAsync(
                MissingMarkerFor(iconId),
                JsonSerializer.Serialize(new MissingIcon(_time.GetUtcNow())),
                cancellationToken).ConfigureAwait(false);
            return null;
        }

        await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);
        // A file that arrives supersedes any earlier "missing" answer.
        File.Delete(MissingMarkerFor(iconId));
        return bytes;
    }

    /// <summary>Forgets one icon, so the next request re-fetches it. True if anything was cached for it — the file, or
    /// a remembered "the wiki has none".</summary>
    public bool Forget(string iconId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(iconId);
        bool had = File.Exists(PathFor(iconId)) || File.Exists(MissingMarkerFor(iconId));
        File.Delete(PathFor(iconId));
        File.Delete(MissingMarkerFor(iconId));
        return had;
    }

    /// <summary>
    /// Fetches one icon from the wiki now, whatever the cache holds — the Settings window's "re-download this icon",
    /// for the rare file somebody has replaced on the wiki. Null means the wiki has no such file, which is then
    /// remembered like any other answer.
    /// </summary>
    public async Task<byte[]?> RedownloadAsync(string iconId, CancellationToken cancellationToken = default)
    {
        Forget(iconId);
        return await GetAsync(iconId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Empties the cache entirely.</summary>
    public void Clear()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    /// <summary>What the cache holds right now: icon files and their total size, and remembered absences — expired
    /// ones included, since they stay on disk until the icon is next asked for.</summary>
    public IconCacheContents Describe()
    {
        if (!Directory.Exists(_directory)) return new IconCacheContents(0, 0, 0);

        var directory = new DirectoryInfo(_directory);
        FileInfo[] icons = directory.GetFiles("item_*.png");
        int missing = directory.GetFiles("item_*.missing.json").Length;
        return new IconCacheContents(icons.Length, icons.Sum(f => f.Length), missing);
    }

    /// <summary>Icon ids come from a wiki parameter, so they are untrusted: a value with a slash or a `..` in it
    /// would otherwise escape the cache directory. Only the characters a real id uses are kept.</summary>
    private string PathFor(string iconId) => Path.Combine(_directory, $"item_{Sanitize(iconId)}.png");

    private string MissingMarkerFor(string iconId) => Path.Combine(_directory, $"item_{Sanitize(iconId)}.missing.json");

    private static string Sanitize(string iconId)
    {
        string clean = new([.. iconId.Where(char.IsLetterOrDigit)]);
        return clean.Length > 0
            ? clean
            : throw new ArgumentException($"'{iconId}' is not a usable icon id.", nameof(iconId));
    }

    private bool TryReadMissingMarker(string iconId, out DateTimeOffset recordedAt)
    {
        recordedAt = default;
        string path = MissingMarkerFor(iconId);
        if (!File.Exists(path)) return false;

        try
        {
            recordedAt = JsonSerializer.Deserialize<MissingIcon>(File.ReadAllText(path))?.RecordedAt ?? default;
            return recordedAt != default;
        }
        catch (JsonException)
        {
            // A corrupt marker is not worth failing over; treat it as absent and re-ask.
            return false;
        }
    }

    private sealed record MissingIcon(DateTimeOffset RecordedAt);
}

/// <summary>What an <see cref="IconCache"/> holds: icon files, their total size in bytes, and remembered absences.</summary>
public sealed record IconCacheContents(int Icons, long Bytes, int Missing);

/// <summary>Downloads icons from a MediaWiki site, resolving the file's real URL first.</summary>
public sealed class WikiIconSource(HttpClient http, Uri endpoint) : IIconSource
{
    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));
    private readonly Uri _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));

    /// <summary>The file naming convention, from the plan: `lucy_img_ID` 584 is `File:item_584.png`. MediaWiki
    /// normalizes that to `File:Item 584.png`, so the title asked for and the title returned differ — which is
    /// fine, since the URL is what gets used.</summary>
    public static string FileTitleFor(string iconId) => $"File:item_{iconId}.png";

    public async Task<byte[]?> DownloadAsync(string iconId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(iconId);

        var query = new Dictionary<string, string>
        {
            ["action"] = "query",
            ["titles"] = FileTitleFor(iconId),
            ["prop"] = "imageinfo",
            ["iiprop"] = "url",
            ["format"] = "json",
            ["formatversion"] = "2",
        };

        string url = $"{_endpoint}?{await new FormUrlEncodedContent(query).ReadAsStringAsync(cancellationToken).ConfigureAwait(false)}";
        using HttpResponseMessage response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using JsonDocument document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));

        if (!document.RootElement.TryGetProperty("query", out JsonElement queryElement) ||
            !queryElement.TryGetProperty("pages", out JsonElement pages) ||
            pages.GetArrayLength() == 0)
            return null;

        JsonElement page = pages[0];
        if (page.TryGetProperty("missing", out _) ||
            !page.TryGetProperty("imageinfo", out JsonElement info) ||
            info.GetArrayLength() == 0 ||
            info[0].GetProperty("url").GetString() is not { } fileUrl)
            return null;

        return await _http.GetByteArrayAsync(fileUrl, cancellationToken).ConfigureAwait(false);
    }
}
