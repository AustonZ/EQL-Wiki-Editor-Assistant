using System.IO;
using System.Net.Http;
using EQLWikiEditorAssistant.Capture;
using EQLWikiEditorAssistant.Core.Glyphs;
using EQLWikiEditorAssistant.Core.Locate;
using EQLWikiEditorAssistant.Core.Icons;
using EQLWikiEditorAssistant.Core.Input;
using EQLWikiEditorAssistant.Core.Ocr;
using EQLWikiEditorAssistant.Ocr;
using EQLWikiEditorAssistant.Pipeline;
using EQLWikiEditorAssistant.Wiki.Ledger;
using EQLWikiEditorAssistant.Wiki.Mapping;
using EQLWikiEditorAssistant.Wiki.MediaWiki;

namespace EQLWikiEditorAssistant.App;

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

    /// <summary>What the wiki sees this app as: its name, its version and where to find it.</summary>
    internal static readonly string UserAgent = MediaWikiClient.UserAgentFor(AppInfo.Version);

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
    private readonly GlyphOcrEngine _windowReader;
    private readonly HttpClient _http;
    private GlobalHotKey? _hotKey;

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

    /// <summary>What the user chose in the settings window, as last saved.</summary>
    public AppSettings Settings { get; private set; }

    /// <summary>
    /// The capture hotkey was pressed. Raised on the hotkey's own thread, so a handler touching the UI must marshal.
    ///
    /// **The hotkey lives here rather than in the main window** so that changing it has one home, the way the font
    /// does: the settings window asks <see cref="UseHotKeyAsync"/>, and whoever listens keeps listening.
    /// </summary>
    public event EventHandler? HotKeyPressed;

    /// <summary>Why the saved hotkey is not registered — usually another program holding the same combination — or
    /// null when it is. Capturing still works from the button either way.</summary>
    public string? HotKeyProblem { get; private set; }

    public AppServices()
    {
        AppPaths.EnsureExists();

        // The full-frame pass keeps RapidOCR (it scans 3D world content and finds the Description anchors); window
        // crops go through exact glyph matching. See CLAUDE.md — this split is the whole extraction-accuracy story.
        // The UI font is one value given to both the reader and the pipeline's wrong-font guard, so they cannot
        // disagree — here, and afterwards only through UseFontAsync, which sets both.
        Settings = AppSettings.Load(AppPaths.SettingsFile);
        _rapidOcr = new RapidOcrEngine();
        _windowReader = new GlyphOcrEngine(Settings.Font);
        IOcrEngine ocr = new RoutingOcrEngine(fullFrame: _rapidOcr, windowCrop: _windowReader);

        Wiki = MediaWikiClient.Create(Endpoint, UserAgent);
        // Every edit and upload says which app and version made it — see MediaWikiClient.SummaryTag.
        Wiki.SummaryTag = $"{AppInfo.SummaryName} {AppInfo.Version}";
        Ledger =CheckedItemsLedger.Load(AppPaths.LedgerFile);

        // The built-in mapping, always. There is no mapping file in v1: the format that existed was lossy, and the
        // settings window shows this mapping read-only (user, 2026-10-05). See WikiMapping.
        Mapping = WikiMapping.Default;

        _http = new HttpClient();
        _http.DefaultRequestHeaders.Add("User-Agent", UserAgent);
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

        // The same font the window reader was given above: a window the game drew in a different one is refused
        // rather than read, because the font decides whether a bare bar is an l or an I.
        Pipeline.ConfiguredFont = Settings.Font;

        // **And the gate is not enough on its own**, because a session can die after it has been passed (user,
        // 2026-10-02). The gate only asks whether this process has logged in; the wiki can expire the session
        // minutes later, and the next write then fails with "not logged in" however correct the credential is. The
        // client repairs that itself and retries the write once — see MediaWikiClient.ReestablishSession.
        Wiki.ReestablishSession = ReestablishSessionAsync;

        Capturer = new WindowCapturer();

        // Last, so a refused combination costs nothing else: the tool starts, the Capture button works, and the
        // settings window says why the hotkey does not.
        _hotKey = TryRegister(Settings.HotKey, out string? problem);
        HotKeyProblem = problem;
    }

    /// <summary>
    /// Makes <paramref name="chord"/> the capture hotkey, and saves it. Returns why not, leaving the current hotkey
    /// working, when the combination is unacceptable or Windows refuses it.
    ///
    /// **The new combination is registered before the old one is released**, so a refusal — another program already
    /// holding it, which is the common case — changes nothing. While suspended for recording there is no old one
    /// registered, and <see cref="ResumeHotKey"/> puts it back after a refusal. Choosing the combination already in use
    /// is accepted rather than refused (user, 2026-10-07: re-entering the same keys is how someone backs out of a
    /// mistake), and simply re-registers it.
    /// </summary>
    public async Task<string?> UseHotKeyAsync(HotKeyChord chord)
    {
        ArgumentNullException.ThrowIfNull(chord);
        if (chord.Problem is { } unacceptable) return unacceptable;
        if (chord == Settings.HotKey && _hotKey is not null) return null;

        GlobalHotKey? registered = TryRegister(chord, out string? problem);
        if (registered is null) return problem;

        _hotKey?.Dispose();
        _hotKey = registered;
        _hotKeySuspended = false;
        HotKeyProblem = null;

        Settings = Settings with { HotKey = chord };
        await Settings.SaveAsync(AppPaths.SettingsFile);
        return null;
    }

    /// <summary>
    /// Releases the hotkey while the settings window records a new one. **Windows delivers a registered combination to
    /// its hotkey and never to a window**, so without this the current combination could not be recorded at all —
    /// pressing it while recording did nothing visible (bug found by the user, 2026-10-07). Nothing is lost: captures
    /// are ignored while the settings window is open anyway. Always paired with <see cref="ResumeHotKey"/>.
    /// </summary>
    public void SuspendHotKey()
    {
        _hotKey?.Dispose();
        _hotKey = null;
        _hotKeySuspended = true;
    }

    /// <summary>Re-registers the saved hotkey after recording, unless a new one has already replaced it. Safe to call
    /// more than once. If the saved combination was taken in the meantime, <see cref="HotKeyProblem"/> says so.</summary>
    public void ResumeHotKey()
    {
        if (!_hotKeySuspended) return;
        _hotKeySuspended = false;
        _hotKey = TryRegister(Settings.HotKey, out string? problem);
        HotKeyProblem = problem;
    }

    private bool _hotKeySuspended;

    private GlobalHotKey? TryRegister(HotKeyChord chord, out string? problem)
    {
        try
        {
            var hotKey = new GlobalHotKey(chord);
            hotKey.Pressed += (_, _) => HotKeyPressed?.Invoke(this, EventArgs.Empty);
            problem = null;
            return hotKey;
        }
        catch (InvalidOperationException)
        {
            // RegisterHotKey's only ordinary failure: the combination is taken, by another program or by Windows.
            problem = $"Windows would not register {chord}: another program, or Windows itself, already uses it.";
            return null;
        }
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

    /// <summary>
    /// Switches the UI font the tool reads, and saves the choice.
    ///
    /// **The one place after start-up that the font changes, and it changes both halves together**: the reader that
    /// decides what a bare bar means, and the pipeline's guard that refuses a window drawn in the other font. Set
    /// apart, a window would be read in one font and checked against the other. Only call it while no capture is
    /// running — the settings window is modal and is disabled during a capture, which is what guarantees that.
    /// </summary>
    public async Task UseFontAsync(UiFont font)
    {
        _windowReader.Font = font;
        Pipeline.ConfiguredFont = font;
        Settings = Settings with { Font = font };
        await Settings.SaveAsync(AppPaths.SettingsFile);
    }

    public async Task UseKeepCapturesAsync(bool keep)
    {
        Settings = Settings with { KeepCaptures = keep };
        await Settings.SaveAsync(AppPaths.SettingsFile);
    }

    /// <summary>
    /// Asks the wiki what a bot password may do, **without writing anything** — the in-app form of
    /// <c>WikiSpike whoami</c>. A throwaway session does the asking, so the app's own session is never touched by a
    /// credential that may turn out to be wrong.
    ///
    /// Asked rather than discovered by attempting an edit: a failed attempt is still a revision in somebody's page
    /// history, and an ordinary editor on this wiki cannot delete one.
    /// </summary>
    public static async Task<CredentialCheck> CheckCredentialAsync(
        BotCredentials credentials, CancellationToken cancellationToken = default)
    {
        using MediaWikiClient client = MediaWikiClient.Create(Endpoint, UserAgent);
        try
        {
            await client.LoginAsync(credentials, cancellationToken);
            return new CredentialCheck(await client.GetUserInfoAsync(cancellationToken), null);
        }
        catch (MediaWikiException ex)
        {
            return new CredentialCheck(null, $"The wiki rejected it ({ex.Code}): {ex.Message}");
        }
        catch (WikiUnavailableException ex)
        {
            return new CredentialCheck(null, $"Could not reach the wiki: {ex.Message}");
        }
        catch (HttpRequestException ex)
        {
            return new CredentialCheck(null, $"Could not reach the wiki: {ex.Message}");
        }
    }

    /// <summary>
    /// Checks a new bot password and stores it only if the wiki accepts it with the right to edit. **A credential the
    /// wiki rejects is never stored**: it would sit in Credential Manager and fail at the moment of saving an edit,
    /// which is the worst moment to find out.
    ///
    /// On success the app's session is forgotten, so the next write logs in with the new credential rather than
    /// carrying on under the old one.
    /// </summary>
    public async Task<CredentialCheck> SaveCredentialAsync(
        BotCredentials credentials, CancellationToken cancellationToken = default)
    {
        CredentialCheck check = await CheckCredentialAsync(credentials, cancellationToken);
        if (!check.MayBeStored) return check;

        Credentials.Write(credentials);
        Wiki.ForgetSession();
        return check;
    }

    /// <summary>Removes the stored bot password. The current session is forgotten too, so nothing more is written
    /// under it.</summary>
    public bool ForgetCredential()
    {
        Wiki.ForgetSession();
        return Credentials.Delete();
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
            return "No bot password is stored. Enter one under Settings > Wiki account.";

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
        _hotKey?.Dispose();
        Capturer.Dispose();
        Wiki.Dispose();
        _http.Dispose();
        _rapidOcr.Dispose();
    }
}

/// <summary>
/// What the wiki said about a bot password. <see cref="Info"/> is null when it could not be asked at all —
/// rejected, or the wiki unreachable — and <see cref="Problem"/> says which.
/// </summary>
public sealed record CredentialCheck(UserInfo? Info, string? Problem)
{
    /// <summary>
    /// Whether this credential is worth keeping: the wiki accepted it, the login actually stuck, and it may edit.
    ///
    /// **An anonymous session counts as a failure even though the login "succeeded"** — that is the failure that
    /// matters most with a fresh bot password, because it would otherwise edit as the user's IP address.
    /// </summary>
    public bool MayBeStored => Info is { IsAnonymous: false, CanEdit: true };

    /// <summary>Why <see cref="MayBeStored"/> is false, in words for the user.</summary>
    public string? Refusal => Problem ?? Info switch
    {
        { IsAnonymous: true } => "The login did not stick: the wiki still sees an anonymous session.",
        { CanEdit: false } => "This bot password may not edit pages. Re-create it at Special:BotPasswords with " +
                              "the \"Edit existing pages\" grant.",
        _ => null,
    };
}
