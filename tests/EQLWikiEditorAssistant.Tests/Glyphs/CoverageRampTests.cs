using EQLWikiEditorAssistant.Core.Glyphs;
using EQLWikiEditorAssistant.Core.Imaging;
using EQLWikiEditorAssistant.Core.Locate;
using EQLWikiEditorAssistant.TestSupport;

namespace EQLWikiEditorAssistant.Tests.Glyphs;

/// <summary>
/// A machine whose game draws the coverage levels at other brightnesses is still read (a tester, 2026-10-15; see
/// <see cref="CoverageRamp"/>). His frames are not audited samples, so the end-to-end case here is made from audited
/// ones: every glyph the reader finds in a real window is drawn again with another ramp, which is exactly what his
/// machine did — the same glyphs, the same chrome, only the levels moved.
/// </summary>
public class CoverageRampTests
{
    /// <summary>The tester's ramp, measured from four of his frames.</summary>
    private static readonly double[] Tester = [0, 4, 7, 9, 11, 13, 15];

    /// <summary>A darker ramp than the standard one, so the levels are moved both ways.</summary>
    private static readonly double[] Darker = [0, 2, 5, 8, 10, 12, 15];

    [Fact]
    public void TheStandardRampIsTheAtlasRamp() =>
        Assert.Equal(GlyphRamp.Fifteenths.Select(f => (double)f), Enumerable.Range(0, 7).Select(l => CoverageRamp.Standard[l]));

    [Theory]
    [InlineData(new double[] { 0, 3, 6, 9, 11, 13 })]           // too few levels
    [InlineData(new double[] { 1, 3, 6, 9, 11, 13, 15 })]       // not from the background
    [InlineData(new double[] { 0, 3, 6, 9, 11, 13, 14 })]       // not to the peak
    [InlineData(new double[] { 0, 6, 3, 9, 11, 13, 15 })]       // not rising
    public void ARampThatCannotBeOneIsRefused(double[] fifteenths) =>
        Assert.Throws<ArgumentException>(() => new CoverageRamp(fifteenths));

    /// <summary>
    /// **A real window redrawn with another ramp locates and reads exactly as the original does**, and says which ramp
    /// it learned. The control is the same redrawn window read with the standard ramp, which must not read the same —
    /// otherwise the redrawing changed nothing the reader looks at, and the test would prove nothing. Covers a frame
    /// of three windows with effect and value colours, a window whose Lore tab is selected (so its Description label is
    /// white and raised), and windows at 2.00 and 4.00.
    /// </summary>
    [Theory]
    [InlineData("07-three-distinct-items.png", nameof(Tester))]
    [InlineData("07-three-distinct-items.png", nameof(Darker))]
    [InlineData("02b-single-item-with-lore-lore-active.png", nameof(Tester))]
    [InlineData("40a-scale-2.00-description.png", nameof(Tester))]
    [InlineData("46b-scale-4.00-item.png", nameof(Darker))]
    public async Task AWindowDrawnWithAnotherRampReadsAsTheOriginalDoes(string sample, string rampName)
    {
        var ramp = new CoverageRamp(rampName == nameof(Tester) ? Tester : Darker);
        CapturedImage original = await ImageFile.LoadAsync(Path.Combine(RepoPaths.SamplesDirectory, sample));
        var reader = new GlyphTextReader(UiFont.Arial);
        IReadOnlyList<LocatedWindow> before = ItemWindowLocator.Locate(original, reader);
        Assert.NotEmpty(before);
        Assert.All(before, w => Assert.True(w.PossiblyOccluded || w.Ramp is { IsStandard: true }));

        CapturedImage redrawn = Redraw(original, before, ramp);
        IReadOnlyList<LocatedWindow> after = ItemWindowLocator.Locate(redrawn, reader);

        Assert.Equal(before.Select(w => w.Bounds), after.Select(w => w.Bounds));
        foreach ((LocatedWindow was, LocatedWindow now) in before.Zip(after))
        {
            Assert.Equal(was.Lines.Select(l => l.Text), now.Lines.Select(l => l.Text));
            if (was.PossiblyOccluded) continue;
            Assert.Equal(ramp.ToString(), now.Ramp?.ToString());

            IReadOnlyList<TextLine> asStandard = reader.Read(redrawn.Crop(now.Bounds), now.Scale);
            Assert.NotEqual(was.Lines.Select(l => l.Text), asStandard.Select(l => l.Text));
        }
    }

    /// <summary>
    /// Every glyph the reader matched in each readable window, drawn again with <paramref name="ramp"/>: each channel's
    /// background and peak are solved from the glyph as the reader solves them, and every inked pixel is set to where
    /// its level falls on the new ramp. Uncovered pixels and everything that is not text are left as they were.
    /// </summary>
    private static CapturedImage Redraw(CapturedImage image, IReadOnlyList<LocatedWindow> windows, CoverageRamp ramp)
    {
        byte[] pixels = (byte[])image.Pixels.Clone();
        foreach (LocatedWindow window in windows.Where(w => !w.PossiblyOccluded))
        {
            GlyphAtlas atlas = GlyphAtlas.BundledAt(window.Scale);
            foreach (GlyphMatch match in GlyphReader.ResolveOverlaps(GlyphReader.FindMatches(image, window.Bounds, atlas)))
            {
                GlyphBitmap bitmap = match.Entry.Bitmap;
                int zero = Array.IndexOf(bitmap.Levels, (byte)0);
                (int bx, int by) = zero >= 0
                    ? (match.Left + zero % bitmap.Width, match.Top + zero / bitmap.Width)
                    : (match.Left, match.Top - 1); // a bare bar's background is the ring above it
                byte brightest = bitmap.Levels.Max();
                int at = Array.IndexOf(bitmap.Levels, brightest);
                (int px, int py) = (match.Left + at % bitmap.Width, match.Top + at / bitmap.Width);

                for (int channel = 0; channel < 3; channel++)
                {
                    int background = image.Pixels[(by * image.Width + bx) * 4 + channel];
                    int bright = image.Pixels[(py * image.Width + px) * 4 + channel];
                    double peak = background + (bright - background) * 15.0 / GlyphRamp.Fifteenths[brightest];
                    for (int y = 0; y < bitmap.Height; y++)
                        for (int x = 0; x < bitmap.Width; x++)
                        {
                            byte level = bitmap[x, y];
                            if (level == 0) continue;
                            pixels[((match.Top + y) * image.Width + match.Left + x) * 4 + channel] =
                                (byte)Math.Round(background + (peak - background) * ramp[level] / 15.0);
                        }
                }
            }
        }
        return new CapturedImage(image.Width, image.Height, pixels);
    }
}
