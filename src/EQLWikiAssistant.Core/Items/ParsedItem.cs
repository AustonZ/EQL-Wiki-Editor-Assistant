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
/// </summary>
public sealed record ParsedItem(
    string Name,
    int Level,
    bool TitleContentNameMismatch,
    IReadOnlyList<string> Flags,
    IReadOnlyList<string> Classes,
    IReadOnlyList<string> Races,
    string? Slot,
    IReadOnlyList<KeyValuePair<string, string>> Stats,
    IReadOnlyList<ExaltationSlot> ExaltationSlots,
    IReadOnlyList<EffectEntry> Effects,
    string? MerchantValue,
    IReadOnlyList<string> Warnings);
