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
    /// <param name="linkTarget">
    /// The page the link should point at, when that is not simply the effect's name. **Preserved from the page
    /// rather than chosen** (user, 2026-10-03): `Rain Caller` links `[[Firestrike_(Effect)|Firestrike]]`, and
    /// `Firestrike` and `Firestrike (Effect)` are genuinely different spells — 422 damage and 138 mana for the
    /// player spell against 302 and 0 for the item effect, whose own page says "None; this spell is found on
    /// weapons". A capture only ever sees the *displayed* name, so the target is knowledge the page holds and the
    /// window cannot, and normalizing it away would repoint the effect at the wrong numbers with nothing visible
    /// to the reader. Null falls back to the name, which is what the tool writes when the page says nothing.
    /// </param>
    public static EffectRender Render(EffectEntry effect, WikiMapping? mapping = null, string? linkTarget = null)
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
        line.Append(WikiLabel).Append(": ").Append(Link(effect.Name, linkTarget));
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

    /// <summary>The wikilink form that carries the tooltip: the name in the display half, and in the target half
    /// too unless the page already pointed somewhere else — see <see cref="Render"/>'s linkTarget.</summary>
    public static string Link(string effectName, string? linkTarget = null)
    {
        ArgumentNullException.ThrowIfNull(effectName);
        string target = string.IsNullOrWhiteSpace(linkTarget) ? effectName : linkTarget.Trim();
        return $"[[{target}|<span class='itemeff'>{effectName}</span>]]";
    }

    /// <summary>
    /// The effect's name as an existing line *displays* it, however the line is written — the modern
    /// <c>[[X|&lt;span…&gt;X&lt;/span&gt;]]</c>, a legacy bare <c>[[X]]</c>, or a piped link to a differently-titled
    /// page (<c>[[Firestrike_(Effect)|Firestrike]]</c>, which is real). Null if there is no link at all.
    ///
    /// Reading the name is what separates "the same effect, written the old way" from "a different effect" — the
    /// first is a formatting correction, the second is a data change, and conflating them would be bad either way.
    ///
    /// **It is the display half, not the link target** (bug found by the user, 2026-10-03, on `Rain Caller`). This
    /// read the target, which is the one half a capture can never see: the game shows `Firestrike` while the page
    /// links `Firestrike_(Effect)`, so the effect matched nothing, was reported `MissingOnWiki`, and the editor
    /// added a *second* effect line beside the one already there. The target is the page's own editorial choice and
    /// is read by <see cref="TryReadTarget"/>; the displayed name is the only half that answers "is this the same
    /// effect the window is showing me?".
    /// </summary>
    public static string? TryReadName(string wikiValue)
    {
        ArgumentNullException.ThrowIfNull(wikiValue);
        if (!TryReadLink(wikiValue, out string? target, out string? display)) return null;

        // No pipe means the link target *is* what the reader sees, so it is the name as well.
        string name = StripMarkup(display ?? target!);
        return name.Length == 0 ? null : name;
    }

    /// <summary>
    /// The page an existing line links to, verbatim as the page wrote it (underscores included). Null if there is
    /// no link.
    ///
    /// Kept separately from <see cref="TryReadName"/> because it is preserved rather than compared: see
    /// <see cref="Render"/>'s linkTarget for why the tool must not normalize it to the effect's name.
    /// </summary>
    public static string? TryReadTarget(string wikiValue)
    {
        ArgumentNullException.ThrowIfNull(wikiValue);
        if (!TryReadLink(wikiValue, out string? target, out _)) return null;
        return target!.Length == 0 ? null : target;
    }

    /// <summary>
    /// Whether a link target names a page other than the effect itself — a disambiguation somebody chose on
    /// purpose, which the tool keeps and reports rather than rewrites.
    ///
    /// Underscores are spaces in a MediaWiki title, so <c>Firestrike_(Effect)</c> and <c>Firestrike (Effect)</c> are
    /// the same page and neither counts as pointing elsewhere than the other.
    /// </summary>
    public static bool PointsElsewhere(string? linkTarget, string effectName)
    {
        ArgumentNullException.ThrowIfNull(effectName);
        if (string.IsNullOrWhiteSpace(linkTarget)) return false;
        return !string.Equals(
            linkTarget.Replace('_', ' ').Trim(), effectName.Replace('_', ' ').Trim(),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Splits the first wikilink in a value into its target and its display half (null when unpiped).
    /// Tolerant by contract, like the rest of the wikitext layer: no link, or an unterminated one, is false rather
    /// than an exception or a guess.</summary>
    private static bool TryReadLink(string wikiValue, out string? target, out string? display)
    {
        target = display = null;

        int open = wikiValue.IndexOf("[[", StringComparison.Ordinal);
        if (open < 0) return false;
        int close = wikiValue.IndexOf("]]", open + 2, StringComparison.Ordinal);
        if (close < 0) return false;

        string inner = wikiValue[(open + 2)..close];
        int pipe = inner.IndexOf('|');
        target = (pipe < 0 ? inner : inner[..pipe]).Trim();
        display = pipe < 0 ? null : inner[(pipe + 1)..].Trim();
        return true;
    }

    /// <summary>The display half without its markup, so the tooltip span's own name reads out as the plain name.
    /// </summary>
    private static string StripMarkup(string text) => MarkupTag.Replace(text, "").Trim();

    private static readonly System.Text.RegularExpressions.Regex MarkupTag =
        new("<[^>]*>", System.Text.RegularExpressions.RegexOptions.Compiled);

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
