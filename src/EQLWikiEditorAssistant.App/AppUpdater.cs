using System.IO;
using EQLWikiEditorAssistant.Pipeline;
using EQLWikiEditorAssistant.Wiki.MediaWiki;
using Velopack;
using Velopack.Sources;

namespace EQLWikiEditorAssistant.App;

/// <summary>
/// Updating an installed copy in place (user, 2026-10-09, bringing the 1.0.0 backlog item forward for the first
/// release: an updater only helps from the release after the one that ships it).
///
/// **Only a copy Velopack installed can update itself** — Setup's, or the portable zip's, both of which carry its
/// Update.exe. A build folder or <c>tools/install.ps1</c>'s copy has none, so <see cref="ForThisCopy"/> answers null
/// and the header keeps linking to the release page (<see cref="ReleaseCheck"/>) instead.
///
/// **Velopack decides what is newer here, not <see cref="ReleaseCheck"/>**, so the offer is always something it can
/// actually install: a release whose package or feed files are missing is never offered. Its rules match
/// ReleaseCheck's — a pre-release is offered only to someone already running one, and drafts are invisible to an
/// anonymous request.
///
/// **Nothing happens without a click.** Checking reads the release list and the feed file; downloading starts only
/// when the user has said yes, and applying it closes the app, so the review screen asks first.
/// </summary>
internal sealed class AppUpdater
{
    /// <summary>
    /// Points the updater at a folder of Velopack release files instead of GitHub — what <c>tools/release.ps1</c> leaves
    /// in <c>artifacts\releases</c>. It exists so an update can be tested end to end from a local build without
    /// publishing anything. Setting it needs the same access as replacing the program outright, so it opens nothing.
    /// </summary>
    public const string FeedOverrideVariable = "EQLWIKI_UPDATE_FEED";

    private readonly UpdateManager _manager;

    private AppUpdater(UpdateManager manager) => _manager = manager;

    /// <summary>The updater for this copy, or null when this copy was not installed by Velopack.</summary>
    public static AppUpdater? ForThisCopy()
    {
        try
        {
            var manager = new UpdateManager(Source());
            return manager.IsInstalled ? new AppUpdater(manager) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    private static IUpdateSource Source()
    {
        if (Environment.GetEnvironmentVariable(FeedOverrideVariable) is { Length: > 0 } folder)
            return new SimpleFileSource(new DirectoryInfo(folder));

        bool prerelease = Pipeline.SemanticVersion.TryParse(AppInfo.Version, out Pipeline.SemanticVersion current) &&
                          current.IsPrerelease;
        return new GithubSource(MediaWikiClient.ProjectUrl, accessToken: null, prerelease);
    }

    /// <summary>
    /// The newer release, or null when there is none or the check failed. **Any failure is silence**, like
    /// ReleaseCheck's: offline, rate-limited, a repository with no releases yet — none of them is the user's problem,
    /// and the app works the same without an update.
    /// </summary>
    public async Task<UpdateInfo?> FindNewerAsync()
    {
        try
        {
            return await _manager.CheckForUpdatesAsync();
        }
        catch (Exception)
        {
            // Velopack's failures span HTTP, JSON, IO and its own types, and none of them may reach the user from a
            // background check they did not ask for.
            return null;
        }
    }

    /// <summary>Downloads the release, reporting 0-100. Throws on failure or cancellation, which the caller reports.</summary>
    public Task DownloadAsync(UpdateInfo update, Action<int> progress, CancellationToken cancellationToken) =>
        _manager.DownloadUpdatesAsync(update, progress, cancellationToken);

    /// <summary>Exits this process at once; Velopack's updater swaps in the new version and starts it.</summary>
    public void ApplyAndRestart(UpdateInfo update) => _manager.ApplyUpdatesAndRestart(update.TargetFullRelease);

    /// <summary>The release's page on GitHub, where its notes are — tags are <c>v</c> plus the version.</summary>
    public static Uri PageFor(UpdateInfo update) =>
        new($"{MediaWikiClient.ProjectUrl}/releases/tag/v{update.TargetFullRelease.Version}");
}
