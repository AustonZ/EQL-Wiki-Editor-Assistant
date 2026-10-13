namespace EQLWikiEditorAssistant.Wiki.Wikitext;

/// <summary>
/// What a token on a statsblock's flags line is: a current flag, a legacy one, the classic item window's own text, or
/// something unrecognized.
///
/// **One rule, shared by the data pass and the formatting pass**, because the two reach the same question from
/// opposite ends: the analyzer decides what to *write* over a flags line, and the formatter decides whether it
/// understands one well enough to lay it out. Two copies would drift, and the way they would drift is the worst
/// kind — one pass rewriting a line the other declared it could not read.
///
/// **Current flags are judged by shape, never against a list** (user, 2026-09-24). The devs keep adding flags —
/// `No Pet`, `Heirloom`, `Free Storage` and `Temporary` are ones the user has seen and no capture in the corpus
/// contains — so a known-flags list would reject exactly the rare items most worth recording. Measured across 1,183
/// real pages rather than assumed: every current flag is Title Case (<c>Lore Equipped</c>, <c>No Trade</c>,
/// <c>Attunable</c>).
///
/// **Legacy flags and window text are recognized by name** (user, 2026-10-12, <see cref="LegacyStatsText"/>). Both
/// are closed lists from a game that is not changing. Until then a legacy flag was anything ALL-CAPS, which also
/// classed a class list that had lost its `Class:` label (`MNK BRD ROG SHM`) as a legacy flag to delete.
/// </summary>
public static class FlagDialect
{
    /// <summary>A flag in EQL's current dialect: Title Case, no sentence punctuation, no colon.</summary>
    public static bool IsCurrent(string flag)
    {
        ArgumentNullException.ThrowIfNull(flag);

        string trimmed = flag.Trim();
        if (trimmed.Length == 0 || trimmed.Contains(':')) return false;
        if (trimmed[^1] is '.' or '!' or '?') return false;

        foreach (string word in trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!char.IsUpper(word[0])) return false;
            // An ALL-CAPS word is the legacy dialect. A single letter cannot tell us either way, so it passes.
            if (word.Length > 1 && !word[1..].Any(char.IsLower)) return false;
        }

        return true;
    }

    /// <summary>One or more pre-EQL flags (<see cref="LegacyStatsText.IsLegacyFlags"/>). The data pass removes them:
    /// the flags line is regenerated from what the game shows.</summary>
    public static bool IsLegacy(string flag) => LegacyStatsText.IsLegacyFlags(flag);

    /// <summary>The classic item window's own text (<see cref="LegacyStatsText.IsWindowText"/>), which the data pass
    /// moves into <c>notes</c>.</summary>
    public static bool IsWindowText(string flag) => LegacyStatsText.IsWindowText(flag);

    /// <summary>
    /// None of the above: text that is not a flag the Assistant knows, and that no pass may touch.
    ///
    /// **This is what stops the flags line being regenerated over somebody's content.** The line is rewritten whole,
    /// so rewriting one that carries text nobody has classified would delete it. It is raised for a human instead.
    /// </summary>
    public static bool IsUnrecognized(string flag) => !IsCurrent(flag) && !IsLegacy(flag) && !IsWindowText(flag);
}
