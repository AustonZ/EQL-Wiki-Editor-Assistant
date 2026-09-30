using System.Text.RegularExpressions;
using EQLWikiAssistant.Wiki.Mapping;

namespace EQLWikiAssistant.Wiki.Wikitext;

/// <summary>
/// A wiki item page, seen through its <c>{{Itempage}}</c> call.
///
/// **Every mutation returns a new document built by re-parsing the edited wikitext**, rather than mutating in
/// place. Parameter spans are byte offsets into the page, so one edit invalidates every offset after it; re-parsing
/// costs microseconds and removes that whole class of bug, which on this tool would surface as an edit spliced
/// into the middle of unrelated human-written content.
///
/// The v1 field scope is deliberately narrow — the fields verifiable from an in-game item window. Everything else
/// on the page (<c>dropsfrom</c>, <c>soldby</c>, <c>relatedquests</c>, <c>recipes</c>, <c>playercrafted</c>,
/// <c>foraged</c>, <c>bookcontents</c>, the era template, an in-world screenshot, unrelated categories) is never
/// read and never written, so it survives untouched by construction rather than by care.
///
/// Note <c>recipes</c>, plural: the plan and CLAUDE.md both say <c>recipe</c>, but all 48 pages in the sampled
/// corpus that have one spell it <c>recipes</c>. It is outside the v1 write scope either way, so this only matters
/// as a correction to the notes.
/// </summary>
public sealed class ItemPageDocument
{
    /// <summary>The template all item pages transclude.</summary>
    public const string TemplateName = "Itempage";

    /// <summary>Wraps the item's lore inside the <c>notes</c> parameter. The only part of <c>notes</c> the tool is
    /// allowed to machine-edit — the rest is human-written commentary.</summary>
    public const string LoreTemplateName = "Item Lore";

    /// <summary>A placeholder meaning "nobody has filled the lore in yet". Always removed when the tool touches a
    /// page: by then either lore has been captured, or the item has no lore tab and so has no lore to add.</summary>
    public const string LoreMissingTemplateName = "Item Lore Missing";

    private ItemPageDocument(string wikitext, TemplateCall template, IReadOnlyList<string> warnings)
    {
        Wikitext = wikitext;
        Template = template;
        Warnings = warnings;
    }

    /// <summary>The complete page source, including everything outside the template.</summary>
    public string Wikitext { get; }

    public TemplateCall Template { get; }

    /// <summary>Things that could not be read confidently. Per the untrusted-data constraint these are surfaced,
    /// never thrown and never silently defaulted.</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>Reads a page, or returns null if it has no usable <c>{{Itempage}}</c> call. Null means "this is
    /// not an item page the tool can work with" — a redirect, a disambiguation page, a mob page, or a page whose
    /// template braces never close — and is an ordinary outcome, not a failure.</summary>
    public static ItemPageDocument? Parse(string wikitext)
    {
        ArgumentNullException.ThrowIfNull(wikitext);

        TemplateCall? template = WikitextScanner.FindTemplate(wikitext, TemplateName);
        if (template is null) return null;

        var warnings = new List<string>();
        foreach (string required in new[] { "itemname", "statsblock" })
            if (template.Find(required) is null)
                warnings.Add($"The page's {{{{{TemplateName}}}}} call has no |{required}= parameter.");

        var duplicates = template.Parameters
            .Where(p => p.Name is not null)
            .GroupBy(p => p.Name!, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key);
        foreach (string duplicate in duplicates)
            warnings.Add($"|{duplicate}= appears more than once. The last one was read, matching MediaWiki, but the " +
                         "page should be cleaned up by hand.");

        return new ItemPageDocument(wikitext, template, warnings);
    }

    /// <summary>A parameter's trimmed value, or null when the parameter is absent. An empty-but-present parameter
    /// returns the empty string, which is a different thing: the page author wrote it and left it blank.</summary>
    public string? GetParameter(string name) => Template.Find(name)?.Value;

