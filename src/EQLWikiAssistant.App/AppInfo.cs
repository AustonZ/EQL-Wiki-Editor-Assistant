using System.Reflection;

namespace EQLWikiAssistant.App;

/// <summary>
/// The app's name and version, read from the build (<c>Product</c> and <c>Version</c> in the project file) so there is
/// one place to change either.
///
/// The version follows Semantic Versioning as adapted for this app (user, 2026-10-08): a patch fixes something without
/// changing what the Assistant writes, a minor version adds a feature or changes what it would write to a page, and a
/// major version is a redesign. The first release is <c>1.0.0-alpha.1</c>.
/// </summary>
public static class AppInfo
{
    private static readonly Assembly Self = typeof(AppInfo).Assembly;

    /// <summary>The product name, as the window title and the wiki edit summaries show it.</summary>
    public static string Name { get; } =
        Self.GetCustomAttribute<AssemblyProductAttribute>()?.Product ?? "EQL Wiki Editor Assistant";

    /// <summary>
    /// The name a wiki edit summary carries: <c>(Editor Assistant 1.0.0-alpha.1)</c>. "EQL Wiki" is implied on the wiki
    /// itself, and the shorter tag keeps page-history lines short and leaves the summary more room (user, 2026-10-08).
    /// </summary>
    public const string SummaryName = "Editor Assistant";

    /// <summary>The full version, including the commit it was built from after a <c>+</c> when there is one — what a
    /// bug report needs.</summary>
    public static string BuildVersion { get; } =
        Self.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    /// <summary>The version as a tester would quote it: <c>1.0.0-alpha.1</c>, without the commit.</summary>
    public static string Version { get; } = BuildVersion.Split('+')[0];
}
