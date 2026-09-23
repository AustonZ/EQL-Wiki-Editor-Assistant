namespace EQLWikiAssistant.Core.Items;

/// <summary>A "Focus Effect"/"Click Effect"/"Combat Effect"/"Proc Effect"/"Charge Effect" line (<see cref="Kind"/>
/// is the label with " Effect" stripped, e.g. "Focus", "Click"). <see cref="Description"/> is the raw remaining
/// text (the effect/spell name plus any trailing modifier like "(Must Equip)" or "(Req Level 50)"), kept unparsed
/// for now — v1 doesn't need to split those apart. <see cref="Modifiers"/> holds sub-lines that follow this
/// specific effect in the window (e.g. "Cast Time: 4.0 seconds", "Required Level: 45", "Cooldown: 240 seconds") —
/// confirmed by real captures to always immediately follow their own effect line and precede the next one, so
/// <see cref="ItemParser"/> attaches each such line to the most recently seen effect rather than dumping it into
/// <see cref="ParsedItem.Stats"/> undifferentiated.</summary>
public sealed record EffectEntry(string Kind, string Description, IReadOnlyList<KeyValuePair<string, string>> Modifiers);
