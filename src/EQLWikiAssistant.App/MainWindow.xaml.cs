using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media.Imaging;
using EQLWikiAssistant.Capture;
using EQLWikiAssistant.Core.Ocr;
using EQLWikiAssistant.Pipeline;

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
    /// <summary>Ctrl+Shift+E. Registered globally so it fires while the game has focus, which is the whole point —
    /// Graphics Capture reads an unfocused window fine, so the user never has to leave the game to capture.</summary>
    private const uint VkE = 0x45;

    private readonly ObservableCollection<ResultViewModel> _results = [];
    private AppServices? _services;
    private GlobalHotKey? _hotKey;
    private bool _capturing;

    public MainWindow()
    {
        InitializeComponent();
        ResultsList.ItemsSource = _results;

        // Built off the UI thread: RapidOcrEngine loads three ONNX models in its constructor, which is seconds of a
        // frozen window if it happens here. The capture button stays disabled until it is ready.
        CaptureButton.IsEnabled = false;
        StatusText.Text = "Loading the OCR models…";
        _ = StartUpAsync();

        try
        {
            _hotKey = new GlobalHotKey(HotKeyModifiers.Control | HotKeyModifiers.Shift, VkE);
            // The hotkey runs its own message-only window on its own thread, so this arrives off the UI thread.
            _hotKey.Pressed += (_, _) => Dispatcher.Invoke(() => _ = CaptureAsync());
            HotKeyLabel.Text = "Hotkey: Ctrl+Shift+E";
        }
        catch (Exception ex)
        {
            // Another application may already own the combination. That is a degraded tool, not a broken one — the
            // Capture button still works — so it is reported rather than fatal.
            HotKeyLabel.Text = "Hotkey unavailable";
            HotKeyLabel.ToolTip = ex.Message;
        }
    }

    private async Task StartUpAsync()
    {
        try
        {
            _services = await Task.Run(() => new AppServices());
            _services.Pipeline.ReCheckAnyway = ReCheckBox.IsChecked == true;
            CaptureButton.IsEnabled = true;
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

        try
        {
            StatusText.Text = "Capturing the game window…";
            (CapturedImage? frame, string? problem) = await _services.CaptureGameWindowAsync();
            if (frame is null)
            {
                StatusText.Text = problem;
                return;
            }

            StatusText.Text = "Reading the item windows and checking them against the wiki…";
            IReadOnlyList<ItemCheckResult> results = await _services.Pipeline.CheckAsync(frame);

            _results.Clear();
            foreach (ItemCheckResult result in results) _results.Add(new ResultViewModel(result));

            if (_results.Count > 0) ResultsList.SelectedIndex = 0;

            // The ledger is written after a batch rather than per row — it is saved whole, and a check can settle a
            // page that already agreed without the user doing anything.
            await _services.SaveLedgerAsync();
            UpdateLedgerText();

            StatusText.Text = results.Count switch
            {
                0 => "No item window was found in that capture.",
                1 => "1 item window checked.",
                _ => $"{results.Count} item windows checked.",
            };
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Capture failed: {ex.Message}";
        }
        finally
        {
            _capturing = false;
            CaptureButton.IsEnabled = true;
        }
    }

    private void OnResultSelected(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        WindowImage.Source = ResultsList.SelectedItem is ResultViewModel { Result.WindowImage: { } image }
            ? ToBitmap(image)
            : null;
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
            if (await _services.EnsureLoggedInAsync() is { } problem)
            {
                StatusText.Text = problem;
                MessageBox.Show(this, problem, "Not logged in", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            CommitResult commit = await _services.Pipeline.CommitAsync(view.Result, view.Wikitext, view.Summary);
            await _services.SaveLedgerAsync();
            UpdateLedgerText();

            view.Outcome = commit.Status switch
            {
                CommitStatus.Committed => $"Saved as revision {commit.RevisionId}.",
                CommitStatus.NoChange => "The wiki found the text identical — recorded as a match.",
                _ => null,
            };

            StatusText.Text = view.Outcome ?? commit.Error ?? "The edit did not go through.";

            if (commit.Status is CommitStatus.PageChangedSinceCheck or CommitStatus.Failed)
                MessageBox.Show(this, commit.Error, "Nothing was written", MessageBoxButton.OK, MessageBoxImage.Warning);
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

    private void OnOpenPageClick(object sender, RoutedEventArgs e)
    {
        if ((sender as Hyperlink)?.DataContext is not ResultViewModel { PageUrl: { } url }) return;
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private void UpdateLedgerText() =>
        LedgerText.Text = _services is null ? "" : $"{_services.Ledger.Count} item(s) in the ledger";

    /// <summary>The capture is tightly packed top-down BGRA32, which is exactly <c>Bgra32</c>'s layout, so this is a
    /// copy rather than a conversion.</summary>
    private static BitmapSource ToBitmap(CapturedImage image) =>
        BitmapSource.Create(
            image.Width, image.Height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null,
            image.Pixels, image.Width * 4);

    protected override void OnClosed(EventArgs e)
    {
        _hotKey?.Dispose();
        _services?.Dispose();
        base.OnClosed(e);
    }
}
