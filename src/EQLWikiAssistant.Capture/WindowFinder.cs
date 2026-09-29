using System.Diagnostics;
using System.Text;
using EQLWikiAssistant.Capture.Interop;

namespace EQLWikiAssistant.Capture;

/// <summary>
/// A visible top-level window.
///
/// <see cref="ProcessName"/> is what actually identifies the game, and it is here because a title does not — see
/// <see cref="WindowFinder.FindByProcessName"/>. It is empty when the owning process could not be read, which is
/// ordinary rather than exceptional: a window can close between being enumerated and being asked about, and a
/// process belonging to another user cannot be opened at all.
/// </summary>
public sealed record FoundWindow(IntPtr Handle, string Title, string ProcessName = "");

/// <summary>Finds top-level, visible windows — used to locate the game window to capture.</summary>
public static class WindowFinder
{
    /// <summary>
    /// All visible top-level windows with a non-empty title.
    ///
    /// **The order is Z-order, topmost first**, because that is what <c>EnumWindows</c> gives. Worth knowing before
    /// taking the first of anything: it means the answer depends on what the user last clicked.
    /// </summary>
    public static IReadOnlyList<FoundWindow> EnumerateVisibleWindows()
    {
        var results = new List<FoundWindow>();
        NativeMethods.EnumWindows((hWnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hWnd)) return true;

            int length = NativeMethods.GetWindowTextLengthW(hWnd);
            if (length == 0) return true;

            var sb = new StringBuilder(length + 1);
            NativeMethods.GetWindowTextW(hWnd, sb, sb.Capacity);
            string title = sb.ToString();
            if (!string.IsNullOrWhiteSpace(title))
                results.Add(new FoundWindow(hWnd, title, ProcessNameOf(hWnd)));

            return true;
        }, IntPtr.Zero);
        return results;
    }

    /// <summary>Visible top-level windows whose title contains <paramref name="titleSubstring"/> (ordinal, case-insensitive).</summary>
    public static IReadOnlyList<FoundWindow> FindByTitleSubstring(string titleSubstring) =>
        [.. EnumerateVisibleWindows().Where(w => w.Title.Contains(titleSubstring, StringComparison.OrdinalIgnoreCase))];

    /// <summary>
    /// Visible top-level windows owned by a process with this name (without <c>.exe</c>, case-insensitive).
    ///
    /// **This is how the game is found, because a title cannot do it** (bug found by the user, 2026-09-29). Matching
    /// "EverQuest" as a title substring also matched their browser on `... - EverQuest Legends Wiki - Vivaldi` and
    /// their Discord on `... | EverQuest Legends - Discord` — both entirely reasonable windows for someone editing
    /// this wiki to have open. Since <see cref="EnumerateVisibleWindows"/> returns Z-order, whichever of those the
    /// user touched last came first, so pressing the hotkey from inside the game worked and clicking the button in
    /// the app captured the browser. The process name is not ambiguous that way.
    /// </summary>
    public static IReadOnlyList<FoundWindow> FindByProcessName(string processName) =>
        [.. EnumerateVisibleWindows()
            .Where(w => string.Equals(w.ProcessName, processName, StringComparison.OrdinalIgnoreCase))];

    private static string ProcessNameOf(IntPtr hWnd)
    {
        try
        {
            NativeMethods.GetWindowThreadProcessId(hWnd, out int pid);
            return pid == 0 ? "" : Process.GetProcessById(pid).ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or
                                       System.ComponentModel.Win32Exception)
        {
            // The process ended between enumerating and asking, or belongs to another user. Not knowing a window's
            // process is a reason to skip that window, never to fail the whole enumeration.
            return "";
        }
    }
}
