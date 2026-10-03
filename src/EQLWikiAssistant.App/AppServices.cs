using System.IO;
using System.Net.Http;
using EQLWikiAssistant.Capture;
using EQLWikiAssistant.Core.Glyphs;
using EQLWikiAssistant.Core.Locate;
using EQLWikiAssistant.Core.Icons;
using EQLWikiAssistant.Core.Ocr;
using EQLWikiAssistant.Ocr;
using EQLWikiAssistant.Pipeline;
using EQLWikiAssistant.Wiki.Ledger;
using EQLWikiAssistant.Wiki.Mapping;
using EQLWikiAssistant.Wiki.MediaWiki;

namespace EQLWikiAssistant.App;

/// <summary>
/// Everything the app owns for its whole lifetime, built once.
///
/// **Built once quite deliberately.** <see cref="RapidOcrEngine"/> loads three ONNX models in its constructor, so
/// per-capture construction would add seconds to every hotkey press; the ledger and the icon cache are the app's
/// state and only mean anything shared; and the <see cref="MediaWikiClient"/> must keep one cookie container for its
/// whole session, since MediaWiki ties the login token, the session and the CSRF token together through cookies.
///
/// This is a plain composition root rather than a DI container: there is one graph, built in one place, and nothing
/// resolves anything at runtime.
/// </summary>
public sealed class AppServices : IDisposable
{
    /// <summary>The wiki this tool edits. Not configurable in v1 — the mapping layer is where wiki-specific
    /// knowledge belongs, and a second wiki would need its own mapping anyway.</summary>
    public static readonly Uri Endpoint = new("https://eqlwiki.com/api.php");

    /// <summary>
    /// The game's process name, without <c>.exe</c> — **how the window to capture is identified**.
    ///
    /// Measured, not assumed (2026-09-29): the running client is <c>eqgame</c>. It is the process rather than the
    /// title because the title is genuinely ambiguous for this tool's own user — see <see cref="GameWindowTitle"/>.
    /// </summary>
    public const string GameProcessName = "eqgame";

    /// <summary>
    /// What the game's window title contains. **Only a fallback**, for the day the client is renamed.
    ///
    /// It cannot be the primary rule: anyone editing this wiki plausibly has a browser on a page titled
    /// `... - EverQuest Legends Wiki - ...` and a Discord on `... | EverQuest Legends - Discord`, and both match.
    /// </summary>
    public const string GameWindowTitle = "EverQuest";

    private readonly RapidOcrEngine _rapidOcr;
    private readonly HttpClient _http;

    public MediaWikiClient Wiki { get; }
    public CheckedItemsLedger Ledger { get; }
    public WikiMapping Mapping { get; }
    public IconCache Icons { get; }

    /// <summary>The wiki's verified-pages list. Read-only here: the tool reports verification and never claims it.</summary>
    public VerifiedPages Verified { get; }
    public ItemCheckPipeline Pipeline { get; }
    public WindowCapturer Capturer { get; }
    /// <summary>Fingerprints of every icon the game ships, or null when the asset folder could not be found.
    /// Null degrades the feature (a generated page gets a blank lucy_img_ID) rather than breaking anything.</summary>
    public IconLibrary? IconLibrary { get; }

    /// <summary>The icon PNGs themselves, for showing a match and uploading it.</summary>
    public IconLibraryFolder? IconFiles { get; }

    public ICredentialStore Credentials { get; } = new WindowsCredentialStore();

    /// <summary>Whether this session has logged in. Reads are anonymous, so this stays false until the first commit
    /// — which keeps the common path (check an item, find it already correct) free of credentials entirely.</summary>
    public bool IsLoggedIn => Wiki.IsLoggedIn;

    public AppServices()
    {
        AppPaths.EnsureExists();

        // The full-frame pass keeps RapidOCR (it scans 3D world content and finds the Description anchors); window
        // crops go through exact glyph matching. See CLAUDE.md — this split is the whole extraction-accuracy story.
        _rapidOcr = new RapidOcrEngine();
        IOcrEngine ocr = new RoutingOcrEngine(fullFrame: _rapidOcr, windowCrop: new GlyphOcrEngine());

        Wiki = MediaWikiClient.Create(Endpoint);
        Ledger = CheckedItemsLedger.Load(AppPaths.LedgerFile);
        Mapping = File.Exists(AppPaths.MappingFile) ? WikiMapping.Load(AppPaths.MappingFile) : WikiMapping.Default;

        _http = new HttpClient();
        _http.DefaultRequestHeaders.Add("User-Agent", MediaWikiClient.UserAgent);
        Icons = new IconCache(AppPaths.IconCacheDirectory, new WikiIconSource(_http, Endpoint));
        Verified = new VerifiedPages(Wiki, AppPaths.VerifiedPagesFile);

        var decoder = new WindowsImageDecoder();

        // The icon library, for identifying a new item's lucy_img_ID. Loaded from a cached fingerprint index that
        // rebuilds itself when the icon folder changes; the first run after an asset re-export costs about eight
        // seconds. Absent entirely when the folder cannot be found, which simply leaves lucy_img_ID blank — the
        // behaviour that existed before the library did. This runs off the UI thread with the rest of the
        // composition root, so the cost lands where the OCR models already do.
        string? iconFolder = AppPaths.IconLibraryDirectory;
        if (iconFolder is not null)
        {
            IconFiles = new IconLibraryFolder(iconFolder);
            IconLibrary = IconLibraryStore
                .LoadOrBuildAsync(iconFolder, AppPaths.IconIndexFile, decoder).GetAwaiter().GetResult();
        }

        Pipeline = new ItemCheckPipeline(
            Wiki,
            new BorderTracingWindowLocator(ocr),
            Ledger,
            Mapping,
            Icons,
            decoder,
            Verified,
            IconLibrary,
            IconFiles);

        // **Set once, here, so no write path can forget it** (bug found by the user, 2026-09-30: the formatting
        // commit never logged in, because logging in was each caller's job and that caller did not). The pipeline
        // runs this before either commit writes, and turns a refusal into an ordinary failed-commit message.
        Pipeline.BeforeWriting = EnsureLoggedInAsync;

        // **And the gate is not enough on its own**, because a session can die after it has been passed (user,
        // 2026-10-02). The gate only asks whether this process has logged in; the wiki can expire the session
        // minutes later, and the next write then fails with "not logged in" however correct the credential is. The
        // client repairs that itself and retries the write once — see MediaWikiClient.ReestablishSession.
        Wiki.ReestablishSession = ReestablishSessionAsync;

        Capturer = new WindowCapturer();
    }

