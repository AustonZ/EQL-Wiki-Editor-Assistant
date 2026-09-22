namespace EQLWikiAssistant.Core.Text;

/// <summary>
/// Small fuzzy-matching helper. OCR (even the good engine — see the plan's milestone 1/2 writeups) makes
/// consistent, small, single-character-class mistakes on this game's UI text (e.g. "Ornamentation" ->
/// "Omamentation"), so exact string equality is the wrong tool for matching OCR'd text against known field
/// labels, item names, or wiki page titles. Used by the field-label lexicon, window-anchor detection, and the
/// native-vs-foreign exaltation name check.
/// </summary>
public static class EditDistance
{
    /// <summary>Levenshtein (edit) distance between two strings: the minimum number of single-character
    /// insertions, deletions, or substitutions to turn one into the other.</summary>
    public static int Levenshtein(string a, string b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) previous[j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
            }
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }

    /// <summary>True if <paramref name="candidate"/> is within <paramref name="maxDistance"/> edits of
    /// <paramref name="target"/> (case-insensitive, ordinal).</summary>
    public static bool IsCloseMatch(string candidate, string target, int maxDistance) =>
        Levenshtein(candidate.ToUpperInvariant(), target.ToUpperInvariant()) <= maxDistance;
}
