using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media.Imaging;
using EQLWikiAssistant.Capture;
using EQLWikiAssistant.Core.Ocr;
using EQLWikiAssistant.Pipeline;
using EQLWikiAssistant.Wiki.Analysis;
using EQLWikiAssistant.Wiki.Ledger;
using EQLWikiAssistant.Wiki.MediaWiki;

namespace EQLWikiAssistant.App;

/// <summary>
/// The review screen: capture, then look at every item window the frame held and decide what to do with each.
///
/// **Nothing here decides anything about an item.** Every judgement was made by the analyzer, the compliance checker
/// and the editor; this shows them and carries the user's answer back. The one thing it owns is that the text on
/// screen is the text that gets saved.
/// </summary>
public partial class MainWindow : Window
{
    private readonly ObservableCollection<ResultViewModel> _results = [];
    private AppServices? _services;

    /// <summary>
    /// True while the Settings or Checked items window is open. **The hotkey is global and bypasses modality**, so
    /// without this a press from inside the game would start a capture behind a dialog that is in the middle of
    /// changing the font it reads with, or editing the ledger it writes to — the two things those windows are modal
    /// to prevent. The buttons are covered by modality; the hotkey is covered here.
    /// </summary>
    private bool _dialogOpen;
    private bool _capturing;

    public MainWindow()
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        ResultsList.ItemsSource = _results;

