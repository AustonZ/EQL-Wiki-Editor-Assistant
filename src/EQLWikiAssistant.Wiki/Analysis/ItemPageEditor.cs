using EQLWikiAssistant.Wiki.Mapping;
using EQLWikiAssistant.Wiki.Wikitext;

namespace EQLWikiAssistant.Wiki.Analysis;

/// <summary>What kind of change the edit made to one thing. The order is the order a summary lists them in.</summary>
public enum EditChangeKind
{
    Removed,
    Added,
    Updated,
}

/// <summary>
/// One change an edit made, as a kind and the *name* of what changed — deliberately never its value.
///
/// **The value belongs in the wiki's own diff, not in the summary** (user, 2026-09-29). Naming values made a
/// routine legacy-page modernization read
/// `removed the lore placeholder, set {{Classic Era}}, added merchant value 8p 5g 7s 1c, flags Lore Equipped,
/// Attunable, Placeable and 2 more` where a human would have written `removed lore placeholder; added era, flags,
/// merchant value, Type; updated effect` — or, honestly, "Updated for EQL". Enumerating is cheap for the tool and
/// worth keeping, so the fix is to drop the values and the four-item truncation rather than to say less.
/// </summary>
public sealed record EditChange(EditChangeKind Kind, string What)
{
    public override string ToString() => $"{Verb(Kind)} {What}";

    internal static string Verb(EditChangeKind kind) => kind switch
    {
        EditChangeKind.Removed => "removed",
        EditChangeKind.Added => "added",
        _ => "updated",
    };
}

/// <summary>
/// The edit the tool proposes: the new page text, what it changed, and whether the result needs the prettifier.
/// </summary>
public sealed record ProposedEdit(
    string OriginalWikitext,
    string NewWikitext,
    IReadOnlyList<EditChange> Changes,
    IReadOnlyList<string> Deferred,
    bool NeedsReformatting)
{
    /// <summary>MediaWiki truncates an edit summary at 500 characters, so one is never allowed to reach that.</summary>
    private const int SummaryLimit = 450;

    public bool HasChanges => !string.Equals(OriginalWikitext, NewWikitext, StringComparison.Ordinal);

    /// <summary>
    /// A one-line edit summary for the wiki's history: the changes grouped by verb, as names only.
    ///
    /// **It names the changes and nothing else** (user, 2026-09-28). An earlier version prefixed every summary with
    /// "Updated from in-game data:", which is assumed — nobody should be writing item data they did not see in game
    /// — so it only made the summary longer and pushed the part that matters off the end of a history listing. The
    /// same reasoning later took the values out; see <see cref="EditChange"/>.
    ///
    /// **Grouping by verb is what makes listing everything affordable.** `added era, flags, merchant value, Type` is
    /// shorter than three of the old entries were, so the four-item cap it needed is gone. The character guard
    /// replacing it exists only because nothing else now bounds the length.
    /// </summary>
    public string Summary
    {
        get
        {
            if (Changes.Count == 0) return "No changes";

            // Duplicates are real and collapse deliberately: every added category is its own change, and a summary
            // saying "categories" once is the point of naming things rather than listing values.
            (EditChangeKind Kind, string What)[] items =
            [
                .. new[] { EditChangeKind.Removed, EditChangeKind.Added, EditChangeKind.Updated }
                    .SelectMany(kind => Changes
                        .Where(c => c.Kind == kind)
                        .Select(c => c.What)
                        .Distinct(StringComparer.Ordinal)
                        .Select(what => (kind, what)))
            ];

            for (int keep = items.Length; keep > 1; keep--)
            {
                string candidate = Render(items.Take(keep)) +
                                   (keep < items.Length ? $" and {items.Length - keep} more" : "");
                if (candidate.Length <= SummaryLimit) return candidate;
            }

            return Render(items.Take(1)) + (items.Length > 1 ? $" and {items.Length - 1} more" : "");
        }
    }

    private static string Render(IEnumerable<(EditChangeKind Kind, string What)> items) =>
        string.Join("; ", items
            .GroupBy(i => i.Kind)
            .Select(g => $"{EditChange.Verb(g.Key)} {string.Join(", ", g.Select(i => i.What))}"));
}

