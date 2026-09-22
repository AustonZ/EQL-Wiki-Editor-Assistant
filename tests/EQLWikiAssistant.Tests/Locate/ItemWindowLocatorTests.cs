using EQLWikiAssistant.Core.Locate;
using EQLWikiAssistant.Core.Ocr;
using EQLWikiAssistant.Ocr;
using EQLWikiAssistant.TestSupport;
using Xunit.Abstractions;

namespace EQLWikiAssistant.Tests.Locate;

/// <summary>
/// Golden tests against real, full (uncropped) screenshots in the gitignored samples/ folder — see
/// WindowsOcrEngineTests for the skip-if-missing rationale. These exercise the real pipeline end to end
/// (whole-frame OCR to find anchors, pixel border tracing for bounds, a second crop+OCR pass for content) since
/// that's what actually matters here — see the plan's milestone 2 writeup for why an OCR-line-clustering
/// approach was tried first and replaced with this one.
/// </summary>
public class ItemWindowLocatorTests
{
    private readonly ITestOutputHelper _output;

    public ItemWindowLocatorTests(ITestOutputHelper output) => _output = output;

    private async Task<(CapturedImage Image, IOcrEngine Engine)?> Load(string fileName)
    {
        string path = Path.Combine(RepoPaths.SamplesDirectory, fileName);
        if (!File.Exists(path))
        {
            _output.WriteLine($"Skipping: {path} not present (samples/ is gitignored, personal data).");
            return null;
        }
        return (await ImageFile.LoadAsync(path), new RapidOcrEngine());
    }

    [Fact]
    public async Task LocateAsync_SingleWindowScreenshot_FindsExactlyOneCleanWindow()
    {
        if (await Load("simple 1 item.jpg") is not { } l) return;
        using (l.Engine as IDisposable)
        {
            IReadOnlyList<LocatedWindow> windows = await ItemWindowLocator.LocateAsync(l.Image, l.Engine);

            Assert.Single(windows);
            LocatedWindow window = windows[0];
            string allText = string.Join('\n', window.Lines.Select(x => x.Text));
            _output.WriteLine(allText);

            Assert.False(window.PossiblyOccluded);
            Assert.Contains("Water Flask", allText);
            Assert.False(window.HasLoreTab);
        }
    }

    [Fact]
    public async Task LocateAsync_ThreeAdjacentWindows_SeparatesAllThreeCleanly()
    {
        if (await Load("screen capture 3 item windows.png") is not { } l) return;
        using (l.Engine as IDisposable)
        {
            IReadOnlyList<LocatedWindow> windows = await ItemWindowLocator.LocateAsync(l.Image, l.Engine);
            foreach (var w in windows)
                _output.WriteLine($"{w.Bounds} occluded={w.PossiblyOccluded} HasLoreTab={w.HasLoreTab}\n  " +
                    string.Join("\n  ", w.Lines.Select(x => x.Text)));

            Assert.Equal(3, windows.Count);
            Assert.All(windows, w => Assert.False(w.PossiblyOccluded));

            // Every window's own bounds should be a plausible single-window size (catches cross-contamination
            // with a neighbor or the surrounding HUD, the original failure mode this design replaced).
            Assert.All(windows, w => Assert.True(w.Bounds.Width < 650 && w.Bounds.Height < 750,
                $"Window bounds {w.Bounds} look too large for a single item window."));

            string[] texts = windows.Select(w => string.Join('\n', w.Lines.Select(x => x.Text))).ToArray();
            Assert.Contains(texts, t => t.Contains("Slime Blood of Cazic-Thule"));
            Assert.Contains(texts, t => t.Contains("Lustrous Russet Bracer"));
            Assert.Contains(texts, t => t.Contains("Bloodmoon"));

            // Bloodmoon is the one with a Lore tab; no cross-contamination between windows' own item names.
            Assert.Single(windows, w => w.HasLoreTab);
            string bloodmoonText = texts.Single(t => t.Contains("Bloodmoon"));
            Assert.DoesNotContain("Slime Blood", bloodmoonText);
            Assert.DoesNotContain("Lustrous Russet", bloodmoonText);
        }
    }

    [Fact]
    public async Task LocateAsync_TooltipNextToRealWindows_ExcludesTheTooltip()
    {
        if (await Load("2 items plus a tooltip.jpg") is not { } l) return;
        using (l.Engine as IDisposable)
        {
            IReadOnlyList<LocatedWindow> windows = await ItemWindowLocator.LocateAsync(l.Image, l.Engine);
            foreach (var w in windows)
                _output.WriteLine($"{w.Bounds} occluded={w.PossiblyOccluded}\n  " + string.Join("\n  ", w.Lines.Select(x => x.Text)));

            // "Fairy-Hide Mantle +1" is a hover tooltip (no title bar, no Description tab — see the plan's
            // window-vs-tooltip rule) sitting right next to these windows; it must never appear as a window or
            // leak into one's content. (The Tenderizer window is a known hard case here — it sits with zero
            // gap against an unrelated Bank window, so it's expected to come back PossiblyOccluded; only
            // Fishbone Earring is asserted clean.)
            string[] texts = windows.Select(w => string.Join('\n', w.Lines.Select(x => x.Text))).ToArray();
            Assert.All(texts, t => Assert.DoesNotContain("Fairy-Hide Mantle", t));
            Assert.All(texts, t => Assert.DoesNotContain("Guardian Spirit", t)); // the tooltip's own click effect

            LocatedWindow? fishbone = windows.FirstOrDefault(w => w.Lines.Any(x => x.Text.Contains("Fishbone Earring")));
            Assert.NotNull(fishbone);
            Assert.False(fishbone!.PossiblyOccluded);
        }
    }

    [Fact]
    public async Task LocateAsync_WindowObscuredByBagPanels_IsReportedOccludedWithNoData()
    {
        if (await Load("single item occluded by bag windows.png") is not { } l) return;
        using (l.Engine as IDisposable)
        {
            IReadOnlyList<LocatedWindow> windows = await ItemWindowLocator.LocateAsync(l.Image, l.Engine);
            foreach (var w in windows)
                _output.WriteLine($"{w.Bounds} occluded={w.PossiblyOccluded}, {w.Lines.Count} line(s)");

            Assert.Single(windows);
            Assert.True(windows[0].PossiblyOccluded);
            Assert.Empty(windows[0].Lines);
        }
    }
}
