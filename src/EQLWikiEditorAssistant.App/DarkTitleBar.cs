using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace EQLWikiEditorAssistant.App;

/// <summary>
/// Asks Windows to draw a window's caption dark.
///
/// **The title bar is the one part of a window WPF does not own.** Styling everything below it leaves a white
/// caption sitting on top of a dark app, which reads as a rendering fault rather than a design. `DwmSetWindowAttribute`
/// is how an application opts in, and it is the whole of the dark-mode surface Windows exposes — there is no way to
/// set the caption's colours, only to pick the dark set.
///
/// **It must run after the handle exists**, so callers hook `SourceInitialized`; before that there is no HWND to
/// set the attribute on.
///
/// **Failure is ignored on purpose.** The attribute is unsupported before Windows 10 20H1, and the two attribute
/// numbers below are the pre- and post-20H1 spellings of the same thing. A light title bar is cosmetic; refusing to
/// open a window over it would not be.
/// </summary>
internal static class DarkTitleBar
{
    private const int UseImmersiveDarkMode = 20;
    private const int UseImmersiveDarkModeBefore20H1 = 19;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Applies it now if the window has a handle, and on <c>SourceInitialized</c> if it does not yet.</summary>
    public static void Apply(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (new WindowInteropHelper(window).Handle is var handle && handle != IntPtr.Zero)
        {
            Set(handle);
            return;
        }

        window.SourceInitialized += OnInitialized;

        void OnInitialized(object? sender, EventArgs e)
        {
            window.SourceInitialized -= OnInitialized;
            Set(new WindowInteropHelper(window).Handle);
        }
    }

    private static void Set(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return;

        int on = 1;
        try
        {
            if (DwmSetWindowAttribute(handle, UseImmersiveDarkMode, ref on, sizeof(int)) != 0)
                DwmSetWindowAttribute(handle, UseImmersiveDarkModeBefore20H1, ref on, sizeof(int));
        }
        catch (DllNotFoundException)
        {
            // Nothing to do: the caption stays light.
        }
        catch (EntryPointNotFoundException)
        {
        }
    }
}
