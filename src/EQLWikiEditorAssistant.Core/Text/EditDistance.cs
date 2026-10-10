namespace EQLWikiEditorAssistant.Core.Text;

/// <summary>
/// Small fuzzy-matching helper, for text that may differ from what it should match by a character or two: field
/// labels, the Lore tab's label, and the native-vs-foreign exaltation name check.
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
