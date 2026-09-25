using System.Text;
using EQLWikiAssistant.Core.Items;
using EQLWikiAssistant.Wiki.Mapping;

namespace EQLWikiAssistant.Wiki.Wikitext;

/// <summary>A rendered effect line, or the reason one could not be rendered.</summary>
public sealed record EffectRender(string? Line, IReadOnlyList<string> Unsupported)
{
    /// <summary>True when every part of the effect had a wiki representation. A false here means the line must not
    /// be written — a partial effect line silently drops real game data.</summary>
    public bool IsComplete => Line is not null && Unsupported.Count == 0;
}

/// <summary>
/// Renders an <see cref="EffectEntry"/> as the wiki's effect line, and reads an effect's name back out of an
/// existing one.
///
/// The convention, given by the user (2026-09-25) as the template documents it:
/// <code>
/// Effect: [[?|&lt;span class='itemeff'&gt;?&lt;/span&gt;]] (Combat / Clicky / Worn / Can Equip / Must Equip / Casting Time: ?) at Level ?
/// </code>
/// Both <c>?</c> in the link are the effect name; the parenthetical keeps whichever parts apply, comma-separated;
/// <c>at Level ?</c> carries the required level or is omitted.
///
/// **The `&lt;span class='itemeff'&gt;` wrapper is functional, not decorative** (user, 2026-09-25): it is what gives
/// the effect a tooltip, which a bare <c>[[Name]]</c> does not. So converting a legacy link to this form is a real
/// correction the user makes on every item they touch — **not** the kind of incidental reformatting this tool is
/// otherwise forbidden from doing. Contrast the `STR: 5` versus `STR: +5` case, which is purely cosmetic and is
/// deliberately left alone.
///
/// **Focus effects do not appear here at all.** The wiki gives them their own template parameter
/// (<c>focus_effect = Improved Vampirism III</c>), so they carry no line, no link and no parenthetical.
/// </summary>
public static class EffectLine
{
    /// <summary>The wiki label a rendered line uses, and the label its grammar parses out.</summary>
    public const string WikiLabel = "Effect";

    /// <summary>
    /// Renders one effect. Returns the parts that have no wiki representation instead of guessing at them — an
    /// effect line missing its cooldown looks complete while having quietly lost data, which is the failure mode
    /// this whole project is built to avoid.
    /// </summary>
    public static EffectRender Render(EffectEntry effect, WikiMapping? mapping = null)
    {
        ArgumentNullException.ThrowIfNull(effect);
        mapping ??= WikiMapping.Default;

        var unsupported = new List<string>();

        if (mapping.IsFocusEffect(effect.Kind))
            return new EffectRender(null, [$"'{effect.Kind}' effects belong in the focus_effect parameter, not a line."]);

        var parenthetical = new List<string>();

        // The kind token: Combat / Clicky / Worn. A kind with no token is not rendered as a bare effect, because
        // dropping it would misrepresent when the effect applies.
        if (mapping.FindEffectKind(effect.Kind) is { } kindToken)
            parenthetical.Add(kindToken);
        else
            unsupported.Add($"'{effect.Kind}' has no agreed token in the wiki's effect parenthetical.");

        // Conditions come through verbatim: "Can Equip" and "Must Equip" are the only two the game uses and the
        // template names both.
        parenthetical.AddRange(effect.Conditions);

        // Modifiers are collected first and appended in a fixed order, so the rendered line does not depend on the
        // order the window happened to list them in. Casting Time, then Cooldown, then Cooldown Group — matching
        // real pages (`Alter Plane: Sky` reads "(Any Slot/Can Equip, Casting Time: Instant, Cooldown: 120 seconds)")
        // and the user's instruction to put cooldowns at the end of the parenthetical (2026-09-25).
        string? castTime = null, cooldown = null, cooldownGroup = null, level = null;
        foreach ((string label, string value) in effect.Modifiers)
        {
            switch (label)
            {
                case "Cast Time": castTime = CastTime(value); break;
                case "Cooldown": cooldown = value; break;
                case "Cooldown Group": cooldownGroup = value; break;
                case "Required Level": level = value; break;
                default:
                    unsupported.Add($"'{label}' ({value}) has no place in the wiki's effect convention.");
                    break;
            }
        }

        if (castTime is not null) parenthetical.Add($"Casting Time: {castTime}");
        if (cooldown is not null) parenthetical.Add($"Cooldown: {cooldown}");
        if (cooldownGroup is not null) parenthetical.Add($"Cooldown Group: {cooldownGroup}");

        var line = new StringBuilder();
        line.Append(WikiLabel).Append(": ").Append(Link(effect.Name));
        if (parenthetical.Count > 0) line.Append(" (").Append(string.Join(", ", parenthetical)).Append(')');
        if (level is not null) line.Append(" at Level ").Append(level);

        return new EffectRender(line.ToString(), unsupported);
    }

