using System.IO;
using System.Runtime.InteropServices;
using EQLWikiEditorAssistant.Pipeline;

namespace EQLWikiEditorAssistant.App;

/// <summary>
/// **One running copy per data folder** (user, 2026-10-21). Two copies would share one ledger, one settings file and
/// one capture hotkey: each would save the ledger over the other's rows, and only one could hold the hotkey. So a second
/// launch brings the running copy to the front, out of the tray if need be, and exits.
///
/// **Per data folder, not per machine**: an installed release and a development build keep separate folders (see
/// <see cref="AppPaths"/>) and may still run side by side, which is what <c>tools/install.ps1</c>'s separate install is
/// for. <c>dotnet run</c> and <c>install.ps1</c>'s copy share the development folder, so they are one instance.
///
/// Claimed in <see cref="App"/>'s <c>Main</c> after Velopack's hooks, which run and exit inside <c>VelopackApp.Run</c>
/// and so never meet this.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _showRequest;
    private RegisteredWaitHandle? _listening;

    private SingleInstance(Mutex mutex, EventWaitHandle showRequest)
    {
        _mutex = mutex;
        _showRequest = showRequest;
    }

    /// <summary>
    /// Claims this data folder for the process, or returns null after asking the copy already running to show itself.
    /// </summary>
    /// <param name="waitForPrevious">Wait a few seconds for a running copy to exit rather than deferring to it: set when
    /// Velopack has just restarted the app after an update, when the old copy may not have let go yet.</param>
    public static SingleInstance? Claim(bool waitForPrevious)
    {
        string name = $@"Local\{Path.GetFileName(AppPaths.Root)}";

        // The request event exists before the mutex is taken, so a second copy that finds the mutex held always finds
        // the event too: one it created itself would vanish with its handle, and the request with it.
        var showRequest = new EventWaitHandle(false, EventResetMode.AutoReset, name + "-show");
        var mutex = new Mutex(false, name + "-instance");

        bool owned;
        try
        {
            owned = mutex.WaitOne(waitForPrevious ? TimeSpan.FromSeconds(10) : TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            // The previous copy died without letting go. The mutex is this process's now.
            owned = true;
        }

        if (owned) return new SingleInstance(mutex, showRequest);

        // Windows lets only the process the user just started take the foreground. This is that process, so it hands
        // the right on, or the running copy could only flash on the taskbar.
        AllowSetForegroundWindow(AnyProcess);
        showRequest.Set();
        showRequest.Dispose();
        mutex.Dispose();
        return null;
    }

    /// <summary>Calls <paramref name="show"/>, on a pool thread, whenever another launch asks this copy to show
    /// itself. A request made before this is called is kept and delivered then.</summary>
    public void Listen(Action show)
    {
        ArgumentNullException.ThrowIfNull(show);
        _listening = ThreadPool.RegisterWaitForSingleObject(
            _showRequest, (_, _) => show(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    public void Dispose()
    {
        _listening?.Unregister(null);
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not owned by this thread: nothing to release, and the handle closes below either way.
        }
        _mutex.Dispose();
        _showRequest.Dispose();
    }

    private const int AnyProcess = -1; // ASFW_ANY

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);
}