/// <summary>
/// Turns an <see cref="ItemPageAnalysis"/> into actual wikitext, under the minimal-edit rule.
///
/// **The rule, and the reason it is this shape** (user, 2026-09-25): a value that already exists is changed *in
/// place*, and anything genuinely new goes on its own line for the follow-up prettifier to position. Its virtue is
/// that it never requires the tool to understand a layout it did not create — and the pages it edits are pages
/// nobody formatted, where any cleverer placement rule would eventually do something surprising.
///
/// **This is the data edit, and it is deliberately not a tidy-up.** Formatting belongs to the separate prettifier
/// pass that runs afterwards; see CLAUDE.md for why that order is settled rather than arbitrary. The one thing this
/// owes that flow is honesty about when it has left something unformatted, which is what
/// <see cref="ProposedEdit.NeedsReformatting"/> reports.
///
/// **Nothing here decides anything.** Every judgement was already made by the analyzer and the compliance checker;
/// this only applies what they concluded, which is what keeps "what would change" reviewable separately from "how
/// it gets written".
/// </summary>
public static class ItemPageEditor
{
    /// <summary>
    /// Points a page's <c>lucy_img_ID</c> at a different icon, leaving everything else byte for byte.
    ///
    /// **The one edit in this file the analyzer did not decide**, and that is the point rather than an exception to
    /// it: the icon check is flag-only because the tool cannot tell a wrong page from an odd capture, so the
    /// judgement is the user's — they look at the two images and press the button (user, 2026-10-02, on
    /// <c>Molten Coil</c>, whose page says 765 against the library's 617). This function exists so that pressing it
    /// writes the id the same way every other value is written: in place through
    /// <see cref="ItemPageDocument.WithParameter"/>, so the diff shows the id changing and nothing else, and in the
    /// blueprint's order when the page has no such parameter at all.
    ///
    /// Returns the text unchanged if it cannot be read as an item page — the caller is editing what is on screen,
    /// which the user may have been typing into.
    /// </summary>
    public static string WithIconId(string wikitext, string iconId, WikiMapping? mapping = null)
    {
        ArgumentNullException.ThrowIfNull(wikitext);
        ArgumentException.ThrowIfNullOrWhiteSpace(iconId);
        mapping ??= WikiMapping.Default;

        return ItemPageDocument.Parse(wikitext) is { } page
            ? page.WithParameter(mapping.IconIdParameter, iconId, mapping.ParameterOrder).Wikitext
            : wikitext;
    }

    public static ProposedEdit BuildEdit(
        ItemPageDocument page,
        ItemPageAnalysis analysis,
        WikiMapping? mapping = null)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(analysis);
        mapping ??= WikiMapping.Default;

        string original = page.Wikitext;
        var changes = new List<EditChange>();
        var deferred = new List<string>();
        bool addedUnformattedLine = false;

        ItemPageDocument edited = page;

        // --- compliance fixes the tool owns ---------------------------------------------------------------
        foreach (ComplianceFinding finding in analysis.Compliance)
        {
            if (!finding.ToolWillFix)
            {
                deferred.Add(finding.Detail);
                continue;
            }

            switch (finding.Rule)
            {
                case ComplianceChecker.DuplicateParameterRule:
                    edited = edited.WithoutDuplicateParameters();
                    changes.Add(new EditChange(EditChangeKind.Removed, "duplicate parameters"));
                    break;
                case ComplianceChecker.LorePlaceholderRule:
                    edited = edited.WithoutLoreMissingPlaceholder();
                    changes.Add(new EditChange(EditChangeKind.Removed, "lore placeholder"));
                    break;
                case ComplianceChecker.EraTemplateRule:
                    edited = edited.WithEraTemplate(mapping.CurrentEra);
                    changes.Add(new EditChange(
                        page.CurrentEra is null ? EditChangeKind.Added : EditChangeKind.Updated, "era"));
                    break;
            }
        }