    /// <summary>
    /// A cast time as the wiki writes it: a bare number or <c>Instant</c>, with no unit.
    ///
    /// The game says <c>12.0 seconds</c> and every real page says <c>Casting Time: 12.0</c> — measured across the
    /// corpus (<c>4.0</c>, <c>8.0</c>, <c>10.0</c>, <c>Instant</c>, never a unit). Found by the live corpus run,
    /// which reported `Careless Lightning` as differing when only the unit did.
    /// </summary>
    private static string CastTime(string value)
    {
        string trimmed = value.Trim();
        foreach (string unit in new[] { " seconds", " second", " sec", "s" })
            if (trimmed.EndsWith(unit, StringComparison.OrdinalIgnoreCase) &&
                // Only strip a unit that leaves a number behind — "Instant" must survive intact.
                double.TryParse(trimmed[..^unit.Length].Trim(), out _))
                return trimmed[..^unit.Length].Trim();

        return trimmed;
    }

    /// <summary>The wikilink form that carries the tooltip, with the name in both halves.</summary>
    public static string Link(string effectName)
    {
        ArgumentNullException.ThrowIfNull(effectName);
        return $"[[{effectName}|<span class='itemeff'>{effectName}</span>]]";
    }

    /// <summary>
    /// The effect name from an existing line's value, however it is written — the modern
    /// <c>[[X|&lt;span…&gt;X&lt;/span&gt;]]</c>, a legacy bare <c>[[X]]</c>, or a piped link to a differently-titled
    /// page (<c>[[Cold Awareness II (Spell)|Cold Awareness II]]</c>, which is real). Null if there is no link at all.
    ///
    /// Reading the name is what separates "the same effect, written the old way" from "a different effect" — the
    /// first is a formatting correction, the second is a data change, and conflating them would be bad either way.
    /// </summary>
    public static string? TryReadName(string wikiValue)
    {
        ArgumentNullException.ThrowIfNull(wikiValue);

        int open = wikiValue.IndexOf("[[", StringComparison.Ordinal);
        if (open < 0) return null;
        int close = wikiValue.IndexOf("]]", open + 2, StringComparison.Ordinal);
        if (close < 0) return null;

        string inner = wikiValue[(open + 2)..close];
        // The link target is before the first pipe. It is the page name, which is the effect's real identity; the
        // display half may be wrapped in markup or shortened.
        int pipe = inner.IndexOf('|');
        string target = (pipe < 0 ? inner : inner[..pipe]).Trim();
        return target.Length == 0 ? null : target;
    }

    /// <summary>Whether an existing line already uses the tooltip-bearing link form. A line that does not is worth
    /// correcting even when its name and details are right, because the tooltip is functionality rather than
    /// styling.</summary>
    public static bool HasTooltipLink(string wikiValue)
    {
        ArgumentNullException.ThrowIfNull(wikiValue);
        return wikiValue.Contains("class='itemeff'", StringComparison.Ordinal)
            || wikiValue.Contains("class=\"itemeff\"", StringComparison.Ordinal);
    }
}
