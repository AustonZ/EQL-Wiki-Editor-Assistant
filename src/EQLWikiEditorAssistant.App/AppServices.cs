using System.IO;
using System.Net.Http;
using EQLWikiEditorAssistant.Capture;
using EQLWikiEditorAssistant.Core.Glyphs;
using EQLWikiEditorAssistant.Core.Locate;
using EQLWikiEditorAssistant.Core.Icons;
using EQLWikiEditorAssistant.Core.Input;
using EQLWikiEditorAssistant.Core.Imaging;
using EQLWikiEditorAssistant.Pipeline;
using EQLWikiEditorAssistant.Wiki.Ledger;
using EQLWikiEditorAssistant.Wiki.Mapping;
using EQLWikiEditorAssistant.Wiki.MediaWiki;

namespace EQLWikiEditorAssistant.App;

/// <summary>
/// Everything the app owns for its whole lifetime, built once.
///
/// **Built once quite deliberately.** The ledger and the icon cache are the app's state and only mean anything shared,
/// and the <see cref="MediaWikiClient"/> must keep one cookie container for its whole session, since MediaWiki ties
/// the login token, the session and the CSRF token together through cookies.
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

    private readonly GlyphTextReader _windowReader;
    private readonly HttpClient _http;
    private readonly HotKeySlot _captureHotKey;
    private readonly HotKeySlot _screenshotHotKey;

    /// <summary>
    /// Whether this is a development build — <c>dotnet run</c> or <c>tools/install.ps1</c>'s copy, anything Velopack
    /// did not install — which adds Settings > Dev-only and its screenshot hotkey (user, 2026-10-21). Decided by the
    /// same test as the data folder (<see cref="AppPaths.IsRelease"/>), so a copy using the release's data never offers
    /// anything dev-only.
    /// </summary>
    public static bool IsDevBuild => !AppPaths.IsRelease;

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
    public string? HotKeyProblem => _captureHotKey.Problem;

    /// <summary>A development build's settings (<see cref="DevSettings"/>); the defaults, unused, in a release.</summary>
    public DevSettings DevSettings { get; private set; }

    /// <summary>The dev-only screenshot hotkey was pressed. Raised on the hotkey's own thread, like
    /// <see cref="HotKeyPressed"/>. Never raised in a release, which registers no such hotkey.</summary>
    public event EventHandler? ScreenshotHotKeyPressed;

    /// <summary>Why the saved screenshot hotkey is not registered, or null when it is or none is set.</summary>
    public string? ScreenshotHotKeyProblem => _screenshotHotKey.Problem;

    /// <param name="progress">Told each slow step as it starts, so the window can say what it is waiting for: the icon
    /// index, loaded, or built with a running count the first time (user, 2026-10-09: "Starting…" alone looked like a
    /// hang).</param>
    public AppServices(IProgress<string>? progress = null)
    {
        AppPaths.EnsureExists();

        // Windows are found by their Description tab's pixels and read by exact glyph matching; no text-recognition
        // model is involved (user, 2026-10-09). See CLAUDE.md. The UI font is one value given to both the reader and
        // the pipeline's wrong-font guard, so they cannot disagree — here, and afterwards only through UseFontAsync,
        // which sets both.
        Settings = AppSettings.Load(AppPaths.SettingsFile);
        DevSettings = IsDevBuild ? DevSettings.Load(AppPaths.DevSettingsFile) : DevSettings.Default;
        _windowReader = new GlyphTextReader(Settings.Font);
        _captureHotKey = new HotKeySlot(() => HotKeyPressed?.Invoke(this, EventArgs.Empty));
        _screenshotHotKey = new HotKeySlot(() => ScreenshotHotKeyPressed?.Invoke(this, EventArgs.Empty));

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
        // behavior that existed before the library did. This runs off the UI thread with the rest of the
        // composition root, so the cost never blocks the window.
        string? iconFolder = AppPaths.IconLibraryDirectory;
        if (iconFolder is not null)
        {
            IconFiles = new IconLibraryFolder(iconFolder);
            progress?.Report("Loading the game's icons…");
            // Building reports every icon; the window hears about each whole percent, which is plenty to see it move.
            int shown = -1;
            IconLibrary = IconLibraryStore
                .LoadOrBuildAsync(iconFolder, AppPaths.IconIndexFile, decoder, (done, total) =>
                {
                    int percent = done * 100 / Math.Max(total, 1);
                    if (percent == shown) return;
                    shown = percent;
                    progress?.Report($"Indexing the game's icons, {percent}%…");
                }).GetAwaiter().GetResult();
        }

        Pipeline = new ItemCheckPipeline(
            Wiki,
            new BorderTracingWindowLocator(_windowReader),
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
        _captureHotKey.Register(Settings.HotKey);
        if (IsDevBuild) _screenshotHotKey.Register(DevSettings.ScreenshotHotKey);
    }

    /// <summary>
    /// Makes <paramref name="chord"/> the capture hotkey, and saves it. Returns why not, leaving the current hotkey
    /// working, when the combination is unacceptable or Windows refuses it.
    ///
    /// **The new combination is registered before the old one is released**, so a refusal — another program already
    /// holding it, which is the common case — changes nothing. While suspended for recording there is no old one
    /// registered, and <see cref="ResumeHotKeys"/> puts it back after a refusal. Choosing the combination already in use
    /// is accepted rather than refused (user, 2026-10-07: re-entering the same keys is how someone backs out of a
    /// mistake), and simply re-registers it.
    /// </summary>
    public async Task<string?> UseHotKeyAsync(HotKeyChord chord)
    {
        ArgumentNullException.ThrowIfNull(chord);
        if (chord.Problem is { } unacceptable) return unacceptable;
        if (IsDevBuild && chord == DevSettings.ScreenshotHotKey)
            return $"{chord} is the screenshot hotkey (Settings > Dev-only).";
        if (chord == Settings.HotKey && _captureHotKey.IsRegistered) return null;

        if (_captureHotKey.Use(chord) is { } problem) return problem;

        Settings = Settings with { HotKey = chord };
        await Settings.SaveAsync(AppPaths.SettingsFile);
        return null;
    }

    /// <summary>
    /// Makes <paramref name="chord"/> the dev-only screenshot hotkey, or removes it when null, and saves the choice.
    /// Returns why not, leaving the current one working — the same rules as <see cref="UseHotKeyAsync"/>, plus that it
    /// cannot be the capture hotkey.
    /// </summary>
    public async Task<string?> UseScreenshotHotKeyAsync(HotKeyChord? chord)
    {
        if (!IsDevBuild) throw new InvalidOperationException("The screenshot hotkey exists only in a development build.");
        if (chord?.Problem is { } unacceptable) return unacceptable;
        if (chord is not null && chord == Settings.HotKey) return $"{chord} is the capture hotkey.";
        if (chord == DevSettings.ScreenshotHotKey && (chord is null || _screenshotHotKey.IsRegistered)) return null;

        if (_screenshotHotKey.Use(chord) is { } problem) return problem;

        DevSettings = DevSettings with { ScreenshotHotKey = chord };
        await DevSettings.SaveAsync(AppPaths.DevSettingsFile);
        return null;
    }

    /// <summary>Remembers the folder a screenshot was last opened from, for the next file picker.</summary>
    public async Task UseScreenshotFolderAsync(string folder)
    {
        DevSettings = DevSettings with { ScreenshotFolder = folder };
        await DevSettings.SaveAsync(AppPaths.DevSettingsFile);
    }

    /// <summary>
    /// Releases every hotkey while the settings window records a new one. **Windows delivers a registered combination
    /// to its hotkey and never to a window**, so without this the current combination could not be recorded at all —
    /// pressing it while recording did nothing visible (bug found by the user, 2026-10-07). Both are released, so either
    /// page can record a combination the other holds and be told why it cannot have it. Nothing is lost: hotkeys are
    /// ignored while the settings window is open anyway. Always paired with <see cref="ResumeHotKeys"/>.
    /// </summary>
    public void SuspendHotKeys()
    {
        _captureHotKey.Suspend();
        _screenshotHotKey.Suspend();
    }

    /// <summary>Re-registers the saved hotkeys after recording, unless a new one has already replaced it. Safe to call
    /// more than once. If a saved combination was taken in the meantime, its problem property says so.</summary>
    public void ResumeHotKeys()
    {
        _captureHotKey.Resume(Settings.HotKey);
        if (IsDevBuild) _screenshotHotKey.Resume(DevSettings.ScreenshotHotKey);
    }

    /// <summary>
    /// One global hotkey and its state: registered, refused (<see cref="Problem"/>), or released while the settings
    /// window records. The capture hotkey and the dev-only screenshot hotkey follow the same rules, so they share this.
    /// </summary>
    private sealed class HotKeySlot(Action pressed) : IDisposable
    {
        private GlobalHotKey? _registered;
        private bool _suspended;

        /// <summary>Why the saved combination is not registered, or null.</summary>
        public string? Problem { get; private set; }

        public bool IsRegistered => _registered is not null;

        /// <summary>Registers the saved combination at start-up or after a suspension; null registers nothing.</summary>
        public void Register(HotKeyChord? chord)
        {
            _registered?.Dispose();
            _registered = null;
            Problem = null;
            if (chord is null) return;

            _registered = TryRegister(chord, out string? problem);
            Problem = problem;
        }

        /// <summary>Changes the combination, registering the new one before releasing the old, so a refusal leaves the
        /// old one working; null removes it. Returns why not.</summary>
        public string? Use(HotKeyChord? chord)
        {
            GlobalHotKey? next = null;
            if (chord is not null && (next = TryRegister(chord, out string? problem)) is null) return problem;

            _registered?.Dispose();
            _registered = next;
            _suspended = false;
            Problem = null;
            return null;
        }

        public void Suspend()
        {
            _registered?.Dispose();
            _registered = null;
            _suspended = true;
        }

        public void Resume(HotKeyChord? saved)
        {
            if (!_suspended) return;
            _suspended = false;
            Register(saved);
        }

        private GlobalHotKey? TryRegister(HotKeyChord chord, out string? problem)
        {
            try
            {
                var hotKey = new GlobalHotKey(chord);
                hotKey.Pressed += (_, _) => pressed();
                problem = null;
                return hotKey;
            }
            catch (InvalidOperationException)
            {
                // RegisterHotKey's only ordinary failure: the combination is taken, by another program or by Windows.
                problem = Refusal(chord);
                return null;
            }
        }

        private static string Refusal(HotKeyChord chord) =>
            $"Windows would not register {chord}: another program, or Windows itself, already uses it.";

        public void Dispose() => _registered?.Dispose();
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
    /// Reads a saved screenshot to check as though it had just been captured — the dev-only screenshot hotkey, for a
    /// tester's screenshot. Returns null with a reason when the file cannot be read as an image.
    /// </summary>
    public static async Task<(CapturedImage? Frame, string? Problem, string? Captured)> LoadScreenshotAsync(
        string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string name = $"'{Path.GetFileName(path)}'";
        try
        {
            byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            return (await WindowsImageDecoder.DecodeScreenshotAsync(bytes, cancellationToken), null, name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or System.Runtime.InteropServices.COMException)
        {
            return (null, $"{name} could not be read as an image: {ex.Message}", null);
        }
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

    /// <summary>The window options. The main window shows or hides its tray icon when the settings window closes;
    /// otherwise each is read when it applies, starting minimized at the next start.</summary>
    public async Task UseWindowOptionsAsync(bool minimizeToTray, bool autoMinimize, bool startMinimized)
    {
        Settings = Settings with
        {
            MinimizeToTray = minimizeToTray, AutoMinimize = autoMinimize, StartMinimized = startMinimized,
        };
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
        _captureHotKey.Dispose();
        _screenshotHotKey.Dispose();
        Capturer.Dispose();
        Wiki.Dispose();
        _http.Dispose();
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
