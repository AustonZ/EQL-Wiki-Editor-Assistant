using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using EQLWikiAssistant.Pipeline;
using EQLWikiAssistant.Wiki.Ledger;

namespace EQLWikiAssistant.App;

/// <summary>
/// What has been checked, and — the reason this exists — what is still waiting for a human.
///
/// **Opened modally.** The ledger is shared state that a capture writes to, so a view of it sitting open beside a
/// running capture would show rows that are already wrong, and a "Forget" pressed against one of them would act on a
/// row the pipeline had just replaced. Blocking the main window for the duration removes that entirely, and a ledger
/// view is a stop-and-look activity rather than something to keep open while working.
///
/// **Nothing here judges a row.** Whether an item is settled is asked of <see cref="CheckedItemsLedger"/> using its
/// own rule — see <see cref="LedgerRowViewModel.For"/>.
/// </summary>
public partial class LedgerWindow : Window
{
    private readonly CheckedItemsLedger _ledger;
    private readonly int _mappingVersion;
    private readonly Func<Task> _save;
    private readonly ObservableCollection<LedgerRowViewModel> _rows = [];

    /// <summary>The filter list, in the order it is offered. <see cref="LedgerFilter.NeedsAttention"/> sits second
    /// because it is the one the user is most often after, and first place belongs to the unfiltered view so the
    /// window does not open looking empty on a clean ledger.</summary>
    private static readonly (LedgerFilter Filter, string Label)[] Filters =
    [
        (LedgerFilter.All, "Everything"),
        (LedgerFilter.NeedsAttention, "Still needs attention"),
        (LedgerFilter.Flagged, "Flagged"),
        (LedgerFilter.Skipped, "Skipped"),
        (LedgerFilter.NotOnWiki, "Not on the wiki"),
        (LedgerFilter.Edited, "Edited"),
        (LedgerFilter.Created, "Created"),
        (LedgerFilter.Matched, "Matched"),
    ];

    public LedgerWindow(
        CheckedItemsLedger ledger, int mappingVersion, Func<Task> save, LedgerFilter filter = LedgerFilter.All)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(save);

        InitializeComponent();
        DarkTitleBar.Apply(this);
        _ledger = ledger;
        _mappingVersion = mappingVersion;
        _save = save;

        RowsGrid.ItemsSource = _rows;
        FilterBox.ItemsSource = Filters.Select(f => f.Label);
        FilterBox.SelectedIndex = Math.Max(0, Array.FindIndex(Filters, f => f.Filter == filter));
        LedgerPathText.Text = AppPaths.LedgerFile;

        Refresh();
    }

    private LedgerFilter SelectedFilter =>
        FilterBox.SelectedIndex >= 0 ? Filters[FilterBox.SelectedIndex].Filter : LedgerFilter.All;

    private void OnQueryChanged(object sender, RoutedEventArgs e)
    {
        // Fires once during InitializeComponent, before the fields are set.
        if (!IsInitialized) return;
        Refresh();
    }

    private void Refresh()
    {
        LedgerSummary summary = LedgerSummary.Of(_ledger.Entries);

        AttentionText.Text = summary.NeedsAttention switch
        {
            0 => "",
            1 => "1 item still needs attention.",
            _ => $"{summary.NeedsAttention} items still need attention.",
        };

        // Only the outcomes that actually occurred: a standing "0 skipped" is noise in the one line the user reads
        // every time they open this.
        string[] parts =
        [
            .. Describe(summary.Matched, "matched"),
            .. Describe(summary.Edited, "edited"),
            .. Describe(summary.Created, "created"),
            .. Describe(summary.Flagged, "flagged"),
            .. Describe(summary.Skipped, "skipped"),
            .. Describe(summary.NotOnWiki, "not on the wiki"),
        ];

        SummaryText.Text = summary.Total == 0
            ? "Nothing has been checked yet."
            : $"{summary.Total} checked — {string.Join(", ", parts)}.";

        static string[] Describe(int count, string label) => count == 0 ? [] : [$"{count} {label}"];

        IReadOnlyList<LedgerEntry> matching = LedgerQuery.Apply(_ledger.Entries, SearchBox.Text, SelectedFilter);

        _rows.Clear();
        foreach (LedgerEntry entry in matching)
            _rows.Add(LedgerRowViewModel.For(entry, _ledger, _mappingVersion));

        ShownText.Text = matching.Count == summary.Total ? "" : $"showing {matching.Count}";

        bool empty = matching.Count == 0;
        EmptyText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        RowsGrid.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        EmptyText.Text = summary.Total == 0
            ? "No item has been checked yet.\n\nCapture an item window and it will be recorded here."
            : "No row matches that.";
    }

    private void OnOpenPageClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkContentElement)?.DataContext is not LedgerRowViewModel { PageUrl: { } url }) return;
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private async void OnForgetClick(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is not LedgerRowViewModel row) return;

        // Confirmed because it is not undoable from here — the row's fingerprint and the revision it saw are gone.
        // Nothing on the wiki changes, though, so this is a small confirmation rather than the commit one.
        if (!await Dialog.AskAsync(
                $"Forget '{row.ItemName}'?",
                "Nothing on the wiki changes. The next capture of this item will check it again from scratch.",
                "Forget"))
            return;

        _ledger.Remove(row.Entry.ItemName, row.Entry.EntityKind);
        await _save();
        Refresh();
    }

    /// <summary>Opens the folder with the ledger selected. The file is the user's own data and readable JSON, so
    /// pointing at it beats an export nobody asked for.</summary>
    private void OnOpenLedgerFolderClick(object sender, RoutedEventArgs e)
    {
        string path = AppPaths.LedgerFile;
        Process.Start(new ProcessStartInfo("explorer.exe")
        {
            Arguments = File.Exists(path) ? $"/select,\"{path}\"" : $"\"{AppPaths.Root}\"",
            UseShellExecute = true,
        });
    }
}
