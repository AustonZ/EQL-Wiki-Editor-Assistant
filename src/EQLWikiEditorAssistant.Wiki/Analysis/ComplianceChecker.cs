using EQLWikiEditorAssistant.Wiki.Mapping;
using EQLWikiEditorAssistant.Wiki.Wikitext;

namespace EQLWikiEditorAssistant.Wiki.Analysis;

/// <summary>
/// One way a page departs from the current template.
///
/// <see cref="ToolWillFix"/> is the load-bearing distinction. A compliance problem the tool can correct is part of
/// the edit it proposes; one it cannot is reported and left alone. Nothing in between — the tool never half-fixes a
/// page, and never claims to have fixed something it only noticed.
///
/// <see cref="Wanted"/> and <see cref="OnPage"/> are set only where the finding is genuinely a two-sided comparison
/// — the era banner and the lore placeholder — so the review screen can show it as an ordinary row in its
/// differences table instead of a warning bar (user, 2026-09-29). They carry the values rather than leaving the UI
/// to read them back out of <see cref="Detail"/>'s prose, which would couple a display to a sentence's wording.
/// A finding with no second side, like a duplicate parameter, leaves both null and stays a warning.
/// </summary>
public sealed record ComplianceFinding(
    string Rule,
    string Detail,
    bool ToolWillFix,
    string? Wanted = null,
    string? OnPage = null);

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
                "Remove placeholder text",
                ToolWillFix: true,
                Wanted: null,
                OnPage: "{{Item Lore Missing}}"));

        // 4 of 744 pages. Without the wrapper the item box cannot be transcluded into lists elsewhere, so this is a
        // real defect — but repairing it means deciding exactly what the wrapper should enclose, which depends on
        // what else the page holds.
        if (!wholePageWikitext.Contains("<onlyinclude>", StringComparison.OrdinalIgnoreCase))
            findings.Add(new ComplianceFinding(
                OnlyIncludeRule,
                "The {{Itempage}} call is not wrapped in <onlyinclude>, so the item box cannot be transcluded " +
                "elsewhere. Adding the wrapper means deciding what it should enclose, which is your call.",
                ToolWillFix: false));

        // The most common defect by far — 232 of 744 pages have no banner and 176 more carry a legacy one — and,
        // since a capture is itself the confirmation that an item is in the game, the one the tool is most entitled
        // to fix.
        if (!page.HasEraTemplate(mapping.CurrentEra))
            findings.Add(new ComplianceFinding(
                EraTemplateRule,
                page.CurrentEra is null
                    ? $"The page has no era template. It will be set to {{{{{mapping.CurrentEra} Era}}}} — capturing " +
                      "the item in game is what confirms it is in the game."
                    : $"The page says {{{{{page.CurrentEra}}}}}. It will be set to {{{{{mapping.CurrentEra} Era}}}}, " +
                      "since this item was just seen in game.",
                ToolWillFix: true,
                Wanted: $"{{{{{mapping.CurrentEra} Era}}}}",
                OnPage: page.CurrentEra is null ? null : $"{{{{{page.CurrentEra}}}}}"));

        return findings;
    }
}
