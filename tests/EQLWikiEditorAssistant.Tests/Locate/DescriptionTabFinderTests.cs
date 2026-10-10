using EQLWikiEditorAssistant.Core.Glyphs;
using EQLWikiEditorAssistant.Core.Locate;
using EQLWikiEditorAssistant.Core.Imaging;
using EQLWikiEditorAssistant.TestSupport;

namespace EQLWikiEditorAssistant.Tests.Locate;

/// <summary>
/// Finding the "Description" tab by its pixels. The search is tolerant by design, for the game's textured skins, so
/// these tests pin the part that must not be: the template is the label exactly as the game draws it.
/// </summary>
public class DescriptionTabFinderTests
{
    private static async Task<CapturedImage> Sample(string name) =>
        await ImageFile.LoadAsync(Path.Combine(RepoPaths.SamplesDirectory, name));

    /// <summary>
    /// **The template built from the atlas is the real label, pixel for pixel**, in each font. On the flat background
    /// of `default_modern` every pixel of a real label lands exactly on the ramp the template predicts — so if the atlas
    /// laid a glyph out a pixel off, or the advance between two letters were wrong, this fails, where the tolerant search
    /// might have quietly matched anyway. Checked in the brightest channel, the glyph reader's own measure.
    /// </summary>
    [Theory]
    [InlineData("01-single-weapon-lvl0-tradeable-pink-background.png", UiFont.Arial)]
    [InlineData("15a-eql-wiki-editor-assistant-font [font EqlWikiEditorAssistant].png", UiFont.EqlWikiEditorAssistant)]
    public async Task TheTemplateIsTheRealLabelPixelForPixel(string sample, UiFont font)
    {
        CapturedImage image = await Sample(sample);
        LabelTemplate template = LabelTemplate.Render(GlyphAtlas.Bundled, DescriptionTabFinder.Label, font);

        IReadOnlyList<DescriptionTab> tabs = DescriptionTabFinder.Find(image);

        Assert.NotEmpty(tabs);
        foreach (DescriptionTab tab in tabs)
        {
            Assert.Equal(font, tab.Font);
            int At(int i) => GlyphRamp.Intensity(image.Pixels, image.Width, image.Height,
                tab.Label.X + i % template.Width, tab.Label.Y + i / template.Width);
            int[] pixels = [.. Enumerable.Range(0, template.Fifteenths.Count)];
            int background = At(pixels.First(i => template.Fifteenths[i] == 0));
            int peak = At(pixels.First(i => template.Fifteenths[i] == 15));
            foreach (int i in pixels)
                Assert.Equal(background + (int)Math.Round(template.Fifteenths[i] * (peak - background) / 15.0), At(i));
        }
    }

    /// <summary>The fonts draw different r's, and "Description" has one, so which template matched says which font drew
    /// the window — what keeps the wrong-font refusal working once no text model reads the frame.</summary>
    [Fact]
    public void TheTwoFontsTemplatesDiffer()
    {
        LabelTemplate arial = LabelTemplate.Render(GlyphAtlas.Bundled, DescriptionTabFinder.Label, UiFont.Arial);
        LabelTemplate ours = LabelTemplate.Render(GlyphAtlas.Bundled, DescriptionTabFinder.Label, UiFont.EqlWikiEditorAssistant);

        Assert.False(arial.Width == ours.Width && arial.Fifteenths.SequenceEqual(ours.Fifteenths));
    }

    /// <summary>The label is yellow on the selected tab and white on the other, raised one; both are found.</summary>
    [Theory]
    [InlineData("02a-single-item-with-lore-description-active.png")]
    [InlineData("02b-single-item-with-lore-lore-active.png")]
    public async Task TheLabelIsFoundSelectedOrNot(string sample) =>
        Assert.Single(Tabs(await Sample(sample)));

    /// <summary>A hover tooltip draws the same item data but has no tabs, which is what the anchor has always excluded
    /// it by. Each frame holds one item window and one tooltip.</summary>
    [Theory]
    [InlineData("08a-hover-tooltip-next-to-same-item-window.png")]
    [InlineData("08b-hover-tooltip-next-to-different-item-window.png")]
    public async Task ATooltipHasNoTab(string sample) =>
        Assert.Single(Tabs(await Sample(sample)));

