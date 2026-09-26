using EQLWikiAssistant.Core.Items;
using EQLWikiAssistant.Wiki.Analysis;
using EQLWikiAssistant.Wiki.Wikitext;

namespace EQLWikiAssistant.Tests.Wiki;

/// <summary>
/// The minimal-edit rule as executable statements: an existing value changes where it stands, new content gets its
/// own line before `Class:`, and nothing else in the page moves. The last part is the one that matters most — this
/// tool edits pages nobody formatted, and an edit that quietly reflows them would bury the real change.
/// </summary>
public class ItemPageEditorTests
{
    private static ParsedItem Captured(
        string name = "Earring of Bashing",
        IReadOnlyList<string>? flags = null,
        IReadOnlyList<string>? classes = null,
        IReadOnlyList<string>? races = null,
        IReadOnlyList<KeyValuePair<string, string>>? stats = null,
        IReadOnlyList<EffectEntry>? effects = null,
        string? merchantValue = null) =>
        new(name, 0, false, flags ?? [], classes ?? [], races ?? [], [], stats ?? [], [], effects ?? [],
            merchantValue, null, []);

    private static ProposedEdit Edit(ParsedItem captured, string fixture)
    {
        string wikitext = WikiFixtures.Load(fixture);
        ItemPageDocument page = ItemPageDocument.Parse(wikitext)!;
        return ItemPageEditor.BuildEdit(page, ItemPageAnalyzer.Analyze(captured, page, fixture));
    }

    /// <summary>The core guarantee. `Earring of Bashing` has `AC: 5`; changing it to 6 must change exactly those
    /// bytes and leave the other 600-odd alone, including the aligned parameter padding and the dropsfrom list.</summary>
    [Fact]
    public void AChangedValueIsEditedInPlaceAndNothingElseMoves()
    {
        // The capture carries the page's own flags, so the only difference is AC — otherwise clearing the flags
        // would be a second, legitimate change and this would not be testing what it claims to.
        ProposedEdit edit = Edit(
            Captured(flags: ["Lore Equipped", "No Trade"], stats: [new("AC", "6")]), "Earring of Bashing");

        Assert.Equal(edit.OriginalWikitext.Replace("AC: 5<br>", "AC: 6<br>"), edit.NewWikitext);
        Assert.False(edit.NeedsReformatting);
    }

    /// <summary>An item with genuinely no flags — 20 of the 101 verified windows — loses the line rather than
    /// keeping an empty one, which would leave a bare `&lt;br&gt;` behind.</summary>
    [Fact]
    public void AnItemWithNoFlagsLosesItsFlagsLine()
    {
        ProposedEdit edit = Edit(Captured(flags: []), "Earring of Bashing");

        Assert.DoesNotContain("Lore Equipped, No Trade", edit.NewWikitext);
        Assert.DoesNotContain("statsblock  = \n<br>", edit.NewWikitext);
        Assert.Contains("statsblock  = \nSlot: EAR<br>", edit.NewWikitext);
    }

    /// <summary>The wiki writes signed stats, so a changed STR is written `+10` — but the rest of its line,
    /// including the double space before `WIS`, is untouched.</summary>
    [Fact]
    public void AChangedStatKeepsItsLineIntact()
    {
        ProposedEdit edit = Edit(Captured(stats: [new("Strength", "10")]), "Earring of Bashing");

        Assert.Contains("STR: +10  WIS: +8<br>", edit.NewWikitext);
        Assert.DoesNotContain("STR: +8", edit.NewWikitext);
    }

    /// <summary>New content goes on its own line before `Class:` — derived from the blueprint putting Class and Race
    /// last, so the diff reads naturally instead of stranding a stat after `Race: ALL`.</summary>
    [Fact]
    public void SomethingNewGoesOnItsOwnLineBeforeClass()
    {
        ProposedEdit edit = Edit(Captured(stats: [new("SV. Void", "7")]), "Earring of Bashing");

        Assert.Contains("SV Void: +7<br>\nClass: WAR SHD SHM BST BER<br>", edit.NewWikitext);
        Assert.True(edit.NeedsReformatting);
    }

