namespace EQLWikiEditorAssistant.Pipeline;

/// <summary>
/// Where the tool keeps its own state: the ledger, the settings, the icon cache.
///
/// All under the roaming app-data folder, as the plan specifies, rather than beside the executable — the settings and
/// the ledger are the user's data and must survive reinstalling the tool. Nothing here holds a screenshot: captures
/// are processed in memory and never written to disk.
///
/// **A release and a developer's build keep separate folders** (user, 2026-10-11). They used to share one, so a build
/// that changed what the ledger records wrote rows the installed release then had to read. A copy Velopack installed
/// calls <see cref="UseReleaseFolders"/> before anything else runs; every other copy — <c>dotnet run</c>,
/// <c>tools/install.ps1</c>'s, the tools — uses the development folders, which <c>install.ps1</c> refreshes from the
/// release's data each time it runs, so a developer works on real data without being able to damage it.
/// </summary>
public static class AppPaths
{
    private const string FolderName = "EQLWikiEditorAssistant";
    private const string DevelopmentFolderName = "EQLWikiEditorAssistant-dev";

    /// <summary>The state folder in use. The development one unless <see cref="UseReleaseFolders"/> was called.</summary>
    public static string Root { get; private set; } = RoamingFolder(DevelopmentFolderName);

    /// <summary>
    /// Where the review browser (WebView2) keeps its cache: local rather than roaming, being a cache. Each copy's is
    /// inside its own install folder — a release's under <c>EQLWikiEditorAssistant</c>, so uninstalling removes it, and
    /// <c>tools/install.ps1</c>'s under <c>EQLWikiEditorAssistant-dev</c> beside it — since two copies running at once
    /// cannot share one.
    /// </summary>
    public static string BrowserDataDirectory { get; private set; } =
        Path.Combine(LocalFolder(DevelopmentFolderName), "WebView2");

    /// <summary>Whether <see cref="UseReleaseFolders"/> was called.</summary>
    public static bool IsRelease { get; private set; }

    /// <summary>Switches to the release's folders. Called once, by an installed release, before anything reads a
    /// path.</summary>
    public static void UseReleaseFolders()
    {
        Root = RoamingFolder(FolderName);
        BrowserDataDirectory = Path.Combine(LocalFolder(FolderName), "WebView2");
        IsRelease = true;
    }

    private static string RoamingFolder(string name) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), name);

    private static string LocalFolder(string name) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), name);

    /// <summary>The checked-items ledger. JSON — see <c>CheckedItemsLedger</c> for why not SQLite.</summary>
    public static string LedgerFile => Path.Combine(Root, "checked-items.json");

    /// <summary>The user's settings — today only the UI font the game draws in. See <c>AppSettings</c>.</summary>
    public static string SettingsFile => Path.Combine(Root, "settings.json");

    /// <summary>Where the main window was when it last closed, so the next start puts it back.</summary>
    public static string WindowPlacementFile => Path.Combine(Root, "window.json");

    /// <summary>The wiki's list of pages marked "Verified for EQLegends", cached so it can be answered offline and
    /// on the ledger-skip path where no wiki request happens at all.</summary>
    public static string VerifiedPagesFile => Path.Combine(Root, "verified-pages.json");

    /// <summary>Downloaded wiki icons, keyed by icon id. Static files, so no expiry.</summary>
    public static string IconCacheDirectory => Path.Combine(Root, "icon-cache");

    /// <summary>
    /// The fingerprint index of the game's extracted icons — derived data, cached here and rebuilt automatically
    /// whenever the icon folder changes (see <c>IconLibraryStore</c>).
    ///
    /// **In app-data rather than committed beside the icons**, deliberately, and not for repo-size reasons: a
    /// committed index is a *second* record of which icons exist, and the moment the two disagree the tool starts
    /// confidently matching against an out-of-date library. Keeping it here, next to a stamp of what it was built
    /// from, means there is only ever one answer to "what icons are there" — the folder.
    /// </summary>
    public static string IconIndexFile => Path.Combine(Root, Core.Icons.IconLibraryIndex.FileName);

    /// <summary>
    /// Where the game's extracted item icons live: <c>game_assets/item_icons</c>, found by walking up from the
    /// executable, with an override for a copy kept elsewhere.
    ///
    /// **Resolved rather than fixed, because this is 46MB of PNGs that must not be copied into the build output on
    /// every build** — and because they are only needed for two things, showing a match and uploading it, so a
    /// missing folder degrades the feature instead of breaking the tool. Null means "no library", which every caller
    /// already handles: before this existed, a generated page simply had a blank <c>lucy_img_ID</c>.
    /// </summary>
    public static string? IconLibraryDirectory
    {
        get
        {
            if (Environment.GetEnvironmentVariable("EQLWIKI_ICON_LIBRARY") is { Length: > 0 } overridden)
                return Directory.Exists(overridden) ? overridden : null;

            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                string candidate = Path.Combine(dir.FullName, "game_assets", "item_icons");
                if (Directory.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }

            return null;
        }
    }

    public static void EnsureExists() => Directory.CreateDirectory(Root);
}
