namespace EQLWikiAssistant.Core.Items;

/// <summary>
/// An item as read from a single (non-occluded) item window. <see cref="Name"/>/<see cref="Level"/> come from the
/// content-area name (the copy least likely to be affected by the known partial-title-occlusion gap — see
/// <see cref="ItemParser"/>), not the window's title bar. <see cref="TitleContentNameMismatch"/> is the required
/// second occlusion safety net documented in the plan/CLAUDE.md: geometry (<c>WindowBoundsFinder</c>) can report
/// clean bounds while the title text itself is truncated by a small, partial occluder; this flag is how Parse
/// closes that gap. A caller must treat a set flag as "don't trust this capture," not as advisory.
/// <see cref="Stats"/> is an intentionally open, ordered label/value bag (reading order top-to-bottom,
/// left-to-right) for everything not given its own field — new field = one parser rule, no model change, per the
/// plan's "extensible field bag" design.
///
/// <see cref="Lore"/> is populated only from a capture of the **Lore tab**, which is a completely different view:
/// it has no content-area name row and no stat block, just the lore prose, so a Lore capture yields a
/// <see cref="ParsedItem"/> with <see cref="Name"/> (taken from the title bar, the only place it appears) and
/// <see cref="Lore"/> set and everything else empty. The two captures are combined later by the two-capture lore
/// flow. Lore content varies: for some items it's genuinely descriptive prose, for others just the item's own
/// name again.
///
/// <see cref="Slots"/> is a list because an item can be equippable in more than one slot: weapons commonly show
/// "Primary Secondary" or "Range Ammo", and odder combinations exist (a shield usable in Secondary or Back, an
/// item usable in Chest or Waist). The game separates them with spaces on a single unlabeled row. Empty for items
/// with no slot at all, such as consumables and containers.
/// </summary>
public sealed record ParsedItem(
    string Name,
    int Level,
    bool TitleContentNameMismatch,
    IReadOnlyList<string> Flags,
    IReadOnlyList<string> Classes,
    IReadOnlyList<string> Races,
    IReadOnlyList<string> Slots,
    IReadOnlyList<KeyValuePair<string, string>> Stats,
    IReadOnlyList<ExaltationSlot> ExaltationSlots,
    IReadOnlyList<EffectEntry> Effects,
    string? MerchantValue,
    string? Lore,
    IReadOnlyList<string> Warnings);