    /// <summary>...and it says so, because the prettifier follow-up is only reliable if the tool reports when it has
    /// left something unpositioned.</summary>
    [Fact]
    public void AnInPlaceEditAloneDoesNotNeedReformatting()
    {
        ProposedEdit edit = Edit(Captured(stats: [new("AC", "6")]), "Earring of Bashing");

        Assert.True(edit.HasChanges);
        Assert.False(edit.NeedsReformatting);
    }

    /// <summary>A page with no `Class:` line falls back to appending, rather than failing or guessing.</summary>
    [Fact]
    public void WithoutAClassLineANewLineIsAppended()
    {
        ItemPageDocument page = ItemPageDocument.Parse(
            "{{Classic Era}}\n<onlyinclude>{{Itempage\n|itemname = Thing\n|lucy_img_ID = 1\n" +
            "|statsblock = \nAC: 5<br>\n}}</onlyinclude>")!;

        ProposedEdit edit = ItemPageEditor.BuildEdit(
            page,
            ItemPageAnalyzer.Analyze(Captured(name: "Thing", stats: [new("HP", "20")]), page, "Thing"));

        Assert.Contains("AC: 5<br>\nHP: +20<br>\n", edit.NewWikitext);
    }

    /// <summary>The flags line is regenerated whole: it has no labels to edit in place, and legacy flags are
    /// discarded rather than translated, so nothing of the old line survives.</summary>
    [Fact]
    public void TheFlagsLineIsReplacedWhole()
    {
        ProposedEdit edit = Edit(
            Captured(name: "Cloak of Scales", flags: ["Lore Equipped", "No Trade"]), "Cloak of Scales");

        Assert.Contains("Lore Equipped, No Trade<br>", edit.NewWikitext);
        Assert.DoesNotContain("MAGIC ITEM", edit.NewWikitext);
        // The rest of the block is untouched — the stat line keeps its single spacing and the stray space after AC.
        Assert.Contains("STR: +10 WIS: +10 INT: +10 HP: +50 MANA: +50<br>", edit.NewWikitext);
        Assert.Contains("AC: 15 <br>", edit.NewWikitext);
    }

    /// <summary>A legacy effect link is rewritten in place, matched to the existing line by the effect's name so the
    /// right one is replaced on a page carrying several.</summary>
    [Fact]
    public void AnEffectLineIsRewrittenInPlace()
    {
        ProposedEdit edit = Edit(
            Captured(name: "Fishbone Earring", effects: [new EffectEntry("Worn", "Enduring Breath", [], [])]),
            "Fishbone Earring");

        Assert.Contains(
            "Effect: [[Enduring Breath|<span class='itemeff'>Enduring Breath</span>]] (Worn)<br>",
            edit.NewWikitext);
        Assert.DoesNotContain("[[Enduring Breath]] (Worn)", edit.NewWikitext);
        Assert.False(edit.NeedsReformatting);
    }

    // --- compliance fixes -------------------------------------------------------------------------------

    /// <summary>`Bladestopper` has no era banner and carries the lore placeholder; both are fixed, and the human's
    /// own commentary in the same parameter survives.</summary>
    [Fact]
    public void ComplianceFixesAreApplied()
    {
        ProposedEdit edit = Edit(Captured(name: "Bladestopper"), "Bladestopper");

        Assert.StartsWith("{{Classic Era}}\n", edit.NewWikitext);
        Assert.DoesNotContain("Item Lore Missing", edit.NewWikitext);
        Assert.Contains("This charged source of Rune is <s>valuable for increasing hate", edit.NewWikitext);
        Assert.Contains(edit.Changes, c => c.Contains("Classic Era"));
    }

    /// <summary>A legacy era banner is replaced rather than added alongside — the item was just seen in game, so it
    /// is Classic whatever the page inherited.</summary>
    [Fact]
    public void ALegacyEraBannerIsReplaced()
    {
        ItemPageDocument page = ItemPageDocument.Parse(
            "{{Velious Era}}\n<onlyinclude>{{Itempage\n|itemname = Thing\n|lucy_img_ID = 1\n" +
            "|statsblock = \nClass: ALL<br>\n}}</onlyinclude>")!;

        ProposedEdit edit = ItemPageEditor.BuildEdit(
            page, ItemPageAnalyzer.Analyze(Captured(name: "Thing", classes: ["ALL"]), page, "Thing"));

        Assert.StartsWith("{{Classic Era}}", edit.NewWikitext);
        Assert.DoesNotContain("Velious", edit.NewWikitext);
    }

