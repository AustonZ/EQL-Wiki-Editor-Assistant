using System.Text;

namespace EQLWikiEditorAssistant.Core.Glyphs;

/// <summary>
/// One glyph, normalized to colour- and background-independent coverage levels (see <see cref="GlyphRamp"/>).
///
/// Equality is exact and structural, which is the point: because the font is a deterministic bitmap blit, two
/// renderings of the same character are byte-identical, so a dictionary keyed on this type clusters glyphs with
/// no threshold, no distance metric and no tuning. Anything fuzzy here would be the wrong tool — fuzziness is for
/// text compared with text a human wrote (<c>EditDistance</c>), not for pixels that are supposed to match exactly.
/// </summary>
public sealed class GlyphBitmap : IEquatable<GlyphBitmap>
{
    public int Width { get; }
    public int Height { get; }

    /// <summary>Ramp level (0..6) per pixel, row-major.</summary>
    public byte[] Levels { get; }

    private readonly int _hash;

    public GlyphBitmap(int width, int height, byte[] levels)
    {
        if (levels.Length != width * height)
            throw new ArgumentException($"levels length {levels.Length} != {width}x{height}", nameof(levels));

        Width = width;
        Height = height;
        Levels = levels;

        var hash = new HashCode();
        hash.Add(width);
        hash.Add(height);
        foreach (byte level in levels) hash.Add(level);
        _hash = hash.ToHashCode();
    }

    /// <summary>Total ink, used only to order clusters in reports so the visually heaviest shapes come first.</summary>
    public int InkWeight => Levels.Sum(l => (int)l);

    public byte this[int x, int y] => Levels[y * Width + x];

    public bool Equals(GlyphBitmap? other) =>
        other is not null && Width == other.Width && Height == other.Height && Levels.AsSpan().SequenceEqual(other.Levels);

    public override bool Equals(object? obj) => Equals(obj as GlyphBitmap);
    public override int GetHashCode() => _hash;

    /// <summary>Compact, stable text form: dimensions plus one character per pixel. This is what the atlas file
    /// stores, so the atlas stays human-readable and diffable in git — a reviewer can see the glyph in the diff
    /// rather than a blob of base64.</summary>
    public string Encode()
    {
        var sb = new StringBuilder(Levels.Length);
        foreach (byte level in Levels) sb.Append((char)('0' + level));
        return sb.ToString();
    }

    public static GlyphBitmap Decode(int width, int height, string encoded)
    {
        var levels = new byte[encoded.Length];
        for (int i = 0; i < encoded.Length; i++) levels[i] = (byte)(encoded[i] - '0');
        return new GlyphBitmap(width, height, levels);
    }

    /// <summary>ASCII-art rendering, for eyeballing a cluster when labelling it.</summary>
    public string Render()
    {
        const string shades = " .:-+*#@";
        var sb = new StringBuilder();
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++) sb.Append(shades[this[x, y] == 0 ? 0 : this[x, y] + 1]);
            sb.Append('\n');
        }
        return sb.ToString();
    }
}