    /// <summary>Several windows in one frame, each found once, in reading order.</summary>
    [Fact]
    public async Task EveryWindowInAFrameIsFoundOnce()
    {
        IReadOnlyList<DescriptionTab> tabs = Tabs(await Sample("10-four-items-against-screen-edges-and-ui-background.png"));

        Assert.Equal(4, tabs.Count);
        Assert.Equal(tabs.OrderBy(t => t.Label.Y).ThenBy(t => t.Label.X), tabs);
    }

    /// <summary>
    /// **The label is found on a texture, and the texture is noticed** — what the game's other two skins do, drawing the
    /// same glyphs blended over a textured background. Synthetic, so the model itself is pinned apart from any one
    /// texture: the label laid out as the blend model describes over seeded noise in the measured `default` range, and
    /// over a flat background as the control. Both are found; only the textured one says so.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ALabelOnATextureIsFoundAndMarkedTextured(bool textured)
    {
        LabelTemplate template = LabelTemplate.Render(GlyphAtlas.Bundled, DescriptionTabFinder.Label, UiFont.Arial);
        const int width = 120, height = 30, left = 20, top = 9;
        var random = new Random(42);
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int background = textured ? random.Next(8, 57) : 16;
                int tx = x - left, ty = y - top;
                int coverage = tx >= 0 && ty >= 0 && tx < template.Width && ty < template.Height
                    ? template.Fifteenths[ty * template.Width + tx] : 0;
                byte v = (byte)Math.Round(background + coverage * (255 - background) / 15.0);
                int i = (y * width + x) * 4;
                pixels[i] = pixels[i + 1] = pixels[i + 2] = v;
                pixels[i + 3] = 255;
            }

        DescriptionTab tab = Assert.Single(DescriptionTabFinder.Find(new CapturedImage(width, height, pixels)));

        Assert.Equal(new Rect(left, top, template.Width, template.Height), tab.Label);
        Assert.Equal(textured, tab.Textured);
    }

    /// <summary>
    /// **The real thing**: one window in each of the game's other two skins (audited by the user, 2026-10-09). Its tab
    /// is found on the skin's actual texture and marked textured, and the locator reports the window as in another
    /// skin — found, so the user is told, but not traced or read. Pins what the synthetic test above can only model.
    /// </summary>
    [Theory]
    [InlineData("30a-skin-default.png")]
    [InlineData("31a-skin-default-light.png")]
    public async Task AWindowInAnotherSkinIsFoundAndReportedAsSuch(string sample)
    {
        CapturedImage image = await Sample(sample);

        DescriptionTab tab = Assert.Single(Tabs(image));
        Assert.True(tab.Textured);
        Assert.Equal(UiFont.Arial, tab.Font);

        LocatedWindow window = Assert.Single(ItemWindowLocator.Locate(image, new GlyphTextReader(UiFont.Arial)));
        Assert.True(window.InOtherSkin);
        Assert.True(window.PossiblyOccluded);
        Assert.Empty(window.Lines);
    }

    /// <summary>Every tab in the modern-skin samples is flat, so none is textured — the measured side of
    /// <see cref="DescriptionTabFinder.TexturedSpread"/> that real frames can pin.</summary>
    [Theory]
    [InlineData("07-three-distinct-items.png")]
    [InlineData("15b-r-and-rn-words [font EqlWikiEditorAssistant].png")]
    public async Task NoTabInTheModernSkinIsTextured(string sample) =>
        Assert.All(Tabs(await Sample(sample)), tab => Assert.False(tab.Textured));

    /// <summary>A character the atlas cannot lay out is refused rather than guessed at: a template with the wrong
    /// spacing would match nothing, and nothing would say why.</summary>
    [Fact]
    public void ATextTheAtlasCannotLayOutIsRefused() =>
        Assert.Throws<InvalidOperationException>(() => LabelTemplate.Render(GlyphAtlas.Bundled, "Déscription", UiFont.Arial));

    private static IReadOnlyList<DescriptionTab> Tabs(CapturedImage image) => DescriptionTabFinder.Find(image);
}
