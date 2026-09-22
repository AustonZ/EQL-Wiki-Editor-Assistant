using System.Text;
using EQLWikiAssistant.Capture.Interop;

namespace EQLWikiAssistant.Capture;

public sealed record FoundWindow(IntPtr Handle, string Title);

/// <summary>Finds top-level, visible windows by title — used to locate the game window to capture.</summary>
public static class WindowFinder
{
    /// <summary>All visible top-level windows with a non-empty title.</summary>
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
                results.Add(new FoundWindow(hWnd, title));

            return true;
        }, IntPtr.Zero);
        return results;
    }

    /// <summary>Visible top-level windows whose title contains <paramref name="titleSubstring"/> (ordinal, case-insensitive).</summary>
    public static IReadOnlyList<FoundWindow> FindByTitleSubstring(string titleSubstring) =>
        EnumerateVisibleWindows()
            .Where(w => w.Title.Contains(titleSubstring, StringComparison.OrdinalIgnoreCase))
            .ToList();
}
