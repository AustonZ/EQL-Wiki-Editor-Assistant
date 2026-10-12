using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;

namespace EQLWikiEditorAssistant.App;

/// <summary>
/// Where the main window was when it last closed: its restored position and size, and whether it was maximized. The
/// next start puts it back (a tester's request, 2026-10-12, made standard behaviour by the user rather than a setting).
///
/// **It is Windows' own window placement, not WPF's <c>Left</c>/<c>Top</c>/<c>Width</c>/<c>Height</c>.** The placement
/// records the *restored* rectangle even while the window is maximized or minimized, which is what has to be kept, and
/// <c>SetWindowPlacement</c> moves a window that would land wholly off screen — a monitor unplugged since — back onto
/// one. Both are things WPF's properties would make this class do by hand. Saved and restored by the same process in the
/// same coordinates, so DPI does not enter into it.
///
/// **Its own file, not <c>settings.json</c>**: that file holds what the user has chosen, and is written by
/// <see cref="AppServices"/> whenever a setting changes; this is written once, on close, by the window.
///
/// Nothing here may stop the app starting or closing: a file that cannot be read is no saved placement, and one that
/// cannot be written is not remembered.
/// </summary>
internal static class WindowPlacement
{
    public sealed record Saved(int Left, int Top, int Right, int Bottom, bool Maximized);

    /// <summary>How the window is to appear on start.</summary>
    public enum Start { Shown, Minimized, Hidden }

    public static Saved? Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            Saved? saved = JsonSerializer.Deserialize<Saved>(File.ReadAllText(path), JsonOptions);
            return saved is { } s && s.Right > s.Left && s.Bottom > s.Top ? saved : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public static void Save(string path, Saved saved)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(saved, JsonOptions));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not remembered this time; the next close tries again.
        }
    }

    /// <summary>The window's placement now, or null without a handle. <paramref name="maximized"/> is the caller's
    /// answer, because a minimized or hidden window's own state does not say what it will restore to.</summary>
    public static Saved? Read(Window window, bool maximized)
    {
        IntPtr handle = new WindowInteropHelper(window).Handle;
        var placement = new Placement { Length = Marshal.SizeOf<Placement>() };
        if (handle == IntPtr.Zero || !GetWindowPlacement(handle, ref placement)) return null;

        Rect32 r = placement.NormalPosition;
        return new Saved(r.Left, r.Top, r.Right, r.Bottom, maximized);
    }

    /// <summary>
    /// Puts the window where it was saved. Call from <c>SourceInitialized</c>, when the handle exists but WPF has not
    /// shown the window yet.
    /// </summary>
    public static void Apply(Window window, Saved saved, Start start)
    {
        IntPtr handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        var placement = new Placement
        {
            Length = Marshal.SizeOf<Placement>(),
            NormalPosition = new Rect32 { Left = saved.Left, Top = saved.Top, Right = saved.Right, Bottom = saved.Bottom },
            MinPosition = new Point32 { X = -1, Y = -1 },
            MaxPosition = new Point32 { X = -1, Y = -1 },
        };
        switch (start)
        {
            case Start.Shown:
                placement.ShowCommand = saved.Maximized ? ShowMaximized : ShowNormal;
                break;
            case Start.Minimized:
                placement.ShowCommand = ShowMinimizedNoActivate;
                if (saved.Maximized) placement.Flags = RestoreToMaximized;
                break;
            case Start.Hidden:
                placement.ShowCommand = Hide;
                break;
        }
        SetWindowPlacement(handle, ref placement);
    }

    private const int Hide = 0, ShowNormal = 1, ShowMaximized = 3, ShowMinimizedNoActivate = 7;
    private const int RestoreToMaximized = 0x2;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct Point32 { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32 { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Placement
    {
        public int Length;
        public int Flags;
        public int ShowCommand;
        public Point32 MinPosition;
        public Point32 MaxPosition;
        public Rect32 NormalPosition;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowPlacement(IntPtr hwnd, ref Placement placement);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPlacement(IntPtr hwnd, ref Placement placement);
}
