namespace EQLWikiAssistant.Wiki.Ledger;

/// <summary>
/// Which rows a ledger view is asking for.
///
/// <see cref="NeedsAttention"/> is the one that earns its place: it is the question the ledger view exists to answer
/// — *what did I leave undone?* — and it is deliberately defined by <see cref="CheckedItemsLedger.MeansDone"/> rather
/// than by listing outcomes here, so it cannot drift from the rule that decides whether a capture may skip the wiki.
/// </summary>
public enum LedgerFilter
{
    All,

    /// <summary>Everything a check left undone: flagged, skipped, or not on the wiki.</summary>
    NeedsAttention,

    Matched,
    Edited,
    Flagged,
    Skipped,
    NotOnWiki,
}

/// <summary>How many rows of each outcome the ledger holds. Shown above the rows, because the count of things still
/// wanting a human is the number the user actually came to read.</summary>
public sealed record LedgerSummary(
    int Total,
    int Matched,
    int Edited,
    int Flagged,
    int Skipped,
    int NotOnWiki)
{
    /// <summary>Everything a check left undone — the complement of the ledger's own "done" rule.</summary>
    public int NeedsAttention => Flagged + Skipped + NotOnWiki;

    public static LedgerSummary Of(IEnumerable<LedgerEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        int total = 0, matched = 0, edited = 0, flagged = 0, skipped = 0, notOnWiki = 0;
        foreach (LedgerEntry entry in entries)
        {
            total++;
            switch (entry.Outcome)
            {
                case CheckOutcome.Matched: matched++; break;
                case CheckOutcome.Edited: edited++; break;
                case CheckOutcome.Flagged: flagged++; break;
                case CheckOutcome.Skipped: skipped++; break;
                case CheckOutcome.NotOnWiki: notOnWiki++; break;
            }
        }

        return new LedgerSummary(total, matched, edited, flagged, skipped, notOnWiki);
    }
}

/// <summary>
/// Searching and filtering the ledger, kept out of the UI so it can be tested.
/// </summary>
public static class LedgerQuery
{
    /// <summary>
    /// The rows matching a search and a filter, newest check first.
    ///
    /// **The search covers the wiki page title as well as the item name**, because the two legitimately differ — an
    /// item whose in-game name cannot be a MediaWiki title lives at a name a human chose, and the user may remember
    /// either one.
    /// </summary>
    public static IReadOnlyList<LedgerEntry> Apply(
        IEnumerable<LedgerEntry> entries,
        string? search = null,
        LedgerFilter filter = LedgerFilter.All)
    {
        ArgumentNullException.ThrowIfNull(entries);

        IEnumerable<LedgerEntry> rows = entries.Where(e => Matches(e, filter));

        if (!string.IsNullOrWhiteSpace(search))
        {
            string term = search.Trim();
            rows = rows.Where(e =>
                e.ItemName.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                (e.WikiPageTitle?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        // Newest first: a ledger view is opened to see what just happened far more often than to browse history.
        return [.. rows.OrderByDescending(e => e.CheckedAt).ThenBy(e => e.ItemName, StringComparer.OrdinalIgnoreCase)];
    }

    private static bool Matches(LedgerEntry entry, LedgerFilter filter) => filter switch
    {
        LedgerFilter.All => true,
        LedgerFilter.NeedsAttention => !CheckedItemsLedger.MeansDone(entry.Outcome),
        LedgerFilter.Matched => entry.Outcome == CheckOutcome.Matched,
        LedgerFilter.Edited => entry.Outcome == CheckOutcome.Edited,
        LedgerFilter.Flagged => entry.Outcome == CheckOutcome.Flagged,
        LedgerFilter.Skipped => entry.Outcome == CheckOutcome.Skipped,
        LedgerFilter.NotOnWiki => entry.Outcome == CheckOutcome.NotOnWiki,
        _ => true,
    };
}
