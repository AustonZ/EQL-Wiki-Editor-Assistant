using EQLWikiEditorAssistant.Core.Locate;
using EQLWikiEditorAssistant.Core.Imaging;

namespace EQLWikiEditorAssistant.Tests.Locate;

/// <summary>
/// The window's top edge, which is where this project's locate bugs keep coming from.
///
/// **These are built from a synthesized frame, which the rest of the locate suite deliberately avoids**, and the
/// reason is worth stating. The golden tests run against real screenshots in `samples/`, which is gitignored — so
/// every one of them skips on a fresh clone, and the three top-edge bugs this file covers all shipped with no test
/// able to fail against them. The geometry here is not invented: the band height, the interior grey, the outline
/// grey and the frame beyond it are the values measured on real captures and recorded in
/// <see cref="WindowBoundsFinder"/>. What is synthetic is only the *arrangement*, which is exactly what each bug
/// was about — a correct window with something awkward above it.
/// </summary>
public class WindowTopEdgeTests
{
    private const int World = 150;      // the 3D world behind the UI: 150-170 on real captures
    private const int Interior = 16;    // the window's content area, R=G=B=16
    private const int Outline = 58;     // the content area's border, measured 50-62
    private const int TitleBarHeight = 16;

    // A window big enough to clear the finder's own minimum, laid out the way a real one is.
    private const int WindowLeft = 100, WindowRight = 494;   // the content outline's columns
    private const int TitleTop = 124;                        // black band 124..139
    private const int ContentTop = 140;                      // outline row
    private const int ContentBottom = 500;                   // outline row

    private sealed class Frame(int width, int height)
    {
        public byte[] Pixels { get; } = new byte[width * height * 4];
        public int Width { get; } = width;
        public int Height { get; } = height;

        public void Set(int x, int y, int value)
        {
            if (x < 0 || y < 0 || x >= Width || y >= Height) return;
            int i = (y * Width + x) * 4;
            Pixels[i] = Pixels[i + 1] = Pixels[i + 2] = (byte)value;
            Pixels[i + 3] = 255;
        }

        public void Fill(int x0, int y0, int x1, int y1, int value)
        {
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++) Set(x, y, value);
        }

