using EQLWikiEditorAssistant.Core.Input;
using EQLWikiEditorAssistant.Pipeline;

namespace EQLWikiEditorAssistant.Tests.Pipeline;

public class DevSettingsTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"eqlwiki-dev-settings-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    /// <summary>No screenshot hotkey unless one is chosen: a global hotkey takes its combination from every other
    /// program.</summary>
    [Fact]
    public void ThereIsNoScreenshotHotKeyUnlessChosen()
    {
        Assert.Null(DevSettings.Load(_path).ScreenshotHotKey);

        File.WriteAllText(_path, "{}");
        Assert.Null(DevSettings.Load(_path).ScreenshotHotKey);
    }

    [Fact]
    public async Task TheScreenshotHotKeyAndFolderRoundTrip()
    {
        var chord = new HotKeyChord(HotKeyModifiers.Control | HotKeyModifiers.Alt, 0x4F);
        await new DevSettings { ScreenshotHotKey = chord, ScreenshotFolder = @"C:\samples\from_testers" }.SaveAsync(_path);

        DevSettings loaded = DevSettings.Load(_path);
        Assert.Equal(chord, loaded.ScreenshotHotKey);
        Assert.Equal(@"C:\samples\from_testers", loaded.ScreenshotFolder);
        Assert.False(File.Exists(_path + ".tmp"));
    }

    /// <summary>Removing the hotkey is saved as removed, not left as the old one.</summary>
    [Fact]
    public async Task ARemovedHotKeyStaysRemoved()
    {
        await new DevSettings { ScreenshotHotKey = new HotKeyChord(HotKeyModifiers.Control, 0x4F) }.SaveAsync(_path);
        await (DevSettings.Load(_path) with { ScreenshotHotKey = null }).SaveAsync(_path);

        Assert.Null(DevSettings.Load(_path).ScreenshotHotKey);
    }

    /// <summary>A combination the settings window would refuse is dropped, keeping the rest of the file.</summary>
    [Fact]
    public async Task AnUnacceptableHotKeyIsDropped()
    {
        await new DevSettings { ScreenshotHotKey = new HotKeyChord(HotKeyModifiers.Shift, 0x4F), ScreenshotFolder = "x" }
            .SaveAsync(_path);

        DevSettings loaded = DevSettings.Load(_path);
        Assert.Null(loaded.ScreenshotHotKey);
        Assert.Equal("x", loaded.ScreenshotFolder);
    }

    [Fact]
    public void ADamagedFileLoadsAsTheDefaults()
    {
        File.WriteAllText(_path, "{ not json");

        Assert.Equal(DevSettings.Default, DevSettings.Load(_path));
    }

    /// <summary>Its own file, which is what survives <c>install.ps1</c> copying the release's settings.json over the
    /// development folder.</summary>
    [Fact]
    public void ItIsNotTheSettingsFile()
    {
        Assert.NotEqual(AppPaths.SettingsFile, AppPaths.DevSettingsFile);
        Assert.Equal(Path.GetDirectoryName(AppPaths.SettingsFile), Path.GetDirectoryName(AppPaths.DevSettingsFile));
    }
}
