using System.Text;
using EQLWikiAssistant.Wiki.Mapping;
using EQLWikiAssistant.Wiki.Wikitext;

namespace EQLWikiAssistant.Wiki.Formatting;

/// <summary>
/// What formatting a page produced: the new text, what it deliberately left alone, and — if it could not prove the
/// result says the same thing — why it gave up.
/// </summary>
public sealed record PrettifyResult(
    string Original,
    string Formatted,
    IReadOnlyList<string> Notes,
    IReadOnlyList<string> Refusals)
{
    public bool Changed => !string.Equals(Original, Formatted, StringComparison.Ordinal);

    /// <summary>False when the verification pass found the formatted text saying something different, in which case
    /// <see cref="Formatted"/> is the original, untouched.</summary>
    public bool IsSafe => Refusals.Count == 0;
}

/// <summary>
/// The formatting pass: lays an item page's <c>{{Itempage}}</c> call out the way the Item Page Blueprint says,
/// changing nothing about what it says.
///
/// **It is a separate edit from the data pass and runs after it** (user, 2026-09-25): *"A single 'automatically
/// reformatted' edit with no actual data changes is much easier to work with when reviewing diff history."* Running
/// it first would not stay pretty, because the data edit adds new content as unpositioned lines and the page would
/// need formatting again — see CLAUDE.md for why that order is settled rather than arbitrary.
///
/// **The whole design rests on one property: it must be provably content-preserving.** Reordering lines and
/// parameters is exactly the kind of edit that can quietly lose a value, and this one runs unattended on a public
/// wiki. So every format is verified by re-parsing the result and comparing it field by field against the original;
/// anything that does not match means the original comes back untouched with a refusal saying what differed. A
/// formatter that cannot prove it preserved the content does not get to write.
///
/// **Two deliberate limits, both of which keep that proof cheap:**
/// - **It only touches the inside of the template call.** Everything else on the page — the era banner, an in-world
///   screenshot, categories, stray prose — survives byte for byte, because the formatter never looks at it. The era
///   banner is the data pass's job (it is a statement about the item, not about layout).
/// - **It will not reorder a statsblock it does not completely understand.** An unparsed line or a blank line in
///   the middle of a block means the tool is guessing about something, so that block keeps its existing line order
///   and only its spacing is normalized. Rearranging content nobody could parse is how a formatter loses data.
/// </summary>
public static class ItemPagePrettifier
{
    /// <summary>Two spaces between fields sharing a line, which is what the blueprint's own examples use.</summary>
    private const string FieldSeparator = "  ";

    public static PrettifyResult Format(string wikitext, WikiMapping? mapping = null)
    {
        ArgumentNullException.ThrowIfNull(wikitext);
        mapping ??= WikiMapping.Default;

        ItemPageDocument? page = ItemPageDocument.Parse(wikitext);
        if (page is null)
            return new PrettifyResult(wikitext, wikitext, [], ["This is not an item page, so there is nothing to lay out."]);

        var notes = new List<string>();
        string formatted = FormatTemplate(wikitext, page, mapping, notes);

        // The verification pass. Everything above is a rearrangement, and a rearrangement that drops a value would
        // be invisible in review — so the result only ships if re-parsing it says the same thing.
        IReadOnlyList<string> refusals = Verify(page, formatted, mapping);
        return refusals.Count > 0
            ? new PrettifyResult(wikitext, wikitext, notes, refusals)
            : new PrettifyResult(wikitext, formatted, notes, []);
    }