        // --- template parameters --------------------------------------------------------------------------
        foreach (FieldFinding finding in analysis.Findings.Where(f => f.IsChange))
        {
            if (finding.Field == ItemPageAnalyzer.MerchantValueField && finding.Captured is { } merchantValue)
            {
                // **A missing parameter is written, not deferred** (user, 2026-09-29). This used to be left for the
                // user on the grounds that placement is a judgement — but the blueprint makes that judgement, and
                // the case is common rather than exotic: a legacy page usually has no merchant_value at all, because
                // legacy EverQuest gave a player no easy way to learn a value. EQL states it in the window outright.
                bool creating = edited.Template.Find(mapping.MerchantValueParameter) is null;
                edited = edited.WithParameter(mapping.MerchantValueParameter, merchantValue, mapping.ParameterOrder);
                changes.Add(new EditChange(Kind(creating), "merchant value"));
                addedUnformattedLine |= creating;
            }
            else if (finding.Field == ItemPageAnalyzer.CategoryField && finding.Captured is { } category)
            {
                // Categories live outside the template call, at the end of the page — the second thing this editor
                // touches beyond it, after the era banner, and for the same reason: it is page furniture rather than
                // item data. Only ever added; see ItemPageDocument.WithCategory.
                edited = edited.WithCategory(category);
                changes.Add(new EditChange(EditChangeKind.Added, "categories"));
            }
            else if (finding.Field == ItemPageAnalyzer.LoreField && finding.Captured is { } lore)
            {
                // Only ever reached for MissingOnWiki: the analyzer reports a *differing* lore as NeedsReview, so
                // existing prose is never replaced. See AddLoreFinding for why that rule is narrower than the rest.
                bool creating = edited.Template.Find(mapping.NotesParameter) is null;
                edited = edited.WithLore(lore, mapping.ParameterOrder);
                changes.Add(new EditChange(EditChangeKind.Added, "lore"));
                addedUnformattedLine |= creating;
            }
            else if (finding.Field.EndsWith(" Effect", StringComparison.Ordinal) &&
                     mapping.IsFocusEffect(finding.Field[..^" Effect".Length]) &&
                     finding.Captured is { } focus)
            {
                bool creating = edited.Template.Find(mapping.FocusEffectParameter) is null;
                edited = edited.WithParameter(mapping.FocusEffectParameter, focus, mapping.ParameterOrder);
                changes.Add(new EditChange(Kind(creating), "focus effect"));
                addedUnformattedLine |= creating;
            }
        }

        // --- statsblock lines -----------------------------------------------------------------------------
        if (edited.ReadStatsBlock() is { } block)
        {
            foreach (FieldFinding finding in analysis.Findings.Where(f => f.IsChange))
            {
                if (finding.Captured is not { } wanted) continue;
                if (finding.Field is ItemPageAnalyzer.MerchantValueField or ItemPageAnalyzer.PageTitleField
                    or ItemPageAnalyzer.ItemNameField or ItemPageAnalyzer.FlagProseField
                    or ItemPageAnalyzer.LoreField
                    or ItemPageAnalyzer.CategoryField) continue; // all written outside the statsblock, above

                if (finding.Field == ItemPageAnalyzer.FlagsField)
                {
                    block = ReplaceFlagsLine(block, wanted, changes);
                    continue;
                }

                if (finding.Field.EndsWith(" Effect", StringComparison.Ordinal))
                {
                    block = WriteEffectLine(block, wanted, changes, ref addedUnformattedLine);
                    continue;
                }

                // An ordinary labelled field: change it where it stands, or add a line if it is genuinely new.
                int index = block.IndexOfField(finding.Field);
                if (index >= 0 && block.Lines[index].ReplaceFieldValue(finding.Field, wanted) is { } rewritten)
                {
                    block = block.ReplaceLine(index, rewritten);
                    changes.Add(new EditChange(EditChangeKind.Updated, finding.Field));
                }
                else
                {
                    block = block.InsertLine($"{finding.Field}: {wanted}");
                    changes.Add(new EditChange(EditChangeKind.Added, finding.Field));
                    addedUnformattedLine = true;
                }
            }

            edited = edited.WithStatsBlock(block);
        }

