namespace EQLWikiEditorAssistant.Core.Ocr;

/// <summary>
/// Flattens straight-alpha BGRA pixels onto a flat background colour.
///
/// **This exists because discarding alpha instead was a real, measured bug.** A wiki icon PNG's fully-transparent
/// pixels keep whatever RGB the encoder left underneath — usually white — so decoding one with the alpha ignored
/// makes the sprite's margin read as ink and the whole 40x40 file look like artwork. Every icon comparison then came
/// back a mismatch at close to the random baseline. See CLAUDE.md's icon-comparison notes.
///
/// The background to composite over is the game's own window grey, because that is what the game draws its icons
/// onto: after this, both sides of the comparison are the same sprite on the same backdrop.
///
/// It lives in <c>Core</c> as a pure function so that the production decoder and the dev/test file loader share one
/// copy of the arithmetic — the icon thresholds in CLAUDE.md were measured through the latter, and they only
/// transfer to production if both do exactly the same thing.
/// </summary>
public static class AlphaComposite
{
    /// <summary>The game's window interior grey, which every in-game icon is drawn onto. Measured, like every other
    /// constant taken from this UI's pixels (see `LocateSpike --probe`).</summary>
    public const byte GameBackground = 16;

    /// <summary>Composites <paramref name="pixels"/> (tightly packed straight-alpha BGRA32) over a flat grey, in
    /// place, leaving every pixel fully opaque.</summary>
    public static void Over(byte[] pixels, byte background)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        if (pixels.Length % 4 != 0)
            throw new ArgumentException($"BGRA32 data must be a multiple of 4 bytes long, but was {pixels.Length}.", nameof(pixels));

        for (int i = 0; i < pixels.Length; i += 4)
        {
            int alpha = pixels[i + 3];
            if (alpha == 255) continue;

            for (int channel = 0; channel < 3; channel++)
                pixels[i + channel] = (byte)((pixels[i + channel] * alpha + background * (255 - alpha)) / 255);
            pixels[i + 3] = 255;
        }
    }
}