    // --- what it refuses to do --------------------------------------------------------------------------

    /// <summary>A parameter that does not exist has no line to change in place, and inventing a position is a
    /// judgement the tool declines — so it is deferred to the user rather than guessed at.</summary>
    [Fact]
    public void AMissingParameterIsDeferredRatherThanInvented()
    {
        ProposedEdit edit = Edit(
            Captured(name: "Cloak of Scales", merchantValue: "2 gold 4 silver"), "Cloak of Scales");

        Assert.DoesNotContain("merchant_value", edit.NewWikitext);
        Assert.Contains(edit.Deferred, d => d.Contains("merchant_value") && d.Contains("Add the parameter first"));
    }

    /// <summary>Compliance the tool cannot fix is reported, never attempted. `Cloak of Scales` has no
    /// `lucy_img_ID`-style problem, so use a page missing the `<onlyinclude>` wrapper.</summary>
    [Fact]
    public void ComplianceTheToolCannotFixIsDeferred()
    {
        ItemPageDocument page = ItemPageDocument.Parse(
            "{{Classic Era}}\n{{Itempage\n|itemname = Thing\n|lucy_img_ID = 1\n|statsblock = \nClass: ALL<br>\n}}")!;

        ProposedEdit edit = ItemPageEditor.BuildEdit(
            page, ItemPageAnalyzer.Analyze(Captured(name: "Thing", classes: ["ALL"]), page, "Thing"));

        Assert.Contains(edit.Deferred, d => d.Contains("onlyinclude"));
        Assert.DoesNotContain("<onlyinclude>", edit.NewWikitext);
    }

    /// <summary>A page that already agrees produces no edit at all — not a no-op rewrite, which would still show as
    /// a revision in the page's history.</summary>
    [Fact]
    public void AnAlreadyCorrectPageProducesNoEdit()
    {
        ProposedEdit edit = Edit(
            Captured(
                name: "Earring of Bashing",
                flags: ["Lore Equipped", "No Trade"],
                classes: ["WAR", "SHD", "SHM", "BST", "BER"],
                races: ["ALL"],
                stats:
                [
                    new("AC", "5"), new("Strength", "8"), new("Wisdom", "8"),
                    new("Weight", "0.1"), new("Size", "TINY"),
                ]),
            "Earring of Bashing");

        Assert.False(edit.HasChanges);
        Assert.Equal(edit.OriginalWikitext, edit.NewWikitext);
    }

    [Fact]
    public void TheEditSummaryNamesWhatChanged()
    {
        ProposedEdit edit = Edit(Captured(stats: [new("AC", "6")]), "Earring of Bashing");

        Assert.Contains("AC 6", edit.Summary);
        Assert.StartsWith("Updated from in-game data:", edit.Summary);
    }

    /// <summary>Every fixture must survive an edit that changes nothing about it — the same byte-for-byte property
    /// the rest of the wikitext layer guarantees, now through the editor.</summary>
    [Theory]
    [MemberData(nameof(Pages))]
    public void AnEditThatChangesNothingLeavesThePageIdentical(string title)
    {
        string wikitext = WikiFixtures.Load(title);
        ItemPageDocument page = ItemPageDocument.Parse(wikitext)!;

        // The precondition is "the analysis found nothing to do" — not "the capture was empty". An empty capture is
        // not a no-op: an item with genuinely no flags should lose its flags line, which is a real change.
        ItemPageAnalysis analysis = ItemPageAnalyzer.Analyze(Captured(name: page.ItemName!), page, title);
        if (analysis.Changes.Any() || analysis.Compliance.Any(c => c.ToolWillFix)) return;

        Assert.Equal(wikitext, ItemPageEditor.BuildEdit(page, analysis).NewWikitext);
    }

    public static TheoryData<string> Pages => WikiFixtures.AllTitles();
}
