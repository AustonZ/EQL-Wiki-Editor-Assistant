using EQLWikiEditorAssistant.Capture.Interop;
using EQLWikiEditorAssistant.Core.Input;

namespace EQLWikiEditorAssistant.Capture;

/// <summary>
/// A single system-wide hotkey, so a capture can be triggered while the game window has focus. Runs its own
/// dedicated message-only window and message loop on a background thread — deliberately not built on WPF's
/// HwndSource, so this project has no UI framework dependency.
///
/// <paramref name="virtualKey"/> is a raw Win32 virtual-key code (see winuser.h VK_*); the App project (which
/// already depends on WPF) is expected to translate from System.Windows.Input.Key via
/// System.Windows.Input.KeyInterop.VirtualKeyFromKey.
/// </summary>
public sealed class GlobalHotKey : IDisposable
{
    private const int HotKeyId = 0xB00; // arbitrary; registrations are per window, and each instance has its own

    /// <summary>
    /// Numbers each instance's window class, because **two instances must be able to exist at once**: changing the
    /// hotkey registers the new combination before releasing the old, so a combination Windows refuses leaves the
    /// old one working. With one shared class name the second <c>RegisterClassExW</c> fails and no change is possible.
    /// </summary>
    private static int _instanceCount;

    private readonly string _windowClassName =
        $"EQLWikiEditorAssistant.GlobalHotKey.MessageWindow.{Interlocked.Increment(ref _instanceCount)}";

    private readonly HotKeyModifiers _modifiers;
    private readonly uint _virtualKey;
    private readonly NativeMethods.WndProc _wndProc; // keep a rooted reference so the GC doesn't collect it
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _started = new(initialState: false);
    private IntPtr _hwnd;
    private uint _threadId;
    private Exception? _startupException;

    public event EventHandler? Pressed;

    public GlobalHotKey(HotKeyChord chord) : this(chord.Modifiers, chord.VirtualKey)
    {
    }

    public GlobalHotKey(HotKeyModifiers modifiers, uint virtualKey)
    {
        _modifiers = modifiers;
        _virtualKey = virtualKey;
        _wndProc = WindowProc;

        _thread = new Thread(RunMessageLoop) { IsBackground = true, Name = "EQLWikiEditorAssistant hotkey listener" };
        _thread.Start();

        _started.Wait();
        if (_startupException is not null)
            throw new InvalidOperationException("Failed to register the global hotkey.", _startupException);
    }

    private void RunMessageLoop()
    {
        try
        {
            _threadId = GetCurrentThreadId();
            IntPtr hInstance = NativeMethods.GetModuleHandleW(null);

            var wndClass = new NativeMethods.WNDCLASSEX
            {
                cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
                lpfnWndProc = _wndProc,
                hInstance = hInstance,
                lpszClassName = _windowClassName,
            };
            if (NativeMethods.RegisterClassExW(ref wndClass) == 0)
                throw new InvalidOperationException("RegisterClassExW failed.", new System.ComponentModel.Win32Exception());

            _hwnd = NativeMethods.CreateWindowExW(
                0, _windowClassName, null, 0, 0, 0, 0, 0,
                new IntPtr(NativeMethods.HWND_MESSAGE_PARENT), IntPtr.Zero, hInstance, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero)
                throw new InvalidOperationException("CreateWindowExW failed.", new System.ComponentModel.Win32Exception());

            uint modifiers = (uint)_modifiers | NativeMethods.MOD_NOREPEAT;
            if (!NativeMethods.RegisterHotKey(_hwnd, HotKeyId, modifiers, _virtualKey))
                throw new InvalidOperationException(
                    "RegisterHotKey failed — the key combination may already be registered by another application.",
                    new System.ComponentModel.Win32Exception());
        }
        catch (Exception ex)
        {
            // Release what was created, so a refused combination leaves nothing behind. This matters now that a
            // refusal is ordinary — another program holding the combination the user just tried.
            if (_hwnd != IntPtr.Zero) NativeMethods.DestroyWindow(_hwnd);
            NativeMethods.UnregisterClassW(_windowClassName, IntPtr.Zero);
            _startupException = ex;
            _started.Set();
            return;
        }

        _started.Set();

        while (NativeMethods.GetMessageW(out NativeMethods.MSG msg, IntPtr.Zero, 0, 0) > 0)
        {
            NativeMethods.TranslateMessage(ref msg);
            NativeMethods.DispatchMessageW(ref msg);
        }

        NativeMethods.UnregisterHotKey(_hwnd, HotKeyId);
        NativeMethods.DestroyWindow(_hwnd);
        NativeMethods.UnregisterClassW(_windowClassName, IntPtr.Zero);
    }

    private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == HotKeyId)
        {
            Pressed?.Invoke(this, EventArgs.Empty);
            return IntPtr.Zero;
        }
        return NativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_threadId != 0)
        {
            const uint WM_QUIT = 0x0012;
            NativeMethods.PostThreadMessageW(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            _thread.Join(TimeSpan.FromSeconds(2));
        }
        _started.Dispose();
        GC.SuppressFinalize(this);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
