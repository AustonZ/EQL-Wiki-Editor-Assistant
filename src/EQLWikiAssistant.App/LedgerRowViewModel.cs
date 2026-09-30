using System.Windows.Media;
using EQLWikiAssistant.Wiki.Ledger;

namespace EQLWikiAssistant.App;

/// <summary>
/// One ledger row, flattened for the grid.
///
/// **"What will the next capture do?" is answered by asking the ledger itself** — <see cref="CheckedItemsLedger.Consult"/>
/// with the row's own fingerprint means exactly "if I captured this same, unchanged item again right now, would it
/// skip the wiki?". Reimplementing that here would be a second copy of the rule that decides whether an item is ever
/// checked properly again, which is the last rule in this tool worth duplicating.
/// </summary>
public sealed record LedgerRowViewModel(LedgerEntry Entry, LedgerVerdict Verdict)
{
    public static LedgerRowViewModel For(LedgerEntry entry, CheckedItemsLedger ledger, int mappingVersion) =>
        new(entry, ledger.Consult(entry.ItemName, entry.Fingerprint, mappingVersion, entry.EntityKind));

    public string ItemName => Entry.ItemName;

    /// <summary>Shown only when it differs, because for almost every item it is simply the name again — and when it
    /// does differ (`Cell Key #5` living at `Cell Key No. 5`) that is the thing worth seeing.</summary>
    public string PageTitle =>
        Entry.WikiPageTitle is { } title &&
        !string.Equals(title, Entry.ItemName, StringComparison.OrdinalIgnoreCase)
            ? title
            : "";

    public string Outcome => Entry.Outcome switch
    {
        CheckOutcome.Matched => "Matched",
        CheckOutcome.Edited => "Edited",
        CheckOutcome.Flagged => "Flagged",
        CheckOutcome.Skipped => "Skipped",
        CheckOutcome.NotOnWiki => "Not on the wiki",
        _ => Entry.Outcome.ToString(),
    };

    /// <summary>The same palette the review screen uses: green settled, red wanting a human, grey neither.</summary>
    public Brush OutcomeBrush => Entry.Outcome switch
    {
        CheckOutcome.Matched or CheckOutcome.Edited => Palette.Done,
        CheckOutcome.Flagged => Palette.Attention,
        CheckOutcome.NotOnWiki => Palette.Warning,
        _ => Palette.Neutral,
    };

    public string Checked => Describe(Entry.CheckedAt);

    /// <summary>The exact time, for the row's tooltip — the relative form is the readable one, but "5 days ago" is no
    /// use when the user is trying to work out which session something happened in.</summary>
    public string CheckedExactly => Entry.CheckedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    public string Revision => Entry.WikiRevisionId?.ToString() ?? "";

    public string NextCapture => Verdict switch
    {
        LedgerVerdict.AlreadyDone => "Settled",
        LedgerVerdict.Unresolved => "Will re-check — not settled",
        LedgerVerdict.MappingChanged => "Will re-check — mapping changed",
        LedgerVerdict.Stale => "Will re-check — older than the maximum age",
        _ => "Will re-check",
    };

    public Brush NextCaptureBrush =>
        Verdict == LedgerVerdict.AlreadyDone ? Palette.Neutral : Palette.Recheck;

    public string? Note => Entry.Note;

    public string? PageUrl =>
        (Entry.WikiPageTitle ?? Entry.ItemName) is { Length: > 0 } title && Entry.Outcome != CheckOutcome.NotOnWiki
            ? $"https://eqlwiki.com/{Uri.EscapeDataString(title.Replace(' ', '_'))}"
            : null;

    private static string Describe(DateTimeOffset when)
    {
        TimeSpan ago = DateTimeOffset.Now - when;
        return ago switch
        {
            { TotalMinutes: < 1 } => "just now",
            { TotalHours: < 1 } => $"{(int)ago.TotalMinutes} min ago",
            { TotalHours: < 24 } => $"{(int)ago.TotalHours} h ago",
            { TotalDays: < 2 } => "yesterday",
            { TotalDays: < 30 } => $"{(int)ago.TotalDays} days ago",
            _ => when.ToLocalTime().ToString("yyyy-MM-dd"),
        };
    }
}
