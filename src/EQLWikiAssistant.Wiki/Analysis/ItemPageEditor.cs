using EQLWikiAssistant.Wiki.Mapping;
using EQLWikiAssistant.Wiki.Wikitext;

namespace EQLWikiAssistant.Wiki.Analysis;

/// <summary>
/// The edit the tool proposes: the new page text, what it changed, and whether the result needs the prettifier.
/// </summary>
public sealed record ProposedEdit(
    string OriginalWikitext,
    string NewWikitext,
    IReadOnlyList<string> Changes,
    IReadOnlyList<string> Deferred,
    bool NeedsReformatting)
{
    public bool HasChanges => !string.Equals(OriginalWikitext, NewWikitext, StringComparison.Ordinal);

    /// <summary>
    /// A one-line edit summary for the wiki's history.
    ///
    /// **It names the changes and nothing else** (user, 2026-09-28). An earlier version prefixed every summary with
    /// "Updated from in-game data:", which is assumed — nobody should be writing item data they did not see in game
    /// — so it only made the summary longer and pushed the part that matters off the end of a history listing.
    /// </summary>
    public string Summary =>
        Changes.Count == 0
            ? "No changes"
            : string.Join(", ", Changes.Take(4)) +
              (Changes.Count > 4 ? $" and {Changes.Count - 4} more" : "");
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
    public static ProposedEdit BuildEdit(
        ItemPageDocument page,
        ItemPageAnalysis analysis,
        WikiMapping? mapping = null)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(analysis);
        mapping ??= WikiMapping.Default;

        string original = page.Wikitext;
        var changes = new List<string>();
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
                    changes.Add("removed duplicate parameters");
                    break;
                case ComplianceChecker.LorePlaceholderRule:
                    edited = edited.WithoutLoreMissingPlaceholder();
                    changes.Add("removed the lore placeholder");
                    break;
                case ComplianceChecker.EraTemplateRule:
                    edited = edited.WithEraTemplate(mapping.CurrentEra);
                    changes.Add($"set {{{{{mapping.CurrentEra} Era}}}}");
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
                changes.Add(creating ? $"added merchant value {merchantValue}" : $"merchant value {merchantValue}");
                addedUnformattedLine |= creating;
            }
            else if (finding.Field == ItemPageAnalyzer.CategoryField && finding.Captured is { } category)
            {
                // Categories live outside the template call, at the end of the page — the second thing this editor
                // touches beyond it, after the era banner, and for the same reason: it is page furniture rather than
                // item data. Only ever added; see ItemPageDocument.WithCategory.
                edited = edited.WithCategory(category);
                changes.Add($"added [[Category:{category}]]");
            }
            else if (finding.Field == ItemPageAnalyzer.LoreField && finding.Captured is { } lore)
            {
                // Only ever reached for MissingOnWiki: the analyzer reports a *differing* lore as NeedsReview, so
                // existing prose is never replaced. See AddLoreFinding for why that rule is narrower than the rest.
                bool creating = edited.Template.Find(mapping.NotesParameter) is null;
                edited = edited.WithLore(lore, mapping.ParameterOrder);
                changes.Add("added the item's lore");
                addedUnformattedLine |= creating;
            }
            else if (finding.Field.EndsWith(" Effect", StringComparison.Ordinal) &&
                     mapping.IsFocusEffect(finding.Field[..^" Effect".Length]) &&
                     finding.Captured is { } focus)
            {
                bool creating = edited.Template.Find(mapping.FocusEffectParameter) is null;
                edited = edited.WithParameter(mapping.FocusEffectParameter, focus, mapping.ParameterOrder);
                changes.Add(creating ? $"added focus effect {focus}" : $"focus effect {focus}");
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
                    block = ReplaceFlagsLine(block, wanted, ref changes);
                    continue;
                }

                if (finding.Field == ItemPageAnalyzer.LegacyFlagsField) continue; // handled with the flags line

                if (finding.Field.EndsWith(" Effect", StringComparison.Ordinal))
                {
                    block = WriteEffectLine(block, wanted, ref changes, ref addedUnformattedLine);
                    continue;
                }

                // An ordinary labelled field: change it where it stands, or add a line if it is genuinely new.
                int index = block.IndexOfField(finding.Field);
                if (index >= 0 && block.Lines[index].ReplaceFieldValue(finding.Field, wanted) is { } rewritten)
                {
                    block = block.ReplaceLine(index, rewritten);
                    changes.Add($"{finding.Field} {wanted}");
                }
                else
                {
                    block = block.InsertLine($"{finding.Field}: {wanted}");
                    changes.Add($"added {finding.Field} {wanted}");
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

    /// <summary>The flags line is regenerated whole rather than edited field by field: it has no labels to edit in
    /// place, and legacy flags are discarded rather than translated, so what replaces it shares nothing with what
    /// was there.</summary>
    private static StatsBlock ReplaceFlagsLine(StatsBlock block, string wanted, ref List<string> changes)
    {
        int index = block.Lines.ToList().FindIndex(l => l.Kind == StatsLineKind.Flags);

        // An item with genuinely no flags — 20 of the 101 verified windows — loses the line entirely rather than
        // keeping an empty one, which would leave a bare `<br>` behind.
        if (string.IsNullOrWhiteSpace(wanted))
        {
            if (index < 0) return block;
            changes.Add("removed the flags line");
            return block.RemoveLine(index);
        }

        if (index < 0)
        {
            changes.Add($"added flags {wanted}");
            return block.InsertLine(wanted);
        }

        changes.Add($"flags {wanted}");
        return block.ReplaceLine(index, block.Lines[index].ReplaceText(wanted));
    }

    /// <summary>An effect line is replaced whole — the rendered line already contains its own `Effect:` label — and
    /// matched to the existing line by the effect's name, so the right one is rewritten when a page has several.</summary>
    private static StatsBlock WriteEffectLine(
        StatsBlock block, string wanted, ref List<string> changes, ref bool addedUnformattedLine)
    {
        string? name = EffectLine.TryReadName(wanted);

        for (int i = 0; i < block.Lines.Count; i++)
        {
            StatsField? effect = block.Lines[i].Fields
                .FirstOrDefault(f => string.Equals(f.Label, EffectLine.WikiLabel, StringComparison.OrdinalIgnoreCase));
            if (effect is null) continue;
            if (name is not null && !string.Equals(EffectLine.TryReadName(effect.Value), name, StringComparison.OrdinalIgnoreCase))
                continue;

            changes.Add($"effect {name}");
            return block.ReplaceLine(i, block.Lines[i].ReplaceText(wanted));
        }

        changes.Add($"added effect {name}");
        addedUnformattedLine = true;
        return block.InsertLine(wanted);
    }
}
