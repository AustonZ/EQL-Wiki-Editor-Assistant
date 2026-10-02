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
/// **One exception, and it is the only value this pass ever changes: a signed stat's positive value gains its
/// <c>+</c>** (user, 2026-09-30): *"Adding a `+` in front of a stat value that is already positive is just data
/// formatting in my book, not a data change."* The blueprint writes every attribute and resist as <c>+?</c>, and
/// before this nothing applied it — the data pass signs a stat only when it is rewriting that line anyway, so an
/// unsigned value on an otherwise correct page stayed unsigned for good. The verification pass is told about this
/// one transformation by name (<c>CanonicalField</c>) and still refuses any other change to a value, including a
/// sign flipped off a negative.
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
    /// <summary>
    /// Two spaces between fields sharing a line.
    ///
    /// This used to rest on the blueprint's examples being inconsistent about it — only its `Size`/`WT` and
    /// `Weight Reduction` lines were double-spaced, and the stat lines were single-spaced. The blueprint's
    /// 2026-09-30 revision (oldid 179818) double-spaced every one of them, so the convention is now stated
    /// uniformly and this constant is what it says.
    /// </summary>
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

    /// <summary>
    /// Whether the verification pass would accept <paramref name="formatted"/> as saying what
    /// <paramref name="original"/> says. Both must be parseable item pages.
    ///
    /// **This exists so the check can be tested directly, and that is worth a little public surface.** The check is
    /// the whole reason this pass is allowed to rewrite a public wiki's pages, and <c>CanonicalField</c> cuts one
    /// deliberate hole in it for the sign. Proving the hole is only that wide needs the comparison fed pairs it must
    /// still reject — which the formatter will never produce, so the only other way to reach it would be to sabotage
    /// the writer, and a test needing a sabotaged writer is testing the sabotage.
    /// </summary>
    public static bool WouldVerify(string original, string formatted, WikiMapping? mapping = null)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(formatted);

        ItemPageDocument before = ItemPageDocument.Parse(original)
            ?? throw new ArgumentException("Not an item page.", nameof(original));

        return Verify(before, formatted, mapping ?? WikiMapping.Default).Count == 0;
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

        // The blueprint's column, widened only by a name even longer than any the blueprint declares — which is an
        // unrecognized parameter, somebody's own content, and it should still line up. See
        // `WikiMapping.ParameterAlignmentWidth` for why this is not the longest name on *this* page.
        int width = Math.Max(
            mapping.ParameterAlignmentWidth,
            ordered.Count == 0 ? 0 : ordered.Max(p => p.Name!.Length));

        var builder = new StringBuilder();
        builder.Append("{{").Append(template.Name).Append('\n');

        foreach (TemplateParameter parameter in positional)
            builder.Append('|').Append(parameter.RawValue.Trim()).Append('\n');

        foreach (TemplateParameter parameter in ordered)
        {
            bool isStatsBlock = string.Equals(parameter.Name, mapping.StatsBlockParameter, StringComparison.Ordinal);
            string value = isStatsBlock ? FormatStatsBlock(parameter.Value, mapping, notes) : parameter.Value;

            builder.Append('|').Append(parameter.Name!.PadRight(width)).Append(" = ");

            // **A block parameter is surrounded by blank lines, always** (user, 2026-09-30) — see
            // `WikiMapping.BlockParameters` for which and why. Deliberately regardless of length, so a one-line
            // `|soldby =` reads the same as a long drop table and does not change shape when a second line
            // arrives. An *empty* one gets nothing: blank lines frame content, and there is none.
            if (value.Length > 0 && mapping.BlockParameters.Contains(parameter.Name!, StringComparer.Ordinal))
                builder.Append("\n\n").Append(value).Append("\n\n");

            // The statsblock always starts on its own line, even when it is currently one line long. Every real page
            // writes it that way, and a block that sat on the `=` line would jump onto its own the moment the data
            // pass added a second line — which is churn the formatting commit exists to prevent.
            else if (isStatsBlock && value.Length > 0) builder.Append('\n').Append(value).Append('\n');
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

        string? blocker = FindReorderBlocker(lines, TrailingLabels(mapping));
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
        bool blankPending = false;

        foreach (IReadOnlyList<string> slot in mapping.StatsBlockLineOrder)
        {
            if (slot is [WikiMapping.BlankLine])
            {
                // Held rather than written, so a statsblock whose trailing section is empty does not end in a stray
                // blank line.
                blankPending = true;
                continue;
            }

            if (slot is ["(flags)"])
            {
                if (flags.Count > 0) Emit(string.Join(", ", flags) + "<br>");
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
            Emit(string.Join(FieldSeparator, onThisLine.Select(f => Render(f, mapping))) + "<br>");
        }

        void Emit(string line)
        {
            if (blankPending) { output.Add(""); blankPending = false; }
            output.Add(line);
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
            output.InsertRange(before, remaining.Select(f => Render(f, mapping) + "<br>"));
        }

        return string.Join('\n', output);
    }

    /// <summary>
    /// One field as the blueprint writes it, which for an attribute or a resist includes a leading <c>+</c>.
    ///
    /// **Signing a positive value is formatting, not data** (user, 2026-09-30): *"Adding a `+` in front of a stat
    /// value that is already positive is just data formatting in my book, not a data change."* The blueprint's
    /// 2026-09-30 revision spells every one of them `+?`, and before this nothing applied it — the data pass signs a
    /// stat only when it is already rewriting that line for another reason, so an unsigned value on an otherwise
    /// correct page stayed unsigned indefinitely.
    ///
    /// **The sign rule is <see cref="StatMapping.WithWikiSign"/>, shared with the data pass** so the two cannot
    /// disagree, and it signs only a value that is wholly a positive number. A negative keeps its own sign, which is
    /// real rather than defensive: 29 values across 1,183 pages are negative, and the game emits them too.
    ///
    /// **This is the one place the formatter changes a value**, so <see cref="Verify"/> has to be told about it
    /// explicitly — see <c>CanonicalField</c>. It is also why a block the formatter refuses to reorder keeps its
    /// unsigned values: that path returns the raw text untouched, and a pass that has just said it does not
    /// understand a block has no business editing values in it.
    /// </summary>
    private static string Render(StatsField field, WikiMapping mapping)
    {
        StatMapping? stat = mapping.FindStatByWikiLabel(field.Label);
        string value = stat is null ? field.Value : stat.WithWikiSign(field.Value);
        return $"{field.Label}: {value}";
    }

    /// <summary>The labels the blueprint places below the blank-line separator, at the bottom of the block.</summary>
    private static IReadOnlyList<string> TrailingLabels(WikiMapping mapping)
    {
        var labels = new List<string>();
        bool past = false;
        foreach (IReadOnlyList<string> slot in mapping.StatsBlockLineOrder)
        {
            if (slot is [WikiMapping.BlankLine]) { past = true; continue; }
            if (past) labels.AddRange(slot);
        }

        return labels;
    }

    /// <summary>Whether every blank line in the block is the separator this formatter writes before the trailing
    /// section — in which case the block is still one it laid out and may lay out again.</summary>
    private static bool OnlyBlankIsTheTrailingSeparator(
        List<StatsBlockLine> lines, IReadOnlyList<string> trailingLabels)
    {
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].Kind != StatsLineKind.Blank) continue;

            StatsBlockLine? next = lines.Skip(i + 1).FirstOrDefault(l => l.Kind != StatsLineKind.Blank);
            if (next is null) return false;
            if (!next.Fields.Any(f => trailingLabels.Contains(f.Label, StringComparer.OrdinalIgnoreCase)))
                return false;
        }

        return true;
    }

    /// <summary>Why a block cannot be safely reordered, or null when it can.</summary>
    private static string? FindReorderBlocker(List<StatsBlockLine> lines, IReadOnlyList<string> trailingLabels)
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

        // A blank line is normally a reason to leave the block alone — it is a paragraph break that may be doing
        // visible work. The exception is the one this formatter puts there itself, separating the trailing section
        // (`Mount Speed`, `Pet Illusion`) from the item's own stats. Without the exception, formatting a block once
        // would freeze it: the next run would see its own blank line and refuse to touch anything.
        if (lines.Any(l => l.Kind == StatsLineKind.Blank) && !OnlyBlankIsTheTrailingSeparator(lines, trailingLabels))
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
    /// Whether a flag token is one of EQL's current ones.
    ///
    /// **The rule itself now lives in <see cref="FlagDialect"/>, shared with the data pass** — the two reach the
    /// same question from opposite ends (what may be written over a flags line, versus whether one can be laid
    /// out), and two copies would drift into one pass rewriting a line the other declared it could not read. Its
    /// summary carries the measurement and the three dialects.
    /// </summary>
    private static bool IsCurrentFlag(string flag) => FlagDialect.IsCurrent(flag);

    /// <summary>
    /// Proves the formatted page says what the original said, and lists every way it does not.
    ///
    /// "What it said" allows exactly one difference, applied by <see cref="Render"/> and recognized by
    /// <c>CanonicalField</c>: a signed stat's positive value may gain a leading <c>+</c>. Everything else about a
    /// value is still compared byte for byte.
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
                refusals.AddRange(CompareStatsBlocks(was, now, mapping));
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

    /// <summary>
    /// A statsblock field reduced to what the verification pass treats as its content.
    ///
    /// **This is the one hole deliberately cut in an otherwise byte-exact check, so it is cut as narrowly as it can
    /// be.** <see cref="Render"/> adds a <c>+</c> to a signed stat's positive value, which is a value change, and
    /// the check would otherwise refuse every page it touched. So for exactly those labels a leading <c>+</c> is
    /// dropped before comparing, making <c>STR: 5</c> and <c>STR: +5</c> the same content and nothing else.
    ///
    /// What it still catches, which is the point: a changed number (<c>5</c> against <c>7</c>), a dropped or
    /// invented field, a sign flipped from <c>-</c> to <c>+</c> (<c>-5</c> canonicalizes to <c>-5</c>, not
    /// <c>5</c>), and any change at all to a label the mapping does not mark signed.
    /// </summary>
    private static string CanonicalField(StatsField field, WikiMapping mapping)
    {
        string value = field.Value.Trim();
        if (mapping.FindStatByWikiLabel(field.Label) is { Signed: true } && value.StartsWith('+'))
            value = value[1..];

        return $"{field.Label}: {value}";
    }

    private static IEnumerable<string> CompareStatsBlocks(string? was, string? now, WikiMapping mapping)
    {
        StatsBlock before = StatsBlock.Parse(was ?? "");
        StatsBlock after = StatsBlock.Parse(now ?? "");

        foreach (string difference in CompareMultisets(
                     [.. before.AllFields().Select(f => CanonicalField(f, mapping))],
                     [.. after.AllFields().Select(f => CanonicalField(f, mapping))],
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
