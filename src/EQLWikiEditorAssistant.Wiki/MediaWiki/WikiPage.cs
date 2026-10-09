namespace EQLWikiEditorAssistant.Wiki.MediaWiki;

/// <summary>
/// A page as fetched from the wiki, with the two values an edit-conflict-safe write needs alongside the text.
///
/// <see cref="RevisionId"/> and <see cref="Timestamp"/> are not bookkeeping: MediaWiki's <c>basetimestamp</c>
/// parameter is what makes an edit fail loudly when somebody else changed the page between our read and our write,
/// instead of silently overwriting their work. This tool edits a public wiki other people are editing at the same
/// time, so that is the difference between a patch and a revert.
/// </summary>
public sealed record WikiPage(
    string Title,
    string Wikitext,
    long RevisionId,
    DateTimeOffset Timestamp);

/// <summary>The outcome of an edit. <see cref="NoChange"/> means MediaWiki accepted the request and found the text
/// identical to what was already there — worth distinguishing from a real edit, since the ledger should record
/// "matched", not "edited".</summary>
public sealed record EditResult(string Title, long? NewRevisionId, bool NoChange);

/// <summary>The outcome of a file upload. <paramref name="Url"/> is where the wiki put it, when it said — useful for
/// linking the user straight at what was just published, which for an upload is the only way to check it by eye.
/// </summary>
/// <param name="FileName">The name the file landed under, without the <c>File:</c> prefix.</param>
public sealed record UploadResult(string FileName, string? Url);

/// <summary>
/// Who the wiki thinks we are and what it will let us do.
///
/// This exists so the credential can be verified <em>without writing anything</em>. A bot password carries its own
/// grant list, narrower than the account's own rights, and the usual way to discover it is to attempt an edit and
/// read the failure — which on a public wiki means a permanent revision in some page's history that the user
/// cannot delete. Asking instead costs one read and leaves no trace.
/// </summary>
public sealed record UserInfo(string Name, bool IsAnonymous, IReadOnlyList<string> Rights)
{
    /// <summary>Whether this session may edit an existing page — the one right the tool actually needs.</summary>
    public bool CanEdit => Rights.Contains("edit", StringComparer.Ordinal);

    /// <summary>Whether it may also create pages. Not needed by v1, which only edits pages that already exist.</summary>
    public bool CanCreate => Rights.Contains("createpage", StringComparer.Ordinal);

    /// <summary>
    /// Whether this session may upload a new file — the grant an icon upload needs, and a *separate* bot-password
    /// grant from editing, so a credential that edits perfectly well can still refuse to upload. Asked rather than
    /// discovered by attempting one, for the same reason as the rest of this type: a failed attempt against a wiki
    /// where an ordinary editor cannot delete anything is not a free experiment.
    /// </summary>
    public bool CanUpload => Rights.Contains("upload", StringComparer.Ordinal);

    /// <summary>Whether this session may overwrite a file that already exists. Deliberately distinct from
    /// <see cref="CanUpload"/>: this tool only ever uploads an icon the wiki does not have, so it never needs this —
    /// it is reported so a missing grant is never mistaken for the cause of a refused first upload.</summary>
    public bool CanReupload => Rights.Contains("reupload", StringComparer.Ordinal);
}

/// <summary>Raised when the wiki rejects a request. Carries MediaWiki's own error code (<c>badtoken</c>,
/// <c>editconflict</c>, <c>protectedpage</c>, <c>abusefilter-disallowed</c>, ...) so a caller can tell a retryable
/// problem from one that needs the user.</summary>
public sealed class MediaWikiException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// The wiki could not be reached, or answered that it is unavailable.
///
/// **Distinct from <see cref="MediaWikiException"/> because the right response is different** (user, 2026-09-29).
/// A `MediaWikiException` means the wiki answered — about one page, one edit, one session — and the tool carries on
/// with the rest. This means there is nothing to carry on *to*: the tool exists to compare captures against the wiki,
/// so if the wiki is gone every remaining step would fail the same way. It aborts the whole chain and says so, rather
/// than degrading into a screenful of identical per-item failures.
///
/// Raised for transport failures and HTTP errors, and for the API codes that mean the site itself is unavailable
/// rather than the request being wrong. A genuine cancellation is never one of these.
/// </summary>
public sealed class WikiUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner)
{
    /// <summary>API error codes that describe the site rather than the request. `readonly` is MediaWiki's own
    /// maintenance mode and `maxlag` means its replicas are behind — both are "come back shortly", not "your request
    /// was wrong".</summary>
    public static bool IsUnavailableCode(string code) =>
        code is "readonly" or "maxlag" || code.StartsWith("internal_api_error", StringComparison.Ordinal);
}
