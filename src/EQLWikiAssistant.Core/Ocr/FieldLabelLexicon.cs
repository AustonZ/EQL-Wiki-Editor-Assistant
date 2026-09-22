using EQLWikiAssistant.Core.Text;

namespace EQLWikiAssistant.Core.Ocr;

/// <summary>
/// Small fixed vocabulary of known item-window field labels, with edit-distance correction for the field-label
/// OCR errors confirmed real in testing (see the plan/CLAUDE.md milestone 1 writeup): the "rn"-ish confusion
/// (`Ornamentation` -&gt; `Omamentation`, `Worn` -&gt; `Wom`/`Womn`) persisted across every engine and scale
/// tested. This is game-domain vocabulary, not an OCR-engine concern (spells/monsters/quests will have their own
/// label sets later), so it lives in Core alongside the Item parser rather than inside an <see cref="IOcrEngine"/>
/// implementation, and stays separate from the wiki mapping config (that's user-editable MediaWiki vocabulary
/// that evolves with the wiki; this is stable, game-UI vocabulary). Deliberately small — grows only as real
/// evidence demands, per the same philosophy that shaped the OCR error census.
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
        "Focus Effect", "Click Effect", "Combat Effect", "Proc Effect",
        "Cast Time", "Cooldown", "Required Level", "Charges",
    ];

    /// <summary>Corrects an OCR'd field-label token to its closest known label, if one is close enough; returns
    /// the input unchanged (trimmed) if nothing matches closely enough to be confident — an unrecognized label
    /// isn't necessarily an error, it may just be a field not yet catalogued here.</summary>
    public static string Correct(string ocrLabel)
    {
        string candidate = ocrLabel.Trim();
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

    /// <summary>True if <paramref name="text"/>'s prefix (of the same length as <paramref name="label"/>) is a
    /// close match for <paramref name="label"/> — used to recognize a known label at the start of a line without
    /// requiring a delimiter, since OCR unpredictably drops the colon after some labels (e.g. "Charge Effect" vs
    /// "Charge Effect:").</summary>
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
        string rest = t.Length > best.Length ? t[best.Length..] : "";
        rest = rest.TrimStart();
        if (rest.StartsWith(':') || rest.StartsWith('.')) rest = rest[1..].TrimStart();
        remainder = rest;
        return true;
    }

    // Edit-distance budget scaled by label length: tight for short labels (a generous budget on e.g. "AC" or
    // "End" would make almost anything match), looser for longer ones, matching the real corruptions confirmed
    // by testing ("Ornamentation" -> "Omamentation" is 2 edits; "Worn" -> "Wom" is 2, "Womn" is 1).
    private static int ToleranceFor(string label) => label.Length switch
    {
        <= 3 => 0,
        <= 6 => 2,
        <= 10 => 2,
        _ => 3,
    };
}
