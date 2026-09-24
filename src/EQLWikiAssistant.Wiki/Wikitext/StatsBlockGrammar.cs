namespace EQLWikiAssistant.Wiki.Wikitext;

/// <summary>
/// Splits a statsblock line into <c>Label: Value</c> pairs and leading unlabelled flags.
///
/// Every rule here was measured against 2,514 statsblock lines from 414 real item pages (eqlwiki.com, 2026-09-24),
/// not taken from the template blueprint. The blueprint describes an idealized block; the pages are a mix of a
/// Project1999-era import and hand edits, and the grammar has to read both.
///
/// **A colon only starts a field at top level.** <c>Effect: [[Rune IV]] (Any Slot, Casting Time: Instant, Level
/// 40)</c> contains two more colons, both of them part of the value, and a depth-blind split cuts the line into
/// nonsense. Parentheses, wikilinks and nested templates all nest.
///
/// **A label may not contain a double space.** This is the one rule that is not obvious and the one that pays for
/// itself: without it <c>EXPENDABLE  Charges: 10</c> reads as a field labelled "EXPENDABLE  Charges", because a
/// label legitimately may contain a single space (<c>SV FIRE</c>, <c>Mount Speed</c>, <c>HP Regen</c>). Real pages
/// use a double space as the separator between two fields on one line, which makes it available as the boundary.
///
/// **Flags split on commas and double spaces only.** The two dialects on the wiki write them differently — the
/// imported pages use <c>MAGIC ITEM  LORE ITEM  NO DROP</c>, current-era pages use <c>Lore Equipped, No Trade</c>
/// — and both are covered. Three sampled pages write <c>MAGIC ITEM LORE ITEM NO TRADE</c> with single spaces,
/// which no separator rule can split without a flag vocabulary to match against; those yield one unrecognized
/// token. That is deliberate. Flag spellings are wiki-side vocabulary and belong in the mapping layer, and a
/// token nothing recognizes gets reported, which is the right outcome — inventing a split would be a silent guess
/// at the exact place the Attunable/No Trade ambiguity already demands human judgment.
/// </summary>
internal static class StatsBlockGrammar
{
    /// <summary>Longest label seen in the corpus is "Percussion Resonance" (20); the cap keeps a runaway
    /// backward scan from claiming half a line of prose as a label.</summary>
    private const int MaxLabelLength = 24;

    /// <summary>Reads every top-level <c>Label:</c> on the line. <paramref name="leading"/> receives the text
    /// before the first one — the flags fragment, empty on a normal stat line.</summary>
    public static IReadOnlyList<StatsField> ReadFields(string text, out string leading)
    {
        List<(int Start, int Colon, string Label)> labels = FindLabels(text);

        if (labels.Count == 0)
        {
            leading = text;
            return [];
        }

        leading = text[..labels[0].Start];

        var fields = new List<StatsField>(labels.Count);
        for (int i = 0; i < labels.Count; i++)
        {
            int valueStart = labels[i].Colon + 1;
            int valueEnd = i + 1 < labels.Count ? labels[i + 1].Start : text.Length;
            fields.Add(new StatsField(labels[i].Label, text[valueStart..valueEnd].Trim()));
        }

        return fields;
    }

    /// <summary>Splits an unlabelled fragment into flag tokens. See the type comment for why the separator set is
    /// exactly "comma" and "two or more spaces".</summary>
    public static IReadOnlyList<string> ReadFlags(string leading)
    {
        var flags = new List<string>();
        foreach (string chunk in leading.Split(','))
        {
            int i = 0;
            while (i < chunk.Length)
            {
                int gap = FindDoubleSpace(chunk, i);
                int end = gap < 0 ? chunk.Length : gap;
                string token = chunk[i..end].Trim();
                if (token.Length > 0) flags.Add(token);
                if (gap < 0) break;
                i = gap;
                while (i < chunk.Length && chunk[i] == ' ') i++;
            }
        }

        return flags;
    }

    private static int FindDoubleSpace(string text, int from)
    {
        for (int i = from; i + 1 < text.Length; i++)
            if (text[i] == ' ' && text[i + 1] == ' ') return i;
        return -1;
    }

