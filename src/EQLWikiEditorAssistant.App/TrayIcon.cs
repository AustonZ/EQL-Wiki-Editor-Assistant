using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace EQLWikiEditorAssistant.App;

/// <summary>
/// The app's icon in the notification area, shown whenever Settings > Window > Minimize to system tray is on (the
/// first tester, 2026-10-10). A click brings the window forward; a right-click offers the same and Exit.
///
/// **WPF has no notification-area icon**, so this is <c>Shell_NotifyIcon</c> directly. Windows Forms has one, but
/// referencing it brings a second UI framework whose names collide with WPF's across the whole project, for a single
/// icon. Plain <c>[DllImport]</c>, as in <see cref="DarkTitleBar"/>.
///
/// **It listens on the main window's own handle**, which stays alive while the window is hidden. A message-only window
/// would be the textbook choice and is wrong here: Explorer announces a restart by broadcasting <c>TaskbarCreated</c>
/// to top-level windows, which a message-only window never receives, and an icon not re-added then is simply gone
/// until the app restarts — with the window hidden and nothing on the taskbar to click.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const int CallbackMessage = 0x8000 + 0x51;   // WM_APP + an arbitrary offset
    private const int WmLeftButtonUp = 0x0202;
    private const int WmRightButtonUp = 0x0205;

    private const int NimAdd = 0, NimDelete = 2;
    private const int NifMessage = 0x1, NifIcon = 0x2, NifTip = 0x4;

    private readonly HwndSource _source;
    private readonly IntPtr _icon;
    private readonly int _taskbarCreated;
    private bool _shown;

    /// <summary>The icon was clicked, or Open was chosen from its menu.</summary>
    public event EventHandler? OpenRequested;

    /// <summary>Exit was chosen from its menu.</summary>
    public event EventHandler? ExitRequested;

    /// <summary>Needs the window's handle, so create it from <c>SourceInitialized</c> or later.</summary>
    public TrayIcon(Window owner)
    {
        IntPtr handle = new WindowInteropHelper(owner).Handle;
        _source = HwndSource.FromHwnd(handle)
            ?? throw new InvalidOperationException("The window has no handle yet.");
        _source.AddHook(OnMessage);
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");

        // The executable's own icon at the notification area's size; the stock application icon if that fails, since
        // an icon of the wrong picture is better than an invisible one.
        IntPtr[] small = [IntPtr.Zero];
        _icon = Environment.ProcessPath is { } exe && ExtractIconEx(exe, 0, null, small, 1) > 0 && small[0] != IntPtr.Zero
            ? small[0]
            : IntPtr.Zero;
    }

    public void Show()
    {
        if (_shown) return;
        NotifyIconData data = Data();
        _shown = Shell_NotifyIcon(NimAdd, ref data);
    }

    public void Hide()
    {
        if (!_shown) return;
        NotifyIconData data = Data();
        Shell_NotifyIcon(NimDelete, ref data);
        _shown = false;
    }

    public void Dispose()
    {
        Hide();
        _source.RemoveHook(OnMessage);
        if (_icon != IntPtr.Zero) DestroyIcon(_icon);
    }

    private NotifyIconData Data() => new()
    {
        cbSize = Marshal.SizeOf<NotifyIconData>(),
        hWnd = _source.Handle,
        uID = 1,
        uFlags = NifMessage | NifIcon | NifTip,
        uCallbackMessage = CallbackMessage,
        hIcon = _icon != IntPtr.Zero ? _icon : LoadIcon(IntPtr.Zero, (IntPtr)32512),
        szTip = AppInfo.Name,
    };

    private IntPtr OnMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == _taskbarCreated && _taskbarCreated != 0 && _shown)
        {
            // Explorer restarted and forgot every icon; put this one back.
            _shown = false;
            Show();
        }
        else if (message == CallbackMessage)
        {
            switch ((int)lParam & 0xFFFF)
            {
                case WmLeftButtonUp:
                    OpenRequested?.Invoke(this, EventArgs.Empty);
                    handled = true;
                    break;
                case WmRightButtonUp:
                    ShowMenu();
                    handled = true;
                    break;
            }
        }
        return IntPtr.Zero;
    }

    private void ShowMenu()
    {
        var open = new MenuItem { Header = "Open " + AppInfo.Name };
        open.Click += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
        var exit = new MenuItem { Header = "Exit" };
        exit.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        var menu = new ContextMenu
        {
            Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint,
            Items = { open, exit },
        };
        // A menu opened by an app that is not in the foreground never hears the click elsewhere that should close it,
        // the long-standing rule for notification-area menus. Bringing the menu's own window forward fixes that.
        //
        // **And then the menu takes keyboard focus** (bug found by the user, 2026-10-10: Escape closed the menu only
        // with the pointer over it). WPF focuses a menu as it opens, which fails while its window is still in the
        // background; hovering focused an item, which is why that case worked. Escape is also handled here directly,
        // so the menu closes whatever inside it ends up with focus.
        menu.Opened += (_, _) =>
        {
            if (PresentationSource.FromVisual(menu) is HwndSource popup) SetForegroundWindow(popup.Handle);
            menu.Focus();
        };
        menu.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != System.Windows.Input.Key.Escape) return;
            menu.IsOpen = false;
            e.Handled = true;
        };
        menu.IsOpen = true;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int message, ref NotifyIconData data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int ExtractIconEx(string file, int index, IntPtr[]? large, IntPtr[]? small, int count);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterWindowMessage(string name);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);
}
