using EQLWikiAssistant.Core.Locate;
using EQLWikiAssistant.Core.Ocr;
using EQLWikiAssistant.Ocr;
using EQLWikiAssistant.TestSupport;
using Xunit.Abstractions;

namespace EQLWikiAssistant.Tests.Locate;

/// <summary>
/// Golden tests against real, full screenshots in the gitignored samples/ folder — see RapidOcrEngineTests for
/// the skip-if-missing rationale. These exercise the real pipeline end to end (whole-frame OCR to find anchors,
/// content-outline tracing for bounds, a second crop+OCR pass for content), which is what actually matters here.
///
/// The sample set is captured through the tool's own lossless Windows Graphics Capture path (tools/CaptureSpike),
/// not saved screenshots, so the pixel values these thresholds depend on are the ones the app will really see.
/// Each case below is a geometry scenario that broke at least one earlier design — see the plan's milestone 2
/// writeup, and don't delete one without reading why it exists.
/// </summary>
public class ItemWindowLocatorTests
{
    private readonly ITestOutputHelper _output;

    public ItemWindowLocatorTests(ITestOutputHelper output) => _output = output;

    /// <summary>A correctly traced window is the content area's own width — real captures measure 388-404px.
    /// An earlier design returned 414-587px because it ran past the real edge into neighbouring UI, so this is
    /// the assertion that catches that whole class of bug.</summary>
    private static void AssertPlausibleSingleWindowSize(LocatedWindow window)
    {
        Assert.InRange(window.Bounds.Width, 380, 430);
        Assert.InRange(window.Bounds.Height, 200, 700);
    }

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

    private void Dump(IReadOnlyList<LocatedWindow> windows)
    {
        foreach (LocatedWindow w in windows)
            _output.WriteLine($"{w.Bounds} occluded={w.PossiblyOccluded} HasLoreTab={w.HasLoreTab}\n  " +
                string.Join("\n  ", w.Lines.Select(x => x.Text)));
    }

    [Fact]
    public async Task LocateAsync_SingleWindow_FindsExactlyOneCleanWindow()
    {
        if (await Load("01-single-weapon-lvl0-tradeable-pink-background.png") is not { } l) return;
        using (l.Engine as IDisposable)
        {
            IReadOnlyList<LocatedWindow> windows = await ItemWindowLocator.LocateAsync(l.Image, l.Engine);
            Dump(windows);

            LocatedWindow window = Assert.Single(windows);
            Assert.False(window.PossiblyOccluded);
            AssertPlausibleSingleWindowSize(window);
            Assert.Contains("Sheer Blade", string.Join('\n', window.Lines.Select(x => x.Text)));
            Assert.False(window.HasLoreTab);
        }
    }

    [Fact]
    public async Task LocateAsync_LoreTabActive_StillTracesTheWindow()
    {
        // With the Lore tab selected, "Description" is the *inactive* tab and is drawn as its own raised box, so
        // a stack of chrome lines sits between the anchor and the content area. Both earlier designs failed this
        // outright: one couldn't find a dark sample point below the anchor at all, the other stopped between the
        // two chrome lines and then read the content area's own top outline as the window's bottom.
        if (await Load("02b-single-item-with-lore-lore-active.png") is not { } l) return;
        using (l.Engine as IDisposable)
        {
            IReadOnlyList<LocatedWindow> windows = await ItemWindowLocator.LocateAsync(l.Image, l.Engine);
            Dump(windows);

            LocatedWindow window = Assert.Single(windows);
            Assert.False(window.PossiblyOccluded);
            AssertPlausibleSingleWindowSize(window);
            Assert.True(window.HasLoreTab);

            // Which tab is *showing* is read from the label's colour (the selected one is yellow), because the
            // two tabs lay their contents out completely differently and the parser has to be told which it is
            // looking at. Text alone can't say — OCR carries no colour.
            Assert.Equal(ItemWindowTab.Lore, window.ActiveTab);
        }
    }

    [Fact]
    public async Task LocateAsync_DescriptionTabActive_ReportsDescriptionEvenWhenALoreTabExists()
    {
        // Same item as the test above, captured with the other tab selected — so HasLoreTab is true in both, and
        // only ActiveTab distinguishes them.
        if (await Load("02a-single-item-with-lore-description-active.png") is not { } l) return;
        using (l.Engine as IDisposable)
        {
            IReadOnlyList<LocatedWindow> windows = await ItemWindowLocator.LocateAsync(l.Image, l.Engine);
            Dump(windows);

            LocatedWindow window = Assert.Single(windows);
            Assert.True(window.HasLoreTab);
            Assert.Equal(ItemWindowTab.Description, window.ActiveTab);
        }
    }

