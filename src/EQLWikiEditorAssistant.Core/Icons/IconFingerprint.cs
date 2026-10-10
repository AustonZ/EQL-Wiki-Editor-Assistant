using EQLWikiEditorAssistant.Core.Imaging;

namespace EQLWikiEditorAssistant.Core.Icons;

/// <summary>
/// An item icon reduced to a small colour grid, for telling "this page points at the right artwork" from "it does
/// not", and for finding the artwork among the game's whole library.
///
/// **The whole icon cell, every pixel, on both sides** (user, 2026-10-10). The game draws every icon in the same
/// place in the window and always at 44x44 — the 40x40 artwork at 1.1x — so the captured cell and the 40x40 file are
/// the same picture at two known sizes, and each is shrunk onto the same grid. This replaced fingerprinting the
/// ink's bounding box, which let one stray pixel or one clipped column move the whole grid: `Spiderling Silk` fills
/// its full width and lost its last column to the old crop, and a 2-pixel mark beside `Bag of Sea Salt` stretched
/// its box, each reading as a different icon. With the cell fixed, every comparison is on the same scale.
///
/// **It flags; it never writes on its own** (per the plan): a mismatch means "check this by eye", and choosing a new
/// `lucy_img_ID` is a human's call.
/// </summary>
public sealed record IconFingerprint(byte[] Signature)
{
    /// <summary>
    /// How far apart two renderings of the same icon may be, as a correlation distance, **when there is no library to
    /// compare against**. With the library, a match is judged against the best icon in it instead — see
    /// <see cref="IconLibrary.SameArtworkMargin"/>, which separates far better, and is what the app uses.
    ///
    /// Measured on the 16x16 grid over 96 captured items against 87 distinct wiki icons (8,256 different-icon pairs):
    /// 0.059 is the largest threshold with no false match, at 11 false alerts. The two closest different icons are a
    /// pair of recoloured twins of one sword drawing (590 and 603), which no absolute threshold can tell apart.
    /// </summary>
    public const double SameIconThreshold = 0.055;

    /// <summary>
    /// How much the signature varies. A blank or near-blank cell has almost none, and comparing two of those is
    /// comparing noise.
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

    /// <summary>
    /// Below this there is not enough variation in the cell to tell one icon from another.
    ///
    /// **Measured on the whole-cell signature (2026-10-10)**: the darkest captured icon in the corpus is 0.039
    /// (`Nightmare Hide`, black on the window's grey, at 0.044, is comparable now), while 100 of the library's 11,592
    /// icons fall under 0.02 — blank or near-blank artwork no capture could identify anyway.
    /// </summary>
    public const double MinimumContrast = 0.02;

    /// <summary>Whether this fingerprint carries enough signal to be worth comparing at all.</summary>
    public bool IsComparable => Signature.Length > 0 && Contrast >= MinimumContrast;

    /// <summary>Whether these are the same artwork by <see cref="SameIconThreshold"/> alone — the fallback for when
    /// no library is available. Callers should check <see cref="IsComparable"/> first.</summary>
    public bool LooksLike(IconFingerprint other) => CorrelationDistanceTo(other) <= SameIconThreshold;

    /// <summary>
    /// 1 - Pearson correlation between the two signatures: 0 is identical, larger is worse.
    ///
    /// Correlation rather than absolute difference because the two sides are not the same rendering: the game draws
    /// the icon 1.1x and interpolates, which shifts values slightly across the whole cell, and correlation ignores
    /// an overall brightness or contrast shift. Measured better than mean absolute difference when it was chosen.
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

/// <summary>Computes <see cref="IconFingerprint"/>s. See that type for why the whole cell is used.</summary>
public static class IconHasher
{
    /// <summary>
    /// The signature is a <see cref="GridSize"/> x <see cref="GridSize"/> grid of average colour, one byte per
    /// channel. **Colour is kept deliberately**: small icons on a dark field look alike in brightness, and what tells
    /// a brown leather band from a silver one is hue.
    ///
    /// **16, measured (2026-10-10)** against 8, 10, 12, 14, 20, 24 and 40, comparing each capture's own icon with the
    /// best match in the library and with every other icon: 16 left the widest gap between the worst genuine pair
    /// and the closest different icon (0.016 against 0.043). Finer grids feel the game's 1.1x interpolation and its
    /// edge outline; coarser ones merge recoloured twins.
    /// </summary>
    public const int GridSize = 16;

    /// <summary>
    /// Fingerprints all of <paramref name="region"/>: the 44x44 icon cell of a capture, or a whole 40x40 icon file.
    /// Returns false only when the region lies outside the image.
    ///
    /// Each grid cell is the area-weighted average of the pixels it covers, fractions included, so a 44-pixel side
    /// and a 40-pixel side land on exactly the same grid rather than on rounded, slightly different ones.
    /// </summary>
    public static bool TryFingerprint(CapturedImage image, Rect region, out IconFingerprint fingerprint)
    {
        ArgumentNullException.ThrowIfNull(image);
        fingerprint = new IconFingerprint([]);
        if (region.Width <= 0 || region.Height <= 0 || region.X < 0 || region.Y < 0 ||
            region.X + region.Width > image.Width || region.Y + region.Height > image.Height)
            return false;

        (int Source, double Weight)[][] columns = Weights(region.X, region.Width);
        (int Source, double Weight)[][] rows = Weights(region.Y, region.Height);
        var signature = new byte[GridSize * GridSize * 3];

        for (int gy = 0; gy < GridSize; gy++)
            for (int gx = 0; gx < GridSize; gx++)
            {
                double b = 0, g = 0, r = 0, total = 0;
                foreach ((int y, double wy) in rows[gy])
                    foreach ((int x, double wx) in columns[gx])
                    {
                        double w = wx * wy;
                        int offset = (y * image.Width + x) * 4;
                        b += image.Pixels[offset] * w;
                        g += image.Pixels[offset + 1] * w;
                        r += image.Pixels[offset + 2] * w;
                        total += w;
                    }

                int cell = (gy * GridSize + gx) * 3;
                signature[cell] = (byte)Math.Round(b / total);
                signature[cell + 1] = (byte)Math.Round(g / total);
                signature[cell + 2] = (byte)Math.Round(r / total);
            }

        fingerprint = new IconFingerprint(signature);
        return true;
    }

    /// <summary>For each of the grid's cells along one axis, the source pixels it covers and how much of each.</summary>
    private static (int, double)[][] Weights(int start, int length)
    {
        var cells = new (int, double)[GridSize][];
        double step = (double)length / GridSize;
        for (int i = 0; i < GridSize; i++)
        {
            double from = i * step, to = (i + 1) * step;
            var covered = new List<(int, double)>();
            for (int p = (int)Math.Floor(from); p < Math.Min(length, (int)Math.Ceiling(to)); p++)
            {
                double overlap = Math.Min(to, p + 1) - Math.Max(from, p);
                if (overlap > 1e-9) covered.Add((start + p, overlap));
            }
            cells[i] = [.. covered];
        }
        return cells;
    }
}
