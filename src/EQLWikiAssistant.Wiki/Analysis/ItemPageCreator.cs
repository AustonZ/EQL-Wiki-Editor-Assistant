using EQLWikiAssistant.Core.Items;
using EQLWikiAssistant.Wiki.Formatting;
using EQLWikiAssistant.Wiki.Mapping;
using EQLWikiAssistant.Wiki.Wikitext;

namespace EQLWikiAssistant.Wiki.Analysis;

/// <summary>
/// A whole page the tool proposes to create, for an item the wiki has never heard of.
///
/// Unlike <see cref="ProposedEdit"/> there is no original to diff against, which is the whole difference: the
/// review is of the text itself rather than of a change to somebody else's text.
/// </summary>
/// <param name="Title">The title to create at. Equal to the item's in-game name — see
/// <see cref="ItemPageCreator"/> for why the tool never invents a different one.</param>
/// <param name="Wikitext">The complete page source, already laid out by the formatting pass when it could be.</param>
/// <param name="Gaps">What is still wrong with the page about to be created, as the compliance checker sees it.
///
/// **Recomputed against the finished text, not inherited from the data pass** — which is the one place creation
/// cannot reuse the edit path's answer, and it was wrong before it was measured. The skeleton is by definition
/// missing every required parameter, so the data pass defers "statsblock is empty" and "lucy_img_ID is empty" for
/// a page that is about to have a statsblock written into it. Reported as-is, that told the user a freshly built
/// page was broken in a way it was not. Checking the result instead leaves exactly the gaps that are really
/// there — in practice the icon ID, which no capture can supply.</param>
/// <param name="FormattingRefusals">Why the layout pass declined, when it did. Empty on the ordinary path — a
/// freshly generated page has nothing in it the formatter cannot read — so a non-empty list here means the
/// generated text surprised the formatter and is worth a human reading the result before saving.</param>
public sealed record ProposedPage(
    string Title,
    string Wikitext,
    IReadOnlyList<string> Gaps,
    IReadOnlyList<string> FormattingRefusals,
    bool HasIconId)
{
    /// <summary>Says what this revision is, which for a creation is the whole story — there are no individual
    /// changes to enumerate the way an edit's summary does.</summary>
    public string Summary => "Created from the in-game item window";
}

/// <summary>
/// Builds a brand-new item page from a capture.
///
/// **It generates a blueprint skeleton and then runs the ordinary data pass over it**, rather than rendering a page
/// directly from the capture. That is the load-bearing decision here, and it is the one this codebase has learned
/// the hard way three times over: a second renderer would be a second home for every rule the analyzer and the
/// editor already own — how a flag line is spelled, where a new stat line goes, which categories an item earns,
/// what a merchant value looks like — and two copies of a rule drift until the two passes disagree about the same
/// page. Here the skeleton is simply a page on which *every* field is missing, so the existing comparison concludes
/// "add all of it" and the existing editor writes it. Nothing about item data is decided twice.
///
/// **The formatting pass runs before the proposal is shown, not after the save** (user, 2026-10-01): the goal is
/// one commit that lands a finished page, and a page generated in blueprint order from an empty skeleton is
/// something the formatter can always lay out. A formatting prompt still follows the save, because the text is
/// editable and a hand-edit can mangle the layout — but on the ordinary path it finds nothing to do.
///
/// **The title is always the item's own name.** The tool never invents a disambiguated or re-spelled title, for the
/// reason <see cref="PageTitle"/> and <c>ItemPageLookup</c> both document: the choice is permanent, it becomes the
/// URL, and an ordinary editor on this wiki cannot delete a page. Deciding whether a page should exist at a
/// different name is the user's call, and the pipeline refuses to offer creation at all where that question is live.
/// </summary>
public static class ItemPageCreator
{
    /// <summary>
    /// The empty page the data pass fills in: the template call with every blueprint parameter declared and blank.
    ///
    /// **Every parameter is declared, including the ones no capture can supply.** Three reasons, all of which point
    /// the same way. A declared-but-blank parameter is idiomatic on this wiki — 343 of 662 sampled pages have at
    /// least one. It gives the user a scaffold for the fields only a human can fill (<c>dropsfrom</c>,
    /// <c>soldby</c>, <c>relatedquests</c>), which on a brand-new page is most of the work the tool cannot do. And
    /// it means the data pass *updates* parameters in place rather than inserting them, so nothing is marked as an
    /// unpositioned line and the result needs no second pass to become blueprint-shaped.
    ///
    /// **No era banner and no categories**, deliberately: the compliance checker reports a missing banner and the
    /// analyzer reports every derivable category, so the ordinary data pass adds both. Putting them here would be
    /// the second home this class exists to avoid.
    ///
    /// **No in-world screenshot line.** v1 preserves one where it exists and generates none — the tool has no
    /// screenshot of the item in the world, only of its window.
    /// </summary>
    public static string Skeleton(string title, WikiMapping? mapping = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        mapping ??= WikiMapping.Default;

        var builder = new System.Text.StringBuilder();
        builder.Append("<onlyinclude>{{").Append(ItemPageDocument.TemplateName).Append('\n');
        foreach (string parameter in mapping.ParameterOrder)
        {
            builder.Append('|').Append(parameter).Append(" = ");
            // itemname is the one field the skeleton can fill, because creating the page at this title is what
            // makes it true. Leaving it blank would have the analyzer report it as missing and the editor decline
            // to write it: a name/title mismatch is reported rather than fixed on an existing page, and that rule
            // is right there and wrong here.
            if (string.Equals(parameter, mapping.ItemNameParameter, StringComparison.Ordinal))
                builder.Append(title);
            builder.Append('\n');
        }
        builder.Append("}}</onlyinclude>\n");
        return builder.ToString();
    }

