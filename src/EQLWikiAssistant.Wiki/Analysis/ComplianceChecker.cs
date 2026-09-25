using EQLWikiAssistant.Wiki.Mapping;
using EQLWikiAssistant.Wiki.Wikitext;

namespace EQLWikiAssistant.Wiki.Analysis;

/// <summary>
/// One way a page departs from the current template.
///
/// <see cref="ToolWillFix"/> is the load-bearing distinction. A compliance problem the tool can correct is part of
/// the edit it proposes; one it cannot is reported and left alone. Nothing in between — the tool never half-fixes a
/// page, and never claims to have fixed something it only noticed.
/// </summary>
public sealed record ComplianceFinding(string Rule, string Detail, bool ToolWillFix);

/// <summary>
/// Checks a page against the current template, separately from comparing it to a capture.
///
/// **This is compliance, not formatting** — the distinction the repo draws throughout. Compliance changes what the
/// page *says* (a parameter whose content never renders, a placeholder that should be gone); formatting changes only
/// how it reads, and belongs to the separate prettifier the user plans. Every rule here was kept or dropped on
/// measured frequency across 744 real item pages, so none of them is a hypothetical.
/// </summary>
public static class ComplianceChecker
{
    public const string DuplicateParameterRule = "duplicate parameter";
    public const string UnknownParameterRule = "unrecognized parameter";
    public const string MissingParameterRule = "missing parameter";
    public const string LorePlaceholderRule = "lore placeholder";
    public const string OnlyIncludeRule = "onlyinclude wrapper";
    public const string EraTemplateRule = "era template";

    /// <summary>
    /// Parameters the `Itempage` template understands. Anything else is silently ignored by MediaWiki, which is why
    /// an unrecognized name matters: the content is invisible, not merely misfiled.
    ///
    /// Note `recipes`, plural — exactly one sampled page writes `recipe`, and that page's recipe content does not
    /// render at all. That is the rule's whole justification.
    /// </summary>
    private static readonly string[] KnownParameters =
    [
        "itemname", "lucy_img_ID", "statsblock", "notes", "focus_effect", "merchant_value",
        "dropsfrom", "soldby", "relatedquests", "recipes", "playercrafted", "foraged", "bookcontents",
    ];

    /// <summary>Parameters an item page cannot do without. Measured at 0 pages missing any of these, so these are
    /// guards rather than a live problem — but a page missing one cannot be processed at all, and saying why beats
    /// failing obscurely.</summary>
    private static readonly string[] RequiredParameters = ["itemname", "statsblock", "lucy_img_ID"];

    public static IReadOnlyList<ComplianceFinding> Check(
        ItemPageDocument page, string wholePageWikitext, WikiMapping? mapping = null)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(wholePageWikitext);
        mapping ??= WikiMapping.Default;

        var findings = new List<ComplianceFinding>();

        // 3 of 744 pages. The tool fixes it, because a parameter that does not render is a defect in what the page
        // says — and the user's own routine already includes cleaning these up.
        foreach (string name in page.Template.Parameters
                     .Where(p => p.Name is not null)
                     .GroupBy(p => p.Name!, StringComparer.Ordinal)
                     .Where(g => g.Count() > 1)
                     .Select(g => g.Key))
            findings.Add(new ComplianceFinding(
                DuplicateParameterRule,
                $"|{name}= appears more than once. MediaWiki renders only the last, so the earlier copies are " +
                "invisible. They will be removed.",
                ToolWillFix: true));

        // 3 of 744 pages: one `recipe` (singular), one `gmitem`, one `style`.
        foreach (string name in page.Template.Parameters
                     .Select(p => p.Name)
                     .Where(n => n is not null && !KnownParameters.Contains(n, StringComparer.Ordinal))
                     .Select(n => n!)
                     .Distinct(StringComparer.Ordinal))
            findings.Add(new ComplianceFinding(
                UnknownParameterRule,
                $"|{name}= is not a parameter the template understands, so whatever it holds does not appear on " +
                "the page at all. Renaming it is a human's call — the closest known name may not be the intended " +
                "one, and guessing would move somebody's content somewhere they did not choose.",
                ToolWillFix: false));

        foreach (string name in RequiredParameters)
            if (string.IsNullOrWhiteSpace(page.GetParameter(name)))
                findings.Add(new ComplianceFinding(
                    MissingParameterRule,
                    $"|{name}= is missing or empty. An item page needs it.",
                    ToolWillFix: false));

        // 35 of 744 pages. Always removed when the tool touches a page: by then either lore has been captured, or
        // the item has no lore tab and so has no lore to add.
        if (page.HasLoreMissingPlaceholder)
            findings.Add(new ComplianceFinding(
                LorePlaceholderRule,
                "The page still carries {{Item Lore Missing}}. It will be removed — by the time this tool edits, " +
                "either the lore has been captured or the item has none.",
                ToolWillFix: true));

        // 4 of 744 pages. Without the wrapper the item box cannot be transcluded into lists elsewhere, so this is a
        // real defect — but repairing it means deciding exactly what the wrapper should enclose, which depends on
        // what else the page holds.
        if (!wholePageWikitext.Contains("<onlyinclude>", StringComparison.OrdinalIgnoreCase))
            findings.Add(new ComplianceFinding(
                OnlyIncludeRule,
                "The {{Itempage}} call is not wrapped in <onlyinclude>, so the item box cannot be transcluded " +
                "elsewhere. Adding the wrapper means deciding what it should enclose, which is a human's call.",
                ToolWillFix: false));

        // 232 of 744 pages — by far the most common defect, and one the tool can never fix: which expansion an item
        // belongs to is not visible in the item window. Reported so the user can supply it, never guessed.
        if (!HasEraTemplate(wholePageWikitext))
            findings.Add(new ComplianceFinding(
                EraTemplateRule,
                "The page has no era template ({{Classic Era}}, {{Kunark Era}}, ...). The item window does not say " +
                "which expansion an item is from, so this tool cannot supply it.",
                ToolWillFix: false));

        return findings;
    }

    /// <summary>Whether the page opens with an era banner. Matched by shape rather than against a list, because the
    /// wiki has twelve of them and adds more per expansion — `Classic`, `Velious`, `Kunark`, `Chardok Revamp`,
    /// `Sky`, `Temple`, `Epics`, `FearHateRevamp`, `EpicQuests`, `Paineel`, `Fear` and `Luclin` all occur — and a
    /// fixed list would report a brand-new era as missing.</summary>
    private static bool HasEraTemplate(string wikitext) =>
        WikitextScanner.FindTemplates(wikitext, "Era").Count > 0 ||
        System.Text.RegularExpressions.Regex.IsMatch(wikitext, @"\{\{\s*[A-Za-z0-9 ]+\s+Era\s*\}\}");
}
