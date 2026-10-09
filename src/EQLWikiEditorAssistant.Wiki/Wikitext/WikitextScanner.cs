namespace EQLWikiEditorAssistant.Wiki.Wikitext;

/// <summary>
/// Finds template calls in a page and records where each parameter's value physically sits, so an edit can splice
/// one value and leave every other byte alone.
///
/// **This is deliberately not a wikitext parser.** It recognizes exactly the four constructs that can hide a
/// <c>|</c> or a <c>}}</c> from a naive scan — nested templates, wikilinks, HTML comments and <c>&lt;nowiki&gt;</c>
/// — and treats everything else as opaque text. That is enough to locate parameters correctly, and nothing more is
/// needed, because the tool never interprets the rest of the page: it preserves it.
///
/// **Tolerance is a requirement, not a nicety** (see the repo's "wiki data is untrusted and often malformed"
/// constraint). Item pages are human-edited and partly imported from a Project1999 fork. An unterminated
/// <c>{{</c>, a stray <c>}}</c> or a mismatched <c>[[</c> must yield "I could not find a template here" — never an
/// exception, and never a confidently wrong extent that a later edit would splice into the middle of unrelated
/// text. Every failure path in here returns nothing rather than a guess.
///
/// Measured against 414 real item pages fetched from eqlwiki.com (2026-09-24): every one uses the literal
/// <c>{{Itempage</c> spelling with named parameters only, none contains an HTML comment, a <c>&lt;nowiki&gt;</c>
/// or a wikitable. The comment/nowiki handling here is therefore defensive rather than evidence-driven — cheap,
/// and the alternative is a silent mis-splice if one ever appears.
/// </summary>
public static class WikitextScanner
{
    /// <summary>Finds the first call to <paramref name="templateName"/> at any nesting depth, or null if the page
    /// has none (or has one whose braces never close). MediaWiki normalizes the first character of a template
    /// name to upper case and treats underscores as spaces, so the comparison does the same — a page writing
    /// <c>{{itempage</c> really does transclude the same template, and missing it would make the tool report a
    /// perfectly ordinary page as having no item data.</summary>
    public static TemplateCall? FindTemplate(string wikitext, string templateName)
    {
        ArgumentNullException.ThrowIfNull(wikitext);
        ArgumentNullException.ThrowIfNull(templateName);

        for (int i = 0; i + 1 < wikitext.Length; i++)
        {
            if (wikitext[i] != '{' || wikitext[i + 1] != '{') continue;

            TemplateCall? call = TryReadTemplate(wikitext, i);
            // Not the one we want, but it parsed: advance past the opening braces only, not the whole call, so a
            // nested target (an Itempage inside a wrapper) is still reachable.
            if (call is not null && NamesMatch(call.Name, templateName)) return call;
        }

        return null;
    }

    /// <summary>All calls to <paramref name="templateName"/> in source order, outermost first. Used to find the
    /// <c>{{Item Lore}}</c> wrapper inside a <c>notes</c> value, which is the only part of that parameter the tool
    /// is allowed to machine-edit.</summary>
    public static IReadOnlyList<TemplateCall> FindTemplates(string wikitext, string templateName)
    {
        ArgumentNullException.ThrowIfNull(wikitext);
        ArgumentNullException.ThrowIfNull(templateName);

        var found = new List<TemplateCall>();
        for (int i = 0; i + 1 < wikitext.Length; i++)
        {
            if (wikitext[i] != '{' || wikitext[i + 1] != '{') continue;
            TemplateCall? call = TryReadTemplate(wikitext, i);
            if (call is not null && NamesMatch(call.Name, templateName)) found.Add(call);
        }

        return found;
    }

    /// <summary>Replaces one parameter's value in place, returning the whole page with every other byte unchanged.
    /// The original value's surrounding whitespace is <em>not</em> preserved automatically — the caller decides,
    /// because for a single-line value like <c>lucy_img_ID</c> the padding is cosmetic alignment worth keeping,
    /// while a rewritten <c>statsblock</c> carries its own leading newline.</summary>
    public static string ReplaceValue(string wikitext, TemplateParameter parameter, string newRawValue)
    {
        ArgumentNullException.ThrowIfNull(wikitext);
        ArgumentNullException.ThrowIfNull(parameter);
        ArgumentNullException.ThrowIfNull(newRawValue);

        if (parameter.ValueStart < 0 || parameter.ValueEnd > wikitext.Length)
            throw new ArgumentOutOfRangeException(nameof(parameter), "Parameter span is outside the given wikitext.");

        return string.Concat(
            wikitext.AsSpan(0, parameter.ValueStart),
            newRawValue,
            wikitext.AsSpan(parameter.ValueEnd));
    }

    /// <summary>MediaWiki normalizes a template name by trimming it, mapping underscores to spaces, collapsing
    /// runs of whitespace and upper-casing the first character; a leading <c>:</c> or <c>Template:</c> prefix is
    /// legal and names the same page.</summary>
    private static bool NamesMatch(string actual, string wanted) =>
        string.Equals(NormalizeName(actual), NormalizeName(wanted), StringComparison.Ordinal);

