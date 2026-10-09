using System.Text.Json;

namespace EQLWikiEditorAssistant.Pipeline;

/// <summary>A release newer than the one running, and the page a tester downloads it from.</summary>
public sealed record AvailableRelease(string Version, Uri Page);

/// <summary>
/// Asks GitHub whether a newer release exists, so the app can say so (user, 2026-10-08: in the alpha, ahead of any
/// automatic updating, which waits until testing shows interest).
///
/// **One anonymous GET to GitHub's public API, and nothing is sent but the request itself** — no version, no
/// identifier, no data about the user. It is the only request the app makes to anywhere but the wiki, and the README
/// says so. The releases *list* is read rather than <c>/releases/latest</c>, because GitHub's "latest" skips
/// pre-releases, and every release in the alpha is one.
///
/// **It never throws and never blocks anything.** Any failure — offline, rate-limited, GitHub down, a response it
/// cannot read — is simply "nothing to report": a missed notice costs nothing, and an error dialog about an optional
/// check would be worse than the missing notice.
/// </summary>
public static class ReleaseCheck
{
    /// <summary>The project's releases, newest first. Unauthenticated, which GitHub allows 60 times an hour per
    /// address; the app asks once per start.</summary>
    public static readonly Uri Feed =
        new("https://api.github.com/repos/AustonZ/EQL-Wiki-Editor-Assistant/releases?per_page=30");

    /// <summary>The newest release that is newer than <paramref name="currentVersion"/>, or null.</summary>
    public static async Task<AvailableRelease?> FindNewerAsync(
        HttpClient http, string currentVersion, CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Feed);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using HttpResponseMessage response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return FindNewer(json, currentVersion);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Picks from GitHub's releases JSON. Drafts never count. **A pre-release counts only for someone already running
    /// one**: a tester on <c>1.0.0-alpha.1</c> wants to hear about <c>alpha.2</c>, while someone on a stable release
    /// has not asked to test anything. A tag that is not a version (<c>v</c> prefix allowed) is ignored rather than
    /// guessed at.
    /// </summary>
    public static AvailableRelease? FindNewer(string releasesJson, string currentVersion)
    {
        if (!SemanticVersion.TryParse(currentVersion, out SemanticVersion current)) return null;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(releasesJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

            (SemanticVersion Version, Uri Page)? best = null;
            foreach (JsonElement release in doc.RootElement.EnumerateArray())
            {
                if (Bool(release, "draft")) continue;
                if (Bool(release, "prerelease") && !current.IsPrerelease) continue;
                if (!release.TryGetProperty("tag_name", out JsonElement tag) || tag.GetString() is not { } tagName) continue;
                if (!SemanticVersion.TryParse(tagName.TrimStart('v', 'V'), out SemanticVersion version)) continue;
                if (version.IsPrerelease && !current.IsPrerelease) continue;
                if (!release.TryGetProperty("html_url", out JsonElement url)
                    || !Uri.TryCreate(url.GetString(), UriKind.Absolute, out Uri? page)
                    || page.Scheme != Uri.UriSchemeHttps || page.Host != "github.com") continue;

                if (version.CompareTo(current) > 0 && (best is null || version.CompareTo(best.Value.Version) > 0))
                    best = (version, page);
            }

            return best is { } found ? new AvailableRelease(found.Version.ToString(), found.Page) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool Bool(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;
}

/// <summary>
/// A Semantic Versioning 2.0.0 version, compared by the specification's precedence rules: numeric parts as numbers, a
/// pre-release lower than its release, pre-release identifiers compared one by one (numeric ones as numbers and lower
/// than alphanumeric ones), and build metadata (after <c>+</c>) ignored. So <c>1.0.0-alpha.2</c> &lt;
/// <c>1.0.0-alpha.10</c> &lt; <c>1.0.0-beta.1</c> &lt; <c>1.0.0-rc.1</c> &lt; <c>1.0.0</c>.
/// </summary>
public readonly record struct SemanticVersion(int Major, int Minor, int Patch, string Prerelease)
    : IComparable<SemanticVersion>
{
    public bool IsPrerelease => Prerelease.Length > 0;

    public static bool TryParse(string? text, out SemanticVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        string core = text.Trim().Split('+')[0];
        string prerelease = "";
        int dash = core.IndexOf('-');
        if (dash >= 0)
        {
            prerelease = core[(dash + 1)..];
            core = core[..dash];
            if (prerelease.Length == 0 || prerelease.Split('.').Any(id => id.Length == 0)) return false;
        }

        string[] parts = core.Split('.');
        if (parts.Length != 3) return false;
        if (!int.TryParse(parts[0], out int major) || !int.TryParse(parts[1], out int minor)
            || !int.TryParse(parts[2], out int patch) || major < 0 || minor < 0 || patch < 0) return false;

        version = new SemanticVersion(major, minor, patch, prerelease);
        return true;
    }

    public int CompareTo(SemanticVersion other)
    {
        int byNumber = (Major, Minor, Patch).CompareTo((other.Major, other.Minor, other.Patch));
        if (byNumber != 0) return byNumber;
        if (IsPrerelease != other.IsPrerelease) return IsPrerelease ? -1 : 1;

        string[] mine = Prerelease.Split('.'), theirs = other.Prerelease.Split('.');
        for (int i = 0; i < Math.Min(mine.Length, theirs.Length); i++)
        {
            bool myNumber = long.TryParse(mine[i], out long a), theirNumber = long.TryParse(theirs[i], out long b);
            int byIdentifier = (myNumber, theirNumber) switch
            {
                (true, true) => a.CompareTo(b),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(mine[i], theirs[i]),
            };
            if (byIdentifier != 0) return Math.Sign(byIdentifier);
        }
        return mine.Length.CompareTo(theirs.Length);
    }

    public override string ToString() => IsPrerelease ? $"{Major}.{Minor}.{Patch}-{Prerelease}" : $"{Major}.{Minor}.{Patch}";
}
