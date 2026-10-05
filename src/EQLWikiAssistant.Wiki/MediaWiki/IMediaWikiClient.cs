namespace EQLWikiAssistant.Wiki.MediaWiki;

/// <summary>
/// The wiki, as the pipeline sees it. A port rather than a concrete class for the reason the plan gives the
/// ledger: the ledger's whole job is to <em>avoid</em> network calls, and "a second capture of an unchanged,
/// already-matched item makes zero wiki requests" is only testable against a fake that counts them.
/// </summary>
public interface IMediaWikiClient
{
    /// <summary>Fetches a page's current wikitext, or null if the page does not exist. A missing page is an
    /// ordinary outcome — it means "this item has no wiki page yet", which is a case the tool handles, not an
    /// error.</summary>
    Task<WikiPage?> FetchPageAsync(string title, CancellationToken cancellationToken = default);

    /// <summary>
    /// Which of these titles exist, in one request. Reads nothing but the page list, so it is the cheap way to ask
    /// an existence question — <see cref="FetchPageAsync"/> would download each page's whole wikitext to answer it.
    ///
    /// A title the wiki cannot use at all (see <see cref="Wikitext.PageTitle"/>) simply does not come back, which is
    /// the same answer as "no such page" and the right one for every caller here.
    /// </summary>
    Task<IReadOnlySet<string>> ExistingTitlesAsync(
        IReadOnlyList<string> titles, CancellationToken cancellationToken = default);

    /// <summary>Logs in with a bot password. Required before <see cref="EditAsync"/>; reads work anonymously.</summary>
    Task LoginAsync(BotCredentials credentials, CancellationToken cancellationToken = default);

    /// <summary>Replaces a page's text. <paramref name="baseTimestamp"/> must be the <see cref="WikiPage.Timestamp"/>
    /// of the revision the new text was built from, so a concurrent edit by somebody else fails the write rather
    /// than silently reverting them.</summary>
    Task<EditResult> EditAsync(
        string title,
        string newWikitext,
        string summary,
        DateTimeOffset baseTimestamp,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a page that does not exist yet.
    ///
    /// **Separate from <see cref="EditAsync"/> because the conflict guard inverts.** An edit sends
    /// <c>basetimestamp</c>, which is meaningless here — there is no base revision to have moved on from. The
    /// symmetric guard is <c>createonly</c>: if somebody created the page between the check and this write, the
    /// wiki refuses rather than overwriting what they wrote. Keeping both in one method would have meant a call
    /// that silently sends neither guard when handed the wrong arguments.
    /// </summary>
    Task<EditResult> CreatePageAsync(
        string title,
        string wikitext,
        string summary,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Uploads a file the wiki does not have, and refuses if it turns out to have one by that name.
    ///
    /// **Refusing is the guard, and it is enforced by the wiki rather than by this process**: the implementation omits
    /// <c>ignorewarnings</c>, so an existing file comes back as a failure instead of an overwrite — the same shape as
    /// <c>createonly</c> on <see cref="CreatePageAsync"/>, and for a stronger reason. Only an admin on this wiki can
    /// delete a file, so an overwrite destroys the original and needs somebody else to undo. It is also the one
    /// grant a bot password may lack while editing perfectly well, so check <c>UserInfo.CanUpload</c> rather than
    /// discovering it from a failure.
    /// </summary>
    /// <param name="fileName">The target name without the <c>File:</c> prefix, e.g. <c>Item_5797.png</c>.</param>
    /// <param name="description">The initial wikitext of the file's own page.</param>
    Task<UploadResult> UploadFileAsync(
        string fileName,
        byte[] content,
        string description,
        string comment,
        CancellationToken cancellationToken = default);
}
