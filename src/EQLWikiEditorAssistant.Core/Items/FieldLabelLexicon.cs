namespace EQLWikiEditorAssistant.Core.Items;

/// <summary>
/// Small fixed vocabulary of known item-window field labels, spelled exactly as the game draws them. This is
/// game-domain vocabulary (spells/monsters/quests will have their own label sets later), so it lives alongside the
/// Item parser, and stays separate from the wiki mapping config (that's MediaWiki vocabulary that evolves with the
/// wiki; this is stable, game-UI vocabulary). Deliberately small — grows only as real windows demand.
///
/// **Matched exactly** (2026-10-10). It used to tolerate a character or two, for a text-recognition model's misreads
/// (`Omamentation`); the glyph reader reads the game's text exactly, and a label that does not match is then a label
/// this list lacks, which should reach the user as an unparsed line rather than be bent into a near neighbor.
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

    /// <summary>True if <paramref name="text"/> is a label this lexicon knows. Used to recognize a label without its
    /// trailing ':', which would otherwise leave the label and its value stranded as two unparsed fragments.</summary>
    public static bool IsKnownLabel(string text) => KnownLabels.Contains(text.Trim(), StringComparer.Ordinal);

    /// <summary>Finds which of <paramref name="candidateLabels"/> <paramref name="text"/> starts with, returning it and
    /// the text after it with one following ':'/'.' and the whitespace stripped. The label must end at a word
    /// boundary, so "Worn" does not match "Wornout". The game writes some labels without a colon ("Focus Effect
    /// Reagent Conservation II"), so none is required. The longest label wins, so "Cooldown Group" is not read as
    /// "Cooldown".</summary>
    public static bool TryMatchPrefixLabel(string text, IReadOnlyList<string> candidateLabels, out string matchedLabel, out string remainder)
    {
        string t = text.TrimStart();
        string? best = null;
        foreach (string label in candidateLabels)
        {
            if (!t.StartsWith(label, StringComparison.Ordinal)) continue;
            if (t.Length > label.Length && t[label.Length] is not (':' or '.' or ' ')) continue;
            if (best is null || label.Length > best.Length) best = label;
        }

        if (best is null)
        {
            matchedLabel = "";
            remainder = "";
            return false;
        }

        matchedLabel = best;
        string rest = t[best.Length..];
        if (rest.StartsWith(':') || rest.StartsWith('.')) rest = rest[1..];
        remainder = rest.TrimStart();
        return true;
    }
}
