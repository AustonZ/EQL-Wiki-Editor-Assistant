namespace EQLWikiEditorAssistant.Pipeline;

/// <summary>
/// Where the tool keeps its own state: the ledger, the settings, the icon cache.
///
/// All under the roaming app-data folder, as the plan specifies, rather than beside the executable — the settings and
/// the ledger are the user's data and must survive reinstalling the tool. Nothing here holds a screenshot: captures
/// are processed in memory and never written to disk.
/// </summary>
public static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EQLWikiEditorAssistant");

    /// <summary>The checked-items ledger. JSON — see <c>CheckedItemsLedger</c> for why not SQLite.</summary>
    public static string LedgerFile => Path.Combine(Root, "checked-items.json");

    /// <summary>The user's settings — today only the UI font the game draws in. See <c>AppSettings</c>.</summary>
    public static string SettingsFile => Path.Combine(Root, "settings.json");

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
    public static string IconIndexFile => Path.Combine(Root, "item-icons.index");

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
