using EQLWikiAssistant.Core.Ocr;
using EQLWikiAssistant.Pipeline;

namespace EQLWikiAssistant.Tests.Pipeline;

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
    [InlineData(UiFont.EqlWikiAssistant)]
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
