using EQLWikiEditorAssistant.Core.Input;
using EQLWikiEditorAssistant.Core.Glyphs;
using EQLWikiEditorAssistant.Pipeline;

namespace EQLWikiEditorAssistant.Tests.Pipeline;

public class AppSettingsTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"eqlwiki-settings-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Fact]
    public void NoFileMeansTheDefaults()
    {
        Assert.Equal(UiFonts.AppDefault, AppSettings.Load(_path).Font);
    }

    /// <summary>Both directions, so a round trip that only ever wrote the default could not pass.</summary>
    [Theory]
    [InlineData(UiFont.Arial)]
    [InlineData(UiFont.EqlWikiEditorAssistant)]
    public async Task TheFontRoundTrips(UiFont font)
    {
        await new AppSettings { Font = font }.SaveAsync(_path);

        Assert.Equal(font, AppSettings.Load(_path).Font);
        Assert.False(File.Exists(_path + ".tmp"));
    }

    /// <summary>By name, so reordering the enum can never turn a saved Arial into something else — the hazard the
    /// ledger's outcomes were persisted by name to avoid.</summary>
    [Fact]
    public async Task TheFontIsWrittenByName()
    {
        await new AppSettings { Font = UiFont.Arial }.SaveAsync(_path);

        Assert.Contains("\"Arial\"", File.ReadAllText(_path));
    }

    [Fact]
    public async Task TheHotKeyRoundTrips()
    {
        var chord = new HotKeyChord(HotKeyModifiers.Control | HotKeyModifiers.Alt, 0x78);
        await new AppSettings { Font = UiFont.Arial, HotKey = chord }.SaveAsync(_path);

        AppSettings loaded = AppSettings.Load(_path);
        Assert.Equal(chord, loaded.HotKey);
        Assert.Equal(UiFont.Arial, loaded.Font);
    }

    /// <summary>Off unless chosen: a kept frame is a full screenshot with other players' names in it. A file written
    /// before the setting existed must load as off too, not as whatever happened to be convenient.</summary>
    [Fact]
    public void CapturesAreNotKeptUnlessChosen()
    {
        Assert.False(AppSettings.Load(_path).KeepCaptures);

        File.WriteAllText(_path, """{ "font": "Arial" }""");
        Assert.False(AppSettings.Load(_path).KeepCaptures);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task KeepingCapturesRoundTrips(bool keep)
    {
        var chord = new HotKeyChord(HotKeyModifiers.Control, 82);
        await new AppSettings { Font = UiFont.Arial, HotKey = chord, KeepCaptures = keep }.SaveAsync(_path);

        AppSettings loaded = AppSettings.Load(_path);
        Assert.Equal(keep, loaded.KeepCaptures);
        Assert.Equal(chord, loaded.HotKey);
        Assert.Equal(UiFont.Arial, loaded.Font);
    }

    /// <summary>The acceptability rule is derived, so it is not saved — and a file from the build that did save it
    /// (the user's own, 2026-10-07, reproduced verbatim) still loads.</summary>
    [Fact]
    public async Task TheDerivedProblemIsNotSavedAndAnOldFileCarryingItStillLoads()
    {
        await new AppSettings().SaveAsync(_path);
        Assert.DoesNotContain("problem", File.ReadAllText(_path), StringComparison.OrdinalIgnoreCase);

        File.WriteAllText(_path, """
            {
              "font": "EqlWikiEditorAssistant",
              "hotKey": {
                "modifiers": "Control",
                "virtualKey": 82,
                "problem": null
              }
            }
            """);
        Assert.Equal(new HotKeyChord(HotKeyModifiers.Control, 82), AppSettings.Load(_path).HotKey);
    }

    /// <summary>A file saved before the hotkey existed, or carrying one the settings window would refuse, falls back
    /// to the default hotkey — without losing the font chosen beside it.</summary>
    [Theory]
    [InlineData("""{ "font": "Arial" }""")]
    [InlineData("""{ "font": "Arial", "hotKey": null }""")]
    [InlineData("""{ "font": "Arial", "hotKey": { "modifiers": "Shift", "virtualKey": 69 } }""")]
    [InlineData("""{ "font": "Arial", "hotKey": { "modifiers": "Control", "virtualKey": 0 } }""")]
    public void AMissingOrUnacceptableHotKeyFallsBackAlone(string content)
    {
        File.WriteAllText(_path, content);

        AppSettings loaded = AppSettings.Load(_path);
        Assert.Equal(HotKeyChord.Default, loaded.HotKey);
        Assert.Equal(UiFont.Arial, loaded.Font);
    }

    /// <summary>Nothing in this file is hard to choose again, so a damaged one must not stop the tool starting.</summary>
    [Theory]
    [InlineData("{ not json")]
    [InlineData("""{ "font": "Comic Sans" }""")]
    [InlineData("""{ "font": 7 }""")]
    [InlineData("null")]
    public void AnUnreadableFileLoadsAsTheDefaults(string content)
    {
        File.WriteAllText(_path, content);

        Assert.Equal(UiFonts.AppDefault, AppSettings.Load(_path).Font);
    }
}
