namespace EQLWikiAssistant.Core.Items;

/// <summary>Why a captured item cannot be processed automatically. Each of these means "this capture cannot be
/// trusted to represent the item as the wiki should record it", not "this capture failed".</summary>
public enum IneligibilityReason
{
    /// <summary>A filled exaltation slot holds something other than the item's own native exaltation, so the
    /// window's stats include contributions from a different item.</summary>
    ForeignExaltation,

    /// <summary>The item has been levelled (<c>+X</c> where X &gt; 0). The wiki stores level-0 data and the
    /// per-stat downscaling formula is deferred to milestone 8, so v1 refuses rather than guessing.</summary>
    UnsupportedLevel,
}

/// <summary>One concrete thing standing in the way, with enough detail to tell the user which slot or level.</summary>
public sealed record IneligibilityDetail(IneligibilityReason Reason, string Explanation);

/// <summary>
/// Whether a captured item may be processed automatically (pipeline step 4b).
///
/// **The load-bearing rule here is the ledger one, and it is easy to get backwards**: an ineligible item gets
/// <em>no ledger row at all</em> — not <c>flagged</c>, not <c>skipped</c>. It was never actually checked, so the
/// next capture (a pristine copy, or a future version that supports levelling) must be treated as new. Writing any
/// row would make the item look handled and quietly exclude it from ever being checked properly.
/// </summary>
public sealed record ItemEligibility(IReadOnlyList<IneligibilityDetail> Blockers)
{
    public bool IsEligible => Blockers.Count == 0;

    /// <summary>False for an ineligible item, and deliberately expressed here rather than left to each caller to
    /// remember — see the type comment for why a "skipped" row would be worse than no row.</summary>
    public bool ShouldWriteLedgerEntry => IsEligible;

    public bool Has(IneligibilityReason reason) => Blockers.Any(b => b.Reason == reason);

    /// <summary>
    /// Checks a parsed item. Reports <em>every</em> blocker rather than the first, so the user sees the whole
    /// picture in one pass instead of fixing one problem and being told about the next.
    /// </summary>
    public static ItemEligibility Check(ParsedItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var blockers = new List<IneligibilityDetail>();

        if (item.Level > 0)
            blockers.Add(new IneligibilityDetail(
                IneligibilityReason.UnsupportedLevel,
                $"'{item.Name}' is levelled (+{item.Level}). The wiki records level-0 data and this version does " +
                "not know how to scale a levelled item's stats back down, so it is skipped rather than guessed at."));

        foreach (ExaltationSlot slot in item.ExaltationSlots)
        {
            if (slot.Name is null) continue;
            if (!ItemParser.IsForeignExaltation(slot, item.Name)) continue;

            // Ornamentation is called out separately because the reason differs: it is foreign by its nature
            // rather than because a name comparison said so, and telling the user "the name doesn't match" about
            // an ornamentation would be actively misleading.
            string explanation = slot.Kind == ExaltationKind.Ornamentation
                ? $"The Ornamentation slot holds '{slot.Name}'. Ornamentation is always applied by a player, never " +
                  "native to the item, so this capture may not contain the unaltered item details (particularly No Trade vs. Attunable)."
                : $"The {slot.Kind} Exaltation slot holds '{slot.Name}', which is not this item's own exaltation, " +
                  "so the window's data includes another item's contribution.";

            blockers.Add(new IneligibilityDetail(IneligibilityReason.ForeignExaltation, explanation));
        }

        return new ItemEligibility(blockers);
    }
}