        return new ProposedEdit(
            original,
            edited.Wikitext,
            changes,
            deferred,
            // The prettifier is a v1 follow-up step, and this is what tells it (and the user) there is something to
            // do. The tool knows, because it knows when it added a line it did not position.
            NeedsReformatting: addedUnformattedLine);
    }

    private static EditChangeKind Kind(bool creating) =>
        creating ? EditChangeKind.Added : EditChangeKind.Updated;

    /// <summary>The flags line is regenerated whole rather than edited field by field: it has no labels to edit in
    /// place, and legacy flags are discarded rather than translated, so what replaces it shares nothing with what
    /// was there.</summary>
    private static StatsBlock ReplaceFlagsLine(StatsBlock block, string wanted, List<EditChange> changes)
    {
        int index = block.Lines.ToList().FindIndex(l => l.Kind == StatsLineKind.Flags);

        // An item with genuinely no flags — 20 of the 101 verified windows — loses the line entirely rather than
        // keeping an empty one, which would leave a bare `<br>` behind.
        if (string.IsNullOrWhiteSpace(wanted))
        {
            if (index < 0) return block;
            changes.Add(new EditChange(EditChangeKind.Removed, "flags"));
            return block.RemoveLine(index);
        }

        if (index < 0)
        {
            changes.Add(new EditChange(EditChangeKind.Added, "flags"));
            return block.InsertLine(wanted);
        }

        changes.Add(new EditChange(EditChangeKind.Updated, "flags"));
        return block.ReplaceLine(index, block.Lines[index].ReplaceText(wanted));
    }

    /// <summary>
    /// An effect line is replaced whole — the rendered line already contains its own `Effect:` label — and matched
    /// to the existing line by **the name that line displays**, so the right one is rewritten when a page has
    /// several.
    ///
    /// **Displayed name, not link target** (bug found by the user, 2026-10-03, on `Rain Caller`). Matching on the
    /// target meant a page writing `[[Firestrike_(Effect)|Firestrike]]` never matched the captured `Firestrike`, so
    /// this method fell through to its insert and the page ended up with two effect lines for one effect. The
    /// target is the page's own choice and the analyzer preserves it into <paramref name="wanted"/>; the displayed
    /// name is the only half a capture can compare against. See EffectLine.TryReadName.
    ///
    /// An unreadable name matches nothing rather than the first effect line it finds, which is how the old loop
    /// behaved: a rendered line always carries a link, so that branch could only ever have overwritten the wrong
    /// line.
    /// </summary>
    private static StatsBlock WriteEffectLine(
        StatsBlock block, string wanted, List<EditChange> changes, ref bool addedUnformattedLine)
    {
        if (EffectLine.TryReadName(wanted) is { } name)
            for (int i = 0; i < block.Lines.Count; i++)
            {
                StatsField? effect = block.Lines[i].Fields.FirstOrDefault(
                    f => string.Equals(f.Label, EffectLine.WikiLabel, StringComparison.OrdinalIgnoreCase));
                if (effect is null) continue;
                if (!string.Equals(EffectLine.TryReadName(effect.Value), name, StringComparison.OrdinalIgnoreCase))
                    continue;

                changes.Add(new EditChange(EditChangeKind.Updated, "effect"));
                return block.ReplaceLine(i, block.Lines[i].ReplaceText(wanted));
            }

        changes.Add(new EditChange(EditChangeKind.Added, "effect"));
        addedUnformattedLine = true;
        return block.InsertLine(wanted);
    }
}
