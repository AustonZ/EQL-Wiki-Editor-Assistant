using EQLWikiAssistant.Core.Ocr;
using EQLWikiAssistant.Ocr;
using EQLWikiAssistant.TestSupport;
using Xunit.Abstractions;

namespace EQLWikiAssistant.Tests.Ocr;

/// <summary>
/// Golden tests for the production-default OCR engine (see the plan's milestone 1 writeup for why RapidOCR
/// replaced WindowsOcrEngine as the default). Same gitignored-samples/ tolerance as WindowsOcrEngineTests.
/// Deliberately does NOT upscale before recognition — testing showed that helps Windows OCR but not RapidOCR.
/// </summary>
public class RapidOcrEngineTests
{
    private readonly ITestOutputHelper _output;

    public RapidOcrEngineTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Construction_Succeeds()
    {
        using var engine = new RapidOcrEngine();
        Assert.NotNull(engine);
    }

    [Fact]
    public async Task RecognizeAsync_WeaponWindow_RecognizesFieldsWindowsOcrGotWrong()
    {
        string path = Path.Combine(RepoPaths.SamplesDirectory, "08a-hover-tooltip-next-to-same-item-window.png");
        if (!File.Exists(path))
        {
            _output.WriteLine($"Skipping: {path} not present (samples/ is gitignored, personal data).");
            return;
        }

        CapturedImage image = await ImageFile.LoadAsync(path);
        image = image.Crop(new Rect(1085, 434, 404, 502)); // native resolution — no upscale, see class doc

        using RapidOcrEngine engine = new();
        IReadOnlyList<OcrLine> lines = await engine.RecognizeAsync(image);
        string allText = string.Join('\n', lines.Select(l => l.Text));
        _output.WriteLine(allText);

        // The classes of field WindowsOcrEngine dropped or corrupted at this same resolution (see the plan's
        // milestone 1 writeup): the "+X" level suffix, numeric stat values, and decimals.
        Assert.Contains("Khyldom the Blood Drinker +1", allText);
        Assert.Contains("Base Dmg:", allText);
        Assert.Contains("39", allText);
        Assert.Contains("0.907", allText);
        Assert.Contains("2H Slashing", allText);
        Assert.Contains("Siphon", allText);
    }
}