    private static string FormatTemplate(
        string wikitext, ItemPageDocument page, WikiMapping mapping, List<string> notes)
    {
        TemplateCall template = page.Template;

        // Ordered by the blueprint; anything the blueprint does not name keeps its relative position afterwards,
        // because an unrecognized parameter is somebody's content and sorting it somewhere nobody chose is a
        // judgement this codebase consistently declines to make.
        List<TemplateParameter> ordered =
        [
            .. template.Parameters
                .Select((parameter, position) => (parameter, position))
                .Where(p => p.parameter.Name is not null)
                .OrderBy(p => OrderOf(p.parameter.Name!, mapping.ParameterOrder))
                .ThenBy(p => p.position)
                .Select(p => p.parameter),
        ];

        List<TemplateParameter> positional = [.. template.Parameters.Where(p => p.Name is null)];
        if (positional.Count > 0)
            notes.Add($"{positional.Count} positional parameter(s) were left where they were — only named " +
                      "parameters have a documented order.");

        int width = ordered.Count == 0 ? 0 : ordered.Max(p => p.Name!.Length);

        var builder = new StringBuilder();
        builder.Append("{{").Append(template.Name).Append('\n');

        foreach (TemplateParameter parameter in positional)
            builder.Append('|').Append(parameter.RawValue.Trim()).Append('\n');

        foreach (TemplateParameter parameter in ordered)
        {
            bool isStatsBlock = string.Equals(parameter.Name, mapping.StatsBlockParameter, StringComparison.Ordinal);
            string value = isStatsBlock ? FormatStatsBlock(parameter.Value, mapping, notes) : parameter.Value;

            builder.Append('|').Append(parameter.Name!.PadRight(width)).Append(" = ");

            // The statsblock always starts on its own line, even when it is currently one line long. Every real page
            // writes it that way, and a block that sat on the `=` line would jump onto its own the moment the data
            // pass added a second line — which is churn the formatting commit exists to prevent.
            if (isStatsBlock && value.Length > 0) builder.Append('\n').Append(value).Append('\n');
            else if (NeedsOwnLine(value)) builder.Append('\n').Append(value).Append('\n');
            else builder.Append(value).Append('\n');
        }

        builder.Append("}}");

        return wikitext[..template.Start] + builder + wikitext[template.End..];
    }

    /// <summary>
    /// Whether a value has to start on a line of its own rather than sitting after the <c>=</c>.
    ///
    /// **Multi-line is the obvious case; wiki markup that is only meaningful at the start of a line is the one that
    /// bites.** A <c>*</c> makes a bullet only when it begins a line — written inline it renders as a literal
    /// asterisk — so moving a one-line <c>|relatedquests = * [[Some Quest]]</c> onto the parameter's line silently
    /// turns a list into prose. The *content* is untouched, which is exactly why the verification pass cannot see
    /// it: this is a rendering change, and the only defence is not making it.
    /// </summary>
    private static bool NeedsOwnLine(string value) =>
        value.Contains('\n') ||
        (value.Length > 0 && LineSensitiveStarts.Contains(value[0])) ||
        value.StartsWith("{|", StringComparison.Ordinal);

    /// <summary>Characters MediaWiki only treats as markup at the start of a line: list bullets, numbered items,
    /// indents and definitions, and headings. A table's <c>{|</c> is checked separately, because a bare <c>{</c>
    /// starts an ordinary template call — <c>{{Item Lore|...}}</c> is perfectly happy inline.</summary>
    private static readonly char[] LineSensitiveStarts = ['*', '#', ':', ';', '='];

    private static int OrderOf(string name, IReadOnlyList<string> order)
    {
        int index = order.ToList().IndexOf(name);
        return index < 0 ? int.MaxValue : index;
    }

