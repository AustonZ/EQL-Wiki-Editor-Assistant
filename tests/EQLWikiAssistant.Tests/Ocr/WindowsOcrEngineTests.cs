using EQLWikiAssistant.Core.Ocr;
using EQLWikiAssistant.Ocr;
using EQLWikiAssistant.TestSupport;
using Xunit.Abstractions;

namespace EQLWikiAssistant.Tests.Ocr;

/// <summary>
/// These are golden tests against real screenshots in the gitignored samples/ folder (see samples/README.md) —
/// they won't exist on a fresh clone or CI, so each test exits early (reported via output, not a hard skip;
/// xunit 2.x has no built-in runtime skip without an extra package, and this is a personal, non-CI-gated repo)
/// when samples/ is absent. Run them on the primary dev machine, where the real screenshots live.
/// </summary>
public class WindowsOcrEngineTests
{
    private readonly ITestOutputHelper _output;

    public WindowsOcrEngineTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Construction_Succeeds_WhenAnOcrLanguagePackIsInstalled()
    {
        // Smoke test: throws with a clear message if no OCR language pack is installed for the user profile.
        var engine = new WindowsOcrEngine();
        Assert.NotNull(engine);
    }

    [Fact]
    public async Task RecognizeAsync_WaterFlaskWindow_RecognizesKeyFields()
    {
        string path = Path.Combine(RepoPaths.SamplesDirectory, "simple 1 item.jpg");
        if (!File.Exists(path))
        {
            _output.WriteLine($"Skipping: {path} not present (samples/ is gitignored, personal data).");
            return;
        }

        CapturedImage image = await ImageFile.LoadAsync(path);
        image = image.Crop(new Rect(1180, 325, 420, 330)).Resize(420 * 3, 330 * 3);

        IOcrEngine engine = new WindowsOcrEngine();
        IReadOnlyList<OcrLine> lines = await engine.RecognizeAsync(image);
        string allText = string.Join('\n', lines.Select(l => l.Text));
        _output.WriteLine(allText);

        // Known-reliable fields per the milestone 1 findings (see the plan) — deliberately not asserting on
        // every field, since some (e.g. the "Description" tab label) are known to be flaky even at this scale.
        Assert.Contains("Water Flask", allText);
        Assert.Contains("Quest", allText);
        Assert.Contains("Class: ALL", allText);
        Assert.Contains("Race: ALL", allText);
    }
}
