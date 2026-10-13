using System.Text;
using System.Text.RegularExpressions;
using System.Web;

namespace EQLWikiEditorAssistant.Wiki.MediaWiki;

/// <summary>
/// Which requests the review's embedded browser may send — **the guard that lets it run the wiki's scripts** (user,
/// 2026-10-12). Nothing inside the review may write to the wiki except the buttons that say so, and the browser is never
/// logged in, so a script that writes would make an anonymous edit under the user's address: anonymous visitors may edit
/// this wiki. The verification toast is one such script ("Verified" appends to a page), and since the site's scripts are
/// the wiki's to change, the rule cannot be a list of the scripts that write. It is a list of what may pass.
///
/// - **Reading passes**: GET, HEAD and OPTIONS. MediaWiki never writes on a GET; its API refuses one that tries.
/// - **A POST passes only to the wiki's <c>api.php</c>, for an action that only reads.** Not every POST is a write:
///   a long <c>parse</c> has to be posted, and EQL's own client-data API (<c>eqlmetadata</c>) accepts nothing else.
///   Every <c>action</c> the request names, in its address or its body, must be on the list.
/// - **Everything else is refused**, a script added tomorrow included, unless it only reads.
/// </summary>
public static class ReviewBrowserRequests
{
    /// <summary>The API actions that read and nothing more. <c>purge</c> is deliberately absent: it saves nothing, but it
    /// is work the wiki does that nobody asked for.</summary>
    public static readonly IReadOnlySet<string> ReadingActions =
        new HashSet<string>(StringComparer.Ordinal) { "parse", "query", "eqlmetadata" };

    public static bool MayPass(string method, Uri address, string? contentType, byte[]? body, Uri wiki)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(wiki);

        if (method.Equals("GET", StringComparison.OrdinalIgnoreCase) ||
            method.Equals("HEAD", StringComparison.OrdinalIgnoreCase) ||
            method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!method.Equals("POST", StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(address.Scheme, wiki.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(address.Authority, wiki.Authority, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(address.AbsolutePath, "/api.php", StringComparison.Ordinal))
            return false;

        List<string> actions = [.. HttpUtility.ParseQueryString(address.Query).GetValues("action") ?? []];
        if (ActionsInBody(contentType, body) is not { } inBody) return false;
        actions.AddRange(inBody);

        return actions.Count > 0 && actions.All(ReadingActions.Contains);
    }

    /// <summary>The <c>action</c> fields of a form body, or null for a body this cannot read — which is then refused,
    /// since what it would do cannot be known.</summary>
    private static List<string>? ActionsInBody(string? contentType, byte[]? body)
    {
        if (body is null || body.Length == 0) return [];

        string type = contentType?.Split(';')[0].Trim() ?? "";
        string text = Encoding.UTF8.GetString(body);

        if (type.Equals("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
            return [.. HttpUtility.ParseQueryString(text).GetValues("action") ?? []];

        if (type.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase))
            return [.. Regex.Matches(text, "name=\"action\"[^\\r\\n]*\\r?\\n(?:[^\\r\\n]+\\r?\\n)*\\r?\\n([^\\r\\n]*)")
                .Select(m => m.Groups[1].Value)];

        return null;
    }
}
