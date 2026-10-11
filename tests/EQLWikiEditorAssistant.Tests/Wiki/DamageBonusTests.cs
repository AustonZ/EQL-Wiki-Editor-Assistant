using EQLWikiEditorAssistant.Core.Items;
using EQLWikiEditorAssistant.Wiki.Analysis;
using EQLWikiEditorAssistant.Wiki.Mapping;
using EQLWikiEditorAssistant.Wiki.Wikitext;

namespace EQLWikiEditorAssistant.Tests.Wiki;

/// <summary>
/// Damage bonus is never written and is removed from a page that has it (user, 2026-10-11): the wiki calculates it
/// for the reader's level, a value on the page overrides that, and the game shows it at the capturing character's
/// level, which the Assistant does not know. The page shapes below are the live wiki's.
/// </summary>
public class DamageBonusTests
{
    private static ParsedItem Captured(IReadOnlyList<KeyValuePair<string, string>> stats) =>
        new("Test Blade", 0, false, [], ["WAR"], [], [], stats, [], [], null, null, []);

    private static string Page(string statsblock) =>
        "{{Itempage\n|itemname = Test Blade\n|statsblock = \n" + statsblock + "|notes = \n}}";

    private static (ItemPageAnalysis Analysis, ProposedEdit Edit) Check(
        string statsblock, IReadOnlyList<KeyValuePair<string, string>>? stats = null)
    {
        ItemPageDocument page = ItemPageDocument.Parse(Page(statsblock))!;
        ItemPageAnalysis analysis = ItemPageAnalyzer.Analyze(Captured(stats ?? [new("Dmg Bon", "13")]), page, "Test Blade");
        return (analysis, ItemPageEditor.BuildEdit(page, analysis));
    }

    [Fact]
    public void AnAnnotatedValueBetweenTwoFieldsIsTakenOutAndTheRestOfTheLineStays()
    {
        (ItemPageAnalysis analysis, ProposedEdit edit) =
            Check("DMG: 10  DMG Bonus: 11 @ lvl 50  BACKSTAB: 10<br>\nClass: WAR<br>\n");

        Assert.Contains("DMG: 10  BACKSTAB: 10<br>\n", edit.NewWikitext);
        Assert.DoesNotContain("Bonus", edit.NewWikitext);
        Assert.Contains(new EditChange(EditChangeKind.Removed, "DMG Bonus"), edit.Changes);

        FieldFinding finding = Assert.Single(analysis.Findings, f => f.Field == "DMG Bonus");
        Assert.Equal(FieldVerdict.Differs, finding.Verdict);
        Assert.Null(finding.Captured);
        Assert.Equal("11 @ lvl 50", finding.OnWiki);
    }

    /// <summary>The last field on a line takes the spacing before it, so no gap is left before the break.</summary>
    [Fact]
    public void ALastFieldOnASingleSpacedLineLeavesNoTrailingGap()
    {
        (_, ProposedEdit edit) = Check("Skill: 1H Blunt  Atk Delay: 28 DMG Bonus: 13<br>\nClass: WAR<br>\n");

        Assert.Contains("Skill: 1H Blunt  Atk Delay: 28<br>\n", edit.NewWikitext);
    }

    /// <summary>`Pick` writes the game's own spelling on a line of its own; the line goes rather than leaving a bare
    /// break behind.</summary>
    [Fact]
    public void AFieldAloneOnItsLineTakesTheLineWithIt()
    {
        (_, ProposedEdit edit) = Check("DMG: 6 <br>\nDmg Bon: 17<br>\nClass: WAR<br>\n");

        Assert.Contains("DMG: 6 <br>\nClass: WAR<br>\n", edit.NewWikitext);
        Assert.Contains(new EditChange(EditChangeKind.Removed, "Dmg Bon"), edit.Changes);
    }

    /// <summary>The game's value is never written, which is also what keeps it off a page the Assistant creates —
    /// creation runs this same pass over an empty skeleton.</summary>
    [Fact]
    public void TheCapturedValueIsNeverWritten()
    {
        (ItemPageAnalysis analysis, ProposedEdit edit) = Check("DMG: 10<br>\nClass: WAR<br>\n",
            [new("Base Dmg", "10"), new("Dmg Bon", "13")]);

        Assert.DoesNotContain("Bon", edit.NewWikitext);
        Assert.DoesNotContain(analysis.Findings, f => f.Field.Contains("Bon", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Removed whatever the capture says — an item captured without one still loses it.</summary>
    [Fact]
    public void ItIsRemovedEvenWhenTheCaptureShowsNone()
    {
        (_, ProposedEdit edit) = Check("DMG: 19 Dmg Bonus: 6 AC: 50<br>\nClass: WAR<br>\n", [new("Base Dmg", "19")]);

        Assert.Contains("DMG: 19 AC: 50<br>\n", edit.NewWikitext);
    }

    /// <summary>The formatter gives it no place, so a page that kept one is reported rather than laid out as if the
    /// blueprint still wanted it.</summary>
    [Fact]
    public void TheFormatterHasNoLineForIt()
    {
        Assert.DoesNotContain(WikiMapping.Default.StatsBlockLineOrder.SelectMany(line => line),
            label => WikiMapping.Default.IsRemovedStatLabel(label));
    }
}