    internal static string NormalizeName(string name)
    {
        string trimmed = name.Replace('_', ' ').Trim();
        if (trimmed.StartsWith(':')) trimmed = trimmed[1..].TrimStart();
        if (trimmed.StartsWith("Template:", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed["Template:".Length..].TrimStart();

        trimmed = string.Join(' ', trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return trimmed.Length == 0 ? trimmed : char.ToUpperInvariant(trimmed[0]) + trimmed[1..];
    }

    /// <summary>Reads a <c>{{...}}</c> starting at <paramref name="start"/>. Returns null on anything malformed —
    /// unterminated braces, or a name containing something a template name cannot contain.</summary>
    private static TemplateCall? TryReadTemplate(string text, int start)
    {
        int i = start + 2;
        int nameStart = i;
        int nameEnd = -1;
        int segmentStart = -1;
        int positional = 0;
        var parameters = new List<TemplateParameter>();

        while (i < text.Length)
        {
            int skipped = SkipOpaque(text, i);
            if (skipped > i) { i = skipped; continue; }

            if (text[i] == '}' && i + 1 < text.Length && text[i + 1] == '}')
            {
                if (nameEnd < 0) nameEnd = i;
                else AddParameter(text, segmentStart, i, parameters, ref positional);

                // Trim before inspecting. Real item pages open the call as "{{Itempage" followed by a newline and
                // then the first parameter, so the name segment legitimately ends in whitespace; checking the
                // untrimmed text rejected every multi-line template call on the wiki, which is all of them.
                string name = text[nameStart..nameEnd].Trim();

                // A template name cannot span lines or contain braces. If it does, this "{{" was never a template
                // call (a stray brace pair in prose, say), and claiming it would be worse than skipping it.
                if (name.Contains('{') || name.Contains('}') || name.Contains('\n')) return null;

                return new TemplateCall(name, start, i + 2 - start, parameters);
            }

            if (text[i] == '|')
            {
                if (nameEnd < 0) nameEnd = i;
                else AddParameter(text, segmentStart, i, parameters, ref positional);
                segmentStart = i + 1;
            }

            i++;
        }

        return null; // unterminated
    }

    /// <summary>Splits one <c>|...</c> segment into name and value, recording the value's exact span. A named
    /// parameter is <c>name=value</c> with the <c>=</c> at top level; anything else is positional. Only the
    /// <em>first</em> top-level <c>=</c> counts, so a value that contains one stays intact.</summary>
    private static void AddParameter(string text, int segmentStart, int segmentEnd, List<TemplateParameter> into, ref int positional)
    {
        if (segmentStart < 0) return;

        int equals = -1;
        for (int i = segmentStart; i < segmentEnd; i++)
        {
            int skipped = SkipOpaque(text, i);
            if (skipped > i) { i = skipped - 1; continue; }
            if (text[i] == '=') { equals = i; break; }
        }

        // segmentStart points just past this parameter's own '|', which is what removing it has to take with it.
        int pipe = segmentStart - 1;

        if (equals < 0)
        {
            into.Add(new TemplateParameter(
                Name: null,
                Index: ++positional,
                RawValue: text[segmentStart..segmentEnd],
                ValueStart: segmentStart,
                ValueLength: segmentEnd - segmentStart,
                SegmentStart: pipe,
                SegmentEnd: segmentEnd));
            return;
        }

        into.Add(new TemplateParameter(
            Name: text[segmentStart..equals].Trim(),
            Index: null,
            RawValue: text[(equals + 1)..segmentEnd],
            ValueStart: equals + 1,
            ValueLength: segmentEnd - equals - 1,
            SegmentStart: pipe,
            SegmentEnd: segmentEnd));
    }

    /// <summary>If <paramref name="i"/> starts a construct whose contents must not be scanned for <c>|</c> or
    /// <c>}}</c>, returns the offset just past it; otherwise returns <paramref name="i"/> unchanged. An
    /// <em>unterminated</em> construct also returns unchanged, so a malformed page degrades to ordinary scanning
    /// (and then, most likely, to "no template found") rather than swallowing the rest of the page.</summary>
    private static int SkipOpaque(string text, int i)
    {
        if (i + 1 < text.Length && text[i] == '{' && text[i + 1] == '{')
        {
            int depth = 0;
            for (int j = i; j + 1 < text.Length; j++)
            {
                if (text[j] == '{' && text[j + 1] == '{') { depth++; j++; }
                else if (text[j] == '}' && text[j + 1] == '}') { depth--; j++; if (depth == 0) return j + 1; }
            }
            return i;
        }

        if (i + 1 < text.Length && text[i] == '[' && text[i + 1] == '[')
        {
            int close = text.IndexOf("]]", i + 2, StringComparison.Ordinal);
            if (close < 0) return i;
            // A wikilink cannot span a blank line. Without this an unclosed "[[" would hide every "|" to the end
            // of the page — including the parameters being looked for.
            int paragraph = text.IndexOf("\n\n", i + 2, StringComparison.Ordinal);
            return paragraph >= 0 && paragraph < close ? i : close + 2;
        }

        if (string.CompareOrdinal(text, i, "<!--", 0, 4) == 0)
        {
            int close = text.IndexOf("-->", i + 4, StringComparison.Ordinal);
            return close < 0 ? i : close + 3;
        }

        if (string.CompareOrdinal(text, i, "<nowiki>", 0, 8) == 0)
        {
            int close = text.IndexOf("</nowiki>", i + 8, StringComparison.Ordinal);
            return close < 0 ? i : close + 9;
        }

        return i;
    }
}
