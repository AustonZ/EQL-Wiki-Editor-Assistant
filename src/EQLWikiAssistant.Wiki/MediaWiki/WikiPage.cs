namespace EQLWikiAssistant.Wiki.MediaWiki;

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
}

/// <summary>Raised when the wiki rejects a request. Carries MediaWiki's own error code (<c>badtoken</c>,
/// <c>editconflict</c>, <c>protectedpage</c>, <c>abusefilter-disallowed</c>, ...) so a caller can tell a retryable
/// problem from one that needs the user.</summary>
public sealed class MediaWikiException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