    public string? ItemName => GetParameter("itemname");
    public string? IconId => GetParameter("lucy_img_ID");
    public string? FocusEffect => GetParameter("focus_effect");
    public string? MerchantValue => GetParameter("merchant_value");
    public string? Notes => GetParameter("notes");

    /// <summary>The <c>statsblock</c> value split into lines, or null if the page has no such parameter. A method
    /// rather than a property because it re-splits the value on every call.</summary>
    public StatsBlock? ReadStatsBlock() =>
        Template.Find("statsblock") is { } p ? StatsBlock.Parse(p.RawValue) : null;

    /// <summary>The lore text from the <c>{{Item Lore|...}}</c> wrapper inside <c>notes</c>, or null if the page
    /// has no lore. An <c>{{Item Lore Missing}}</c> placeholder is not lore — it is the absence of it.</summary>
    public string? Lore
    {
        get
        {
            if (Template.Find("notes") is not { } notes) return null;
            IReadOnlyList<TemplateCall> calls = WikitextScanner.FindTemplates(notes.RawValue, LoreTemplateName);
            if (calls.Count == 0) return null;
            return calls[0].Parameters.FirstOrDefault(p => p.Index == 1)?.Value;
        }
    }

    /// <summary>
    /// The page's <c>[[Category:...]]</c> names, in the order they appear.
    ///
    /// Categories sit *outside* the template call, at the end of the page, so they are read from the whole wikitext
    /// rather than from a parameter — the second thing after the era banner that this layer touches beyond the
    /// <c>{{Itempage}}</c> call.
    /// </summary>
    public IReadOnlyList<string> Categories =>
        [.. CategoryPattern.Matches(Wikitext).Select(m => m.Groups["name"].Value.Trim())];

    /// <summary>
    /// Adds a category at the end of the page if it is not already there.
    ///
    /// **Only ever adds.** Removing one would mean deciding that somebody else's category is wrong, and a page's
    /// categories include plenty the tool cannot derive — zone names, `Quest Items`, `Fashion:` entries. See
    /// <see cref="CategoryRules.IsDerivable"/> for what the tool considers its own territory.
    /// </summary>
    public ItemPageDocument WithCategory(string category)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        string trimmed = category.Trim();

        if (Categories.Any(c => string.Equals(c, trimmed, StringComparison.OrdinalIgnoreCase))) return this;

