using System.Text.RegularExpressions;

namespace EQLWikiEditorAssistant.Wiki.Wikitext;

/// <summary>
/// The pre-EQL text that turns up on a statsblock's flags line, recognized by name (user, 2026-10-12): the
/// Project1999-era flags, and the lines the classic item window printed under the name — food and drink sizes, a book
/// that is closed, a note that is rolled up. Both are closed lists, since that game is not changing, so they are known
/// in full rather than guessed at by shape.
///
/// **The lists come from reading every item page on the wiki** (`WikiSpike flagcensus` over all 11,546 of them,
/// 2026-10-12): 7,202 carry something on the flags line that is not a current flag, and everything recognized here is
/// in that census. What is left unrecognized is genuinely unexpected — a class list that lost its `Class:` label, stats
/// written without colons, a pasted HTML merchant value, `Required level of 46.` — and is raised for a human rather
/// than deleted (user, 2026-10-12). It used to be deleted: every all-caps token counted as a legacy flag by shape, so
/// `MNK BRD ROG SHM` on a flags line would have been thrown away with the real legacy flags around it.
///
/// **Current flags are still judged by shape, never by list** (<see cref="FlagDialect.IsCurrent"/>): the devs keep
/// adding them, and a list would reject exactly the rare items most worth recording.
/// </summary>
public static class LegacyStatsText
{
    /// <summary>
    /// The legacy flags, as pages write them: always upper case, and removed by the data pass. Two kinds:
    /// <list type="bullet">
    /// <item>Project1999's own. `NO SELL` and `CONSUMABLE` appear on no page today and are listed for completeness.</item>
    /// <item>**EQL's current flags written in its style** (user, 2026-10-12): a new contributor updating a page keeps the
    /// legacy look without knowing they should not, so `QUEST` turns up where the game shows `Quest` — `Prickly Pear`
    /// did, 2026-10-02. Every current flag seen in a capture or on a page is here in capitals; the game supplies the
    /// right spelling. A capitalized flag not on the list, or a misspelt one (`ATTUNEABLE`), is rare and goes to a
    /// human.</item>
    /// </list>
    /// </summary>
    public static IReadOnlyList<string> LegacyFlags { get; } =
    [
        // Project1999's.
        "LORE ITEM", "MAGIC ITEM", "QUEST ITEM", "NO DROP", "NODROP", "NO SELL", "CONSUMABLE", "ARTIFACT",
        "EXPENDABLE", "NO RENT", "NORENT",
        // EQL's current flags, in capitals.
        "NO TRADE", "LORE EQUIPPED", "ATTUNABLE", "PLACEABLE", "QUEST", "NO DESTROY", "NO STORAGE", "FREE STORAGE",
        "NO PET", "HEIRLOOM", "TEMPORARY", "BANK ONLY", "MARKETPLACE",
    ];

    // Longest first, so `LORE EQUIPPED` is tried before anything that shares its first word.
    private static readonly string[] ByLength = [.. LegacyFlags.OrderByDescending(f => f.Length)];

    /// <summary>
    /// Whether a flags-line token is one or more legacy flags. More than one, because about 240 pages write them separated
    /// by single spaces (`MAGIC ITEM LORE ITEM NO TRADE`), which the grammar cannot split into tokens of their own. Case
    /// matters: `No Trade` is a current flag and `NO TRADE` a legacy one.
    /// </summary>
    public static bool IsLegacyFlags(string token)
    {
        ArgumentNullException.ThrowIfNull(token);

        string rest = token.Trim();
        if (rest.Length == 0) return false;
        while (rest.Length > 0)
        {
            string? match = ByLength.FirstOrDefault(f =>
                rest.StartsWith(f, StringComparison.Ordinal) && (rest.Length == f.Length || rest[f.Length] == ' '));
            if (match is null) return false;
            rest = rest[match.Length..].TrimStart();
        }

        return true;
    }

    /// <summary>
    /// The classic item window's own lines, any case and with or without closing punctuation: food and drink sizes
    /// (`This is a Hearty Meal!`), and what it said about books, notes, letters, compasses, tradeskill items and light
    /// sources. EQL's window shows none of them, so the data step moves them from the flags line into <c>notes</c>,
    /// after the lore, exactly as the page wrote them (user, 2026-10-12). The spelling variants are the wiki's own:
    /// `a enduring` beside `an enduring`, `banquet size`, `banquet sized` and `banquet-sized`, `This is a Banquet` with
    /// no "meal", and `This meal is a feast!` (one page writes `mealis`).
    /// </summary>
    public static bool IsWindowText(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        return WindowText.IsMatch(token.Trim());
    }

    private static readonly Regex WindowText = new(
        @"^\(?\s*(?:" +
        @"this is an? (?:snack|meal|hearty meal|banquet(?:[- ]?sized?)?(?: meal)?|feast|enduring meal|miraculous meal" +
        @"|whistle wetter|drink|refreshing drink|lasting drink|flowing drink|enduring drink|miraculous drink)" +
        @"|this meal ?is a feast" +
        @"|(?:the|this) (?:note|book|letter) is (?:rolled up|closed|sealed|open)" +
        @"|the compass points (?:north|south|east|west)" +
        @"|this item can be used in tradeskills" +
        @"|this item acts as a light source" +
        @")\s*[.!]*\s*\)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
