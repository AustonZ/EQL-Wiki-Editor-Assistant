using EQLWikiAssistant.Wiki.Wikitext;

namespace EQLWikiAssistant.Wiki.MediaWiki;

/// <summary>What looking up an item's page turned up.</summary>
public enum LookupOutcome
{
    /// <summary>A page exists at the item's own in-game name. The ordinary case.</summary>
    Found,

    /// <summary>No page at the correct name, but one exists whose title differs only in which quote character it
    /// uses. Almost certainly this item under a misspelt name — but the tool does not assume it, because acting on
    /// the wrong page is worse than treating a real item as new.</summary>
    FoundMisnamedCandidate,

    /// <summary>No page, and no near-miss. The item is new to the wiki.</summary>
    NotFound,

    /// <summary>The in-game name cannot be a MediaWiki title at all, so it cannot even be queried. See
    /// <see cref="ItemPageLookupResult.TitleProblem"/>.</summary>
    NameUnusable,
}

/// <summary>
/// The outcome of a lookup. <see cref="Page"/> is set for <see cref="LookupOutcome.Found"/> and for
/// <see cref="LookupOutcome.FoundMisnamedCandidate"/> — in the latter case it is the *candidate*, offered for the
/// user to judge, never something to edit automatically.
/// </summary>
public sealed record ItemPageLookupResult(
    LookupOutcome Outcome,
    string RequestedTitle,
    WikiPage? Page,
    TitleProblem? TitleProblem,
    string? Warning)
{
    /// <summary>True when the tool should treat this item as not yet on the wiki. Note this includes the
    /// misnamed-candidate case: the item has no page at its correct name, so from the tool's point of view it is
    /// new — the candidate is information for the user, not a page to edit.</summary>
    public bool TreatAsNew => Outcome is not LookupOutcome.Found;

    /// <summary>
    /// Whether the tool may offer to <em>create</em> this item's page — which is narrower than
    /// <see cref="TreatAsNew"/>, and the gap between them is the whole point.
    ///
    /// **Only <see cref="LookupOutcome.NotFound"/> qualifies.** The other two "new" outcomes are precisely the cases
    /// where creating a page does permanent damage, and an ordinary editor on this wiki cannot delete a page to
    /// undo it:
    /// - <see cref="LookupOutcome.FoundMisnamedCandidate"/> — a page for this item almost certainly already exists
    ///   under a quote-character variant. Creating a second one produces the duplicate nobody can remove, which is
    ///   the exact failure the variant search was built to prevent.
    /// - <see cref="LookupOutcome.NameUnusable"/> — the in-game name cannot be a MediaWiki title, so there is no
    ///   title to create at. The page very likely exists under a name a human chose (<c>Cell Key #5</c> living at
    ///   <c>Cell Key No. 5</c>), and picking a replacement is a permanent, URL-defining judgement the tool never
    ///   makes.
    ///
    /// Both still reach the user as a warning; what they do not reach is a Create button.
    /// </summary>
    public bool MayCreate => Outcome is LookupOutcome.NotFound;
}

/// <summary>
/// Finds the wiki page for a captured item, by its in-game name.
///
/// **Two real hazards make this more than one API call**, both measured on the live wiki:
///
/// 1. **The grave accent and the apostrophe get confused, in both directions.** Real pages exist titled
///    <c>Engraved Di`Zok Deathbringer</c> whose <c>itemname</c> is <c>Engraved Di'Zok Deathbringer</c>, and others
///    the other way round. Because glyph matching keeps the two characters distinct (the atlas has separate entries
///    and a test pins that), the captured name is authoritative — so a lookup that misses can check the quote
///    variants and tell the user "this probably exists, spelt wrong" instead of silently reporting a new item.
///    That matters because the user's only remedy is to create a correctly-named page and redirect the old one
///    (they cannot rename), so a missed candidate means a duplicate nobody can delete.
/// 2. **A name containing a title-illegal character cannot be queried at all** — the API errors or returns nothing.
///    That is reported as <see cref="LookupOutcome.NameUnusable"/> rather than as "not found", because the page
///    very likely exists under a name a human chose (<c>Cell Key #5</c> -> <c>Cell Key No. 5</c>) and creating a
///    second one would be the wrong move.
///
/// Redirects are followed by the client, so an already-redirected misspelt page resolves to the right one and this
/// never sees it — which is the desired behaviour, not a gap.
/// </summary>
public static class ItemPageLookup
{
    /// <summary>The quote characters that get substituted for one another. The first two are the pair that actually
    /// occurs in this game's item names; the curly forms are included because a human editing by hand may have been
    /// given them by an editor's autocorrect.</summary>
    private static readonly char[] QuoteCharacters = ['\'', '`', '‘', '’'];