        string text = Wikitext.TrimEnd('\n', '\r');
        // On its own line, after whatever is already there. A page ending in categories gains one more in the run;
        // a page with none gets a blank line first, which is what every real page does.
        string separator = CategoryPattern.IsMatch(text) ? "\n" : "\n\n";
        return Reparse(text + separator + $"[[Category:{trimmed}]]");
    }

    private static readonly Regex CategoryPattern =
        new(@"\[\[\s*Category\s*:\s*(?<name>[^\]|]+?)\s*(\|[^\]]*)?\]\]", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>True if the page still carries the "lore not filled in" placeholder.</summary>
    public bool HasLoreMissingPlaceholder =>
        Template.Find("notes") is { } notes &&
        WikitextScanner.FindTemplates(notes.RawValue, LoreMissingTemplateName).Count > 0;

    /// <summary>Replaces one parameter's value, preserving the whitespace that surrounded the old one so the diff
    /// shows the value changing and nothing else. Throws if the parameter is absent: adding a parameter is a
    /// different operation with different placement questions, and silently appending one would put it somewhere
    /// the page author did not choose.</summary>
    public ItemPageDocument WithParameter(
        string name,
        string newValue,
        IReadOnlyList<string>? blueprintOrder = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(newValue);

        if (Template.Find(name) is { } parameter)
        {
            (string lead, string trail) = SplitPadding(parameter.RawValue);
            return Reparse(WikitextScanner.ReplaceValue(Wikitext, parameter, lead + newValue + trail));
        }

        // Without an order there is no basis for a position, so this still refuses rather than guessing — which is
        // what it did in every case before the blueprint order was threaded through.
        if (blueprintOrder is null)
            throw new InvalidOperationException($"The page has no |{name}= parameter to replace.");

        return Reparse(Insert(Wikitext, Template, name, newValue, blueprintOrder));
    }

    /// <summary>
    /// Writes a parameter the page does not have, in the blueprint's order.
    ///
    /// **This is the minimal-edit rule's third case** (user, 2026-09-25): a parameter that does not exist has no line
    /// to be changed in place, so it needs a position — and the blueprint supplies one, which is why this is derived
    /// rather than invented. It matters in practice because a legacy page frequently has no <c>merchant_value</c> at
    /// all: in original EverQuest a player could not easily learn an item's value, so nobody recorded one. EQL states
    /// it outright in the window, so the tool can (user, 2026-09-29).
    ///
    /// **The anchor is the last parameter the blueprint puts _before_ this one, not the first it puts after.** Real
    /// pages are not in blueprint order — `Dragon Bone Bracelet` opens with <c>|notes=</c>, which the blueprint puts
    /// sixth — so anchoring on what comes after would have dropped the merchant value at the very top of the call.
    /// Anchoring on what comes before lands it after <c>statsblock</c>, where a reader expects it. The two rules agree
    /// on a page that *is* in blueprint order, and differ only on the messy ones, which is the case that matters here.
    ///
    /// **The separator is copied from the anchor rather than chosen**, so a page writing one parameter per line gets
    /// another line and a single-line call stays on one line. Same principle as the rest of this file: the page's own
    /// source is the truth, and nothing about a layout this tool did not create has to be understood.
    /// </summary>
    private static string Insert(
        string wikitext,
        TemplateCall template,
        string name,
        string value,
        IReadOnlyList<string> blueprintOrder)
    {
        List<TemplateParameter> named =
            [.. template.Parameters.Where(p => p.Name is not null && p.SegmentStart >= 0)];

        // A call with no named parameters at all has no separator to copy and no anchor to sit beside; the only
        // position left is just inside the closing braces.
        if (named.Count == 0)
            return wikitext[..(template.End - 2)] + $"|{name} = {value}" + wikitext[(template.End - 2)..];

        int wanted = PositionIn(blueprintOrder, name);

        TemplateParameter? precedes = wanted < 0
            ? null
            : named.LastOrDefault(p => PositionIn(blueprintOrder, p.Name!) is >= 0 and var i && i < wanted);

        if (precedes is not null) return Splice(precedes.SegmentEnd, precedes);

        TemplateParameter? follows = wanted < 0
            ? null
            : named.FirstOrDefault(p => PositionIn(blueprintOrder, p.Name!) > wanted);

        // Nothing the blueprint places earlier, and nothing it places later either — a name the blueprint does not
        // know at all. The end of the call is the one position that cannot be wrong about an order it has no place in.
        return follows is not null
            ? Splice(follows.SegmentStart, follows)
            : Splice(named[^1].SegmentEnd, named[^1]);

        string Splice(int at, TemplateParameter like)
        {
            string segment = wikitext[like.SegmentStart..like.SegmentEnd];
            string separator = segment[segment.TrimEnd().Length..];
            return wikitext[..at] + $"|{name} = {value}" + separator + wikitext[at..];
        }
    }

    private static int PositionIn(IReadOnlyList<string> order, string name)
    {
        for (int i = 0; i < order.Count; i++)
            if (string.Equals(order[i], name, StringComparison.Ordinal))
                return i;
        return -1;
    }

    /// <summary>
    /// Splits a raw parameter value into the whitespace before it and the whitespace after it.
    ///
    /// The empty-value case is the whole reason this is a method. A page writing <c>|notes       = </c> with
    /// nothing after it has a raw value of <c>" \n"</c>, which is <em>entirely</em> padding — so "leading
    /// whitespace" and "trailing whitespace" both describe the same two characters, and taking each independently
    /// emits them twice. That silently doubled a blank line on 343 of 662 sampled pages, and the fixtures did not
    /// catch it because none of them has an empty parameter.
    ///
    /// For an all-whitespace value the split goes at the first newline instead: whatever sits between the <c>=</c>
    /// and the end of that line is the lead, the rest is the trail. That puts a newly written value where a human
    /// would put it — on the same line as the <c>=</c> — rather than below it.
    /// </summary>
    private static (string Lead, string Trail) SplitPadding(string raw)
    {
        if (raw.Trim().Length > 0)
            return (raw[..(raw.Length - raw.TrimStart().Length)], raw[raw.TrimEnd().Length..]);

        int newline = raw.IndexOf('\n');
        return newline < 0 ? (raw, "") : (raw[..newline], raw[newline..]);
    }

    /// <summary>Replaces the whole <c>statsblock</c> value verbatim — the value carries its own line breaks, so
    /// unlike <see cref="WithParameter"/> no whitespace is re-applied around it.</summary>
    public ItemPageDocument WithStatsBlock(StatsBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);

        TemplateParameter parameter = Template.Find("statsblock")
            ?? throw new InvalidOperationException("The page has no |statsblock= parameter to replace.");

        return Reparse(WikitextScanner.ReplaceValue(Wikitext, parameter, block.Render()));
    }

    /// <summary>
    /// Ensures the page opens with exactly one era banner, and that it is the given era.
    ///
    /// **The era is how the wiki records that an item has actually been seen in the game** (user, 2026-09-25), which
    /// is why this is an automatic fix rather than something to report: a capture *is* the confirmation. Every item
    /// currently in EQL is Classic Era, so a page that is missing a banner or carries a legacy one
    /// (176 sampled pages say `Velious Era`, inherited from the Project1999 import) is corrected outright.
    ///
    /// This is the one place the tool rewrites something outside the `Itempage` call, and it is deliberate: the
    /// banner is page-level furniture, not item data.
    /// </summary>
    public ItemPageDocument WithEraTemplate(string era)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(era);
        string wanted = $"{{{{{era} Era}}}}";

        var existing = EraTemplates(Wikitext).OrderByDescending(c => c.Start).ToList();

        // Already correct and unduplicated: leave the page completely alone rather than re-splicing identical text.
        if (existing.Count == 1 &&
            string.Equals(Wikitext[existing[0].Start..existing[0].End], wanted, StringComparison.Ordinal))
            return this;

        string wikitext = Wikitext;
        foreach (TemplateCall call in existing)
        {
            int end = call.End;
            // Take a newline that immediately followed the banner, so removing it does not leave a blank line.
            if (end < wikitext.Length && wikitext[end] == '\r') end++;
            if (end < wikitext.Length && wikitext[end] == '\n') end++;
            wikitext = wikitext[..call.Start] + wikitext[end..];
        }

        return Reparse(wanted + "\n" + wikitext.TrimStart('\r', '\n'));
    }

    /// <summary>True when the page carries exactly one banner and it names this era.</summary>
    public bool HasEraTemplate(string era)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(era);
        IReadOnlyList<TemplateCall> banners = EraTemplates(Wikitext);
        return banners.Count == 1 &&
               string.Equals(banners[0].Name, $"{era} Era", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The era banner currently on the page, or null. More than one is itself a defect.</summary>
    public string? CurrentEra
    {
        get
        {
            IReadOnlyList<TemplateCall> banners = EraTemplates(Wikitext);
            return banners.Count == 0 ? null : banners[0].Name;
        }
    }

    /// <summary>Every `{{... Era}}` banner, matched by shape rather than against a list: the wiki has twelve and
    /// gains one per expansion, so a fixed list would fail to notice a brand-new era already on a page.</summary>
    private static IReadOnlyList<TemplateCall> EraTemplates(string wikitext)
    {
        var found = new List<TemplateCall>();
        for (int i = 0; i + 1 < wikitext.Length; i++)
        {
            if (wikitext[i] != '{' || wikitext[i + 1] != '{') continue;

            int close = wikitext.IndexOf("}}", i + 2, StringComparison.Ordinal);
            if (close < 0) break;

            string name = wikitext[(i + 2)..close].Trim();
            if (name.EndsWith(" Era", StringComparison.OrdinalIgnoreCase) && !name.Contains('\n'))
                found.Add(new TemplateCall(name, i, close + 2 - i, []));
        }

        return found;
    }

    /// <summary>
    /// Removes every duplicate of a named parameter, keeping the last — the one MediaWiki actually renders.
    ///
    /// The user's own routine includes cleaning these up (2026-09-25), and it is a *compliance* fix rather than a
    /// formatting one: a page with two <c>|notes=</c> has content that does not render, which is a defect in what
    /// the page says. Rare but real — 3 of 744 sampled pages.
    ///
    /// Removing takes the parameter's whole <c>|name = value</c> run, not just its value, or a stray <c>|notes =</c>
    /// is left behind. Duplicates are removed back to front so the earlier spans stay valid while iterating.
    /// </summary>
    public ItemPageDocument WithoutDuplicateParameters()
    {
        TemplateParameter[] doomed = [.. Template.Parameters
            .Where(p => p.Name is not null && p.SegmentStart >= 0)
            .GroupBy(p => p.Name!, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            // Keep the last occurrence, which is the one MediaWiki uses; drop everything before it.
            .SelectMany(g => g.SkipLast(1))
            .OrderByDescending(p => p.SegmentStart)];

        if (doomed.Length == 0) return this;

        string wikitext = Wikitext;
        foreach (TemplateParameter parameter in doomed)
            wikitext = wikitext[..parameter.SegmentStart] + wikitext[parameter.SegmentEnd..];

        return Reparse(wikitext);
    }

    /// <summary>Removes the <c>{{Item Lore Missing}}</c> placeholder wherever it appears in <c>notes</c>, along
    /// with a <c>&lt;br&gt;</c> immediately following it — real pages write
    /// <c>{{Item Lore Missing}}&lt;br&gt;</c> before the human's own text, and leaving that break behind opens the
    /// note with a blank line. Returns the document unchanged when there is no placeholder.</summary>
    public ItemPageDocument WithoutLoreMissingPlaceholder()
    {
        if (Template.Find("notes") is not { } notes) return this;

        IReadOnlyList<TemplateCall> calls = WikitextScanner.FindTemplates(notes.RawValue, LoreMissingTemplateName);
        if (calls.Count == 0) return this;

        string value = notes.RawValue;
        foreach (TemplateCall call in calls.Reverse())
        {
            int end = call.End;
            if (string.CompareOrdinal(value, end, "<br>", 0, 4) == 0) end += 4;
            else if (string.CompareOrdinal(value, end, "<br/>", 0, 5) == 0) end += 5;

            // **The line break goes only when something else follows it** (bug found by the user, 2026-09-29, on
            // `Shield of the Stalwart Seas`). Where a human's own note sits below the placeholder, that break belongs
            // to the placeholder and taking it avoids leaving a blank first line. Where the placeholder *was* the
            // whole value, the very same break is the parameter's own line terminator — the last character before
            // the next `|` — so taking it merged the following parameter onto this line and produced
            // `|notes     = |itemname    = Shield of the Stalwart Seas`.
            int afterBreak = end;
            if (afterBreak < value.Length && value[afterBreak] == '\r') afterBreak++;
            if (afterBreak < value.Length && value[afterBreak] == '\n') afterBreak++;
            if (value[afterBreak..].Trim().Length > 0) end = afterBreak;

            value = value[..call.Start] + value[end..];
        }

        return Reparse(WikitextScanner.ReplaceValue(Wikitext, notes, value));
    }

    /// <summary>
    /// Characters that would change what a <c>{{Item Lore|...}}</c> call means rather than appear inside it: a pipe
    /// starts a second parameter, and a brace pair opens or closes a template. Lore is prose and none of these has
    /// ever appeared in a captured one, but writing a value that silently restructures somebody's page is exactly
    /// the failure this codebase refuses to risk — so such text is reported to the user instead of written.
    /// </summary>
    public static bool CanBeWrittenAsLore(string loreText) =>
        loreText is not null &&
        !loreText.Contains('|') && !loreText.Contains("{{", StringComparison.Ordinal) &&
        !loreText.Contains("}}", StringComparison.Ordinal);

    /// <summary>
    /// Writes the item's lore into the <c>{{Item Lore|...}}</c> wrapper inside <c>notes</c>.
    ///
    /// **Two cases, because lore is the one v1 field that lives nested inside another parameter.** When the wrapper
    /// is already there its value is spliced in place, so the rest of <c>notes</c> — which routinely holds a human's
    /// own commentary — survives byte for byte. When it is not, the call is inserted at the front of <c>notes</c>,
    /// which is where the <c>{{Item Lore Missing}}</c> placeholder it replaces also sat.
    ///
    /// Throws when the page has no <c>notes</c> parameter at all: that is the minimal-edit rule's third case, where
    /// there is no line to change and choosing a position is a judgement — <see cref="WithParameter"/> refuses it
    /// for the same reason.
    /// </summary>
    public ItemPageDocument WithLore(string loreText, IReadOnlyList<string>? blueprintOrder = null)
    {
        ArgumentNullException.ThrowIfNull(loreText);
        if (!CanBeWrittenAsLore(loreText))
            throw new ArgumentException(
                "This lore text contains characters that would change the template's meaning.", nameof(loreText));

        // A page with no |notes= at all is the same missing-parameter case as merchant_value, and gets the same
        // answer: the blueprint says where notes goes, so the wrapper can be written into a parameter created for it.
        if (Template.Find("notes") is null && blueprintOrder is not null)
            return WithParameter("notes", $"{{{{{LoreTemplateName}|{loreText}}}}}", blueprintOrder);

        TemplateParameter notes = Template.Find("notes")
            ?? throw new InvalidOperationException("The page has no |notes= parameter to write lore into.");

        string value = notes.RawValue;
        IReadOnlyList<TemplateCall> calls = WikitextScanner.FindTemplates(value, LoreTemplateName);

        if (calls.Count > 0 && calls[0].Parameters.FirstOrDefault(p => p.Index == 1) is { } existing)
            return Reparse(WikitextScanner.ReplaceValue(
                Wikitext, notes, WikitextScanner.ReplaceValue(value, existing, loreText)));

        string call = $"{{{{{LoreTemplateName}|{loreText}}}}}";

        // An empty notes parameter becomes the call outright — WithParameter already knows how to keep the padding
        // of a value that is nothing but whitespace, which is a trap this file documents elsewhere.
        if (string.IsNullOrWhiteSpace(value)) return WithParameter("notes", call);

        // Otherwise the call goes in front of whatever a human wrote, on its own line, per the minimal-edit rule.
        int start = 0;
        while (start < value.Length && char.IsWhiteSpace(value[start])) start++;
        return Reparse(WikitextScanner.ReplaceValue(
            Wikitext, notes, value[..start] + call + "<br>\n" + value[start..]));
    }

    private ItemPageDocument Reparse(string wikitext) =>
        Parse(wikitext) ?? throw new InvalidOperationException(
            "The edit left the page without a parseable {{Itempage}} call; this is a bug in the edit, not in the page.");
}
