using EQLWikiAssistant.Core.Locate;
using EQLWikiAssistant.Core.Ocr;
using EQLWikiAssistant.Ocr;
using EQLWikiAssistant.TestSupport;
using Xunit.Abstractions;

namespace EQLWikiAssistant.Tests.Locate;

/// <summary>
/// Golden tests against real, full (uncropped) screenshots in the gitignored samples/ folder — see
/// WindowsOcrEngineTests for the skip-if-missing rationale. These exercise the whole pipeline (OCR the full
/// frame, then locate) since that is what actually matters: locate is only as good as its input, and whole-
/// screenshot OCR behaves differently from OCR-ing a hand-picked crop (see the plan's milestone 2 writeup).
/// </summary>
public class ItemWindowLocatorTests
{
    private readonly ITestOutputHelper _output;

    public ItemWindowLocatorTests(ITestOutputHelper output) => _output = output;

    private async Task<(CapturedImage Image, IReadOnlyList<OcrLine> Lines)?> LoadAndRecognize(string fileName)
    {
        string path = Path.Combine(RepoPaths.SamplesDirectory, fileName);
        if (!File.Exists(path))
        {
            _output.WriteLine($"Skipping: {path} not present (samples/ is gitignored, personal data).");
            return null;
        }

        CapturedImage image = await ImageFile.LoadAsync(path);
        using RapidOcrEngine engine = new();
        IReadOnlyList<OcrLine> lines = await engine.RecognizeAsync(image);
        return (image, lines);
    }

    [Fact]
    public async Task Locate_SingleWindowScreenshot_FindsExactlyOneWindow()
    {
        var loaded = await LoadAndRecognize("simple 1 item.jpg");
        if (loaded is not { } l) return;

        IReadOnlyList<LocatedWindow> windows = ItemWindowLocator.Locate(l.Image, l.Lines);

        Assert.Single(windows);
        LocatedWindow window = windows[0];
        string allText = string.Join('\n', window.Lines.Select(x => x.Text));
        _output.WriteLine(allText);

        Assert.Contains("Water Flask", allText);
        Assert.False(window.HasLoreTab);
        Assert.False(window.PossiblyOccluded);
    }

    [Fact]
    public async Task Locate_ThreeAdjacentWindows_SeparatesAllThreeWithoutCrossContamination()
    {
        var loaded = await LoadAndRecognize("screen capture 3 item windows.png");
        if (loaded is not { } l) return;

        IReadOnlyList<LocatedWindow> windows = ItemWindowLocator.Locate(l.Image, l.Lines);
        foreach (var w in windows)
            _output.WriteLine($"{w.Bounds} HasLoreTab={w.HasLoreTab}\n  " + string.Join("\n  ", w.Lines.Select(x => x.Text)));

        Assert.Equal(3, windows.Count);

        // Each window should be a plausible single-window size, not have swallowed a neighbor or the HUD.
        Assert.All(windows, w => Assert.True(w.Bounds.Width < 700 && w.Bounds.Height < 900,
            $"Window bounds {w.Bounds} look too large for a single item window — likely cross-contamination."));

        string[] texts = windows.Select(w => string.Join('\n', w.Lines.Select(x => x.Text))).ToArray();
        Assert.Contains(texts, t => t.Contains("Slime Blood of Cazic-Thule"));
        Assert.Contains(texts, t => t.Contains("Lustrous Russet Bracer"));
        Assert.Contains(texts, t => t.Contains("Bloodmoon"));

        // Bloodmoon is the one with a Lore tab; the other two don't have one.
        Assert.Single(windows, w => w.HasLoreTab);

        // No window's text should contain another window's item name (cross-contamination check).
        LocatedWindow bloodmoon = windows.Single(w => w.HasLoreTab);
        string bloodmoonText = string.Join('\n', bloodmoon.Lines.Select(x => x.Text));
        Assert.DoesNotContain("Slime Blood", bloodmoonText);
        Assert.DoesNotContain("Lustrous Russet", bloodmoonText);
    }

    [Fact]
    public async Task Locate_TooltipNextToRealWindows_ExcludesTheTooltip()
    {
        var loaded = await LoadAndRecognize("2 items plus a tooltip.jpg");
        if (loaded is not { } l) return;

        IReadOnlyList<LocatedWindow> windows = ItemWindowLocator.Locate(l.Image, l.Lines);
        foreach (var w in windows)
            _output.WriteLine($"{w.Bounds}\n  " + string.Join("\n  ", w.Lines.Select(x => x.Text)));

        // "Fairy-Hide Mantle +1" is a hover tooltip (no title bar, no Description tab — see the plan's
        // window-vs-tooltip rule) sitting right next to these two real windows; only the 2 real windows
        // (The Tenderizer +7, Fishbone Earring +5) should be found.
        Assert.Equal(2, windows.Count);

        string[] texts = windows.Select(w => string.Join('\n', w.Lines.Select(x => x.Text))).ToArray();
        Assert.Contains(texts, t => t.Contains("Tenderizer"));
        Assert.Contains(texts, t => t.Contains("Fishbone Earring"));
        Assert.All(texts, t => Assert.DoesNotContain("Fairy-Hide Mantle", t));
        Assert.All(texts, t => Assert.DoesNotContain("Guardian Spirit", t)); // the tooltip's own click effect
    }
}