    private static List<(int Start, int Colon, string Label)> FindLabels(string text)
    {
        var labels = new List<(int, int, string)>();
        int parens = 0;

        for (int i = 0; i < text.Length; i++)
        {
            if (i + 1 < text.Length && ((text[i] == '[' && text[i + 1] == '[') || (text[i] == '{' && text[i + 1] == '{')))
            {
                i = SkipNested(text, i) - 1;
                continue;
            }

            if (text[i] == '(') { parens++; continue; }
            if (text[i] == ')') { if (parens > 0) parens--; continue; }
            if (text[i] != ':' || parens > 0) continue;

            // A field's value is separated from its label by whitespace (or is empty at end of line). Requiring
            // that rejects a bare "12:30"-shaped token before the label scan even runs.
            if (i + 1 < text.Length && !char.IsWhiteSpace(text[i + 1])) continue;

            int previousColon = labels.Count > 0 ? labels[^1].Item2 : -1;
            int start = ScanLabelBackwards(text, i, previousColon + 1);
            if (start < 0) continue;

            // If claiming that label would leave the field before it with no value at all, the label took a word
            // too many: "Size: MEDIUM WT: 3.0" (a real page, single-spaced where every other page double-spaces)
            // reads as a field "MEDIUM WT" and an empty "Size". No page in the corpus has a genuinely empty value,
            // so the emptiness is the signal, and giving the word back fixes both fields.
            if (previousColon >= 0 && IsBlank(text, previousColon + 1, start))
            {
                int shrunk = DropLeadingWord(text, start, i);
                if (shrunk < 0) continue;
                start = shrunk;
            }

            labels.Add((start, i, text[start..i].Trim()));
        }

        return labels;
    }

    /// <summary>
    /// Walks back from a colon to where its label begins, or returns -1 if what precedes the colon cannot be a
    /// label. <paramref name="floor"/> stops the scan from reaching back past the previous field's colon and
    /// claiming that field's value as part of this label.
    ///
    /// Two bounds beyond the floor, both found by a failing test rather than by reasoning. A digit ends the scan,
    /// because <c>STR: +10 WIS: +10</c> separates its fields with a <em>single</em> space and nothing else marks
    /// where the value <c>+10</c> stops and the label <c>WIS</c> starts; treating digits as label characters made
    /// the whole run read as one field. And a label is at most <see cref="MaxLabelWords"/> words, which bounds the
    /// damage on a single-spaced line whose value is alphabetic (<c>Skill: 1H Slashing Atk Delay: 29</c>).
    /// </summary>
    private static int ScanLabelBackwards(string text, int colon, int floor)
    {
        int j = colon - 1;
        int words = 1;
        while (j >= floor && colon - j <= MaxLabelLength && IsLabelChar(text[j]))
        {
            if (text[j] == ' ')
            {
                // A double space separates two fields on one line, so it bounds the label — see the type comment.
                if (j - 1 >= floor && text[j - 1] == ' ') break;
                if (++words > MaxLabelWords) break;
            }
            j--;
        }

        int start = j + 1;
        while (start < colon && char.IsWhiteSpace(text[start])) start++;

        // Labels start with a letter. Anything else at this position means the colon belongs to the value.
        return start < colon && char.IsLetter(text[start]) ? start : -1;
    }

    /// <summary>
    /// Every real label in the corpus is one or two words: "SV FIRE", "Atk Delay", "Weight Reduction",
    /// "Percussion Resonance", "Size Capacity", "HP Regen", "Mana Cost", "Fire DMG", "Mount Speed".
    ///
    /// Allowing three was tried, on the reasoning that it left room for a label the sample happened not to contain.
    /// Measuring said otherwise: every three-word label the grammar then produced was a mis-split of a
    /// single-spaced line — <c>Skill: Archery Atk Delay: 0</c> read as "Archery Atk Delay", and likewise "Blunt Atk
    /// Delay" and "Slashing Atk Delay". Two words produced no mis-splits and lost no real label across 662 sampled
    /// pages.
    /// </summary>
    private const int MaxLabelWords = 2;

    private static bool IsBlank(string text, int start, int end)
    {
        for (int i = start; i < end; i++)
            if (!char.IsWhiteSpace(text[i])) return false;
        return true;
    }

    /// <summary>Moves a label's start past its first word, or returns -1 if that leaves nothing that can be a
    /// label.</summary>
    private static int DropLeadingWord(string text, int start, int colon)
    {
        int space = text.IndexOf(' ', start);
        if (space < 0 || space >= colon) return -1;

        while (space < colon && text[space] == ' ') space++;
        return space < colon && char.IsLetter(text[space]) ? space : -1;
    }

    /// <summary>Letters, spaces and the punctuation labels actually use. Deliberately excludes digits, <c>+</c> and
    /// <c>/</c>: those appear in <em>values</em> (<c>+10</c>, <c>0.4</c>, <c>61/128</c>, <c>1H Slashing</c>) and
    /// including them let the backward scan run straight through a value into the field before it.</summary>
    private static bool IsLabelChar(char c) => char.IsLetter(c) || c is ' ' or '.' or '\'' or '-';

    /// <summary>Offset just past a matched <c>[[...]]</c> or <c>{{...}}</c>; just past the opener if it never
    /// closes, so a malformed line degrades to ordinary scanning rather than swallowing the rest of it.</summary>
    private static int SkipNested(string text, int open)
    {
        char closer = text[open] == '[' ? ']' : '}';
        int depth = 0;
        for (int i = open; i + 1 < text.Length; i++)
        {
            if (text[i] == text[open] && text[i + 1] == text[open]) { depth++; i++; }
            else if (text[i] == closer && text[i + 1] == closer) { depth--; i++; if (depth == 0) return i + 1; }
        }
        return open + 2;
    }
}