    /// <summary>
    /// Lays the statsblock out in the blueprint's line order.
    ///
    /// **It refuses to reorder a block it does not completely understand**, and normalizes only the spacing instead.
    /// An unparsed line means the grammar could not read something a human wrote; a blank line mid-block is a
    /// paragraph break that may be doing visible work. Moving either would be the formatter guessing, and this pass
    /// exists precisely because guessing about layout is what the data pass refuses to do.
    /// </summary>
    private static string FormatStatsBlock(string rawValue, WikiMapping mapping, List<string> notes)
    {
        if (string.IsNullOrWhiteSpace(rawValue)) return rawValue.Trim();

        StatsBlock block = StatsBlock.Parse(rawValue);
        List<StatsBlockLine> lines = [.. block.Lines];

        // Leading and trailing blank lines are not content — MediaWiki trims a parameter's value anyway.
        while (lines.Count > 0 && lines[0].Kind == StatsLineKind.Blank) lines.RemoveAt(0);
        while (lines.Count > 0 && lines[^1].Kind == StatsLineKind.Blank) lines.RemoveAt(lines.Count - 1);

        string? blocker = FindReorderBlocker(lines);
        if (blocker is not null)
        {
            // Left **completely** untouched, not merely unordered — normalizing the spacing of a line the tool does
            // not understand is still modifying it (user, 2026-09-28). Only the value's outer whitespace goes, and
            // MediaWiki trims that anyway.
            notes.Add($"The statsblock was left exactly as it was: {blocker}");
            return rawValue.Trim();
        }

        var remaining = new List<StatsField>(lines.SelectMany(l => l.Fields));

        // **Flags are gathered from every line, not only from a flags-only one.** The blueprint's own example
        // writes `EXPENDABLE  Charges: 10<br>` — one line carrying a flag *and* a field — so reading flags only
        // from lines that are nothing but flags silently dropped the flag on 14 real pages. The verification pass
        // is what caught that, which is the entire reason it exists.
        List<string> flags = [.. lines.SelectMany(l => l.Flags)];
        var output = new List<string>();

        foreach (IReadOnlyList<string> slot in mapping.StatsBlockLineOrder)
        {
            if (slot is ["(flags)"])
            {
                if (flags.Count > 0) output.Add(string.Join(", ", flags) + "<br>");
                continue;
            }

            // Fields are emitted in the blueprint's own order within the line, not the page's.
            List<StatsField> onThisLine =
            [
                .. slot
                    .Select(label => remaining.FirstOrDefault(
                        f => string.Equals(f.Label, label, StringComparison.OrdinalIgnoreCase)))
                    .OfType<StatsField>(),
            ];

            if (onThisLine.Count == 0) continue;
            foreach (StatsField field in onThisLine) remaining.Remove(field);
            output.Add(string.Join(FieldSeparator, onThisLine.Select(f => $"{f.Label}: {f.Value}")) + "<br>");
        }

        // A label the blueprint does not name — `Range` and `Accuracy` are live examples the user is still settling
        // with the other editors. It keeps its own line just before Class/Race rather than being dropped or guessed
        // at a position, which mirrors where the data pass inserts new content.
        if (remaining.Count > 0)
        {
            notes.Add("The blueprint has no place for: " +
                      string.Join(", ", remaining.Select(f => f.Label).Distinct(StringComparer.OrdinalIgnoreCase)) +
                      ". They were left on their own lines before Class.");

            int before = output.FindIndex(l => l.StartsWith("Class:", StringComparison.OrdinalIgnoreCase));
            if (before < 0) before = output.Count;
            output.InsertRange(before, remaining.Select(f => $"{f.Label}: {f.Value}<br>"));
        }

        return string.Join('\n', output);
    }

    /// <summary>Why a block cannot be safely reordered, or null when it can.</summary>
    private static string? FindReorderBlocker(List<StatsBlockLine> lines)
    {
        if (lines.FirstOrDefault(l => l.Kind == StatsLineKind.Unparsed) is { } unparsed)
            return $"'{unparsed.Text}' could not be read, and moving a line nobody could parse is how a formatter " +
                   "loses data.";

        // **Legacy flags stop the formatter dead** (user, 2026-09-28): *"I don't want the prettifier to get into the
        // business of understanding/reformatting obsolete flags and fields."* Real usage aims at items already
        // updated for EQL; an old page should be left alone and reported rather than half-modernized by a pass whose
        // only job is layout. Discarding legacy flags is the *data* pass's decision, made against a live capture.
        if (lines.SelectMany(l => l.Flags).FirstOrDefault(f => !IsCurrentFlag(f)) is { } legacy)
            return $"'{legacy}' is not a current EQL flag — it is a legacy one, or prose the grammar read as a " +
                   "flag. Laying out a line the tool does not understand is how a formatter loses meaning, so the " +
                   "whole block was left as it is.";

        if (lines.Any(l => l.Kind == StatsLineKind.Blank))
            return "it has a blank line in the middle, which is a paragraph break that may be doing visible work.";

        // Counted across every line that carries a flag, not just flags-only lines, because the blueprint's own
        // `EXPENDABLE  Charges: 10` shape puts one on a field line. Two such lines would be merged into one, and
        // merging is a bigger claim than a formatter should make unasked.
        if (lines.Count(l => l.Flags.Count > 0) > 1)
            return "it carries flags on more than one line, and merging them is a bigger claim than laying them out.";

        List<string> duplicates =
        [
            .. lines.SelectMany(l => l.Fields)
                .GroupBy(f => f.Label, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Select(f => f.Value).Distinct(StringComparer.Ordinal).Count() > 1)
                .Select(g => g.Key),
        ];

        return duplicates.Count > 0
            ? $"'{duplicates[0]}' appears more than once with different values, which is a page defect a human " +
              "should settle rather than a layout the tool should tidy."
            : null;
    }

