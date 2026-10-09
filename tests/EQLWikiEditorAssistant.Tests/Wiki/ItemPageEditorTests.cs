using EQLWikiEditorAssistant.Core.Items;
using EQLWikiEditorAssistant.Wiki.Analysis;
using EQLWikiEditorAssistant.Wiki.Wikitext;

namespace EQLWikiEditorAssistant.Tests.Wiki;

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
        IReadOnlyList<string>? slots = null,
        IReadOnlyList<KeyValuePair<string, string>>? stats = null,
        IReadOnlyList<EffectEntry>? effects = null,
        string? merchantValue = null) =>
        new(name, 0, false, flags ?? [], classes ?? [], races ?? [], slots ?? [], stats ?? [], [], effects ?? [],
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
        Assert.Contains(new EditChange(EditChangeKind.Added, "era"), edit.Changes);
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
    public void AMissingParameterIsWrittenInTheBlueprintsOrder()
    {
        ProposedEdit edit = Edit(
            Captured(name: "Cloak of Scales", merchantValue: "2 gold 4 silver"), "Cloak of Scales");

        Assert.Contains("|merchant_value = 2g 4s", edit.NewWikitext);
        Assert.Empty(edit.Deferred);

        // The blueprint puts merchant_value after statsblock and before dropsfrom, and that is where it lands even
        // though this page's own parameters are not in blueprint order (it opens with |notes=).
        int statsblock = edit.NewWikitext.IndexOf("|statsblock", StringComparison.Ordinal);
        int merchant = edit.NewWikitext.IndexOf("|merchant_value", StringComparison.Ordinal);
        int dropsfrom = edit.NewWikitext.IndexOf("|dropsfrom", StringComparison.Ordinal);
        Assert.True(statsblock < merchant && merchant < dropsfrom);
    }

    /// <summary>The anchor is what the blueprint places *before* the new parameter, not what it places after —
    /// which only shows on a page whose own order differs from the blueprint's. `Cloak of Scales` opens with
    /// `|notes=`, sixth in the blueprint, so anchoring on the first later parameter would drop the merchant value at
    /// the very top of the call instead of after the statsblock.</summary>
    [Fact]
    public void ANewParameterIsNotDraggedToTheTopByAnOutOfOrderPage()
    {
        ProposedEdit edit = Edit(
            Captured(name: "Cloak of Scales", merchantValue: "2 gold 4 silver"), "Cloak of Scales");

        Assert.True(
            edit.NewWikitext.IndexOf("|notes", StringComparison.Ordinal) <
            edit.NewWikitext.IndexOf("|merchant_value", StringComparison.Ordinal));
    }

    /// <summary>Adding a parameter is adding a line the tool did not position, so the formatting follow-up is what
    /// aligns it — the same composition every other new line uses.</summary>
    [Fact]
    public void AddingAParameterAsksForTheFormattingPass()
    {
        ProposedEdit edit = Edit(
            Captured(name: "Cloak of Scales", merchantValue: "2 gold 4 silver"), "Cloak of Scales");

        Assert.True(edit.NeedsReformatting);
    }

    /// <summary>
    /// The whole point of the minimal-edit rule: inserting a parameter must not disturb one byte of the rest of the
    /// page, including the aligned padding it deliberately does not match.
    ///
    /// **Moved off `Cloak of Scales` on 2026-10-01**, whose flags line is entirely legacy — the tool now removes
    /// such a line (it used to leave it, which was the bug), so that page no longer isolates a single insertion.
    /// `Earring of Bashing` writes its flags in the current dialect, so a capture agreeing with them leaves the
    /// flags line alone and the parameter is the only change.
    /// </summary>
    [Fact]
    public void InsertingAParameterLeavesEveryOtherLineByteForByte()
    {
        ProposedEdit edit = Edit(
            Captured(
                name: "Earring of Bashing",
                flags: ["Lore Equipped", "No Trade"],
                merchantValue: "2 gold 4 silver"),
            "Earring of Bashing");

        string[] before = edit.OriginalWikitext.Split('\n');
        string[] after = edit.NewWikitext.Split('\n');

        Assert.Equal(before.Length + 1, after.Length);
        Assert.Equal(before, after.Where(l => !l.StartsWith("|merchant_value", StringComparison.Ordinal)));
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

        // The summary names the changes and nothing else: "updated from in-game data" is assumed, and a prefix only
        // pushes the part that matters off the end of a history listing. It names *what* changed and not the value,
        // because the wiki's own diff shows the value (user, 2026-09-29). (This capture carries no flags, so losing
        // the flags line is a real second change and belongs in the summary.)
        Assert.Equal("removed flags; updated AC", edit.Summary);
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

    /// <summary>
    /// The shape the user reported on `Shield of the Stalwart Seas` (2026-09-29): a page still in Project1999 state,
    /// so nearly everything the tool touches changes at once. The old summary spelled every value out and then
    /// truncated —
    /// `removed the lore placeholder, set {{Classic Era}}, added merchant value 8p 5g 7s 1c, flags Lore Equipped,
    /// Attunable, Placeable and 2 more` — which is both longer than a human would write and cut off before the end.
    /// Grouped nouns fit the whole list in less space, and the wiki's diff is where the values belong.
    /// </summary>
    [Fact]
    public void TheSummaryGroupsChangesByVerbAndNamesNoValues()
    {
        ItemPageDocument page = ItemPageDocument.Parse(
            "{{Velious Era}}\n<onlyinclude>{{Itempage\n|notes       = {{Item Lore Missing}}\n" +
            "|itemname    = Shield of the Stalwart Seas\n|lucy_img_ID = 1234\n|statsblock  = \n" +
            "MAGIC ITEM  LORE ITEM  NO DROP<br>\nAC: 10<br>\nClass: ALL<br>\nRace: ALL<br>\n}}</onlyinclude>")!;

        ProposedEdit edit = ItemPageEditor.BuildEdit(
            page,
            ItemPageAnalyzer.Analyze(
                Captured(
                    name: "Shield of the Stalwart Seas",
                    flags: ["Lore Equipped", "Attunable", "Placeable"],
                    classes: ["ALL"],
                    races: ["ALL"],
                    stats: [new("AC", "12"), new("Type", "Shield")],
                    merchantValue: "8 platinum 5 gold 7 silver 1 copper"),
                page,
                "Shield of the Stalwart Seas"));

        Assert.Equal(
            "removed lore placeholder; added merchant value, Type; updated era, flags, AC",
            edit.Summary);
        Assert.True(edit.Summary.Length < 80);
    }

    /// <summary>
    /// Every added category is its own change, and a summary that listed them individually would be back where it
    /// started — a `Class: ALL` item earns sixteen. Naming the *kind* of thing that changed is what lets the summary
    /// list everything without a truncation cap.
    /// </summary>
    [Fact]
    public void RepeatedChangesOfOneKindCollapseInTheSummary()
    {
        ProposedEdit edit = Edit(
            Captured(
                flags: ["Lore Equipped", "No Trade"],
                classes: ["WAR", "CLR", "PAL"],
                slots: ["Ear"]),
            "Earring of Bashing");

        Assert.Contains(new EditChange(EditChangeKind.Added, "categories"), edit.Changes);
        Assert.True(edit.Changes.Count(c => c.What == "categories") > 1, "several categories should be added");
        Assert.Equal("added categories; updated Class", edit.Summary);
    }

    /// <summary>
    /// Nothing else bounds a summary's length now the four-item cap is gone, and MediaWiki truncates a comment at
    /// 500 characters — so an implausibly large edit has to trim itself rather than be trimmed by the server.
    /// </summary>
    [Fact]
    public void AnImplausiblyLargeSummaryTrimsItself()
    {
        var edit = new ProposedEdit(
            "a",
            "b",
            [.. Enumerable.Range(0, 200).Select(i => new EditChange(EditChangeKind.Updated, $"Field{i}"))],
            [],
            NeedsReformatting: false);

        Assert.True(edit.Summary.Length <= 450, $"summary was {edit.Summary.Length} characters");
        Assert.EndsWith(" more", edit.Summary);
        Assert.StartsWith("updated Field0, Field1, ", edit.Summary);
    }

    // --- pointing a page at a different icon ------------------------------------------------------------

    /// <summary>
    /// The id changes where it stands and the rest of the page is byte-identical — the minimal-edit rule, which
    /// matters as much for this value as any other: the user presses a button about an icon and must not be handed
    /// a reflowed page to review (user, 2026-10-02).
    /// </summary>
    [Fact]
    public void WithIconIdChangesTheIdInPlaceAndNothingElse()
    {
        string original = WikiFixtures.Load("Earring of Bashing");

        string edited = ItemPageEditor.WithIconId(original, "617");

        Assert.Contains("|lucy_img_ID = 617", edited);
        Assert.DoesNotContain("752", edited);
        // Everything either side of the value is untouched, which is what "in place" has to mean here.
        Assert.Equal(
            original.Replace("|lucy_img_ID = 752", "|lucy_img_ID = 617"),
            edited);
    }

    /// <summary>A page with no such parameter gets one in the blueprint's order, the minimal-edit rule's third
    /// case — not appended wherever it happened to be convenient.</summary>
    [Fact]
    public void WithIconIdWritesAMissingParameterInBlueprintOrder()
    {
        const string original = """
            {{Itempage
            |itemname = Molten Coil
            |statsblock = AC: 5<br>
            }}
            """;

        string edited = ItemPageEditor.WithIconId(original, "617");

        Assert.Contains("617", edited);
        Assert.True(
            edited.IndexOf("itemname", StringComparison.Ordinal) <
            edited.IndexOf("lucy_img_ID", StringComparison.Ordinal),
            edited);
        Assert.True(
            edited.IndexOf("lucy_img_ID", StringComparison.Ordinal) <
            edited.IndexOf("statsblock", StringComparison.Ordinal),
            edited);
    }

    /// <summary>Text that is not an item page comes back untouched. The caller is editing what is on screen, which
    /// the user may have been typing into — guessing at it would be worse than doing nothing.</summary>
    [Fact]
    public void WithIconIdLeavesSomethingItCannotReadAlone()
    {
        const string notAPage = "This page was never an item page.";

        Assert.Equal(notAPage, ItemPageEditor.WithIconId(notAPage, "617"));
    }
}
