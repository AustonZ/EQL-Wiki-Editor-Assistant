using System.IO;
using System.Runtime.InteropServices;
using EQLWikiEditorAssistant.Pipeline;
using EQLWikiEditorAssistant.Wiki.MediaWiki;

namespace EQLWikiEditorAssistant.App;

/// <summary>
/// Offers to delete the user's data when the Assistant is uninstalled (user, 2026-10-09: "I'm not a fan of programs that
/// leave trash behind when uninstalled").
///
/// **Asked, never assumed, and No is the default.** The data is the history of checked items, which is real work, and
/// someone reinstalling — or moving to a newer release by uninstalling first — wants it back. So Enter, Escape and
/// closing the box all keep it. The install folder itself, with the review browser's cache, goes regardless; Velopack
/// removes it.
///
/// **It runs inside Velopack's uninstall hook, which is ended after 30 seconds** and the uninstall carried on. An
/// unanswered question therefore keeps the data, the same answer as No. The login is deleted before the folder, since
/// it is the quicker of the two and the one that matters more if the hook is cut short.
///
/// **A Windows message box, not the app's own dialog**: no window of the app is open during an uninstall, and this is
/// the one place outside the crash handler where that is so. It is set topmost and foreground, because Windows'
/// Settings window started the uninstall and would otherwise cover it.
///
/// The developer's own build (<c>tools/install.ps1</c>) shares this folder, so answering Yes clears its state too.
/// </summary>
internal static class UninstallCleanup
{
    public static void OfferToRemoveData()
    {
        var login = new WindowsCredentialStore();
        bool hasData = Directory.Exists(AppPaths.Root);
        bool hasLogin = HasLogin(login);
        if (!hasData && !hasLogin) return;

        string message =
            $"{AppInfo.Name} is being uninstalled.\n\n" +
            "Delete your data as well? That is your settings, the history of checked items, cached icons, any saved " +
            $"screenshots and your wiki login, in:\n{AppPaths.Root}\n\n" +
            "Choose No to keep them for a later reinstall.";
        if (MessageBoxW(0, message, AppInfo.Name, MbYesNo | MbIconQuestion | MbDefButton2 | MbSetForeground | MbTopmost)
            != IdYes)
            return;

        try
        {
            login.Delete();
        }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException)
        {
            // Best effort: the uninstall goes ahead regardless, and there is nobody left to tell.
        }

        try
        {
            if (hasData) Directory.Delete(AppPaths.Root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file held open by something else stays behind; everything else is gone.
        }
    }

    private static bool HasLogin(WindowsCredentialStore login)
    {
        try
        {
            return login.Read() is not null;
        }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException)
        {
            // Unreadable is not proof of absence, so offer to delete it anyway.
            return true;
        }
    }

    private const uint MbYesNo = 0x4;
    private const uint MbIconQuestion = 0x20;
    private const uint MbDefButton2 = 0x100;
    private const uint MbSetForeground = 0x10000;
    private const uint MbTopmost = 0x40000;
    private const int IdYes = 6;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(nint owner, string text, string caption, uint type);
}