    /// <summary>
    /// Builds the page, or null if the skeleton could not be read back — which would mean the generated template
    /// call is malformed, so refusing beats offering to create a page out of text the tool cannot parse.
    /// </summary>
    public static ProposedPage? Build(ParsedItem captured, string title, WikiMapping? mapping = null)
    {
        ArgumentNullException.ThrowIfNull(captured);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        mapping ??= WikiMapping.Default;

        ItemPageDocument? skeleton = ItemPageDocument.Parse(Skeleton(title, mapping));
        if (skeleton is null) return null;

        ItemPageAnalysis analysis = ItemPageAnalyzer.Analyze(captured, skeleton, title, mapping);
        ProposedEdit filled = ItemPageEditor.BuildEdit(skeleton, analysis, mapping);

        PrettifyResult formatted = ItemPagePrettifier.Format(filled.NewWikitext, mapping);

        // A refusal keeps the unformatted text rather than failing: it is correct, just not laid out, and the
        // formatting prompt after the save will offer it again. Losing the page over its indentation would be the
        // wrong trade.
        string wikitext = formatted.IsSafe ? formatted.Formatted : filled.NewWikitext;

        // Read the finished page back and ask what is still wrong with *it*. See ProposedPage.Gaps for why the data
        // pass's own answer cannot be used here. Re-parsing is also the check that the generated text is still a
        // readable item page after being filled in and laid out.
        ItemPageDocument? result = ItemPageDocument.Parse(wikitext);
        if (result is null) return null;

        return new ProposedPage(title, wikitext, GapsIn(result, wikitext, mapping), formatted.Refusals, HasIcon(result, mapping));
    }

    /// <summary>
    /// What is still wrong with a page, as the compliance checker sees it.
    ///
    /// **Shared with the commit path on purpose.** The text that gets saved is the text on screen, which the user
    /// may have edited — so "does this page still lack its icon ID?" has to be asked again of what was actually
    /// written, and asking it the same way both times is what keeps the review and the ledger row agreeing. A
    /// created page that still has a gap is recorded as needing a human rather than as done.
    /// </summary>
    public static IReadOnlyList<string> GapsIn(string wikitext, WikiMapping? mapping = null)
    {
        ArgumentNullException.ThrowIfNull(wikitext);
        ItemPageDocument? page = ItemPageDocument.Parse(wikitext);
        return page is null
            ? ["The page is not readable as an item page."]
            : GapsIn(page, wikitext, mapping ?? WikiMapping.Default);
    }

    /// <summary>Whether the page names an icon — the one field no capture can supply, so the gap every creation
    /// starts with.</summary>
    public static bool HasIconId(string wikitext, WikiMapping? mapping = null)
    {
        ArgumentNullException.ThrowIfNull(wikitext);
        mapping ??= WikiMapping.Default;
        return ItemPageDocument.Parse(wikitext) is { } page && HasIcon(page, mapping);
    }

    private static IReadOnlyList<string> GapsIn(ItemPageDocument page, string wikitext, WikiMapping mapping) =>
        [.. ComplianceChecker.Check(page, wikitext, mapping).Select(f => f.Detail)];

    private static bool HasIcon(ItemPageDocument page, WikiMapping mapping) =>
        !string.IsNullOrWhiteSpace(page.GetParameter(mapping.IconIdParameter));
}
