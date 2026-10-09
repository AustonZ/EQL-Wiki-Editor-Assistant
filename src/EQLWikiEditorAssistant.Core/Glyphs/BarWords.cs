namespace EQLWikiEditorAssistant.Core.Glyphs;

/// <summary>
/// The dictionary <see cref="GlyphReader.ResolveAmbiguous"/> consults where the word alone cannot say whether Arial's
/// bar is an l or a capital I (user, 2026-10-09, reversing the 2026-10-05 decision to leave "lost" reading as "Iost").
///
/// **The list is ENABLE** (public domain), cut to the only words the reader ever asks about: those beginning with i or
/// l, and the few that are one letter followed only by l's or only by i's ("all", "ell", "ill"). Its archaic words are a
/// feature here, since a fantasy game reaches for them.
///
/// **Two small edits make it the game's list rather than Scrabble's** (user, 2026-10-09), kept here rather than in the
/// file so the file stays an unedited piece of ENABLE:
/// <list type="bullet">
/// <item><see cref="GameWords"/> adds what this game writes and ENABLE lacks: "lvl" and "loc", which its players and NPCs
/// use constantly, and "lizardman", a race it names on items too;</item>
/// <item><see cref="UnlikelyWords"/> drops "iamb" and "lota", the only words that collided with another reading
/// ("lamb", "Iota"). With them gone no word is real both ways, so every word the list knows is decided outright.</item>
/// </list>
///
/// **A word it does not know leaves the reader's default alone**, and that default is a capital I — right for the case
/// that matters, because an unknown word is almost always a name and names are capitalised ("Iksar", "Innoruuk"). What is
/// left over is a lowercase word the list lacks (compounds like "lionskin", dialect like "lookin'") or a capitalised name
/// that happens to be a word ("Ias", read as "las"). There is deliberately no warning for either: it would fire on every
/// capitalised name in lore and tell the user nothing.
/// </summary>
public static class BarWords
{
    /// <summary>Words this game uses that ENABLE lacks. Lowercase, and only ones beginning with i or l: no other word is
    /// ever looked up except the one-letter-then-l's shape, which ENABLE already covers.</summary>
    public static readonly IReadOnlyList<string> GameWords = ["lvl", "lvls", "loc", "locs", "lizardman", "lizardmen"];

    /// <summary>ENABLE words removed because they collided with the reading the game actually uses: a fantasy game says
    /// "lamb", not "iamb", and "Iota", not "lota".</summary>
    public static readonly IReadOnlyList<string> UnlikelyWords = ["iamb", "iambs", "lota", "lotas"];

    private static readonly Lazy<HashSet<string>> WordSet = new(() =>
    {
        using Stream stream = typeof(BarWords).Assembly
            .GetManifestResourceStream("EQLWikiEditorAssistant.Core.Glyphs.enable-i-l-words.txt")
            ?? throw new InvalidOperationException("The bundled word list is missing from this assembly.");
        using var reader = new StreamReader(stream);
        var words = new HashSet<string>(StringComparer.Ordinal);
        while (reader.ReadLine() is { } line)
            if (line.Length > 0 && line[0] != '#') words.Add(line);
        words.UnionWith(GameWords);
        words.ExceptWith(UnlikelyWords);
        return words;
    });

    /// <summary>
    /// True when the bars in <paramref name="word"/> (marked by <paramref name="bar"/>) read as l's make a word and read
    /// as I's do not. Every bar is read the same way, which is all the reader needs: its two questions are a word's first
    /// letter alone, and a word whose letters after the first are all bars. No word in the list is real both ways (a
    /// test pins it), so the second half is a guard for a future list rather than a rule that decides anything today.
    /// </summary>
    public static bool ReadsAsL(string word, char bar)
    {
        string lower = word.ToLowerInvariant();
        return Words.Contains(lower.Replace(bar, 'l')) && !Words.Contains(lower.Replace(bar, 'i'));
    }

    /// <summary>The whole list, edits applied, for the test that no word in it reads both ways.</summary>
    public static IReadOnlySet<string> Words => WordSet.Value;
}
