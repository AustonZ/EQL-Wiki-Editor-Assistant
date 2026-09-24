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
}
