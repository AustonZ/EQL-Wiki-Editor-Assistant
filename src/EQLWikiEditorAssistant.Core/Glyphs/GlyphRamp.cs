namespace EQLWikiEditorAssistant.Core.Glyphs;

/// <summary>
/// The game's anti-aliasing coverage ramp, and the conversion from a raw pixel to a ramp level.
///
/// The UI font is a deterministic bitmap blit, not a rasterizer: the same character drawn twice produces
/// byte-identical pixels (measured — a 'A' in an item window and the same 'A' in the in-game Notes Window match
/// exactly, including every anti-aliased intermediate). Anti-aliasing uses a fixed, quantized coverage ramp, so a
/// glyph can be identified by exact comparison rather than by recognition. That is the whole basis of
/// <c>GlyphOcrEngine</c>.
///
/// **The ramp is the same in every text colour and on every background — but only after normalizing.** Measured
/// absolute values differ per colour because the ramp is applied to whichever channels the colour uses and scaled
/// to that colour's peak:
/// <code>
///   white on content area (bg  16, peak 255):  16  64 112 159 191 223 255
///   grey on title bar     (bg   0, peak 192):   0  38  77 115 141 166 192
///   magenta on content    (bg  16, peak 224):  16  58 100 141 169 196 224
/// </code>
/// Divide by (peak - background) and all three collapse onto one ramp: 0, 3/15, 6/15, 9/15, 11/15, 13/15, 15/15.
/// So a single colour-blind atlas covers white labels, yellow tab text, magenta effect names and the dimmer
/// title bar alike — no per-colour atlas is needed, which was an open question when this was planned.
/// </summary>
public static class GlyphRamp
{
    /// <summary>The ramp as fifteenths, which is exactly what the measurements land on.</summary>
    public static readonly IReadOnlyList<int> Fifteenths = [0, 3, 6, 9, 11, 13, 15];

    public const int LevelCount = 7;

    /// <summary>A pixel this far above the background is ink. The smallest real ink step is 3/15 of the span
    /// (measured: 38 above a black title bar, the tightest case), so this sits well below it while staying above
    /// anything a lossless capture could produce on a flat background.</summary>
    public const int InkThreshold = 8;

    /// <summary>Maximum deviation from a ramp value before a pixel is reported as off-ramp. Captures are lossless
    /// PNG, so in practice this is 0; a non-zero count means the model is wrong (a different skin, a different UI
    /// scale, a rescaled image) and the caller should say so rather than quietly rounding.</summary>
    public const double RampTolerance = 0.02;

    /// <summary>Per-pixel text intensity: the maximum channel.
    ///
    /// The ramp is applied to whichever channels the text colour uses — white moves all three, yellow R+G,
    /// magenta R+B, green G alone — so the maximum channel recovers the same coverage whatever the colour.
    /// (Note this is the opposite of <c>WindowBoundsFinder</c>'s frame test, which uses the *minimum* channel:
    /// there the question is whether a pixel is neutral-dark, not how much ink it carries.)</summary>
    public static int Intensity(byte[] pixels, int width, int height, int x, int y)
    {
        if (x < 0 || y < 0 || x >= width || y >= height) return 0;
        int i = (y * width + x) * 4; // BGRA
        return Math.Max(pixels[i], Math.Max(pixels[i + 1], pixels[i + 2]));
    }

    /// <summary>Quantizes a raw intensity to its ramp level (0..6), given the run's background and peak.
    /// <paramref name="offRamp"/> is set when the value doesn't land on a ramp step, which is a signal that the
    /// assumptions above no longer hold — never something to silently round away.</summary>
    public static byte Quantize(int intensity, int background, int peak, out bool offRamp)
    {
        int span = peak - background;
        if (span <= 0)
        {
            offRamp = false;
            return 0;
        }

        double ratio = (intensity - background) / (double)span;
        byte best = 0;
        double bestError = double.MaxValue;
        for (byte level = 0; level < Fifteenths.Count; level++)
        {
            double error = Math.Abs(ratio - Fifteenths[level] / 15.0);
            if (error < bestError)
            {
                bestError = error;
                best = level;
            }
        }

        offRamp = bestError > RampTolerance;
        return best;
    }
}
