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
    /// How different two renderings of the same icon may be, as a correlation distance.
    ///
    /// **Measured, and the measurement is the only reason to trust it.** Over 80 real same-icon pairs and 134
    /// different-icon controls (`WikiSpike icons`):
    ///
    /// <code>
    /// threshold   false mismatches (of 80)   false matches (of 134)
    ///   0.10               6                          0
    ///   0.12               4                          0
    ///   0.13               4                          0
    ///   0.15               4                          2
    /// </code>
    ///
    /// 0.13 is the last point with **no false matches**, which is the error that matters: a false mismatch costs the
    /// user a glance, while a false match silently blesses a page pointing at the wrong artwork.
    ///
    /// Combined with the <see cref="MinimumContrast"/> gate this leaves 2 false alerts out of the 75 pairs it is
    /// willing to judge — under 3%, against 9% for the mean-absolute-difference measure it replaced.
    /// </summary>
    public const double SameIconThreshold = 0.13;

    /// <summary>
    /// How much the signature varies. A near-black icon has almost none, and comparing two of those is comparing
    /// noise.
    ///
    /// Real case: `Nightmare Hide` is an almost entirely black sprite with a faint outline. The ink box ends up
    /// driven by that thin outline rather than by the artwork, the 12x12 signature is nearly uniform, and the
    /// comparison produced a confident mismatch against the item's own correct icon. Refusing to judge is the right
    /// answer — this check exists to flag wrong icons, and an alert nobody can act on is worse than no alert.
    /// </summary>
    public double Contrast
    {
        get
        {
            if (Signature.Length == 0) return 0;
            double mean = Signature.Average(v => (double)v);
            double variance = Signature.Sum(v => (v - mean) * (v - mean)) / Signature.Length;
            return Math.Sqrt(variance) / 255.0;
        }
    }

    /// <summary>Below this there is not enough variation in the sprite to tell one icon from another. Measured: the
    /// icons that defeated the comparison sit under 0.06, while ordinary ones are well above it.</summary>
    public const double MinimumContrast = 0.06;

    /// <summary>Whether this fingerprint carries enough signal to be worth comparing at all.</summary>
    public bool IsComparable => Signature.Length > 0 && Contrast >= MinimumContrast;

    /// <summary>Whether these are the same artwork. Uses the correlation measure, which separated the classes
    /// measurably better than comparing absolute values — see <see cref="SameIconThreshold"/>. Callers should check
    /// <see cref="IsComparable"/> first; this answers the question it is asked either way.</summary>
    public bool LooksLike(IconFingerprint other) => CorrelationDistanceTo(other) <= SameIconThreshold;

    /// <summary>
    /// An alternative distance that ignores overall brightness and contrast, expressed as 1 - Pearson correlation
    /// so that 0 is identical and larger is worse, like <see cref="DistanceTo"/>.
    ///
    /// Worth having because the two sides are not the same rendering: the game draws the icon about 1.10x larger
    /// than the wiki's file and interpolates when it does, which shifts values slightly across the whole sprite.
    /// A measure that only cares about the *pattern* should be less sensitive to that than one comparing absolute
    /// values — whether it actually is, is a question for the corpus rather than for reasoning.
    /// </summary>
    public double CorrelationDistanceTo(IconFingerprint other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (other.Signature.Length != Signature.Length)
            throw new ArgumentException("Fingerprints were built with different grids and cannot be compared.", nameof(other));
        if (Signature.Length == 0) return 1;

        double meanA = Signature.Average(v => (double)v);
        double meanB = other.Signature.Average(v => (double)v);

        double covariance = 0, varianceA = 0, varianceB = 0;
        for (int i = 0; i < Signature.Length; i++)
        {
            double a = Signature[i] - meanA;
            double b = other.Signature[i] - meanB;
            covariance += a * b;
            varianceA += a * a;
            varianceB += b * b;
        }

        if (varianceA == 0 || varianceB == 0) return varianceA == varianceB ? 0 : 1;
        return 1 - covariance / Math.Sqrt(varianceA * varianceB);
    }
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