    [Fact]
    public async Task LocateAsync_WindowOverlappingAnother_StillTracesTheFullyVisibleOne()
    {
        // The front window is completely visible and must parse; only the one underneath is unreadable, and its
        // Description tab is covered so it is never even anchored. This regressed once: the two windows' title
        // bars sit ~7px apart, and a rule that tolerated any short non-black run while walking the title bar
        // bridged that gap into the neighbour's chrome, overran the band-height cap, and reported the visible
        // window as occluded.
        if (await Load("06c-two-items-overlapping-tab-text-covered.png") is not { } l) return;
        using (l.Engine as IDisposable)
        {
            IReadOnlyList<LocatedWindow> windows = await ItemWindowLocator.LocateAsync(l.Image, l.Engine);
            Dump(windows);

            LocatedWindow window = Assert.Single(windows);
            Assert.False(window.PossiblyOccluded);
            AssertPlausibleSingleWindowSize(window);
            Assert.Contains("Obtenebrate", string.Join('\n', window.Lines.Select(x => x.Text)));
        }
    }

    [Theory]
    [InlineData("04-single-item-over-bags-flush-with-inventory.png", "Dark Cloak")]
    [InlineData("05-single-item-flush-with-bags.png", "Spit")]
    [InlineData("09-one-item-plus-full-bank-interface.png", "Toolbox")]
    public async Task LocateAsync_WindowFlushAgainstDarkUi_IsStillTraced(string sample, string expectedName)
    {
        // The case that killed the brightness-transition design: the window is edge-to-edge with other dark game
        // UI, so there is no brightness change at its border in any direction to scan for. The window's own
        // outline and title-bar band are drawn regardless of what's behind them.
        if (await Load(sample) is not { } l) return;
        using (l.Engine as IDisposable)
        {
            IReadOnlyList<LocatedWindow> windows = await ItemWindowLocator.LocateAsync(l.Image, l.Engine);
            Dump(windows);

            LocatedWindow window = Assert.Single(windows);
            Assert.False(window.PossiblyOccluded);
            AssertPlausibleSingleWindowSize(window);
            Assert.Contains(expectedName, string.Join('\n', window.Lines.Select(x => x.Text)));
        }
    }

    [Fact]
    public async Task LocateAsync_TwoWindowsTouching_SeparatesThemWithoutBleed()
    {
        if (await Load("06-two-items-touching.png") is not { } l) return;
        using (l.Engine as IDisposable)
        {
            IReadOnlyList<LocatedWindow> windows = await ItemWindowLocator.LocateAsync(l.Image, l.Engine);
            Dump(windows);

            Assert.Equal(2, windows.Count);
            Assert.All(windows, w => Assert.False(w.PossiblyOccluded));
            Assert.All(windows, AssertPlausibleSingleWindowSize);

            string[] texts = windows.Select(w => string.Join('\n', w.Lines.Select(x => x.Text))).ToArray();
            string prayers = Assert.Single(texts, t => t.Contains("Prayers of Life"));
            string bracers = Assert.Single(texts, t => t.Contains("Hero Bracers"));
            Assert.DoesNotContain("Hero Bracers", prayers);
            Assert.DoesNotContain("Prayers of Life", bracers);
        }
    }

    [Fact]
    public async Task LocateAsync_WindowPartlyCoveredByAnother_IsReportedOccluded()
    {
        // The covering window is fully visible and must trace cleanly; the one underneath has an edge hidden
        // beneath it and must NOT come back with confident bounds. Probe agreement alone can't tell these apart
        // (a covered edge scores about the same as a clean window touching a neighbour) — what rejects it is
        // that the traced rectangle doesn't close, because the covered window's own outline never reaches the
        // corner the consensus picked. Before that check it came back 587px wide, silently merged with its
        // neighbour.
        if (await Load("06a-two-items-overlapping.png") is not { } l) return;
        using (l.Engine as IDisposable)
        {
            IReadOnlyList<LocatedWindow> windows = await ItemWindowLocator.LocateAsync(l.Image, l.Engine);
            Dump(windows);

            Assert.Equal(2, windows.Count);

            LocatedWindow covered = Assert.Single(windows, w => w.PossiblyOccluded);
            Assert.Empty(covered.Lines);

            LocatedWindow onTop = Assert.Single(windows, w => !w.PossiblyOccluded);
            AssertPlausibleSingleWindowSize(onTop);
        }
    }

