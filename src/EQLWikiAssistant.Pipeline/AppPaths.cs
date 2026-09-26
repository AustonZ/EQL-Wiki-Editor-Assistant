namespace EQLWikiAssistant.Pipeline;

/// <summary>
/// Where the tool keeps its own state: the ledger, the user's edited mapping, the icon cache.
///
/// All under the roaming app-data folder, as the plan specifies, rather than beside the executable — the mapping and
/// the ledger are the user's data and must survive reinstalling the tool. Nothing here holds a screenshot: captures
/// are processed in memory and never written to disk.
/// </summary>
public static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EQLWikiAssistant");

    /// <summary>The checked-items ledger. JSON — see <c>CheckedItemsLedger</c> for why not SQLite.</summary>
    public static string LedgerFile => Path.Combine(Root, "checked-items.json");

    /// <summary>The user's edited wiki mapping, if they have one; the built-in defaults apply when absent.</summary>
    public static string MappingFile => Path.Combine(Root, "wiki-mapping.json");

    /// <summary>Downloaded wiki icons, keyed by icon id. Static files, so no expiry.</summary>
    public static string IconCacheDirectory => Path.Combine(Root, "icon-cache");

    public static void EnsureExists() => Directory.CreateDirectory(Root);
}
