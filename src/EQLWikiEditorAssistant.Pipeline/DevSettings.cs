using System.Text.Json;
using System.Text.Json.Serialization;
using EQLWikiEditorAssistant.Core.Input;

namespace EQLWikiEditorAssistant.Pipeline;

/// <summary>
/// What a development build adds to <see cref="AppSettings"/>, under Settings > Dev-only. An installed release neither
/// reads nor writes it.
///
/// **A file of its own, not more properties on <see cref="AppSettings"/>**: <c>tools/install.ps1</c> copies the
/// release's data over the development folder on every run, <c>settings.json</c> included, which would reset these
/// each time. A release never has this file, so the copy leaves it alone.
///
/// Like <see cref="AppSettings"/>, a file that cannot be read loads as the defaults.
/// </summary>
public sealed record DevSettings
{
    public static DevSettings Default { get; } = new();

    /// <summary>The global hotkey that opens a saved screenshot and checks it as if it had just been captured (user,
    /// 2026-10-21, for testers' screenshots). **None unless chosen**: a global hotkey takes its combination away from
    /// every other program, and any default would collide with something (Ctrl+Shift+O is VS Code's Go to Symbol).</summary>
    public HotKeyChord? ScreenshotHotKey { get; init; }

    /// <summary>The folder the last screenshot was opened from, where the next file picker starts.</summary>
    public string? ScreenshotFolder { get; init; }

    public static DevSettings Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path)) return Default;

        try
        {
            DevSettings? settings = JsonSerializer.Deserialize<DevSettings>(File.ReadAllText(path), JsonOptions);
            if (settings is null) return Default;
            // A hotkey the settings window would refuse is dropped, as AppSettings does with the capture hotkey.
            return settings.ScreenshotHotKey is { Problem: not null } ? settings with { ScreenshotHotKey = null } : settings;
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

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };
}
