namespace EQLWikiAssistant.Core.Items;

/// <summary>One of an item's five exaltation slots (see the plan's Augmentations/exaltations section). A slot
/// only appears in the window once its tier unlocks it, so absence from <see cref="ParsedItem.ExaltationSlots"/>
/// means "not yet unlocked," not "empty."</summary>
public enum ExaltationKind { Ornamentation, Focus, Click, Worn, Proc }

/// <summary><see cref="Name"/> is null for an empty slot, or the exalted item's name (with the trailing
/// "(Exaltation)" suffix stripped) for a filled one. Whether a filled slot is the item's own native exaltation or
/// a foreign one requires comparing <see cref="Name"/> against the item's own base name — see
/// <see cref="ItemParser.IsForeignExaltation"/> — and is deliberately not decided here, since that decision needs
/// the containing item's own (possibly separately-corrected) name.</summary>
public sealed record ExaltationSlot(ExaltationKind Kind, string? Name);
