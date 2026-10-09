using System.Text.Json;
using System.Text.Json.Serialization;
using EQLWikiEditorAssistant.Core.Input;
using EQLWikiEditorAssistant.Core.Ocr;

namespace EQLWikiEditorAssistant.Pipeline;

/// <summary>
/// What the user has chosen in the settings window: the UI font the game draws in, the capture hotkey, and whether
/// captured frames are kept on disk.
///
/// **A file that cannot be read loads as the defaults rather than throwing**, the same rule the ledger follows and
/// for a stronger reason: there is nothing here that cannot be chosen again in a few seconds, so refusing to start
/// over a damaged settings file would be the worse failure. A wrong font is not silent either way — the pipeline
/// refuses a window drawn in the other one and says which it saw (<c>ItemCheckStatus.WrongFont</c>).
/// </summary>
public sealed record AppSettings
{
    public static AppSettings Default { get; } = new();

    /// <summary>The font the game is set to draw its UI in, which decides how the window reader treats a bare bar.
    /// See <see cref="UiFont"/>.</summary>
    public UiFont Font { get; init; } = UiFonts.AppDefault;

    /// <summary>The global hotkey that captures the game window. Ctrl+Shift+E unless the user chose another.</summary>
    public HotKeyChord HotKey { get; init; } = HotKeyChord.Default;

    /// <summary>Whether each captured frame is kept in app-data. **Off unless chosen**: a frame is a full screenshot,
    /// with other players' names and chat in it, and otherwise never touches the disk.</summary>
    public bool KeepCaptures { get; init; }

    public static AppSettings Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path)) return Default;

        try
        {
            AppSettings? settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions);
            // An enum outside the known members (a font a newer build knew about) is as unreadable as a corrupt file.
            if (settings is null || !Enum.IsDefined(settings.Font)) return Default;

            // A hotkey is judged on its own: one the settings window would refuse falls back to the default without
            // costing the font choice beside it. A file written before the hotkey existed has none, and gets the
            // default the same way.
            return settings.HotKey is { Problem: null } ? settings : settings with { HotKey = HotKeyChord.Default };
        }
        catch (JsonException)
        {
            return Default;
        }
    }

    /// <summary>Written through a temporary file, so an interrupted save cannot leave a truncated one behind.</summary>
    public async Task SaveAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        string temporary = full + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(this, JsonOptions), cancellationToken)
            .ConfigureAwait(false);
        File.Move(temporary, full, overwrite: true);
    }

    /// <summary>Enums by name, so the file reads <c>"font": "Arial"</c> and survives the enum being reordered —
    /// the lesson of the ledger's outcomes, which are persisted by name for exactly that reason.</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };
}
