namespace EQLWikiAssistant.Core.Items;

/// <summary>A "Focus Effect"/"Click Effect"/"Combat Effect"/"Proc Effect" line (<see cref="Kind"/> is the label
/// with " Effect" stripped, e.g. "Focus", "Click"). <see cref="Description"/> is the raw remaining text (the
/// effect/spell name plus any trailing modifier like "(Must Equip)" or "(Req Level 50)"), kept unparsed for now —
/// v1 doesn't need to split those apart. Sub-lines like "Cast Time: 4.0 seconds" or "Cooldown: 240 seconds" that
/// follow an effect line in the window aren't associated back to a specific effect here; they land in
/// <see cref="ParsedItem.Stats"/> like any other label/value line, which preserves the raw data losslessly even
/// though it loses the effect-to-modifier grouping — an acceptable v1 simplification since nothing downstream
/// needs that grouping yet.</summary>
public sealed record EffectEntry(string Kind, string Description);
