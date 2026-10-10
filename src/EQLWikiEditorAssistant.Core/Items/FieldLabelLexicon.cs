using EQLWikiEditorAssistant.Core.Text;

namespace EQLWikiEditorAssistant.Core.Items;

/// <summary>
/// Small fixed vocabulary of known item-window field labels, matched with a small edit-distance tolerance
/// (`Omamentation` still finds `Ornamentation`). This is game-domain vocabulary (spells/monsters/quests will have
/// their own label sets later), so it lives alongside the Item parser, and stays separate from the wiki mapping
/// config (that's user-editable MediaWiki vocabulary that evolves with the wiki; this is stable, game-UI
/// vocabulary). Deliberately small — grows only as real windows demand.
///
/// The tolerance predates the glyph reader, which reads labels exactly; it was built for a text-recognition model's
/// misreads, and is kept because it costs nothing on exact text.
/// </summary>
public static class FieldLabelLexicon
{
    /// <summary>Every field label the item-window parser currently knows about, canonical spelling.</summary>
    public static readonly IReadOnlyList<string> KnownLabels =
    [
        "Ornamentation", "Focus Exaltation", "Click Exaltation", "Worn Exaltation", "Proc Exaltation",
        "Size", "Weight", "AC", "HP", "Mana", "End",
        "Strength", "Stamina", "Agility", "Dexterity", "Wisdom", "Intelligence", "Charisma",
        "SV. Magic", "SV. Fire", "SV. Cold", "SV. Poison", "SV. Disease", "SV. Void",
        "Base Dmg", "Delay", "Skill", "Dmg Bon", "Ratio", "Range",
        "Value",
        "Focus Effect", "Click Effect", "Combat Effect", "Proc Effect", "Charge Effect",
        "Worn Effect", "Consumable Effect",
        "Container", "Type", "Accuracy", "Mana Regen",
        "Cast Time", "Cooldown", "Cooldown Group", "Required Level", "Charges",
    ];

    /// <summary>Corrects a field-label token to its closest known label, if one is close enough; returns
    /// the input unchanged (trimmed) if nothing matches closely enough to be confident — an unrecognized label
    /// isn't necessarily an error, it may just be a field not yet catalogued here.</summary>
    public static string Correct(string label)
    {
        string candidate = label.Trim();
        if (candidate.Length == 0) return candidate;

        string? best = null;
        int bestDistance = int.MaxValue;
        foreach (string known in KnownLabels)
        {
            int distance = EditDistance.Levenshtein(candidate.ToUpperInvariant(), known.ToUpperInvariant());
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = known;
            }
        }

        return best is not null && bestDistance <= ToleranceFor(best) ? best : candidate;
    }

    /// <summary>True if <paramref name="text"/> corrects to a label this lexicon knows. Used to recognize a
    /// label without its trailing ':', which would otherwise leave the label and its value stranded as two unparsed
    /// fragments.</summary>
    public static bool IsKnownLabel(string text) =>
        KnownLabels.Contains(Correct(text), StringComparer.OrdinalIgnoreCase);

    /// <summary>True if <paramref name="text"/>'s prefix (of the same length as <paramref name="label"/>) is a
    /// close match for <paramref name="label"/> — used to recognize a known label at the start of a line without
    /// requiring a delimiter, since the game writes some labels without a colon (e.g. "Focus Effect Reagent
    /// Conservation II").</summary>
    public static bool StartsWithLabel(string text, string label)
    {
        string t = text.TrimStart();
        string prefix = t.Length >= label.Length ? t[..label.Length] : t;
        return EditDistance.Levenshtein(prefix.ToUpperInvariant(), label.ToUpperInvariant()) <= ToleranceFor(label);
    }

    /// <summary>Tries each of <paramref name="candidateLabels"/> as a fuzzy prefix of <paramref name="text"/>,
    /// returning the best match (if any) and the remaining text after the label and an optional following
    /// ':'/'.' and whitespace are stripped.</summary>
    public static bool TryMatchPrefixLabel(string text, IReadOnlyList<string> candidateLabels, out string matchedLabel, out string remainder)
    {
        string t = text.TrimStart();
        string? best = null;
        int bestDistance = int.MaxValue;
        foreach (string label in candidateLabels)
        {
            if (t.Length < label.Length - ToleranceFor(label)) continue; // too short to plausibly hold this label
            string prefix = t.Length >= label.Length ? t[..label.Length] : t;
            int distance = EditDistance.Levenshtein(prefix.ToUpperInvariant(), label.ToUpperInvariant());
            if (distance <= ToleranceFor(label) && distance < bestDistance)
            {
                bestDistance = distance;
                best = label;
            }
        }

        if (best is null)
        {
            matchedLabel = "";
            remainder = "";
            return false;
        }

        matchedLabel = best;

        // Find the real label/value separator near the expected boundary rather than assuming the label
        // occupies exactly best.Length characters — a stray leading character (".Class:" instead of "Class:")
        // still matches fuzzily but shifts where the label actually ends in the text, so
        // slicing at a fixed offset previously corrupted the remainder (e.g. "s: WAR PAL..." instead of
        // "WAR PAL..."). Search a small window straddling the expected boundary (not from index 0 — a leading
        // junk character like that stray '.' is itself a candidate separator, and would otherwise be found
        // first) for the actual ':'/'.' and cut there; fall back to the fixed-length slice when there's no
        // separator nearby at all (e.g. effect lines, which sometimes have no colon —
        // "Focus Effect Reagent Conservation II").
        int searchStart = Math.Max(0, best.Length - 2);
        int searchEnd = Math.Min(t.Length, best.Length + 3);
        int sepIdx = searchEnd > searchStart ? t[searchStart..searchEnd].IndexOfAny([':', '.']) : -1;
        string rest = sepIdx >= 0
            ? t[(searchStart + sepIdx + 1)..]
            : (t.Length > best.Length ? t[best.Length..] : "");
        remainder = rest.TrimStart();
        return true;
    }

    // Edit-distance budget scaled by label length: tight for short labels (a generous budget on e.g. "AC" or
    // "End" would make almost anything match), looser for longer ones ("Ornamentation" -> "Omamentation" is 2
    // edits; "Worn" -> "Wom" is 2, "Womn" is 1).
    private static int ToleranceFor(string label) => label.Length switch
    {
        <= 3 => 0,
        <= 6 => 2,
        <= 10 => 2,
        _ => 3,
    };
}
