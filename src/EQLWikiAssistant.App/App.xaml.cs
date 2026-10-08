using System.IO;
using System.Windows;
using System.Windows.Threading;
using EQLWikiAssistant.Pipeline;

namespace EQLWikiAssistant.App;

/// <summary>
/// The application, and the last line of defence under it.
///
/// **There is no debug log and deliberately so** (user, 2026-09-29, asking for a non-invasive option). What was
/// actually missing was somewhere for an exception nobody caught to *go*: the review screen's click handlers are
/// `async void`, which is what an event handler has to be, and an exception thrown inside one does not reach the
/// caller — it reaches the dispatcher, and without a handler there it closes the window. So the failure mode this
/// guards is a tool that vanishes rather than a tool that misbehaves visibly.
///
/// It writes the detail to a file rather than only showing it, because the useful part of an unexpected exception is
/// the stack, which is too much for a dialog and too easy to lose once the dialog is dismissed.
/// </summary>
public partial class App : Application
{
    /// <summary>Beside the ledger and the settings, in the user's own app-data — never beside the executable, and
    /// never containing a screenshot. Only the exception's own text reaches it.</summary>
    public static string ErrorLogFile => Path.Combine(AppPaths.Root, "errors.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            // Handled, so the app survives. An unexpected exception in one click is not a reason to lose the other
            // items on screen — several of which may represent captures the user cannot easily retake.
            args.Handled = true;
            Report(args.Exception);
        };

        // A faulted Task nobody awaited. Not fatal by default in .NET, so this is purely so it leaves a trace
        // instead of disappearing.
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            args.SetObserved();
            Record(args.Exception);
        };
    }

    private void Report(Exception ex)
    {
        Record(ex);
        MessageBox.Show(
            MainWindow,
            $"{ex.Message}\n\nNothing was written to the wiki by this failure. The details are in:\n{ErrorLogFile}",
            "Something went wrong",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    /// <summary>Appends one entry. Never throws: a failure to record a failure must not become the failure.</summary>
    internal static void Record(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            File.AppendAllText(ErrorLogFile, $"{DateTimeOffset.Now:O}\n{ex}\n\n");
        }
        catch (Exception writing) when (writing is IOException or UnauthorizedAccessException)
        {
            // Nothing sensible to do — the dialog still carries the message.
        }
    }
}
