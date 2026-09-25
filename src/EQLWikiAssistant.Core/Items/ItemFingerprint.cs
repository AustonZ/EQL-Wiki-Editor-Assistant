using System.Security.Cryptography;
using System.Text;
using EQLWikiAssistant.Core.Icons;

namespace EQLWikiAssistant.Core.Items;

/// <summary>
/// A hash of everything about a captured item that the tool would write to the wiki.
///
/// **Its only job is to answer "is this the same item data I checked last time?"** — so it must cover exactly what
/// an edit depends on, and nothing else. Too little and a real change slips past as "already checked"; too much
/// and the ledger invalidates itself constantly for differences nobody cares about.
///
/// **The captured level is part of it**, even though only `+0` items are processed today. A `+7` capture is
/// ineligible and writes no ledger row at all, so it never reaches here — but including the level means that if a
/// future version *does* process levelled items, a `+0` entry and a `+7` entry can never be confused for one
/// another.
///
/// **Parser warnings are deliberately excluded.** They quote OCR fragments and churn with every tuning change,
/// which is the same reason the accuracy corpus stores warning counts rather than warning text. A fingerprint that
/// changed whenever a warning's wording changed would expire every ledger row on every release.
/// </summary>
public static class ItemFingerprint
{
    /// <summary>
    /// Fingerprints a parsed item, optionally including its icon and lore.
    /// </summary>
    /// <param name="icon">The captured icon, when one could be read. Included because an item whose artwork changed
    /// is worth re-checking, and because the icon comparison is one of the things a check concludes.</param>
    /// <param name="lore">Lore from the second capture of the two-capture flow, when there is one. A Description
    /// capture alone does not have it, and the absence must not look like a change.</param>
    public static string Compute(ParsedItem item, IconFingerprint? icon = null, string? lore = null)
    {
        ArgumentNullException.ThrowIfNull(item);

        var text = new StringBuilder();

        // Field order is fixed here rather than taken from the item, so two captures that differ only in the order
        // the parser happened to emit stats never look like different items.
        text.Append("name=").Append(item.Name).Append('\n');
        text.Append("level=").Append(item.Level).Append('\n');
        Append(text, "flags", item.Flags);
        Append(text, "classes", item.Classes);
        Append(text, "races", item.Races);
        Append(text, "slots", item.Slots);

        text.Append("stats=");
        foreach ((string label, string value) in item.Stats.OrderBy(s => s.Key, StringComparer.Ordinal))
            text.Append(label).Append(':').Append(value).Append(';');
        text.Append('\n');

        text.Append("exaltations=");
        foreach (ExaltationSlot slot in item.ExaltationSlots.OrderBy(s => s.Kind))
            text.Append(slot.Kind).Append(':').Append(slot.Name ?? "").Append(';');
        text.Append('\n');

        text.Append("effects=");
        foreach (EffectEntry effect in item.Effects.OrderBy(e => e.Kind, StringComparer.Ordinal).ThenBy(e => e.Name, StringComparer.Ordinal))
        {
            text.Append(effect.Kind).Append(':').Append(effect.Name).Append('(');
            foreach (string condition in effect.Conditions.Order(StringComparer.Ordinal)) text.Append(condition).Append(',');
            foreach ((string label, string value) in effect.Modifiers.OrderBy(m => m.Key, StringComparer.Ordinal))
                text.Append(label).Append('=').Append(value).Append(',');
            text.Append(");");
        }
        text.Append('\n');

        text.Append("merchant=").Append(item.MerchantValue ?? "").Append('\n');
        text.Append("lore=").Append(lore ?? item.Lore ?? "").Append('\n');

        // The icon's signature rather than its match verdict: the verdict depends on what the wiki currently says,
        // and this has to describe the capture alone.
        text.Append("icon=");
        if (icon is not null) text.Append(Convert.ToHexString(icon.Signature));
        text.Append('\n');

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private static void Append(StringBuilder text, string name, IReadOnlyList<string> values)
    {
        text.Append(name).Append('=');
        // Sorted, because these are sets: an item allowed to WAR and CLR is the same item however the window
        // happened to order them.
        foreach (string value in values.Order(StringComparer.Ordinal)) text.Append(value).Append(';');
        text.Append('\n');
    }
}
