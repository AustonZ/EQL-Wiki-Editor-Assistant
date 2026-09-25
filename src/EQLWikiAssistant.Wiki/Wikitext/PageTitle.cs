namespace EQLWikiAssistant.Wiki.Wikitext;

/// <summary>How an item's in-game name relates to the wiki page title it would live at.</summary>
public enum TitleMatch
{
    /// <summary>The page's <c>itemname</c> is exactly its title — what the template requires.</summary>
    Exact,

    /// <summary>
    /// The title is the <c>itemname</c> plus a parenthesised qualifier — the page
    /// <c>Essence of Barbarian (Wormwood)</c> whose <c>itemname</c> is <c>Essence of Barbarian</c>.
    ///
    /// **This is still a defect**, distinguished from <see cref="Divergent"/> only because it has a recognizable
    /// cause worth telling the user about. It arises where one in-game item needs several wiki pages (craft
    /// material, deity, quest variant, left/right book page) and the editor left <c>itemname</c> as the shared
    /// in-game name. See <see cref="PageTitle"/> for what it breaks and why the fix is not obvious.
    /// </summary>
    DisambiguatedTitle,

    /// <summary>The two disagree in some other way. Always a human's call.</summary>
    Divergent,
}

/// <summary>Why an in-game name cannot be used as a wiki page title verbatim.</summary>
public sealed record TitleProblem(string Explanation, IReadOnlyList<char> OffendingCharacters);

/// <summary>
/// MediaWiki page-title rules, as they constrain item names.
///
/// **An item name is not freely a page title, and the tool must never invent a substitute** (user, 2026-09-25).
/// MediaWiki forbids a handful of characters in titles outright, and `#` is the one that turns up on real items —
/// a `Cell Key #5` cannot have a page at its own name. The wiki's editors resolved that case by hand as
/// `Cell Key No. 5`, and that is exactly the kind of decision the tool has no business guessing: `No. 5`, `Number
/// 5`, `5` and dropping the `#` are all defensible, the choice is permanent, it becomes the page's URL, and
/// getting it wrong creates a duplicate page nobody can delete. So this type only ever *reports*.
///
/// **A name with an illegal character also breaks lookup**, which matters as much as the write side: querying the
/// API for it returns an error or nothing. Treat that as "the page does not exist" — but never quietly, because
/// the page very likely *does* exist under a hand-chosen name. The user must be warned before creating anything.
///
/// The character set is MediaWiki's own, taken from this wiki's `legaltitlechars` (fetched from
/// <c>action=query&amp;meta=siteinfo</c> on 2026-09-24):
/// <c> %!"$&amp;'()*,\-.\/0-9:;=?@A-Z\\^_`a-z~\x80-\xFF+</c>. Everything outside it is illegal; in practice that
/// is the eight characters in <see cref="IllegalCharacters"/>.
/// </summary>
public static class PageTitle
{
    /// <summary>The characters MediaWiki will not accept in a page title. `#` separates a fragment, `[`/`]` and
    /// `{`/`}` and `|` are wikitext markup, and `&lt;`/`&gt;` are HTML.</summary>
    public static readonly IReadOnlyList<char> IllegalCharacters = ['#', '[', ']', '{', '}', '|', '<', '>'];

    /// <summary>
    /// Whether this in-game name can be a page title as it stands. Returns null when it can.
    /// </summary>
    /// <remarks>
    /// No sample in the 101-window corpus contains an illegal character, so this is implemented from MediaWiki's
    /// rule rather than from measured data — the `Cell Key #5` case is the user's, from items not in the corpus.
    /// </remarks>
    public static TitleProblem? FindProblem(string itemName)
    {
        ArgumentNullException.ThrowIfNull(itemName);

        char[] offenders = [.. itemName.Where(IllegalCharacters.Contains).Distinct()];
        if (offenders.Length > 0)
            return new TitleProblem(
                $"'{itemName}' contains {Describe(offenders)}, which MediaWiki does not allow in a page title. " +
                "Choose the page name yourself — the wiki's own convention for this has been e.g. 'Cell Key #5' -> " +
                "'Cell Key No. 5'. The page may already exist under a name someone picked earlier.",
                offenders);

        // These are the remaining ways MediaWiki rejects a title outright. None has been seen on a real item; they
        // are here so a caller gets a reason rather than a confusing API error.
        if (itemName.Trim().Length == 0)
            return new TitleProblem("The item name is empty, so there is no page title to look up.", []);

        if (itemName.Length > 255)
            return new TitleProblem(
                $"'{itemName}' is {itemName.Length} characters; MediaWiki titles are limited to 255 bytes.", []);

        return null;
    }

    /// <summary>True when the name can be used as a page title as it stands.</summary>
    public static bool IsUsableAsTitle(string itemName) => FindProblem(itemName) is null;

    /// <summary>
    /// Compares a page's <c>itemname</c> parameter against its actual title.
    ///
    /// **Any mismatch is a defect** — see <see cref="IsDefect"/>. Measured on 538 real item pages: 10 disagree, 8
    /// of them by the parenthetical pattern and 2 in some other way (one being a grave accent written as an
    /// apostrophe). At ~1.5% of item pages, expect a few hundred broken pages wiki-wide.
    /// </summary>
    public static TitleMatch Compare(string? itemName, string pageTitle)
    {
        ArgumentNullException.ThrowIfNull(pageTitle);
        if (string.IsNullOrWhiteSpace(itemName)) return TitleMatch.Divergent;

        // MediaWiki normalizes underscores to spaces and collapses runs of whitespace in titles, so compare on
        // that footing rather than treating "Water_Flask" as a different page from "Water Flask".
        string name = Normalize(itemName);
        string title = Normalize(pageTitle);

        if (string.Equals(name, title, StringComparison.Ordinal)) return TitleMatch.Exact;

        // "<name> (<qualifier>)" — one in-game item, several wiki pages.
        if (title.Length > name.Length + 2 &&
            title.StartsWith(name + " (", StringComparison.Ordinal) &&
            title.EndsWith(')'))
            return TitleMatch.DisambiguatedTitle;

        return TitleMatch.Divergent;
    }

    /// <summary>
    /// Whether this outcome needs a human. True for every mismatch, including
    /// <see cref="TitleMatch.DisambiguatedTitle"/>.
    ///
    /// **Verified against the live wiki (2026-09-25), because an earlier note in this repo claimed the opposite.**
    /// `Itempage` renders the item as a hover box whose visible anchor is a link to <c>[[itemname]]</c>, so when
    /// <c>itemname</c> is not the page's own title the page shows a link to somewhere else. Two failure modes, both
    /// observed: if no page has that name the reader gets a red "page does not exist" link where the item should be
    /// (`Essence of Barbarian (Wormwood)`), and if a page *does* have that name the box silently anchors to an
    /// unrelated article — `Tailoring (Item)` links to the Tailoring *skill* page, which is worse for being
    /// invisible.
    ///
    /// **The fix is not obvious, which is why this only reports.** The in-game item genuinely shares one name
    /// across its variants, so setting <c>itemname</c> to the qualified title would render a name the game never
    /// shows. Properly resolving it likely needs a template change rather than a page edit.
    /// </summary>
    public static bool IsDefect(TitleMatch match) => match != TitleMatch.Exact;

    private static string Normalize(string value) =>
        string.Join(' ', value.Replace('_', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static string Describe(char[] offenders) =>
        offenders.Length == 1 ? $"the character '{offenders[0]}'" : $"the characters {string.Join(", ", offenders.Select(c => $"'{c}'"))}";
}