    /// <summary>
    /// Captures the game window, or returns null with a reason if it cannot be found.
    ///
    /// Graphics Capture reads an unfocused window fine, which is what makes a global hotkey useful at all: the user
    /// presses it with the game focused and the result appears in this window behind it.
    /// </summary>
    public async Task<(CapturedImage? Frame, string? Problem, string? Captured)> CaptureGameWindowAsync(
        CancellationToken cancellationToken = default)
    {
        // The process first. Falling back to the title is deliberate but second: it is what to do if the client is
        // ever renamed, not a way to identify it.
        IReadOnlyList<FoundWindow> windows = WindowFinder.FindByProcessName(GameProcessName);
        bool byTitle = windows.Count == 0;
        if (byTitle) windows = WindowFinder.FindByTitleSubstring(GameWindowTitle);

        if (windows.Count == 0)
            return (null, $"No '{GameProcessName}' window is open, and nothing has '{GameWindowTitle}' in its " +
                          "title either. Start the game first.", null);

        FoundWindow window = windows[0];
        CapturedImage? frame = await Capturer.CaptureAsync(window.Handle, cancellationToken);
        if (frame is null) return (null, $"'{window.Title}' could not be captured.", null);

        // **Say which window was read.** Silently picking one of several is the bug this is fixing, and a capture
        // that says what it captured diagnoses itself — which the previous one, taking whatever sat topmost, could
        // not. The title-fallback case names its competition too, since that is the ambiguous path.
        string captured = byTitle && windows.Count > 1
            ? $"'{window.Title}' (of {windows.Count} windows matching '{GameWindowTitle}')"
            : $"'{window.Title}'";

        return (frame, null, captured);
    }

    /// <summary>Logs in for a commit, using the bot password in Windows Credential Manager. Returns why it could not
    /// rather than throwing, since "no credential stored yet" is an ordinary first-run state.</summary>
    public async Task<string?> EnsureLoggedInAsync(CancellationToken cancellationToken = default)
    {
        if (Wiki.IsLoggedIn) return null;
        return await LogInAsync(cancellationToken);
    }

    /// <summary>
    /// Starts a fresh session after the wiki has disowned the current one — <see cref="MediaWikiClient
    /// .ReestablishSession"/>. The credential is already saved, so this needs nothing from the user and the write
    /// that tripped it goes through on its retry.
    ///
    /// **It is the same login as the gate's, deliberately.** Two ways to log in would be two places to get the
    /// credential lookup or its failure wording wrong, and the second one only ever runs when something has already
    /// gone slightly wrong.
    /// </summary>
    private async Task<bool> ReestablishSessionAsync(CancellationToken cancellationToken) =>
        await LogInAsync(cancellationToken) is null;

    private async Task<string?> LogInAsync(CancellationToken cancellationToken)
    {
        BotCredentials? credentials = Credentials.Read();
        if (credentials is null)
            return "No bot password is stored. Create one at https://eqlwiki.com/Special:BotPasswords with the " +
                   "\"Edit existing pages\" right, then run: dotnet run --project tools/WikiSpike -- login";

        try
        {
            await Wiki.LoginAsync(credentials, cancellationToken);
            return null;
        }
        catch (MediaWikiException ex)
        {
            return $"The wiki rejected the stored bot password ({ex.Code}): {ex.Message}";
        }
        catch (HttpRequestException ex)
        {
            return $"Could not reach the wiki to log in: {ex.Message}";
        }
    }

    public Task SaveLedgerAsync() => Pipeline.SaveLedgerAsync(AppPaths.LedgerFile);

    public void Dispose()
    {
        Capturer.Dispose();
        Wiki.Dispose();
        _http.Dispose();
        _rapidOcr.Dispose();
    }
}
