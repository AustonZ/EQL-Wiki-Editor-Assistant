namespace EQLWikiAssistant.Core.Items;

/// <summary>
/// A "Focus Effect"/"Click Effect"/"Combat Effect"/"Proc Effect"/"Charge Effect"/"Worn Effect"/"Consumable
/// Effect" line. <see cref="Kind"/> is the label with " Effect" stripped, e.g. "Focus", "Click".
///
/// <see cref="Name"/> is the effect/spell name on its own — the part the game draws in magenta, and the only part
/// that identifies the effect (it's what a wiki lookup keys on and what gets wikilinked). <see cref="Conditions"/>
/// holds the parenthesised qualifiers that follow it, e.g. "Must Equip" or "Can Equip". These are kept separate
/// because they are genuinely different pieces of information: "Rune IV" is the effect, "(Must Equip)" is a rule
/// about when it applies.
///
/// <see cref="Modifiers"/> holds the sub-lines that follow this specific effect ("Cast Time: 4.0 seconds",
/// "Cooldown: 240 seconds", "Required Level: 45"), which real captures confirm always sit between their own
/// effect line and the next one — so <see cref="ItemParser"/> attaches each to the most recently seen effect.
///
/// **A required level reaches us two different ways** and is normalized to one: click effects put it on its own
/// sub-line ("Required Level: 40"), while proc effects fold it into the parenthetical ("Siphon (Req Level 50)").
/// Both end up in <see cref="Modifiers"/> under "Required Level", so a consumer never has to know which style the
/// game happened to use. Only that one qualifier is hoisted; anything else parenthesised stays a condition.
/// </summary>
public sealed record EffectEntry(
    string Kind,
    string Name,
    IReadOnlyList<string> Conditions,
    IReadOnlyList<KeyValuePair<string, string>> Modifiers);
