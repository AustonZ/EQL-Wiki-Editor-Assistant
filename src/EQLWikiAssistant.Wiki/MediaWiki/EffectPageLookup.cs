namespace EQLWikiAssistant.Wiki.MediaWiki;

/// <summary>
/// Which page an effect's link should point at, for the effects a page has no line for yet.
///
/// **The wiki keeps an item effect and its same-named player spell on separate pages, and they are different
/// spells** (user, 2026-10-03, who is establishing the convention). `Firestrike` does 422 damage for 138 mana with
/// a 4.00 cast time, as a Druid 38 / Ranger 52 spell; `Firestrike (Effect)` does 302 for no mana, instantly, and
/// its own page says "None; this spell is found on weapons". So an item linking the bare title points its reader
/// at the wrong numbers, invisibly — the same failure as an `itemname` that anchors a hover box on an unrelated
/// article.
///
/// A capture cannot know any of this: the window shows the effect's displayed name and nothing else. So where the
/// page already has a line, its target is preserved (see <c>EffectLine.Render</c>) and this lookup is not
/// consulted. It exists only for the line the tool writes itself — on a page missing one, and on a page it is
/// creating — where there is no editorial choice to preserve and the alternative is to guess the bare name.
///
/// **Measured before it was wired in, because it is a rule that fires rarely**: the live wiki has exactly three
/// `<c>(Effect)</c>` pages — Firestrike, Fungus Spores and Nature's Melody — and every one of them has a
/// same-named bare twin, which is the whole reason the qualifier exists. So the lookup is one batched request on
/// an item whose effect line is missing, and it will matter more as the convention spreads.
/// </summary>
public static class EffectPageLookup
{
    /// <summary>The qualifier the wiki appends to tell an item effect from the player spell of the same name.</summary>
    public const string EffectPageSuffix = " (Effect)";

    /// <summary>
    /// Maps each effect name that has a dedicated <c>&lt;Name&gt; (Effect)</c> page to that page's title. Names
    /// with no such page are simply absent, so a caller treats a miss as "link the name itself".
    ///
    /// Asks nothing when given no names, which is the common case: most items have no effect at all, and an item
    /// whose page already carries its effect line resolves nothing either.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, string>> ResolveLinkTargetsAsync(
        IMediaWikiClient client,
        IReadOnlyList<string> effectNames,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(effectNames);

        var targets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (effectNames.Count == 0) return targets;

        List<string> candidates = effectNames
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim() + EffectPageSuffix)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (candidates.Count == 0) return targets;

        IReadOnlySet<string> existing = await client
            .ExistingTitlesAsync(candidates, cancellationToken).ConfigureAwait(false);

        foreach (string name in effectNames)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            string candidate = name.Trim() + EffectPageSuffix;

            // Compared against what the API returned rather than against the string we sent: MediaWiki normalizes
            // a title (an underscore is a space, the first letter is capitalized), and the link should be written
            // the way the wiki spells it.
            if (existing.FirstOrDefault(t => string.Equals(t, candidate, StringComparison.OrdinalIgnoreCase)) is { } title)
                targets[name] = title;
        }

        return targets;
    }
}