        // Built off the UI thread: RapidOcrEngine loads three ONNX models in its constructor, which is seconds of a
        // frozen window if it happens here. The capture button stays disabled until it is ready.
        CaptureButton.IsEnabled = false;
        LedgerButton.IsEnabled = false;
        SettingsButton.IsEnabled = false;
        StatusText.Text = "Loading the OCR models…";
        _ = StartUpAsync();
    }

    /// <summary>
    /// The capture hotkey fired. It is registered globally so it fires while the game has focus, which is the whole
    /// point — Graphics Capture reads an unfocused window fine, so the user never has to leave the game to capture.
    /// It arrives on the hotkey's own thread, hence the dispatch.
    /// </summary>
    private void OnHotKeyPressed(object? sender, EventArgs e) => Dispatcher.Invoke(() =>
    {
        if (_dialogOpen) return;

        // Front first, so the capture's progress and its result are both visible without alt-tabbing. The game window
        // is captured from the frame grabbed inside CaptureAsync, which reads it unfocused, so stealing focus here
        // cannot spoil the capture.
        ComeToTheFront();
        _ = CaptureAsync();
    });

    /// <summary>Says which hotkey is live — or, when Windows refused the saved one, that none is and why. A refused
    /// hotkey is a degraded tool rather than a broken one, since the Capture button still works.</summary>
    private void UpdateHotKeyLabel()
    {
        if (_services is null) return;

        HotKeyLabel.Text = _services.HotKeyProblem is null
            ? $"Hotkey: {_services.Settings.HotKey}"
            : "Hotkey unavailable";
        HotKeyLabel.ToolTip = _services.HotKeyProblem is { } problem
            ? $"{problem} Choose another under Settings > Capture hotkey."
            : null;
    }

    private async Task StartUpAsync()
    {
        try
        {
            _services = await Task.Run(() => new AppServices());
            _services.Pipeline.ReCheckAnyway = ReCheckBox.IsChecked == true;
            _services.HotKeyPressed += OnHotKeyPressed;
            UpdateHotKeyLabel();
            CaptureButton.IsEnabled = true;
            LedgerButton.IsEnabled = true;
            SettingsButton.IsEnabled = true;
            UpdateLedgerText();
            StatusText.Text = "Ready. Press the hotkey with an item window open in game.";
        }
        catch (Exception ex)
        {
            // Most likely the RapidOCR models did not make it next to the executable — the documented packaging
            // trap. Saying so beats a startup crash with a stack trace.
            StatusText.Text = $"Could not start: {ex.Message}";
        }
    }

    private void OnCaptureClick(object sender, RoutedEventArgs e) => _ = CaptureAsync();

    private void OnReCheckChanged(object sender, RoutedEventArgs e)
    {
        if (_services is not null) _services.Pipeline.ReCheckAnyway = ReCheckBox.IsChecked == true;
    }

    private async Task CaptureAsync()
    {
        if (_capturing || _services is null) return;
        _capturing = true;
        CaptureButton.IsEnabled = false;
        // The ledger too: a capture writes rows, so a ledger view opened mid-capture would show some of them stale.
        // The window being modal stops a capture starting while it is open; this is the same guard the other way.
        LedgerButton.IsEnabled = false;
        // Settings too: the capture in progress reads with the configured font, so switching it mid-capture would
        // read part of a frame in each.
        SettingsButton.IsEnabled = false;
        BusyPanel.Visibility = Visibility.Visible;

        try
        {
            BusyLabel.Text = "Capturing the game window…";
            StatusText.Text = BusyLabel.Text;
            (CapturedImage? frame, string? problem, string? captured) = await _services.CaptureGameWindowAsync();
            if (frame is null)
            {
                StatusText.Text = problem;
                return;
            }

            BusyLabel.Text = "Reading the item windows and checking them against the wiki…";
            StatusText.Text = BusyLabel.Text;
            IReadOnlyList<ItemCheckResult> results = await _services.Pipeline.CheckAsync(frame);

            // Kept when Settings > Saved captures says so, named after what was in it, so a bug can be reported by
            // naming an item rather than by keeping it in the game — see CaptureArchive.
            string? archived = _services.Settings.KeepCaptures
                ? CaptureArchive.Save(frame, results.Select(r => r.ItemName))
                : null;

            // Results accumulate across captures rather than replacing each other (user, 2026-09-28), so an item
            // just updated stays on screen to refer back to while working on the next one. Closing one is explicit.
            foreach (ItemCheckResult result in results)
            {
                await MergeAsync(result);
                if (archived is not null && Find(result) is { } view) view.CaptureFile = archived;
            }

            // The ledger is written after a batch rather than per row — it is saved whole, and a check can settle a
            // page that already agreed without the user doing anything.
            await _services.SaveLedgerAsync();
            UpdateLedgerText();

            // The "none found" case names the window it read. That is the one message where the user needs to know
            // *what* was searched — a capture of the wrong window looks exactly like a capture with no item windows
            // open, which is how a browser being captured instead of the game went undiagnosed (user, 2026-09-29).
            StatusText.Text = results.Count switch
            {
                0 => $"No item window was found in {captured}.",
                1 => "1 item window checked.",
                _ => $"{results.Count} item windows checked.",
            };
        }
        catch (WikiUnavailableException ex)
        {
            // The whole frame is abandoned, not degraded into one identical failure per item (user, 2026-09-29).
            // Nothing local was recorded, so re-capturing once the wiki is back loses nothing.
            ReportWikiUnavailable(ex, "Nothing was checked");
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Capture failed: {ex.Message}";
        }
        finally
        {
            _capturing = false;
            CaptureButton.IsEnabled = true;
            LedgerButton.IsEnabled = true;
            SettingsButton.IsEnabled = true;
            BusyPanel.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Brings this window to the front (user, 2026-09-28), so pressing the hotkey in game puts the result in view
    /// without alt-tabbing.
    ///
    /// <see cref="Window.Activate"/> alone is unreliable: Windows refuses foreground changes from a process the user
    /// is not interacting with. Flicking <see cref="Window.Topmost"/> is the long-standing way to ask anyway, and it
    /// leaves the window ordinary afterwards rather than permanently pinned above the game.
    /// </summary>
    private void ComeToTheFront()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;

        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    /// <summary>
    /// Folds one capture into the list: a Lore capture joins the item it belongs to, a re-capture refreshes the
    /// entry already on screen, and anything else is added.
    /// </summary>
    private async Task MergeAsync(ItemCheckResult result)
    {
        ResultViewModel? existing = string.IsNullOrEmpty(result.ItemName)
            ? null
            : _results.FirstOrDefault(
                v => string.Equals(v.Result.ItemName, result.ItemName, StringComparison.OrdinalIgnoreCase));

        // The Lore tab is a second view of an item, not a second item. It joins the entry that is waiting for it,
        // and the analysis is redone so the lore actually reaches the proposed edit.
        if (result.Status == ItemCheckStatus.LoreRecorded && existing is not null && _services is not null)
        {
            BusyLabel.Text = $"Adding the lore to {existing.ItemName}…";
            ItemCheckResult updated = await _services.Pipeline.ReanalyzeAsync(existing.Result);
            existing.Load(updated);
            existing.LoreImage = result.WindowImage;
            ResultsList.SelectedItem = existing;
            ShowSelected();
            return;
        }

        if (existing is not null)
        {
            // Re-capturing an item the user already has open updates it rather than adding a duplicate; a lore
            // capture taken earlier stays attached, since it is still the same item.
            CapturedImage? lore = existing.LoreImage;
            existing.Load(result);
            existing.LoreImage = lore;
            ResultsList.SelectedItem = existing;
            ShowSelected();
            return;
        }

        var view = new ResultViewModel(result);
        _results.Add(view);
        ResultsList.SelectedItem = view;
        ShowSelected();
    }

    /// <summary>The entry showing this item, if it is still on screen.</summary>
    private ResultViewModel? Find(ItemCheckResult result) =>
        _results.FirstOrDefault(
            v => string.Equals(v.Result.ItemName, result.ItemName, StringComparison.OrdinalIgnoreCase));

    private void OnCloseResultClick(object sender, RoutedEventArgs e)
    {
        if ((sender as System.Windows.Controls.Button)?.DataContext is not ResultViewModel view) return;
        _results.Remove(view);
        if (_results.Count > 0 && ResultsList.SelectedItem is null) ResultsList.SelectedIndex = 0;
    }

    private void OnResultSelected(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => ShowSelected();

    private void ShowSelected()
    {
        // Collapsed rather than left to the bindings: with no selection there is no DataContext for them to resolve
        // against, so each `Visibility` falls back to Visible and the whole empty scaffold renders.
        bool selected = ResultsList.SelectedItem is not null;
        DetailScroller.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = selected ? Visibility.Collapsed : Visibility.Visible;

        var view = ResultsList.SelectedItem as ResultViewModel;
        WindowImage.Source = view?.Result.WindowImage is { } image ? ToBitmap(image) : null;
        LoreImage.Source = view?.LoreImage is { } lore ? ToBitmap(lore) : null;

        // The wiki's icon is a 40x40 file and the game draws its own at roughly 1.1x, so both are shown at 4x —
        // large enough to compare by eye, which is the whole reason they are here.
        CapturedIconImage.Source = view?.Result.CapturedIconImage is { } shot ? ToBitmap(shot, 4) : null;
        WikiIconImage.Source = view?.Result.WikiIconImage is { } wiki ? ToBitmap(wiki, 4) : null;
        // The library's match, at the same 4x, so comparing it against the captured strip beside it is a glance.
        MatchedIconImage.Source = view?.MatchedIconImage is { } matched ? ToBitmap(matched, 4) : null;
    }

    /// <summary>
    /// Does whatever this item's icon needs: uploads the matched file, points the page's <c>lucy_img_ID</c> at it,
    /// or both.
    ///
    /// **Only the upload is confirmed, because only the upload is permanent.** Setting the id edits the text in the
    /// box, which the user then reads in the diff and saves or does not — ordinary reviewable work. Publishing a
    /// file is not: the client refuses rather than overwrites, so the worst outcome of a mistake is a refusal, but a
    /// file uploaded under the wrong name needs an admin to clean up, and the name is the one thing the user can
    /// check that the tool cannot — so the confirmation says it.
    ///
    /// **A failed upload stops before the id is written.** Pointing a page at a file that is not there would trade a
    /// wrong picture for no picture, which is the one way this could leave the page worse than it found it.
    /// </summary>
    private async void OnIconActionClick(object sender, RoutedEventArgs e)
    {
        if (ResultsList.SelectedItem is not ResultViewModel view || _services is null) return;
        if (view.Result.IconSuggestion is not { } suggestion) return;

        bool upload = suggestion.NeedsUpload;
        if (upload && MessageBox.Show(
                this,
                $"Upload this icon to the wiki as '{suggestion.WikiFileName}'?\n\nCheck it against the in-game " +
                "icon first — only a wiki admin can delete a file once it is uploaded.",
                "Upload an item icon",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;

        view.IsBusy = true;
        try
        {
            if (upload)
            {
                IconUploadResult uploaded = await _services.Pipeline.UploadIconAsync(suggestion.IconId);
                view.RecordIconUpload(uploaded.Uploaded, uploaded.Url, uploaded.Error);
                if (!uploaded.Uploaded) return;
            }

            // On a creation the id is already in the generated text — the file was the only thing missing. On an
            // existing page the id *is* the fix, and this is the press that writes it: see ItemPageEditor.WithIconId
            // for why it goes through the ordinary parameter edit rather than being spliced in here.
            if (!view.IsCreation)
                view.ApplyIconId(
                    ItemPageEditor.WithIconId(view.Wikitext, suggestion.IconId, _services.Mapping), suggestion.IconId);
        }
        catch (WikiUnavailableException ex)
        {
            ReportWikiUnavailable(ex, "The icon was not uploaded");
        }
        finally
        {
            view.IsBusy = false;
        }
    }

    /// <summary>
    /// Creates the page for an item the wiki has never heard of.
    ///
    /// **Confirmed more pointedly than an edit is**, because creating is the one write this tool does that nobody
    /// here can undo: an ordinary editor on this wiki cannot delete a page, so a page created at the wrong title
    /// stays. The title is in the prompt for that reason — it is the part that is permanent, and the part the user
    /// is the only one able to judge.
    /// </summary>
    private async void OnCreateClick(object sender, RoutedEventArgs e)
    {
        if (ResultsList.SelectedItem is not ResultViewModel view || _services is null) return;
        if (view.Result.Creation is not { } creation) return;

        // **Asked of the text on screen, not of the proposal.** The box is editable and typing the icon ID is the
        // first thing the user does on a creation, so the proposal's answer is "missing" on every page the tool
        // generates and would warn about a gap the user had just filled in (bug found by the user, 2026-10-02).
        // `ItemPageCreator.HasIconId` is the same question `CreateAsync` asks of the saved text to decide the
        // ledger outcome, which is what keeps the dialog and the ledger row agreeing about the same page.
        string warning = ItemPageCreator.HasIconId(view.Wikitext, _services.Mapping)
            ? ""
            : "\n\nIt has no lucy_img_ID, so the item box will show no artwork until one is added.";

        if (MessageBox.Show(
                this,
                $"Create the page '{creation.Title}'?\n\nNobody on this wiki can delete a page, so the title is " +
                $"permanent once this is saved.{warning}",
                "Create a wiki page",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;

        view.IsBusy = true;
        try
        {
            CommitResult commit = await _services.Pipeline
                .CreateAsync(view.Result, view.Wikitext, view.Summary);
            await _services.SaveLedgerAsync();
            UpdateLedgerText();

            view.Outcome = commit.Status == CommitStatus.Committed
                ? $"Created as revision {commit.RevisionId}."
                : null;

            view.Settled = commit.Status == CommitStatus.Committed;

            // Normally finds nothing — the generated text is laid out before it is ever shown. It is offered anyway
            // because the box is editable, and a hand-edit is exactly what a formatting pass is for.
            view.Formatting = commit.Formatting;
            foreach (string note in commit.FormattingNotes)
                if (!view.Warnings.Contains(note))
                    view.Warnings.Add(note);

            StatusText.Text = view.Outcome ?? commit.Error ?? "The page was not created.";

            if (commit.Status == CommitStatus.Failed)
                MessageBox.Show(this, commit.Error, "Nothing was written", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (WikiUnavailableException ex)
        {
            ReportWikiUnavailable(ex, "The page was not created");
        }
        finally
        {
            view.IsBusy = false;
        }
    }

    private async void OnCommitClick(object sender, RoutedEventArgs e)
    {
        if (ResultsList.SelectedItem is not ResultViewModel view || _services is null) return;

        // Confirmed explicitly, because this writes to a public wiki under the user's own account and cannot be
        // undone by this tool — an ordinary editor here cannot delete a revision.
        if (MessageBox.Show(
                this,
                $"Save this edit to '{view.PageTitle}'?\n\n{view.Summary}",
                "Save to the wiki",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        view.IsBusy = true;
        try
        {
            // Logging in is the pipeline's gate now, not this handler's — see AppServices.BeforeWriting. Doing it
            // here as well is what let the formatting commit ship without it.
            CommitResult commit = await _services.Pipeline.CommitAsync(view.Result, view.Wikitext, view.Summary);
            await _services.SaveLedgerAsync();
            UpdateLedgerText();

            view.Outcome = commit.Status switch
            {
                CommitStatus.Committed => $"Saved as revision {commit.RevisionId}.",
                CommitStatus.NoChange => "The wiki found the text identical — recorded as a match.",
                _ => null,
            };

            // Settled only when the write actually landed — a refused or failed commit leaves the item wanting
            // attention, which is exactly what the status is read for.
            view.Settled = commit.Status is CommitStatus.Committed or CommitStatus.NoChange;

            // The formatting pass has already run against the page as it now stands; offering it is a prompt, never
            // an automatic second write.
            view.Formatting = commit.Formatting;
            foreach (string note in commit.FormattingNotes)
                if (!view.Warnings.Contains(note))
                    view.Warnings.Add(note);

            StatusText.Text = view.Outcome ?? commit.Error ?? "The edit did not go through.";

            if (commit.Status is CommitStatus.PageChangedSinceCheck or CommitStatus.Failed)
                MessageBox.Show(this, commit.Error, "Nothing was written", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (WikiUnavailableException ex)
        {
            ReportWikiUnavailable(ex, "The edit was not saved");
        }
        finally
        {
            view.IsBusy = false;
        }
    }

    private async void OnSkipClick(object sender, RoutedEventArgs e)
    {
        if (ResultsList.SelectedItem is not ResultViewModel view || _services is null) return;

        _services.Pipeline.RecordSkipped(view.Result);
        await _services.SaveLedgerAsync();
        UpdateLedgerText();

        // Deliberately not phrased as "done": Skipped never counts as checked, so the item comes back next capture.
        view.Outcome = "Skipped — it will come back on the next capture.";
        StatusText.Text = $"'{view.ItemName}' skipped.";
    }

    private async void OnCommitFormattingClick(object sender, RoutedEventArgs e)
    {
        if (ResultsList.SelectedItem is not ResultViewModel { Formatting: { } proposal } view || _services is null)
            return;

        view.IsBusy = true;
        try
        {
            // What is in the box, not what the formatter proposed — the user may have repositioned something.
            CommitResult commit = await _services.Pipeline.CommitFormattingAsync(
                proposal with { Formatted = view.FormattedWikitext });

            view.FormattingOutcome = commit.Status switch
            {
                CommitStatus.Committed => $"Formatting saved as revision {commit.RevisionId}.",
                CommitStatus.NoChange => "The wiki found the text identical.",
                _ => null,
            };

            StatusText.Text = view.FormattingOutcome ?? commit.Error ?? "The formatting edit did not go through.";

            if (commit.Status is CommitStatus.PageChangedSinceCheck or CommitStatus.Failed)
                MessageBox.Show(this, commit.Error, "Nothing was written", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (WikiUnavailableException ex)
        {
            ReportWikiUnavailable(ex, "The formatting was not saved");
        }
        finally
        {
            view.IsBusy = false;
        }
    }

    private async void OnMarkCheckedClick(object sender, RoutedEventArgs e)
    {
        if (ResultsList.SelectedItem is not ResultViewModel view || _services is null) return;

        _services.Pipeline.RecordCheckedByHand(view.Result);
        await _services.SaveLedgerAsync();
        UpdateLedgerText();

        view.Settled = true;
        view.Outcome = "Marked as checked — it will not come back unless the item itself changes.";
        StatusText.Text = $"'{view.ItemName}' marked as checked.";

        // Settling the data question is what makes formatting safe to offer (user, 2026-09-29): the page is one the
        // user has just said is right, so the formatter is not being asked to understand data nobody has vouched for.
        // No request — the page was fetched by the check and nothing has changed it since.
        if (view.Result.Page is { } page)
            view.Formatting = _services.Pipeline.PrepareFormatting(page).Proposal;
    }

    /// <summary>
    /// Sends the wheel to the page instead of letting a nested control eat it.
    ///
    /// A <see cref="System.Windows.Controls.DataGrid"/> brings its own <see cref="ScrollViewer"/>, which handles the
    /// wheel whether or not it has anywhere to scroll — so the page under the cursor sat still (user, 2026-09-28).
    /// Re-raising the event on the parent is the standard way out; the grid itself is set never to scroll, so
    /// nothing is lost by taking the wheel off it.
    /// </summary>
    private void OnPassScrollToPage(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (sender is not UIElement source) return;
        e.Handled = true;

        DetailScroller.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = System.Windows.Input.Mouse.MouseWheelEvent,
            Source = source,
        });
    }

    private void OnOpenPageClick(object sender, RoutedEventArgs e)
    {
        if ((sender as Hyperlink)?.DataContext is not ResultViewModel { PageUrl: { } url }) return;
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    /// <summary>
    /// The ledger, and what it is still holding for a human.
    ///
    /// Modal, deliberately: the ledger is shared state that a capture writes to, so a view of it left open beside one
    /// would show rows that were already wrong. See <see cref="LedgerWindow"/>.
    /// </summary>
    private void OnLedgerClick(object sender, RoutedEventArgs e)
    {
        if (_services is null) return;

        _dialogOpen = true;
        try
        {
            new LedgerWindow(_services.Ledger, _services.Mapping.Version, _services.SaveLedgerAsync) { Owner = this }
                .ShowDialog();
        }
        finally
        {
            _dialogOpen = false;
        }

        // A row may have been forgotten while it was open.
        UpdateLedgerText();
    }

    /// <summary>The UI font, the wiki login and the mapping. Modal, and disabled during a capture — see
    /// <see cref="SettingsWindow"/>.</summary>
    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        if (_services is null) return;

        _dialogOpen = true;
        try
        {
            new SettingsWindow(_services) { Owner = this }.ShowDialog();
        }
        finally
        {
            _dialogOpen = false;
        }

        // The hotkey may have changed while it was open.
        UpdateHotKeyLabel();
    }

    /// <summary>Counts the unsettled rows alongside the total, because over a long session that is the number worth
    /// glancing at — the total only ever grows.</summary>
    /// <summary>
    /// The wiki is gone — say so once, loudly, and stop.
    ///
    /// **A critical dialog rather than a status line** (user, 2026-09-29): this tool exists to compare captures
    /// against the wiki, so without it there is nothing useful left to do, and quietly carrying on produces a screen
    /// of identical failures that reads like a tool malfunction rather than an outage.
    /// </summary>
    private void ReportWikiUnavailable(WikiUnavailableException ex, string consequence)
    {
        StatusText.Text = $"{consequence} — {ex.Message}";
        MessageBox.Show(
            this,
            $"{ex.Message}\n\n{consequence}. Nothing was written to the wiki, and nothing was recorded locally, so " +
            "try again once the wiki is reachable.",
            "The wiki is unavailable",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private void UpdateLedgerText()
    {
        if (_services is null)
        {
            LedgerText.Text = "";
            return;
        }

        LedgerSummary summary = LedgerSummary.Of(_services.Ledger.Entries);
        LedgerText.Text = summary.NeedsAttention == 0
            ? $"{summary.Total} checked"
            : $"{summary.Total} checked · {summary.NeedsAttention} need attention";
    }

    /// <summary>The capture is tightly packed top-down BGRA32, which is exactly <c>Bgra32</c>'s layout, so this is a
    /// copy rather than a conversion.</summary>
    private static BitmapSource ToBitmap(CapturedImage image, int magnify = 1)
    {
        BitmapSource bitmap = BitmapSource.Create(
            image.Width, image.Height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null,
            image.Pixels, image.Width * 4);

        if (magnify <= 1) return bitmap;

        // Nearest-neighbour, so a magnified icon shows the artwork rather than a blurred guess at it — the user is
        // being asked to compare two sprites, and interpolation would invent detail in both.
        var scaled = new TransformedBitmap(bitmap, new System.Windows.Media.ScaleTransform(magnify, magnify));
        scaled.Freeze();
        return scaled;
    }

    protected override void OnClosed(EventArgs e)
    {
        _services?.Dispose();
        base.OnClosed(e);
    }
}