    /// <summary>
    /// Whether a flag token is one of EQL's current ones, judged by *shape* rather than against a list.
    ///
    /// **A list would be the wrong tool**, for the same reason the capture side refuses to keep one: the devs keep
    /// adding flags, and a list would reject exactly the rare items most worth recording. The shape separates them
    /// cleanly, which was measured across 1,183 real pages rather than assumed — every legacy flag there is
    /// ALL-CAPS (<c>MAGIC ITEM</c>, <c>EXPENDABLE</c>, <c>NODROP</c>, <c>NO RENT</c>) and every current one is Title
    /// Case (<c>Lore Equipped</c>, <c>No Trade</c>, <c>Attunable</c>, <c>Placeable</c>, <c>Quest</c>).
    ///
    /// It also catches the third category the census turned up, which is neither: prose the grammar read as a flag
    /// because it sat on the flags line — <c>This is a meal!</c>, <c>The Book is closed.</c>,
    /// <c>Required level of 55.</c>, and one page's mis-parsed <c>Class:CLR DRU SHM</c>. Those fail on a lowercase
    /// word, a terminal full stop or a colon, and a formatter should not be moving any of them.
    /// </summary>
    private static bool IsCurrentFlag(string flag)
    {
        string trimmed = flag.Trim();
        if (trimmed.Length == 0 || trimmed.Contains(':')) return false;
        if (trimmed[^1] is '.' or '!' or '?') return false;

        foreach (string word in trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!char.IsUpper(word[0])) return false;
            // An ALL-CAPS word is the legacy dialect. A single letter cannot tell us either way, so it passes.
            if (word.Length > 1 && !word[1..].Any(char.IsLower)) return false;
        }

