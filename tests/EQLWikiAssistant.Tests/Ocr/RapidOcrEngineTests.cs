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
    public async Task RecognizeAsync_BladestopperWindow_RecognizesFieldsWindowsOcrGotWrong()
    {
        string path = Path.Combine(RepoPaths.SamplesDirectory, "bladstopper +7 with exaltations.jpg");
        if (!File.Exists(path))
        {
            _output.WriteLine($"Skipping: {path} not present (samples/ is gitignored, personal data).");
            return;
        }

        CapturedImage image = await ImageFile.LoadAsync(path);
        image = image.Crop(new Rect(700, 410, 420, 550)); // native resolution — no upscale, see class doc

        using RapidOcrEngine engine = new();
        IReadOnlyList<OcrLine> lines = await engine.RecognizeAsync(image);
        string allText = string.Join('\n', lines.Select(l => l.Text));
        _output.WriteLine(allText);

        // Fields WindowsOcrEngine dropped or corrupted at this same resolution (see plan milestone 1):
        // the +7 level suffix, AC/Weight/HP/Stamina numeric values, and the roman-numeral effect ranks.
        Assert.Contains("Bladestopper +7", allText);
        Assert.Contains("AC:", allText);
        Assert.Contains("43", allText);
        Assert.Contains("2.4", allText);
        Assert.Contains("Improved Healing III", allText);
        Assert.Contains("Rune IV", allText);
    }
}