    /// <summary>The two that actually occur in this game's item names. Variants built only from these are tried
    /// first — see <see cref="QuoteVariants"/> for why the ordering, not just the cap, matters.</summary>
    private static readonly char[] LikelyQuoteCharacters = ['\'', '`'];

    /// <summary>Cap on how many variants are queried, since each one is an API request. Item names carry one or two
    /// quotes in practice, and the plausibility ordering means this only ever discards curly-quote forms.</summary>
    private const int MaxVariants = 8;

    public static async Task<ItemPageLookupResult> FindAsync(
        IMediaWikiClient client,
        string itemName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(itemName);

        if (PageTitle.FindProblem(itemName) is { } problem)
            return new ItemPageLookupResult(
                LookupOutcome.NameUnusable, itemName, null, problem,
                $"'{itemName}' cannot be a wiki page title, so it could not be looked up. {problem.Explanation}");

        WikiPage? exact = await client.FetchPageAsync(itemName, cancellationToken).ConfigureAwait(false);
        if (exact is not null)
            return new ItemPageLookupResult(LookupOutcome.Found, itemName, exact, null, null);

        foreach (string candidate in QuoteVariants(itemName))
        {
            WikiPage? page = await client.FetchPageAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (page is null) continue;

            return new ItemPageLookupResult(
                LookupOutcome.FoundMisnamedCandidate, itemName, page, null,
                $"No page exists at '{itemName}', but '{page.Title}' does — the same name with a different quote " +
                "character. That is very likely this item under a misspelt name. This item is being treated as new; " +
                "check the existing page before creating another one, and if it is this item, move it to the right title.");
        }

        return new ItemPageLookupResult(LookupOutcome.NotFound, itemName, null, null, null);
    }

    /// <summary>
    /// Titles differing from <paramref name="itemName"/> only in which quote characters it uses, excluding the name
    /// itself, most plausible first. Empty when the name contains no quotes — the common case, which therefore costs
    /// no extra requests at all.
    /// </summary>
    /// <remarks>
    /// **The ordering is what makes the cap safe.** Four quote characters across two positions is sixteen
    /// combinations, more than <see cref="MaxVariants"/>, so an arbitrary order would let the cap discard a
    /// perfectly plausible apostrophe/grave swap in favour of a curly-quote form that has never been observed.
    /// Sorting by how many curly quotes a variant uses puts every likely candidate ahead of every unlikely one, so
    /// the cap only ever trims the tail.
    /// </remarks>
    public static IReadOnlyList<string> QuoteVariants(string itemName)
    {
        int[] positions = [.. Enumerable.Range(0, itemName.Length).Where(i => QuoteCharacters.Contains(itemName[i]))];
        if (positions.Length == 0) return [];

        var variants = new List<string> { itemName };
        foreach (int position in positions)
        {
            var grown = new List<string>(variants.Count * QuoteCharacters.Length);
            foreach (string seed in variants)
                foreach (char quote in QuoteCharacters)
                {
                    char[] chars = seed.ToCharArray();
                    chars[position] = quote;
                    grown.Add(new string(chars));
                }

            variants = grown;
        }

        return [.. variants
            .Distinct(StringComparer.Ordinal)
            .Where(v => !string.Equals(v, itemName, StringComparison.Ordinal))
            .OrderBy(v => v.Count(c => !LikelyQuoteCharacters.Contains(c) && QuoteCharacters.Contains(c)))
            .Take(MaxVariants)];
    }
}
