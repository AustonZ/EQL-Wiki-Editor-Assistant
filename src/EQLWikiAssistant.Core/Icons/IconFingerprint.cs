using EQLWikiAssistant.Core.Ocr;

namespace EQLWikiAssistant.Core.Icons;

/// <summary>
/// A scale-invariant fingerprint of an item icon, for telling "this page points at the right artwork" from "it does
/// not".
///
/// **It has to be scale-invariant, which is why this resamples rather than compares pixels.** The wiki stores icons
/// as 40x40 PNGs; the game draws the same sprite about 1.1x larger (measured: a Water Flask's ink is 21x44 on screen
/// against 19x40 on the wiki). Any exact comparison would report every icon as wrong.
///
/// **It is only ever used to flag, never to fix** (per the plan): a mismatch means "check this by eye", because the
/// tool cannot know whether the page's icon id is wrong or the capture caught something odd, and choosing a new
/// `lucy_img_ID` is a human's call.
/// </summary>
public sealed record IconFingerprint(byte[] Signature, int InkWidth, int InkHeight)
{
    /// <summary>
    /// Mean absolute difference between two signatures, 0 (identical) to 1 (maximally different).
    ///
    /// A plain difference rather than a bit-hash Hamming distance, because that was tried and measurably failed:
    /// a 64-bit luminance-only difference hash put same-icon pairs at 0..16 and *different*-icon pairs at 6 and up,
    /// so the two overlapped and no threshold separated them. Keeping the actual cell values — in colour — retains
    /// the information that was being thrown away.
    /// </summary>
    public double DistanceTo(IconFingerprint other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (other.Signature.Length != Signature.Length)
            throw new ArgumentException("Fingerprints were built with different grids and cannot be compared.", nameof(other));

        long total = 0;
        for (int i = 0; i < Signature.Length; i++) total += Math.Abs(Signature[i] - other.Signature[i]);
        return total / (255.0 * Signature.Length);
    }

    /// <summary>
    /// How different two renderings of the same icon may be.
    ///
    /// **Measured, and the measurement is the only reason to trust it.** Over 80 real same-icon pairs and 134
    /// different-icon controls from the sample set (`WikiSpike icons`), same-icon distances run 0.002 to 0.070
    /// (median 0.016) while different-icon distances start at 0.037 (median 0.145). The two overlap between 0.037
    /// and 0.070, so no value is perfect and the choice is which error to make:
    ///
    /// <code>
    /// threshold   false mismatches (of 80)   false matches (of 134)
    ///   0.033              7                          0
    ///   0.035              7                          0
    ///   0.038              6                          2
    ///   0.040              6                          5
    /// </code>
    ///
    /// 0.035 is the last point with **no false matches**, which is the error that matters: a false mismatch costs
    /// the user a glance, while a false match silently blesses a page pointing at the wrong artwork — and silently
    /// wrong is the failure this whole project is built to avoid. Roughly 9% of correct icons will ask for that
    /// glance, which is the price.
    /// </summary>
    public const double SameIconThreshold = 0.035;

    public bool LooksLike(IconFingerprint other) => DistanceTo(other) <= SameIconThreshold;
}

/// <summary>Computes <see cref="IconFingerprint"/>s. See that type for why the comparison is perceptual.</summary>
public static class IconHasher
{
    /// <summary>
    /// The signature is a <see cref="GridSize"/> x <see cref="GridSize"/> grid of average colour, one byte per
    /// channel.
    ///
    /// **Colour is kept deliberately.** A luminance-only signature was tried first and could not separate small,
    /// similar icons — rings and earrings are all little objects on a dark field, and their brightness patterns
    /// look much alike. What actually distinguishes them is hue: a brown leather band against a silver one.
    /// </summary>
    private const int GridSize = 12;

