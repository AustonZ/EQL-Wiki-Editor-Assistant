using System.Diagnostics;

namespace EQLWikiEditorAssistant.App;

/// <summary>
/// Opens a web page in the user's browser, or a folder in Explorer — every process the Assistant starts goes through
/// here.
///
/// **The process starts in the user's profile folder, never in ours** (bug found by a tester, 2026-10-12: an update
/// failed because Firefox's <c>crashhelper.exe</c> had the install folder locked). With no working directory given,
/// the shell starts the new process in the Assistant's own, which for an installed copy is its install folder. A
/// process holds its working directory open for as long as it runs, and its children inherit it, so a browser started
/// from a link here — one not already running, which would otherwise just take the URL and exit — kept the install
/// folder open until it was closed, and Velopack cannot replace a folder that is open. Reproduced with Vivaldi too.
/// </summary>
internal static class ExternalLaunch
{
    /// <summary>Opens a URL (or anything else the shell can open) with the user's default handler.</summary>
    public static void Open(string target) => Start(new ProcessStartInfo(target));

    /// <summary>Opens Explorer with these arguments, e.g. a folder or <c>/select,"file"</c>.</summary>
    public static void Explorer(string arguments) => Start(new ProcessStartInfo("explorer.exe") { Arguments = arguments });

    private static void Start(ProcessStartInfo start)
    {
        start.UseShellExecute = true;
        start.WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Process.Start(start);
    }
}