        return true;
    }

    /// <summary>
    /// Proves the formatted page says exactly what the original said, and lists every way it does not.
    ///
    /// This is the load-bearing part. Everything above rearranges text, and a rearrangement that drops a value looks
    /// like a tidy-up in review — so the formatter is not trusted, it is checked. Compared: the set of parameters,
    /// each one's trimmed value, and for the statsblock the multiset of its fields, its flags and any line the
    /// grammar could not read.
    /// </summary>
    private static IReadOnlyList<string> Verify(ItemPageDocument before, string formattedText, WikiMapping mapping)
    {
        var refusals = new List<string>();

        ItemPageDocument? after = ItemPageDocument.Parse(formattedText);
        if (after is null)
            return ["Laying the page out left it without a parseable Itempage call, so nothing was changed."];

        string[] beforeNames = [.. before.Template.Parameters.Where(p => p.Name is not null).Select(p => p.Name!).Distinct()];
        string[] afterNames = [.. after.Template.Parameters.Where(p => p.Name is not null).Select(p => p.Name!).Distinct()];

        foreach (string lost in beforeNames.Except(afterNames, StringComparer.Ordinal))
            refusals.Add($"|{lost}= went missing.");
        foreach (string gained in afterNames.Except(beforeNames, StringComparer.Ordinal))
            refusals.Add($"|{gained}= appeared from nowhere.");

        foreach (string name in beforeNames.Intersect(afterNames, StringComparer.Ordinal))
        {
            string? was = before.Template.Find(name)?.Value;
            string? now = after.Template.Find(name)?.Value;

            if (string.Equals(name, mapping.StatsBlockParameter, StringComparison.Ordinal))
            {
                refusals.AddRange(CompareStatsBlocks(was, now));
                continue;
            }

            if (!string.Equals(was, now, StringComparison.Ordinal))
            {
                refusals.Add($"|{name}= changed from '{Shorten(was)}' to '{Shorten(now)}'.");
                continue;
            }

            // Content equality is not enough for a value whose markup only works at the start of a line: a `*`
            // written after the `=` renders as a literal asterisk instead of a bullet, and the value string is
            // identical either way. So the invariant is checked directly rather than inferred from the text.
            if (now is { Length: > 0 } && NeedsOwnLine(now) && after.Template.Find(name) is { } parameter &&
                !StartsItsOwnLine(formattedText, parameter.ValueStart))
                refusals.Add($"|{name}= starts with line-sensitive markup but was not put on its own line, which " +
                             "would change how the page renders.");
        }

        // Everything outside the call must be untouched, which is a property this pass gets for free by only ever
        // splicing the call itself — so a failure here means a real bug, not a judgement call.
        if (!string.Equals(before.Wikitext[..before.Template.Start], formattedText[..after.Template.Start], StringComparison.Ordinal) ||
            !string.Equals(before.Wikitext[before.Template.End..], formattedText[after.Template.End..], StringComparison.Ordinal))
            refusals.Add("The text outside the template call changed, which this pass must never do.");

        return refusals;
    }

    private static IEnumerable<string> CompareStatsBlocks(string? was, string? now)
    {
        StatsBlock before = StatsBlock.Parse(was ?? "");
        StatsBlock after = StatsBlock.Parse(now ?? "");

        foreach (string difference in CompareMultisets(
                     [.. before.AllFields().Select(f => $"{f.Label}: {f.Value}")],
                     [.. after.AllFields().Select(f => $"{f.Label}: {f.Value}")],
                     "statsblock field"))
            yield return difference;

        foreach (string difference in CompareMultisets(
                     [.. before.Lines.SelectMany(l => l.Flags)],
                     [.. after.Lines.SelectMany(l => l.Flags)],
                     "flag"))
            yield return difference;

        foreach (string difference in CompareMultisets(
                     [.. before.Lines.Where(l => l.Kind == StatsLineKind.Unparsed).Select(l => l.Text)],
                     [.. after.Lines.Where(l => l.Kind == StatsLineKind.Unparsed).Select(l => l.Text)],
                     "unreadable statsblock line"))
            yield return difference;
    }

    /// <summary>Multisets, not sets: a value appearing twice and a value appearing once are different pages, and a
    /// set comparison would call them the same.</summary>
    private static IEnumerable<string> CompareMultisets(List<string> before, List<string> after, string what)
    {
        var remaining = new List<string>(after);
        foreach (string item in before)
            if (!remaining.Remove(item))
                yield return $"The {what} '{Shorten(item)}' was lost.";

        foreach (string item in remaining)
            yield return $"The {what} '{Shorten(item)}' appeared from nowhere.";
    }

    /// <summary>Whether the value starting at this offset has only whitespace between it and the previous newline —
    /// i.e. whether MediaWiki will see its first character as starting a line.</summary>
    private static bool StartsItsOwnLine(string text, int valueStart)
    {
        // The raw value begins right after the `=`, so it may carry leading padding; the character that matters is
        // the first non-whitespace one.
        int first = valueStart;
        while (first < text.Length && char.IsWhiteSpace(text[first])) first++;

        for (int i = first - 1; i >= 0; i--)
        {
            if (text[i] == '\n') return true;
            if (!char.IsWhiteSpace(text[i])) return false;
        }

        return false;
    }

    private static string Shorten(string? text) =>
        text is null ? "(absent)" : text.Length <= 60 ? text : text[..57] + "...";
}