        public CapturedImage ToImage() => new(Width, Height, Pixels);
    }

    /// <summary>
    /// A window as the chrome really draws it: world behind, a 16px pure-black title bar, then a content area
    /// bounded by the grey outline with the window's near-black frame just beyond it.
    /// </summary>
    private static Frame AWindow()
    {
        var f = new Frame(600, 600);
        f.Fill(0, 0, 599, 599, World);

        // The frame just outside the content outline — darker than the interior, which is what tells a real
        // boundary from one of the window's internal divider rules.
        f.Fill(WindowLeft - 5, TitleTop, WindowRight + 5, ContentBottom + 5, 0);

        // The title bar: pure black, spanning the window's full outer width.
        f.Fill(WindowLeft - 5, TitleTop, WindowRight + 5, TitleTop + TitleBarHeight - 1, 0);

        // The content area and its outline.
        f.Fill(WindowLeft, ContentTop, WindowRight, ContentBottom, Outline);
        f.Fill(WindowLeft + 1, ContentTop + 1, WindowRight - 1, ContentBottom - 1, Interior);

        return f;
    }

    /// <summary>The tab label's box, which is what the locator hands in as its anchor.</summary>
    private static Rect Anchor() => new(260, ContentTop + 6, 72, 14);

    private static Rect? Find(Frame f) => WindowBoundsFinder.TryFindBounds(f.ToImage(), Anchor());

    /// <summary>The baseline: an ordinary window is found, and its top is the title bar's first row.</summary>
    [Fact]
    public void TheTopEdgeIsTheTitleBarsFirstRow()
    {
        Rect? bounds = Find(AWindow());

        Assert.NotNull(bounds);
        Assert.Equal(TitleTop, bounds.Value.Y);
        Assert.Equal(WindowLeft, bounds.Value.X);
    }

    /// <summary>
    /// **The Lore tab draws a second outline inside the first**, around its text area (bug found by the user,
    /// 2026-10-07, on `Tarnished Ancient Tiara`). Measured: 3px in from the tab's outline at the sides and 6px up from
    /// it at the bottom, with interior between. The inner one has the frame within reach beyond it too, so stopping at
    /// the first outline cropped every Lore capture narrower than the Description capture of the same window. The
    /// window's edge is the outermost outline before the frame. Not strictly a top-edge case, but this frame builder
    /// is the only one in the suite that runs on a fresh clone.
    /// </summary>
    [Fact]
    public void TheLoreTabsTextAreaOutlineIsNotMistakenForTheWindowsEdge()
    {
        Frame f = AWindow();
        f.Fill(WindowLeft + 3, ContentTop + 3, WindowRight - 3, ContentBottom - 6, Outline);
        f.Fill(WindowLeft + 4, ContentTop + 4, WindowRight - 4, ContentBottom - 7, Interior);

        Rect? bounds = Find(f);

        Assert.NotNull(bounds);
        Assert.Equal(WindowLeft, bounds.Value.X);
        Assert.Equal(WindowRight - WindowLeft + 1, bounds.Value.Width);
        Assert.Equal(ContentBottom, bounds.Value.Y + bounds.Value.Height - 1);
    }

    /// <summary>
    /// **The bug that prompted the rewrite** (user, 2026-10-01, on `Armor Ornamentation Token`). Unrelated dark
    /// chrome sits a little above the window with open game world between the two. The old scan walked up through
    /// the black, treated the intervening world as the title's own anti-aliased glyph rows — it was within the
    /// tolerance that existed for exactly that — and latched onto the chrome 15px too high, taking the icon strip
    /// with it. Measured on the real capture: black at y 225-239, world at 240-251, the real title bar at 252.
    /// </summary>
    [Fact]
    public void DarkChromeAboveTheWindowIsNotMistakenForItsTitleBar()
    {
        Frame f = AWindow();
        f.Fill(0, TitleTop - 27, 599, TitleTop - 13, 0);   // a black panel, 15 rows, 12 rows of world below it

        Rect? bounds = Find(f);

        Assert.NotNull(bounds);
        Assert.Equal(TitleTop, bounds.Value.Y);
    }

    /// <summary>
    /// The case the previous fix was for (user, 2026-09-30, on `Shield of the Stalwart Seas`): the player's HP bar
    /// is a black HUD panel and it can sit flush on the title bar with no gap, so the two black regions merge and
    /// nothing marks where the window starts. Columns under the panel must abstain — and with part of the window
    /// still clear, the clear columns decide, so the window is still found at the right place rather than lost.
    /// </summary>
    [Fact]
    public void APanelFlushOnTheTitleBarDoesNotDragTheTopEdgeUp()
    {
        Frame f = AWindow();
        f.Fill(0, TitleTop - 25, 300, TitleTop - 1, 0);   // covers the left of the window, flush, no gap

        Rect? bounds = Find(f);

        Assert.NotNull(bounds);
        Assert.Equal(TitleTop, bounds.Value.Y);
    }

    /// <summary>
    /// **The negative control for the two above.** A rule that simply answered "the first black row going up" would
    /// pass neither, but one that answered "wherever the black ends" would pass the flush-panel case by accident.
    /// Covering the window's whole width flush with the title bar leaves no clear column, so there is genuinely no
    /// way to know where it starts — and the finder must say so rather than invent a top edge.
    /// </summary>
    [Fact]
    public void APanelCoveringTheWholeWidthLeavesNothingToMeasureAndIsRefused()
    {
        Frame f = AWindow();
        f.Fill(0, TitleTop - 25, 599, TitleTop - 1, 0);

        Assert.Null(Find(f));
    }

    /// <summary>
    /// A window flush against the top of the screen has its title bar genuinely clipped — one real sample reads 14
    /// rows rather than 16 — so the height test has to admit that rather than refusing the window.
    /// </summary>
    [Fact]
    public void AWindowClippedByTheTopOfTheScreenIsStillFound()
    {
        var f = new Frame(600, 600);
        f.Fill(0, 0, 599, 599, World);
        const int clipped = 14;
        f.Fill(WindowLeft - 5, 0, WindowRight + 5, ContentBottom - TitleTop + 5, 0);
        f.Fill(WindowLeft - 5, 0, WindowRight + 5, clipped - 1, 0);
        int contentTop = clipped, contentBottom = ContentBottom - TitleTop;
        f.Fill(WindowLeft, contentTop, WindowRight, contentBottom, Outline);
        f.Fill(WindowLeft + 1, contentTop + 1, WindowRight - 1, contentBottom - 1, Interior);

        Rect? bounds = WindowBoundsFinder.TryFindBounds(
            f.ToImage(), new Rect(260, contentTop + 6, 72, 14));

        Assert.NotNull(bounds);
        Assert.Equal(0, bounds.Value.Y);
    }
}
