namespace EQLWikiAssistant.Wiki.Wikitext;

/// <summary>
/// Which dialect a token on a statsblock's flags line belongs to — or whether it is not a flag at all.
///
/// **One rule, shared by the data pass and the formatting pass**, because the two reach the same question from
/// opposite ends: the analyzer decides what to *write* over a flags line, and the formatter decides whether it
/// understands one well enough to lay it out. Two copies would drift, and the way they would drift is the worst
/// kind — one pass rewriting a line the other declared it could not read.
///
/// **Judged by shape, never against a list** (user, 2026-09-24). The devs keep adding flags — `No Pet`,
/// `Heirloom` and `Free Storage` are ones the user has seen and no capture in the corpus contains — so a known-flags
/// list would reject exactly the rare items most worth recording. Measured across 1,183 real pages rather than
/// assumed: every legacy flag there is ALL-CAPS (<c>MAGIC ITEM</c>, <c>EXPENDABLE</c>, <c>NODROP</c>,
/// <c>NO RENT</c>) and every current one is Title Case (<c>Lore Equipped</c>, <c>No Trade</c>, <c>Attunable</c>).
///
/// The third category is the one that bites, because it is neither: prose that landed on the flags line.
/// <c>This is a meal!</c>, <c>The Book is closed.</c>, <c>Required level of 55.</c>, and one page's mis-parsed
/// <c>Class:CLR DRU SHM</c> all fail on a lowercase word, a terminal stop or a colon.
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

    /// <summary>
    /// A pre-EQL flag, inherited from the Project1999 import: every letter is upper case.
    ///
    /// Deliberately crude, and it never has to split a legacy line into individual flags — which the nine sampled
    /// single-spaced pages (<c>MAGIC ITEM LORE ITEM NO TRADE</c>) make impossible anyway. The line is discarded
    /// wholesale either way, so this only decides what the user is *told*.
    /// </summary>
    public static bool IsLegacy(string flag)
    {
        ArgumentNullException.ThrowIfNull(flag);
        return flag.Any(char.IsLetter) && flag.Where(char.IsLetter).All(char.IsUpper);
    }

    /// <summary>
    /// Neither dialect: text that is not a flag and that no pass may touch.
    ///
    /// **This is what stops the flags line being regenerated over somebody's content.** The line is rewritten
    /// whole, so a pass that rewrites one carrying <c>This is a meal!</c> deletes it — and the tool is forbidden
    /// to move or discard that text (user, 2026-09-24): it may only raise it and leave it to the human.
    /// </summary>
    public static bool IsProse(string flag) => !IsCurrent(flag) && !IsLegacy(flag);
}