    /// <summary>
    /// A pixel at or below this (max channel) is background rather than sprite. The game draws icons straight onto
    /// the window's flat 16-grey with no frame, so this only has to clear that plus a little noise.
    ///
    /// **It must stay close to the background, not at some comfortable mid-brightness.** A floor of 70 was tried
    /// first and produced a silent, item-specific failure: a dark brown pauldrons icon is almost entirely below it,
    /// so only its few white highlights counted as ink and the "icon" was measured as a 27x10 sliver of a 38x14
    /// sprite. The signature of a fragment is meaningless, and it read as a confident mismatch — the worst outcome
    /// for a check whose only job is to flag.
    /// </summary>
    public const int InkFloor = 28;

    /// <summary>Smallest ink area worth fingerprinting. Below this there is not enough sprite to say anything, and
    /// a confident answer from a handful of pixels would be worse than none.</summary>
    public const int MinimumInkPixels = 40;

    /// <summary>
    /// Fingerprints the artwork inside <paramref name="region"/>, or returns false when there is too little there.
    ///
    /// The region only has to *contain* the icon: the ink's own bounding box is found first and the signature is
    /// built from that, which is what makes the result independent of where the sprite sits in its cell and of how
    /// much padding the caller included — and what lets a 40x40 wiki file and a larger on-screen sprite compare.
    /// </summary>
    public static bool TryFingerprint(CapturedImage image, Rect region, out IconFingerprint fingerprint)
    {
        ArgumentNullException.ThrowIfNull(image);
        fingerprint = new IconFingerprint([], 0, 0);

        if (!TryFindInk(image, region, out Rect ink, out int inkPixels) || inkPixels < MinimumInkPixels)
            return false;

        var signature = new byte[GridSize * GridSize * 3];

        for (int gy = 0; gy < GridSize; gy++)
            for (int gx = 0; gx < GridSize; gx++)
            {
                int x0 = ink.X + gx * ink.Width / GridSize;
                int x1 = Math.Max(x0 + 1, ink.X + (gx + 1) * ink.Width / GridSize);
                int y0 = ink.Y + gy * ink.Height / GridSize;
                int y1 = Math.Max(y0 + 1, ink.Y + (gy + 1) * ink.Height / GridSize);

                long b = 0, g = 0, r = 0;
                int count = 0;
                // Averaged rather than point-sampled: point sampling a 21px-wide sprite onto 12 columns would throw
                // away most of it, and the two sides sample different source resolutions.
                for (int y = y0; y < y1 && y < image.Height; y++)
                    for (int x = x0; x < x1 && x < image.Width; x++)
                    {
                        int offset = (y * image.Width + x) * 4;
                        b += image.Pixels[offset];
                        g += image.Pixels[offset + 1];
                        r += image.Pixels[offset + 2];
                        count++;
                    }

                int cell = (gy * GridSize + gx) * 3;
                if (count == 0) continue;
                signature[cell] = (byte)(b / count);
                signature[cell + 1] = (byte)(g / count);
                signature[cell + 2] = (byte)(r / count);
            }

        fingerprint = new IconFingerprint(signature, ink.Width, ink.Height);
        return true;
    }

    /// <summary>The bounding box of everything bright enough to be sprite rather than background.</summary>
    private static bool TryFindInk(CapturedImage image, Rect region, out Rect ink, out int inkPixels)
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        inkPixels = 0;

        int right = Math.Min(region.X + region.Width, image.Width);
        int bottom = Math.Min(region.Y + region.Height, image.Height);

        for (int y = Math.Max(0, region.Y); y < bottom; y++)
            for (int x = Math.Max(0, region.X); x < right; x++)
            {
                int offset = (y * image.Width + x) * 4;
                int max = Math.Max(image.Pixels[offset + 2], Math.Max(image.Pixels[offset + 1], image.Pixels[offset]));
                if (max <= InkFloor) continue;

                inkPixels++;
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            }

        if (maxX < 0)
        {
            ink = new Rect(0, 0, 0, 0);
            return false;
        }

        ink = new Rect(minX, minY, maxX - minX + 1, maxY - minY + 1);
        return true;
    }
}