    [Fact]
    public async Task LocateAsync_ThreeSeparateWindows_SeparatesAllThreeCleanly()
    {
        if (await Load("07-three-distinct-items.png") is not { } l) return;
        using (l.Engine as IDisposable)
        {
            IReadOnlyList<LocatedWindow> windows = await ItemWindowLocator.LocateAsync(l.Image, l.Engine);
            Dump(windows);

            Assert.Equal(3, windows.Count);
            Assert.All(windows, w => Assert.False(w.PossiblyOccluded));
            Assert.All(windows, AssertPlausibleSingleWindowSize);

            string[] texts = windows.Select(w => string.Join('\n', w.Lines.Select(x => x.Text))).ToArray();
            string rod = Assert.Single(texts, t => t.Contains("Rod of the Protecting Winds"));
            Assert.Single(texts, t => t.Contains("Glassy Gauntlets"));
            Assert.Single(texts, t => t.Contains("Fruit"));

            // No cross-contamination between neighbouring windows' content.
            Assert.DoesNotContain("Glassy Gauntlets", rod);
        }
    }

    [Fact]
    public async Task LocateAsync_TooltipNextToWindow_ExcludesTheTooltip()
    {
        // A hover tooltip has no title bar and no Description tab (see the plan's window-vs-tooltip rule), so it
        // must never be picked up as a window or leak into a real one's content.
        if (await Load("08b-hover-tooltip-next-to-different-item-window.png") is not { } l) return;
        using (l.Engine as IDisposable)
        {
            IReadOnlyList<LocatedWindow> windows = await ItemWindowLocator.LocateAsync(l.Image, l.Engine);
            Dump(windows);

            LocatedWindow window = Assert.Single(windows);
            Assert.False(window.PossiblyOccluded);
            AssertPlausibleSingleWindowSize(window);

            string text = string.Join('\n', window.Lines.Select(x => x.Text));
            Assert.Contains("Khyldom the Blood Drinker", text);
            Assert.DoesNotContain("Dark Cloak", text); // the tooltip's own item
        }
    }

    [Fact]
    public async Task LocateAsync_WindowsAgainstScreenEdges_AreAllTraced()
    {
        // Four windows pushed against the top, left, right and bottom edges of the game window, where a scan can
        // run out of image before it finds anything.
        if (await Load("10-four-items-against-screen-edges-and-ui-background.png") is not { } l) return;
        using (l.Engine as IDisposable)
        {
            IReadOnlyList<LocatedWindow> windows = await ItemWindowLocator.LocateAsync(l.Image, l.Engine);
            Dump(windows);

            Assert.Equal(4, windows.Count);
            Assert.All(windows, w => Assert.False(w.PossiblyOccluded));
            Assert.All(windows, AssertPlausibleSingleWindowSize);

            string[] texts = windows.Select(w => string.Join('\n', w.Lines.Select(x => x.Text))).ToArray();
            Assert.Single(texts, t => t.Contains("Karana's Tear"));
            Assert.Single(texts, t => t.Contains("Earthshaker"));
            Assert.Single(texts, t => t.Contains("Treasure Hunter's Satchel"));
            Assert.Single(texts, t => t.Contains("Pegasus-Hide Belt"));
        }
    }

    /// <summary>
    /// **A black HUD panel directly above the title bar, with no gap** (bug found by the user, 2026-09-30). The
    /// player's HP bar is a black panel, and here it sits flush on top of the window's title bar: the two black
    /// regions are contiguous, so `ScanToWindowTop` measures a 41px band where a real one is ~16 and correctly
    /// abstains — on every column the panel covers. It covered x 780-1145 of a window spanning 807-1210, so the
    /// only columns that could answer sat outside the ±100 probe span around the tab, and a **fully visible**
    /// window was reported as occluded with no bounds at all.
    ///
    /// The window is completely unobstructed, so `PossiblyOccluded` is the assertion that matters; the width is
    /// here because the fallback span could in principle latch onto the wrong edge, and a plausible width is what
    /// says it did not.
    /// </summary>
    [Fact]
    public async Task LocateAsync_BlackHudPanelFlushAboveTheTitleBar_StillFindsTheWindow()
    {
        if (await Load("20-stalwart-seas-false-occlusion.png") is not { } l) return;
        using (l.Engine as IDisposable)
        {
            IReadOnlyList<LocatedWindow> windows = await ItemWindowLocator.LocateAsync(l.Image, l.Engine);
            Dump(windows);

            LocatedWindow window = Assert.Single(windows);
            Assert.False(window.PossiblyOccluded, "the window is entirely unobstructed");
            AssertPlausibleSingleWindowSize(window);

            // Read from the crop, so it also proves the bounds are the *right* 404px and not merely 404px wide.
            Assert.Contains(window.Lines, line => line.Text.Contains("Stalwart Seas", StringComparison.Ordinal));
        }
    }
}
