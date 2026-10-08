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
    /// True while the Settings or History window is open. **The hotkey is global and bypasses modality**, so
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
        Dialog.Opened += (_, _) => UpdateBrowserVisibility();
        Dialog.Closed += (_, _) => UpdateBrowserVisibility();
        _results.CollectionChanged += (_, _) =>
            ResultsHeader.Visibility = _results.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        // Built off the UI thread: RapidOcrEngine loads three ONNX models in its constructor, which is seconds of a
        // frozen window if it happens here. The capture button stays disabled until it is ready.
        CaptureButton.IsEnabled = false;
        LedgerLink.IsEnabled = false;
        AttentionLink.IsEnabled = false;
        SettingsButton.IsEnabled = false;
        StatusText.Text = "Starting…";
        _ = StartUpAsync();
    }

    /// <summary>
    /// The capture hotkey fired. It is registered globally so it fires while the game has focus, which is the whole
    /// point — Graphics Capture reads an unfocused window fine, so the user never has to leave the game to capture.
    /// It arrives on the hotkey's own thread, hence the dispatch.
    /// </summary>
    private void OnHotKeyPressed(object? sender, EventArgs e) => Dispatcher.Invoke(() =>
    {
        // An in-app dialog as well: it is waiting on an answer about what is on screen.
        if (_dialogOpen || Dialog.IsOpen) return;

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

        HotKeyRun.Text = _services.HotKeyProblem is null
            ? $"Hotkey: {_services.Settings.HotKey}"
            : $"Hotkey {_services.Settings.HotKey} unavailable";
        HotKeyLink.ToolTip = _services.HotKeyProblem is { } problem
            ? $"{problem} Click to choose another."
            : "Change the hotkey";
        EmptyState.Text = _services.HotKeyProblem is null
            ? $"Open an item window in game and press {_services.Settings.HotKey}, or press Capture now.\n\n" +
              "Every item window in the " +
              "capture is checked against the wiki and listed on the left."
            : "Open an item window in game and press Capture now.\n\nEvery item window in the capture is checked " +
              "against the wiki and listed on the left.";
    }

    private async Task StartUpAsync()
    {
        try
        {
            _services = await Task.Run(() => new AppServices());
            _services.HotKeyPressed += OnHotKeyPressed;
            UpdateHotKeyLabel();
            CaptureButton.IsEnabled = true;
            LedgerLink.IsEnabled = true;
            AttentionLink.IsEnabled = true;
            SettingsButton.IsEnabled = true;
            UpdateLedgerText();
            StatusText.Text = "Ready. Press the hotkey with an item window open in game.";
        }
        catch (Exception ex)
        {
            // Most likely the RapidOCR models did not make it next to the executable — the documented packaging
            // trap. Fatal (user, 2026-10-07): nothing in the window works without the services, so it says why in a
            // dialog and exits rather than sitting there in a state that only looks usable.
            App.Record(ex);
            await Dialog.TellAsync(
                "Could not start", $"{ex.Message}\n\nThe details are in:\n{App.ErrorLogFile}", DialogTone.Error);
            Application.Current.Shutdown();
        }
    }

    private void OnCaptureClick(object sender, RoutedEventArgs e) => _ = CaptureAsync();


    private async Task CaptureAsync()
    {
        if (_capturing || _services is null) return;
        _capturing = true;
        CaptureButton.IsEnabled = false;
        // The ledger too: a capture writes rows, so a ledger view opened mid-capture would show some of them stale.
        // The window being modal stops a capture starting while it is open; this is the same guard the other way.
        LedgerLink.IsEnabled = false;
        AttentionLink.IsEnabled = false;
        // Settings too: the capture in progress reads with the configured font, so switching it mid-capture would
        // read part of a frame in each.
        SettingsButton.IsEnabled = false;
        BusyPanel.Visibility = Visibility.Visible;

        try
        {
            // **Off the UI thread, all of it** (user, 2026-10-07: the window hung, and the progress label never moved
            // past the screenshot). Both OCR engines do their work synchronously and hand back a finished task, so
            // awaiting them here ran the whole full-frame pass on this thread: nothing could repaint, and every
            // progress report queued behind it. The ledger and the pending-lore table are locked for exactly this,
            // since the review screen's buttons stay live while a capture runs.
            AppServices services = _services;
            ShowBusy("Taking game screenshot…");
            (CapturedImage? frame, string? problem, string? captured) =
                await Task.Run(() => services.CaptureGameWindowAsync());
            if (frame is null)
            {
                ReportCaptureProblem(problem ?? "The game window could not be captured.");
                return;
            }

            // Each stage named as it starts (user, 2026-10-07): finding the windows is the slow full-frame OCR pass,
            // and folding it into the screenshot's label made taking the screenshot look slow.
            var progress = new Progress<CheckProgress>(p => ShowBusy(p.Stage switch
            {
                CheckStage.FindingWindows => "Finding item windows in screenshot…",
                _ when p.Of == 1 => "Checking the item against the wiki…",
                _ => $"Checking item {p.Window} of {p.Of} against the wiki…",
            }));
            IReadOnlyList<ItemCheckResult> results =
                await Task.Run(() => services.Pipeline.CheckAsync(frame, progress: progress));

            // Kept when Settings > Saved captures says so, named after what was in it, so a bug can be reported by
            // naming an item rather than by keeping it in the game — see CaptureArchive. Encoding a full frame as PNG
            // takes long enough to be felt, so it is off the UI thread too.
            string? archived = services.Settings.KeepCaptures
                ? await Task.Run(() => CaptureArchive.Save(frame, results.Select(r => r.ItemName).ToList()))
                : null;

            // Results accumulate across captures rather than replacing each other (user, 2026-09-28), so an item
            // just updated stays on screen to refer back to while working on the next one. Closing one is explicit.
            //
            // **An item the user is still working on stays selected** (user, 2026-10-08). Otherwise the last window in
            // the frame took the selection — and capturing a Lore tab with several item windows open meant hunting for
            // the item it was for. With nothing in progress, the new capture is shown as before.
            _keepSelected = ResultsList.SelectedItem is ResultViewModel { IsDone: false } current ? current : null;
            try
            {
                foreach (ItemCheckResult result in results)
                {
                    await MergeAsync(result);
                    if (archived is not null && Find(result) is { } view) view.CaptureFile = archived;
                }
            }
            finally
            {
                _keepSelected = null;
            }

            // The ledger is written after a batch rather than per row — it is saved whole, and a check can settle a
            // page that already agreed without the user doing anything.
            await _services.SaveLedgerAsync();
            UpdateLedgerText();

            // The "none found" case names the window it read. That is the one message where the user needs to know
            // *what* was searched — a capture of the wrong window looks exactly like a capture with no item windows
            // open, which is how a browser being captured instead of the game went undiagnosed (user, 2026-09-29).
            if (results.Count == 0)
                ReportCaptureProblem($"No item window was found in {captured}.");
            else
                StatusText.Text = results.Count == 1 ? "1 item window checked." : $"{results.Count} item windows checked.";
        }
        catch (WikiUnavailableException ex)
        {
            // The whole frame is abandoned, not degraded into one identical failure per item (user, 2026-09-29).
            // Nothing local was recorded, so re-capturing once the wiki is back loses nothing.
            ReportWikiUnavailable(ex, "Nothing was checked");
        }
        catch (Exception ex)
        {
            App.Record(ex);
            ReportCaptureProblem($"Capture failed: {ex.Message}");
        }
        finally
        {
            _capturing = false;
            CaptureButton.IsEnabled = true;
            LedgerLink.IsEnabled = true;
            AttentionLink.IsEnabled = true;
            SettingsButton.IsEnabled = true;
            BusyPanel.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>A capture that produced nothing to review: said in the status line and in a dialog, because the
    /// hotkey is pressed from inside the game and a status line alone is easy to miss (user, 2026-10-07).</summary>
    private void ReportCaptureProblem(string message)
    {
        StatusText.Text = message;
        _ = Dialog.TellAsync("Nothing to check", message, DialogTone.Warning);
    }

    /// <summary>The busy label and the status line say the same thing while work is under way.</summary>
    private void ShowBusy(string text)
    {
        BusyLabel.Text = text;
        StatusText.Text = text;
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
            Select(existing);
            return;
        }

        // Partway through, and nothing about the item has changed: the work on screen stays (see KeepsWorkAgainst).
        if (existing is not null && existing.KeepsWorkAgainst(result)) return;

        if (existing is not null)
        {
            // Re-capturing an item the user already has open updates it rather than adding a duplicate; a lore
            // capture taken earlier stays attached, since it is still the same item.
            CapturedImage? lore = existing.Result.Status == ItemCheckStatus.LoreRecorded
                ? existing.Result.WindowImage
                : existing.LoreImage;
            existing.Load(result);
            existing.LoreImage = lore;
            Select(existing);
            return;
        }

        var view = new ResultViewModel(result);
        _results.Add(view);
        Select(view);
    }

    /// <summary>The item a capture is folding in, kept selected rather than moved off — see the capture loop.</summary>
    private ResultViewModel? _keepSelected;

    /// <summary>Shows an item a capture has just added or changed, unless the user is in the middle of another — in
    /// which case that one stays, refreshed if it was this.</summary>
    private void Select(ResultViewModel view)
    {
        if (_keepSelected is { } kept && kept != view && _results.Contains(kept)) return;
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

    /// <summary>Closes every item. No confirmation, like closing one: a tab is a view of a capture, and closing it
    /// changes nothing on the wiki or in the ledger.</summary>
    private void OnCloseAllClick(object sender, RoutedEventArgs e) => _results.Clear();

    private void OnResultSelected(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => ShowSelected();

    /// <summary>
    /// The narrowest the edit pane may get with the screenshot pane beside it. Below this the screenshot moves to the
    /// top of the page being shown, where it scrolls with the rest (user, 2026-10-07: "above when especially narrow").
    /// </summary>
    private const double NarrowestEditPane = 420;

    private void OnDetailAreaSizeChanged(object sender, SizeChangedEventArgs e) => PlaceScreenshot();

    /// <summary>
    /// Puts the screenshot beside the edit pane, or at the top of the page on show when there is not room for both.
    ///
    /// **Not on the two browser pages in a narrow window**: they are native windows that WPF cannot put anything above
    /// inside a scroller, so there the screenshot waits, hidden, on the page it was last on.
    /// </summary>
    private void PlaceScreenshot()
    {
        bool beside = DetailArea.ActualWidth >= (double)FindResource("ShotPaneWidth") + NarrowestEditPane;
        System.Windows.Controls.Panel? target = beside ? ShotPaneHost
            : _shownPage == SubmitPage ? SubmitShotHost
            : _shownPage == FormattingPage ? FormattingShotHost
            : _shownPage is null || _shownPage == DetailScroller ? InlineShotHost
            : null;

        ShotPane.Visibility = beside ? Visibility.Visible : Visibility.Collapsed;
        if (target is not null && ShotStack.Parent != target)
        {
            ((System.Windows.Controls.Panel)ShotStack.Parent).Children.Remove(ShotStack);
            target.Children.Add(ShotStack);
        }

        foreach (System.Windows.Controls.Panel host in new[] { InlineShotHost, SubmitShotHost, FormattingShotHost })
            host.Visibility = ShotStack.Parent == host ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Compares an item the ledger let skip the wiki, now, without waiting for its data to change — the per-item form
    /// of the old "Re-check anyway" box (user, 2026-10-07). It goes through the same re-analysis a lore capture uses,
    /// which asks the wiki and records the outcome, so the ledger ends up exactly where a fresh check would leave it.
    /// </summary>
    private async void OnReCheckClick(object sender, RoutedEventArgs e)
    {
        if (ResultsList.SelectedItem is not ResultViewModel { CanReCheck: true } view) return;
        await ReloadFromWikiAsync(view, $"Checking {view.ItemName} against the wiki…", "re-checked");
    }

    /// <summary>
    /// "Edit again" and "Make another edit": back to the first page, compared afresh against the page as it now
    /// stands (user, 2026-10-07). After a save that is the only honest starting point — the earlier comparison was
    /// against a revision that is no longer there.
    /// </summary>
    private async void OnEditAgainClick(object sender, RoutedEventArgs e)
    {
        if (ResultsList.SelectedItem is not ResultViewModel { Result.Item: not null } view) return;
        await ReloadFromWikiAsync(view, $"Comparing {view.ItemName} against the wiki again…", "compared again");
    }

    /// <summary>Re-analyzes one item against the wiki and shows it from its first page.</summary>
    private async Task ReloadFromWikiAsync(ResultViewModel view, string busy, string done)
    {
        if (_services is null) return;

        AppServices services = _services;
        view.IsBusy = true;
        ShowBusy(busy);
        BusyPanel.Visibility = Visibility.Visible;
        try
        {
            ItemCheckResult updated = await Task.Run(() => services.Pipeline.ReanalyzeAsync(view.Result, refreshVerification: true));
            CapturedImage? lore = view.LoreImage;
            view.Load(updated);
            view.LoreImage = lore;
            await services.SaveLedgerAsync();
            UpdateLedgerText();
            if (ResultsList.SelectedItem == view)
            {
                ShowImages(view);
                ShowStep(view, animate: true);
            }

            StatusText.Text = $"'{view.ItemName}' {done}.";
        }
        catch (WikiUnavailableException ex)
        {
            ReportWikiUnavailable(ex, "Nothing was checked");
        }
        finally
        {
            view.IsBusy = false;
            if (!_capturing) BusyPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowSelected()
    {
        // Collapsed rather than left to the bindings: with no selection there is no DataContext for them to resolve
        // against, so each `Visibility` falls back to Visible and the whole empty scaffold renders.
        bool selected = ResultsList.SelectedItem is not null;
        DetailArea.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = selected ? Visibility.Collapsed : Visibility.Visible;

        var view = ResultsList.SelectedItem as ResultViewModel;
        ShowImages(view);
        // Each item keeps its own page of the review; switching items shows it in place, without a slide.
        if (view is not null) ShowStep(view, animate: false);
        else UpdateBrowserVisibility();
    }

    private void ShowImages(ResultViewModel? view)
    {
        WindowImage.Source = view?.Result.WindowImage is { } image ? Bitmaps.From(image) : null;
        LoreImage.Source = view?.LoreImage is { } lore ? Bitmaps.From(lore) : null;
        ShotLabel.Text = WindowImage.Source is not null && LoreImage.Source is not null
            ? "Captured screenshots"
            : "Captured screenshot";
        ShotLabel.Visibility = WindowImage.Source is null && LoreImage.Source is null
            ? Visibility.Collapsed
            : Visibility.Visible;

        // The wiki's icon is a 40x40 file and the game draws its own at roughly 1.1x, so both are shown at 4x —
        // large enough to compare by eye, which is the whole reason they are here.
        CapturedIconImage.Source = view?.Result.CapturedIconImage is { } shot ? Bitmaps.From(shot, 4) : null;
        WikiIconImage.Source = view?.Result.WikiIconImage is { } wiki ? Bitmaps.From(wiki, 4) : null;
        // The library's match, at the same 4x, so comparing it against the captured strip beside it is a glance.
        MatchedIconImage.Source = view?.MatchedIconImage is { } matched ? Bitmaps.From(matched, 4) : null;
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
        if (upload && !await Dialog.AskAsync(
                $"Upload '{suggestion.WikiFileName}'?",
                "Check it against the in-game icon first — only a wiki admin can delete a file once it is uploaded.",
                "Upload", tone: DialogTone.Warning, writesToWiki: true))
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

            // The id is written either way (user, 2026-10-07: never upload a file the page does not then use). On a
            // creation it is normally in the generated text already, so this changes nothing unless the user had
            // typed another; on an existing page the id *is* the fix. See ItemPageEditor.WithIconId for why it goes
            // through the ordinary parameter edit rather than being spliced in here.
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

    // ============================ the step-by-step review ============================

    private FrameworkElement? _shownPage;
    private ReviewStep _shownStep;
    private ResultViewModel? _shownView;
    private int _slide;
    private bool _sliding;

    private FrameworkElement PageFor(ReviewStep step) => step switch
    {
        ReviewStep.Preview => PreviewPage,
        ReviewStep.Submit => SubmitPage,
        ReviewStep.Formatting => FormattingPage,
        ReviewStep.Done => DonePage,
        _ => DetailScroller,
    };

    private FrameworkElement[] Pages => [DetailScroller, PreviewPage, SubmitPage, FormattingPage, DonePage];

    /// <summary>Moves an item to another page of its review, sliding if it is the one on screen.</summary>
    private void GoTo(ResultViewModel view, ReviewStep step)
    {
        view.Step = step;
        if (ResultsList.SelectedItem == view) ShowStep(view, animate: true);
    }

    /// <summary>
    /// Shows the page the selected item is on, and does whatever arriving there needs — rendering the preview, building
    /// the final diff, loading the live page.
    /// </summary>
    private void ShowStep(ResultViewModel view, bool animate)
    {
        ReviewStep step = view.HasReview ? view.Step : ReviewStep.Edit;
        FrameworkElement incoming = PageFor(step);
        FrameworkElement? outgoing = _shownPage;
        bool forward = step >= _shownStep;
        bool sameItem = view == _shownView;

        _shownPage = incoming;
        _shownStep = step;
        _shownView = view;

        // Anything left over from an interrupted slide is put away first.
        int slide = ++_slide;
        foreach (FrameworkElement page in Pages)
        {
            if (page == incoming || page == outgoing) continue;
            page.BeginAnimation(OpacityProperty, null);
            page.RenderTransform = null;
            page.Visibility = Visibility.Collapsed;
        }

        incoming.Visibility = Visibility.Visible;
        if (incoming != outgoing && incoming is System.Windows.Controls.ScrollViewer scroller) scroller.ScrollToTop();
        PlaceScreenshot();

        if (!animate || !sameItem || outgoing is null || outgoing == incoming || PageHost.ActualWidth <= 0)
        {
            if (outgoing is not null && outgoing != incoming) outgoing.Visibility = Visibility.Collapsed;
            incoming.RenderTransform = null;
            _sliding = false;
        }
        else
        {
            Slide(outgoing, incoming, forward, slide);
        }

        UpdateBrowserVisibility();
        _ = ArriveAsync(view, step);
    }

    /// <summary>The slide itself: the old page out one side, the new one in from the other.</summary>
    private void Slide(FrameworkElement outgoing, FrameworkElement incoming, bool forward, int slide)
    {
        _sliding = true;
        double distance = PageHost.ActualWidth * (forward ? 1 : -1);
        var duration = TimeSpan.FromMilliseconds(220);
        var ease = new System.Windows.Media.Animation.CubicEase
        {
            EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut,
        };

        var outMove = new System.Windows.Media.TranslateTransform();
        var inMove = new System.Windows.Media.TranslateTransform(distance, 0);
        outgoing.RenderTransform = outMove;
        incoming.RenderTransform = inMove;

        var arrive = new System.Windows.Media.Animation.DoubleAnimation(distance, 0, duration) { EasingFunction = ease };
        arrive.Completed += (_, _) =>
        {
            // A newer page change has taken over; leave its elements alone.
            if (slide != _slide) return;
            outgoing.Visibility = Visibility.Collapsed;
            outgoing.RenderTransform = null;
            incoming.RenderTransform = null;
            _sliding = false;
            UpdateBrowserVisibility();
        };

        outMove.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty,
            new System.Windows.Media.Animation.DoubleAnimation(0, -distance, duration) { EasingFunction = ease });
        inMove.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, arrive);
    }

    /// <summary>What each page needs on arrival. Only ever acts for the item still on screen.</summary>
    private async Task ArriveAsync(ResultViewModel view, ReviewStep step)
    {
        if (_services is null) return;

        switch (step)
        {
            case ReviewStep.Preview:
                await ShowPreviewAsync(view);
                break;

            case ReviewStep.Submit:
                view.RefreshSubmitDiff();
                // Asked of the text on screen, not of the proposal — typing the icon ID is the first thing the user
                // does on a creation (bug found by the user, 2026-10-02). Same question CreateAsync asks of the saved
                // text, so the warning and the ledger row agree.
                view.SubmitNotice = view.IsCreation && !ItemPageCreator.HasIconId(view.Wikitext, _services.Mapping)
                    ? "It has no lucy_img_ID, so the item box will show no artwork until one is added."
                    : null;
                break;

            case ReviewStep.Formatting:
                bool nothingToLayOut = view.Formatting is null;
                bool quiet = nothingToLayOut && !view.HasFormattingNotes;
                FormattingGood.Visibility = quiet ? Visibility.Visible : Visibility.Collapsed;
                FormattingNextButton.Visibility = nothingToLayOut ? Visibility.Visible : Visibility.Collapsed;

                // Nothing to do and nothing to say: a moment to read that, then on (user, 2026-10-07).
                if (quiet)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1.2));
                    if (ResultsList.SelectedItem == view && view.Step == ReviewStep.Formatting && !Dialog.IsOpen)
                        GoTo(view, ReviewStep.Done);
                }

                break;

            case ReviewStep.Done:
                await ShowLivePageAsync(view);
                break;
        }
    }

    private void OnStepClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not StepViewModel { CanJump: true } step) return;
        if (ResultsList.SelectedItem is not ResultViewModel view) return;
        GoTo(view, step.Step);
    }

    /// <summary>
    /// The first page's Next: on to Preview when there is something to save, or — for a page that already agrees and
    /// was settled by the check — straight to its formatting.
    /// </summary>
    private void OnNextClick(object sender, RoutedEventArgs e)
    {
        if (ResultsList.SelectedItem is not ResultViewModel { CanGoNext: true } view || _services is null) return;

        if (view.HasChangesToSubmit)
        {
            GoTo(view, ReviewStep.Preview);
            return;
        }

        // No request: the page was fetched by the check and nothing has changed it since.
        if (view.Result.Page is { } page)
        {
            (FormattingProposal? proposal, IReadOnlyList<string> notes) = _services.Pipeline.PrepareFormatting(page);
            view.SetFormatting(proposal, notes);
        }

        view.SkippedSubmit = true;
        GoTo(view, ReviewStep.Formatting);
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        if (ResultsList.SelectedItem is not ResultViewModel { Step: > ReviewStep.Edit } view) return;
        GoTo(view, view.Step - 1);
    }

    private void OnPreviewNextClick(object sender, RoutedEventArgs e)
    {
        if (ResultsList.SelectedItem is not ResultViewModel view) return;
        GoTo(view, ReviewStep.Submit);
    }

    /// <summary>
    /// Saves the edit or creates the page — what the Submit page exists for.
    ///
    /// **No confirmation** (user, 2026-10-07): the Submit page shows exactly what is written, under the title it goes
    /// to, and reaching it took two deliberate steps, so a dialog asking again would be the same question twice.
    /// </summary>
    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (ResultsList.SelectedItem is not ResultViewModel { CanSave: true } view || _services is null) return;

        bool creating = view.IsCreation;
        view.IsBusy = true;
        StatusText.Text = creating ? "Creating the page…" : "Saving the edit…";
        try
        {
            // Logging in is the pipeline's gate, not this handler's — see AppServices.BeforeWriting.
            CommitResult commit = creating
                ? await _services.Pipeline.CreateAsync(view.Result, view.Wikitext, view.Summary)
                : await _services.Pipeline.CommitAsync(view.Result, view.Wikitext, view.Summary);
            await _services.SaveLedgerAsync();
            UpdateLedgerText();

            string? outcome = commit.Status switch
            {
                CommitStatus.Committed when creating => $"Created as revision {commit.RevisionId}.",
                CommitStatus.Committed => $"Saved as revision {commit.RevisionId}.",
                CommitStatus.NoChange => "The wiki found the text identical — recorded as a match.",
                _ => null,
            };

            if (outcome is null)
            {
                StatusText.Text = commit.Error ?? "Nothing was written.";
                await Dialog.TellAsync("Nothing was written", commit.Error ?? "The wiki did not accept the edit.",
                    DialogTone.Warning);
                return;
            }

            // Settled only when the write actually landed — a refused or failed commit leaves the item wanting
            // attention, which is exactly what the status is read for.
            view.Outcome = outcome;
            view.Settled = true;
            view.Written = true;

            // The formatting pass has already run against the page as it now stands; offering it is the next page,
            // never an automatic second write.
            view.SetFormatting(commit.Formatting, commit.FormattingNotes);
            StatusText.Text = outcome;
            GoTo(view, ReviewStep.Formatting);
        }
        catch (WikiUnavailableException ex)
        {
            ReportWikiUnavailable(ex, creating ? "The page was not created" : "The edit was not saved");
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
        StatusText.Text = $"'{view.ItemName}' skipped. It will come back on the next capture.";

        // Skipping closes the item (user, 2026-10-07): there is nothing left to do with it in this session.
        CloseItem(view);
    }

    private async void OnAcceptFormattingClick(object sender, RoutedEventArgs e)
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
                CommitStatus.NoChange => "The wiki found the formatting identical.",
                _ => null,
            };

            if (view.FormattingOutcome is null)
            {
                StatusText.Text = commit.Error ?? "The formatting edit did not go through.";
                await Dialog.TellAsync("Nothing was written", commit.Error ?? "The wiki did not accept the edit.",
                    DialogTone.Warning);
                return;
            }

            StatusText.Text = view.FormattingOutcome;
            GoTo(view, ReviewStep.Done);
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

    /// <summary>Skip on the Formatting page, and its Next when there was nothing to lay out.</summary>
    private void OnSkipFormattingClick(object sender, RoutedEventArgs e)
    {
        if (ResultsList.SelectedItem is not ResultViewModel view) return;
        // Next past a page with nothing to lay out has finished formatting; Skip past a proposal has not.
        view.SkippedFormatting = view.Formatting is not null;
        GoTo(view, ReviewStep.Done);
    }

    /// <summary>Done closes the item, like its ✕ in the list: nothing on the wiki or in the ledger changes.</summary>
    private void OnDoneClick(object sender, RoutedEventArgs e)
    {
        if (ResultsList.SelectedItem is not ResultViewModel view) return;
        CloseItem(view);
    }

    private void CloseItem(ResultViewModel view)
    {
        _results.Remove(view);
        if (_results.Count > 0 && ResultsList.SelectedItem is null) ResultsList.SelectedIndex = 0;
    }

    /// <summary>
    /// Expanding the wikitext source scrolls it into view (user, 2026-10-07): it opens at the bottom of the page, so
    /// otherwise expanding it can look like nothing happened. Waits for the layout, since the editor has no height
    /// until it has been measured.
    /// </summary>
    private void OnSourceExpanded(object sender, RoutedEventArgs e)
    {
        // Only when the user opened it: switching to an item that has it open sets it too.
        if (sender is not FrameworkElement expander || !(expander.IsMouseOver || expander.IsKeyboardFocusWithin)) return;
        _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => expander.BringIntoView());
    }

    /// <summary>
    /// The "Not verified for EQL" badge, explained (user, 2026-10-07), with the page offered so the user can verify it
    /// there. The tool itself never verifies a page: verification vouches for the whole page, and the tool reads only
    /// the item window.
    /// </summary>
    private async void OnNotVerifiedClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ResultsList.SelectedItem is not ResultViewModel view) return;

        bool open = await Dialog.AskAsync(
            "Not verified for EQL",
            "The wiki keeps a list of pages that an editor has checked against EverQuest Legends — the whole page, " +
            "including where the item drops, who sells it and the quests it is in, not only what the item window " +
            "shows. Until a page is on that list, the wiki shows a notice at the top of it.\n\n" +
            "This tool only reads the item window, so it never marks a page verified itself. If you have checked the " +
            "page, type \"Verified\" into the notice on the wiki.",
            view.HasPage ? "Open the wiki" : "OK",
            view.HasPage ? "Close" : null,
            DialogTone.Info);

        if (!open || view.PageUrl is not { } url) return;
        // Whatever the user does there, the next capture should see it rather than a list read minutes ago.
        _services?.Pipeline.ExpectVerificationChange();
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
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
        {
            (FormattingProposal? proposal, IReadOnlyList<string> notes) = _services.Pipeline.PrepareFormatting(page);
            view.SetFormatting(proposal, notes);
        }

        view.SkippedSubmit = true;
        GoTo(view, ReviewStep.Formatting);
    }

    // ============================ the embedded browser ============================

    /// <summary>The wiki's own address, which the preview's site-relative links resolve against.</summary>
    private static readonly Uri WikiSite = new(AppServices.Endpoint.GetLeftPart(UriPartial.Authority) + "/");

    private Task<Microsoft.Web.WebView2.Core.CoreWebView2Environment>? _browserEnvironment;

    /// <summary>The navigation each browser is about to make on purpose; anything else is a click on a link.</summary>
    private readonly Dictionary<Microsoft.Web.WebView2.Wpf.WebView2, bool> _expectingNavigation = [];

    private readonly HashSet<Microsoft.Web.WebView2.Wpf.WebView2> _loaded = [];

    /// <summary>
    /// Readies a browser the first time it is needed, so the app does not start one at launch.
    ///
    /// **Scripts are off.** The pages shown are the wiki's own, and its scripts include the "not verified" toast, which
    /// edits the wiki's verification list when someone types into it — from this browser that would be an anonymous
    /// edit nobody meant to make. Nothing inside the review may write to the wiki except the buttons that say so, and
    /// the item box, the effect links and the categories all render without scripts. A link opens in the user's own
    /// browser rather than navigating this one away from the review.
    /// </summary>
    private async Task<bool> EnsureBrowserAsync(Microsoft.Web.WebView2.Wpf.WebView2 browser, System.Windows.Controls.TextBlock status)
    {
        if (browser.CoreWebView2 is not null) return true;

        try
        {
            _browserEnvironment ??= Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(
                userDataFolder: System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "EQLWikiAssistant", "WebView2"));
            await browser.EnsureCoreWebView2Async(await _browserEnvironment);
        }
        catch (Exception ex) when (ex is Microsoft.Web.WebView2.Core.WebView2RuntimeNotFoundException
                                       or System.Runtime.InteropServices.COMException
                                       or InvalidOperationException)
        {
            App.Record(ex);
            status.Text = "The page cannot be shown here: the Microsoft Edge WebView2 runtime is not available. " +
                          $"({ex.Message})";
            return false;
        }

        Microsoft.Web.WebView2.Core.CoreWebView2 core = browser.CoreWebView2!;
        core.Settings.IsScriptEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.IsWebMessageEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;

        core.NavigationStarting += (_, e) =>
        {
            if (_expectingNavigation.GetValueOrDefault(browser) || e.IsRedirected)
            {
                _expectingNavigation[browser] = false;
                return;
            }

            e.Cancel = true;
            OpenExternally(e.Uri);
        };
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            OpenExternally(e.Uri);
        };
        browser.NavigationCompleted += (_, e) =>
        {
            if (e.IsSuccess)
            {
                _loaded.Add(browser);
                status.Text = "";
            }
            else
            {
                status.Text = $"The page did not load ({e.WebErrorStatus}).";
            }

            UpdateBrowserVisibility();
        };

        return true;
    }

    private static void OpenExternally(string uri)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out Uri? target) &&
            (target.Scheme == Uri.UriSchemeHttps || target.Scheme == Uri.UriSchemeHttp))
            Process.Start(new ProcessStartInfo(target.AbsoluteUri) { UseShellExecute = true });
    }

    /// <summary>
    /// Shows each browser only when its page is on screen, it has finished loading, and nothing is meant to be in front
    /// of it. **A browser is a native window that WPF cannot draw over**, so a dialog or a sliding page would otherwise
    /// appear behind it.
    /// </summary>
    private void UpdateBrowserVisibility()
    {
        bool clear = !_sliding && !Dialog.IsOpen && DetailArea.Visibility == Visibility.Visible;
        PreviewBrowser.Visibility = clear && _shownPage == PreviewPage && _loaded.Contains(PreviewBrowser)
            ? Visibility.Visible : Visibility.Hidden;
        DoneBrowser.Visibility = clear && _shownPage == DonePage && _loaded.Contains(DoneBrowser)
            ? Visibility.Visible : Visibility.Hidden;
    }

    private void Navigate(Microsoft.Web.WebView2.Wpf.WebView2 browser, Action<Microsoft.Web.WebView2.Core.CoreWebView2> go)
    {
        _loaded.Remove(browser);
        UpdateBrowserVisibility();
        _expectingNavigation[browser] = true;
        go(browser.CoreWebView2);
    }

    /// <summary>
    /// Renders the text on screen through the wiki and shows it.
    ///
    /// **Sent to the wiki only now, because the user asked to see it** (user, 2026-10-07), and only once per version
    /// of the text: going back and forward without changing anything shows the rendering already made.
    /// </summary>
    private async Task ShowPreviewAsync(ResultViewModel view)
    {
        if (_services is null || !await EnsureBrowserAsync(PreviewBrowser, PreviewStatus)) return;

        string text = view.Wikitext;
        if (view.PreviewDocument is null || view.PreviewedText != text)
        {
            PreviewStatus.Text = "Asking the wiki to render the page…";
            _loaded.Remove(PreviewBrowser);
            UpdateBrowserVisibility();

            AppServices services = _services;
            try
            {
                RenderedPage rendered = await Task.Run(() => services.Pipeline.PreviewAsync(view.Result, text));
                view.PreviewDocument = rendered.ToDocument(WikiSite);
                view.PreviewedText = text;
            }
            catch (WikiUnavailableException ex)
            {
                PreviewStatus.Text = "The wiki could not be reached, so there is no preview.";
                ReportWikiUnavailable(ex, "The preview was not rendered");
                return;
            }
            catch (MediaWikiException ex)
            {
                PreviewStatus.Text = $"The wiki could not render a preview ({ex.Code}): {ex.Message}";
                return;
            }
        }

        if (ResultsList.SelectedItem != view || view.Step != ReviewStep.Preview) return;
        PreviewStatus.Text = "Loading…";
        Navigate(PreviewBrowser, core => core.NavigateToString(view.PreviewDocument!));
    }

    /// <summary>The page as readers now see it — an ordinary page view, the natural sequel to a save.</summary>
    private async Task ShowLivePageAsync(ResultViewModel view)
    {
        if (view.PageUrl is not { } url)
        {
            DoneStatus.Text = "There is no page to show.";
            return;
        }

        if (!await EnsureBrowserAsync(DoneBrowser, DoneStatus)) return;
        if (ResultsList.SelectedItem != view || view.Step != ReviewStep.Done) return;

        DoneStatus.Text = "Loading the page…";
        Navigate(DoneBrowser, core => core.Navigate(url));
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

    /// <summary>
    /// Sends the wheel from a sideways-only scroller to the one that scrolls vertically around it. The screenshot's
    /// own scroller sits in the screenshot pane or in the edit pane depending on the width, so the target is found by
    /// walking up rather than named.
    /// </summary>
    private void OnPassScrollToParent(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (sender is not DependencyObject source) return;

        DependencyObject? parent = System.Windows.Media.VisualTreeHelper.GetParent(source);
        while (parent is not null and not System.Windows.Controls.ScrollViewer)
            parent = System.Windows.Media.VisualTreeHelper.GetParent(parent);
        if (parent is not System.Windows.Controls.ScrollViewer outer) return;

        e.Handled = true;
        outer.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
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
    private void OnLedgerClick(object sender, RoutedEventArgs e) => ShowHistory(LedgerFilter.All);

    /// <summary>The status bar's attention count, opening History on exactly those rows (user, 2026-10-07).</summary>
    private void OnAttentionClick(object sender, RoutedEventArgs e) => ShowHistory(LedgerFilter.NeedsAttention);

    private void ShowHistory(LedgerFilter filter)
    {
        if (_services is null) return;

        _dialogOpen = true;
        try
        {
            new LedgerWindow(_services.Ledger, _services.Mapping.Version, _services.SaveLedgerAsync, filter)
                { Owner = this }.ShowDialog();
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
    private void OnSettingsClick(object sender, RoutedEventArgs e) => OpenSettings(SettingsPage.Font);

    /// <summary>The hotkey hint opens Settings at the page that changes it.</summary>
    private void OnHotKeyLinkClick(object sender, RoutedEventArgs e) => OpenSettings(SettingsPage.HotKey);

    private void OpenSettings(SettingsPage page)
    {
        if (_services is null) return;

        _dialogOpen = true;
        try
        {
            new SettingsWindow(_services, page) { Owner = this }.ShowDialog();
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
        _ = Dialog.TellAsync(
            "The wiki is unavailable",
            $"{ex.Message}\n\n{consequence}. Nothing was written to the wiki, and nothing was recorded locally, so " +
            "try again once the wiki is reachable.",
            DialogTone.Error);
    }

    private void UpdateLedgerText()
    {
        LedgerSummary? summary = _services is null ? null : LedgerSummary.Of(_services.Ledger.Entries);
        LedgerTotalRun.Text = summary is null ? "" : $"{summary.Total} checked";
        LedgerSeparatorRun.Text = summary is { NeedsAttention: > 0 } ? " · " : "";
        AttentionRun.Text = summary is { NeedsAttention: > 0 } ? $"{summary.NeedsAttention} need attention" : "";
    }

    protected override void OnClosed(EventArgs e)
    {
        _services?.Dispose();
        base.OnClosed(e);
    }
}
