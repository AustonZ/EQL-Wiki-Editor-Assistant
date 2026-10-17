namespace EQLWikiEditorAssistant.Core.Glyphs;

/// <summary>
/// How bright the game draws each of the atlas's seven coverage levels, as fifteenths of the way from the background to
/// the text's peak: level 0 is the background and level 6 the peak, always.
///
/// **Usually the steps in <see cref="GlyphRamp.Fifteenths"/>, but not on every machine** (a tester, 2026-10-15). After a
/// Windows update, one tester's game drew the same glyphs, pixel for pixel, with levels 1 and 2 brighter: 4 and 7
/// fifteenths where every capture before had 3 and 6. It followed him to every monitor and survived a reboot, so it is a
/// property of the machine, and no fixed list of steps can be relied on. So each window's ramp is learned from its own
/// Description tab (<see cref="Locate.DescriptionTabFinder"/>) and handed to the reader with that window. Every sample
/// learns <see cref="Standard"/> exactly, at every UI scale.
///
/// Held in fifteenths, as doubles, so that the standard ramp does exactly the arithmetic the integer table always did.
/// </summary>
public sealed class CoverageRamp
{
    /// <summary>The ramp every capture showed until 2026-10-15: <see cref="GlyphRamp.Fifteenths"/>.</summary>
    public static CoverageRamp Standard { get; } = new([.. GlyphRamp.Fifteenths.Select(f => (double)f)]);

    private readonly double[] _fifteenths;

    /// <param name="fifteenths">One value per level: 0 for level 0, 15 for level 6, and strictly rising between.</param>
    public CoverageRamp(IReadOnlyList<double> fifteenths)
    {
        ArgumentNullException.ThrowIfNull(fifteenths);
        if (fifteenths.Count != GlyphRamp.LevelCount)
            throw new ArgumentException($"A ramp has {GlyphRamp.LevelCount} levels, not {fifteenths.Count}.", nameof(fifteenths));
        if (fifteenths[0] != 0 || fifteenths[^1] != 15)
            throw new ArgumentException("A ramp runs from 0 (the background) to 15 (the peak).", nameof(fifteenths));
        for (int i = 1; i < fifteenths.Count; i++)
            if (fifteenths[i] <= fifteenths[i - 1])
                throw new ArgumentException("A ramp's levels must rise strictly.", nameof(fifteenths));
        _fifteenths = [.. fifteenths];
    }

    /// <summary>Level <paramref name="level"/>'s brightness, in fifteenths of the way from background to peak.</summary>
    public double this[int level] => _fifteenths[level];

    public bool IsStandard => _fifteenths.SequenceEqual(Standard._fifteenths);

    /// <summary>The levels in fifteenths, as the plan and the tools write them: "0, 4, 7, 9, 11, 13, 15".</summary>
    public override string ToString() => string.Join(", ", _fifteenths.Select(f => f.ToString("0.##")));
}
