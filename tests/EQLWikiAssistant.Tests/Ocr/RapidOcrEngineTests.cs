using EQLWikiAssistant.Core.Ocr;
using EQLWikiAssistant.Ocr;
using EQLWikiAssistant.TestSupport;
using Xunit.Abstractions;

namespace EQLWikiAssistant.Tests.Ocr;

/// <summary>
/// Golden tests for the OCR engine (see the plan's milestone 1 writeup for why RapidOCR was chosen over the
/// OS-provided Windows.Media.Ocr engine, which has since been removed entirely).
///
/// These read from <c>samples/</c>, which is gitignored because the captures are real game screenshots and may
/// contain private information (character and player names, chat). So a test that can't find its sample logs a
/// skip and returns rather than failing — that keeps the suite green on a fresh clone, at the cost of silently
/// losing coverage, which is why sample filenames are asserted-on constants rather than globs.
///
/// Deliberately does NOT upscale before recognition — testing showed upscaling measurably hurts this engine.
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

        // The classes of field the since-removed Windows.Media.Ocr engine dropped or corrupted at this same
        // resolution (see the plan's milestone 1 writeup): "+X" level suffixes, numeric stat values, decimals.
        Assert.Contains("Khyldom the Blood Drinker +1", allText);
        Assert.Contains("Base Dmg:", allText);
        Assert.Contains("39", allText);
        Assert.Contains("0.907", allText);
        Assert.Contains("2H Slashing", allText);
        Assert.Contains("Siphon", allText);
    }
}
