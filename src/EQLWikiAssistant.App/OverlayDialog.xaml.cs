using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace EQLWikiAssistant.App;

/// <summary>How serious a dialog is, which decides the colour of its title.</summary>
public enum DialogTone
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// A dialog drawn over the window's own content instead of a Windows message box (user, 2026-10-07).
///
/// **A message box stays light whatever the theme**, since the OS draws it, and it opens wherever Windows decides; this
/// one is part of the window, in the palette, over the content it is about. Each window carries one as the last child
/// of its root grid, so it covers everything the window draws.
///
/// **Modal by covering, not by a nested message loop**: the backdrop takes every click and the card keeps keyboard
/// focus inside it, and callers <c>await</c> the answer. One dialog shows at a time; a second request waits its turn
/// rather than replacing the first, so a question is never silently lost behind an error.
///
/// <see cref="Opened"/> and <see cref="Closed"/> exist for the review's embedded browser, which is a native window
/// that WPF cannot draw over — its host hides it while a dialog is up.
/// </summary>
public partial class OverlayDialog : UserControl
{
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);
    private TaskCompletionSource<bool>? _answer;
    private bool _hasCancel;
    private IInputElement? _focusBefore;

    public OverlayDialog() => InitializeComponent();

    public event EventHandler? Opened;
    public event EventHandler? Closed;

    /// <summary>True while a dialog is showing — the main window's global hotkey checks it, since the hotkey bypasses
    /// anything drawn here.</summary>
    public bool IsOpen => _answer is not null;

    /// <summary>
    /// Asks a question and answers true when the user confirms. Escape, or the cancel button, answers false.
    /// </summary>
    /// <param name="writesToWiki">Gives the confirm button the solid wiki-write style, for the one confirmation left
    /// that publishes something (an icon upload).</param>
    public async Task<bool> AskAsync(
        string title, string message, string confirm, string? cancel = "Cancel",
        DialogTone tone = DialogTone.Warning, bool writesToWiki = false)
    {
        await _oneAtATime.WaitAsync();
        try
        {
            TitleText.Text = title;
            TitleText.Foreground = tone switch
            {
                DialogTone.Error => Palette.Attention,
                DialogTone.Warning => Palette.Warning,
                _ => Palette.Text,
            };
            MessageText.Text = message;
            MessageText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;

            ConfirmButton.Content = confirm;
            ConfirmButton.Style = writesToWiki
                ? (Style)FindResource("WikiWriteButton")
                : (Style)FindResource(typeof(Button));
            _hasCancel = cancel is not null;
            CancelButton.Content = cancel ?? "";
            CancelButton.Visibility = _hasCancel ? Visibility.Visible : Visibility.Collapsed;

            _answer = new TaskCompletionSource<bool>();
            _focusBefore = Keyboard.FocusedElement;
            Visibility = Visibility.Visible;
            Opened?.Invoke(this, EventArgs.Empty);

            // Cancel takes the focus when there is one, so an Enter pressed by habit does not confirm a question that
            // was meant to be read. Deferred until the card is laid out, or the focus request is lost.
            _ = Dispatcher.BeginInvoke(() => Keyboard.Focus(_hasCancel ? CancelButton : ConfirmButton),
                System.Windows.Threading.DispatcherPriority.Input);

            return await _answer.Task;
        }
        finally
        {
            _answer = null;
            Visibility = Visibility.Collapsed;
            Closed?.Invoke(this, EventArgs.Empty);
            if (_focusBefore is { } before) Keyboard.Focus(before);
            _focusBefore = null;
            _oneAtATime.Release();
        }
    }

    /// <summary>Tells the user something, with a single button to acknowledge it.</summary>
    public Task TellAsync(string title, string message, DialogTone tone = DialogTone.Info) =>
        AskAsync(title, message, "OK", cancel: null, tone);

    private void OnConfirmClick(object sender, RoutedEventArgs e) => _answer?.TrySetResult(true);

    private void OnCancelClick(object sender, RoutedEventArgs e) => _answer?.TrySetResult(false);

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (_answer is null) return;

        if (e.Key == Key.Escape)
        {
            // With no cancel button, Escape just dismisses — the only answer there is.
            _answer.TrySetResult(!_hasCancel);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            _answer.TrySetResult(!Equals(Keyboard.FocusedElement, CancelButton));
            e.Handled = true;
        }
    }

    /// <summary>A click on the backdrop is swallowed, so nothing under the dialog can be pressed.</summary>
    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        e.Handled = true;
    }
}
