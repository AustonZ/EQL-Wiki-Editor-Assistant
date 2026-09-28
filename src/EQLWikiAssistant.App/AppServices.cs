using System.IO;
using System.Net.Http;
using EQLWikiAssistant.Capture;
using EQLWikiAssistant.Core.Glyphs;
using EQLWikiAssistant.Core.Locate;
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

    /// <summary>What the game's window title contains. Used to find the window to capture.</summary>
    public const string GameWindowTitle = "EverQuest";

    private readonly RapidOcrEngine _rapidOcr;
    private readonly HttpClient _http;

    public MediaWikiClient Wiki { get; }
    public CheckedItemsLedger Ledger { get; }
    public WikiMapping Mapping { get; }
    public IconCache Icons { get; }
    public ItemCheckPipeline Pipeline { get; }
    public WindowCapturer Capturer { get; }
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

        Pipeline = new ItemCheckPipeline(
            Wiki,
            new BorderTracingWindowLocator(ocr),
            Ledger,
            Mapping,
            Icons,
            new WindowsImageDecoder());

        Capturer = new WindowCapturer();
    }

    /// <summary>
    /// Captures the game window, or returns null with a reason if it cannot be found.
    ///
    /// Graphics Capture reads an unfocused window fine, which is what makes a global hotkey useful at all: the user
    /// presses it with the game focused and the result appears in this window behind it.
    /// </summary>
    public async Task<(CapturedImage? Frame, string? Problem)> CaptureGameWindowAsync(
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<FoundWindow> windows = WindowFinder.FindByTitleSubstring(GameWindowTitle);
        if (windows.Count == 0)
            return (null, $"No window with '{GameWindowTitle}' in its title is open. Start the game first.");

        CapturedImage? frame = await Capturer.CaptureAsync(windows[0].Handle, cancellationToken);
        return frame is null
            ? (null, $"'{windows[0].Title}' could not be captured.")
            : (frame, null);
    }

    /// <summary>Logs in for a commit, using the bot password in Windows Credential Manager. Returns why it could not
    /// rather than throwing, since "no credential stored yet" is an ordinary first-run state.</summary>
    public async Task<string?> EnsureLoggedInAsync(CancellationToken cancellationToken = default)
    {
        if (Wiki.IsLoggedIn) return null;

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
