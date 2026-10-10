using EQLWikiEditorAssistant.Core.Icons;
using EQLWikiEditorAssistant.Core.Imaging;
using EQLWikiEditorAssistant.Pipeline;

namespace EQLWikiEditorAssistant.Tests.Icons;

/// <summary>
/// The whole-cell fingerprint and the rule that judges a match against the best icon in the library (2026-10-10).
/// How well they separate real icons is measured by `WikiSpike icons --corpus` and `iconsearch`; these pin the
/// properties that measurement relies on.
/// </summary>
public class IconFingerprintTests
{
    /// <summary>A smooth, colourful picture sampled at a given size, so the same artwork can be drawn at 40 and at
    /// 44 the way the wiki's file and the game's 1.1x rendering are.</summary>
    private static CapturedImage Picture(int size, Action<byte[], int>? touch = null)
    {
        var pixels = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                double u = (x + 0.5) / size, v = (y + 0.5) / size;
                int o = (y * size + x) * 4;
                pixels[o] = (byte)(16 + 200 * u * v);                                // B
                pixels[o + 1] = (byte)(16 + 180 * Math.Abs(Math.Sin(6 * u)) * v);    // G
                pixels[o + 2] = (byte)(16 + 220 * (1 - u) * Math.Abs(Math.Cos(5 * v))); // R
                pixels[o + 3] = 255;
            }
        touch?.Invoke(pixels, size);
        return new CapturedImage(size, size, pixels);
    }

    private static IconFingerprint Of(CapturedImage image)
    {
        Assert.True(IconHasher.TryFingerprint(image, new Rect(0, 0, image.Width, image.Height), out IconFingerprint f));
        return f;
    }

    /// <summary>**The point of fingerprinting the whole cell**: the game's 44x44 and the wiki's 40x40 are the same
    /// picture at two sizes, and both land on the same grid, so they compare as the same artwork.</summary>
    [Fact]
    public void TheSameArtworkAtFortyAndFortyFourFingerprintsAlike() =>
        Assert.True(Of(Picture(44)).CorrelationDistanceTo(Of(Picture(40))) < 0.005);

    /// <summary>
    /// **A stray pixel no longer moves the comparison** (`Bag of Sea Salt`, 2026-10-10). The game draws a 2-pixel mark
    /// beside some icons; when the fingerprint was fitted to the ink's bounding box, that mark stretched the box and
    /// a correct icon read as a different one (0.013 to 0.212). In a fixed cell it is two pixels of 1,936.
    /// </summary>
    [Fact]
    public void AStrayMarkInTheCellsCornerBarelyMovesTheFingerprint()
    {
        CapturedImage marked = Picture(44, (p, size) =>
        {
            foreach (int y in new[] { 42, 43 })
            {
                int o = (y * size + 43) * 4;
                p[o] = p[o + 1] = p[o + 2] = 64;
            }
        });

        Assert.True(Of(marked).CorrelationDistanceTo(Of(Picture(44))) < 0.005);
    }

    [Fact]
    public void ARegionOutsideTheImageIsRefused() =>
        Assert.False(IconHasher.TryFingerprint(Picture(40), new Rect(10, 10, 44, 44), out _));

    private static readonly IconFingerprint Any = Of(Picture(40));

    /// <summary>The match is judged against the best icon in the library, not by a fixed distance — within the margin
    /// is the same artwork, beyond it is not, whatever the absolute numbers.</summary>
    [Theory]
    [InlineData(0.20, 0.18, true)]    // a poor absolute score, but as close as anything in the library
    [InlineData(0.05, 0.00, false)]   // a good absolute score, but the library holds something far closer
    public void AMatchIsJudgedAgainstTheBestIconInTheLibrary(double distance, double best, bool matches) =>
        Assert.Equal(matches, new IconComparison("1", Any, Any, distance, best).Matches);

    /// <summary>"The id is right, the wiki's file is not" only when the library's own icon for the page's id is the
    /// best match; a mismatch whose id the library does not back up is an ordinary wrong id.</summary>
    [Fact]
    public void AWikiFileDiffersOnlyWhenTheLibrarysOwnIconForTheIdMatches()
    {
        Assert.True(new IconComparison("1", Any, Any, 0.10, 0.01, PageIdInLibrary: 0.01).WikiFileDiffersFromGame);
        Assert.False(new IconComparison("1", Any, Any, 0.10, 0.01, PageIdInLibrary: 0.10).WikiFileDiffersFromGame);
        Assert.False(new IconComparison("1", Any, Any, 0.02, 0.01, PageIdInLibrary: 0.01).WikiFileDiffersFromGame);
    }

    /// <summary>Without a library the absolute threshold decides, as the fallback it is documented to be.</summary>
    [Fact]
    public void WithNoLibraryTheAbsoluteThresholdDecides() =>
        Assert.True(new IconComparison("1", Any, Any, 0).Matches);
}
