using EQLWikiAssistant.Core.Items;
using EQLWikiAssistant.TestSupport;
using EQLWikiAssistant.TestSupport.Accuracy;
using EQLWikiAssistant.Wiki.Analysis;
using EQLWikiAssistant.Wiki.Wikitext;

// The accuracy scorer has its own FieldVerdict (correct/missing/wrong/extra — how well extraction did) which is a
// different question from this one (matches/differs/unverifiable — how the page compares to the capture). Both are
// in scope here only because this file loads real captures from the corpus.
using FieldVerdict = EQLWikiAssistant.Wiki.Analysis.FieldVerdict;

namespace EQLWikiAssistant.Tests.Wiki;

public class ItemPageAnalyzerTests
{
    private static ParsedItem Captured(
        string name = "Earring of Bashing",
        IReadOnlyList<string>? flags = null,
        IReadOnlyList<string>? classes = null,
        IReadOnlyList<string>? races = null,
        IReadOnlyList<string>? slots = null,
        IReadOnlyList<KeyValuePair<string, string>>? stats = null,
        string? merchantValue = null,
        string? lore = null) =>
        new(name, 0, false, flags ?? [], classes ?? [], races ?? [], slots ?? [],
            stats ?? [], [], [], merchantValue, lore, []);

    private static ItemPageAnalysis Analyze(ParsedItem captured, string fixture) =>
        ItemPageAnalyzer.Analyze(captured, ItemPageDocument.Parse(WikiFixtures.Load(fixture))!, fixture);

    // ---- the merchant-value rules, which are where a wrong answer does real damage ----

    /// <summary>The rule the corpus proves: all 63 No Trade windows show no merchant-value row, while all 12
    /// Attunable windows show a real one. So absence means untradeable, not worthless — and an attuned copy can
    /// never be re-captured with the price visible, so overwriting destroys the figure for good.</summary>
    [Fact]
    public void ANoTradeCaptureNeverOverwritesTheWikisMerchantValue()
    {
        ItemPageAnalysis analysis = Analyze(
            Captured(name: "Water Flask", flags: ["No Trade"], merchantValue: null), "Water Flask");

        FieldFinding finding = analysis.Find(ItemPageAnalyzer.MerchantValueField)!;
        Assert.Equal(FieldVerdict.Unverifiable, finding.Verdict);
        Assert.Equal("1s", finding.OnWiki);
        Assert.False(finding.IsChange);
        Assert.Contains("untradeable, not", finding.Explanation);
    }

    /// <summary>"absolutely nothing" is a *verified* worthless item, not a gap, so it is written. The two signals
    /// never co-occur in the corpus, which is what keeps them distinguishable.</summary>
    [Fact]
    public void AbsolutelyNothingIsAVerifiedValueAndGetsWritten()
    {
        ItemPageAnalysis analysis = Analyze(
            Captured(name: "Water Flask", merchantValue: "absolutely nothing"), "Water Flask");

        FieldFinding finding = analysis.Find(ItemPageAnalyzer.MerchantValueField)!;
        Assert.Equal(FieldVerdict.Differs, finding.Verdict);
        Assert.Equal("absolutely nothing", finding.Captured);
    }

    [Fact]
    public void ACapturedMerchantValueIsNormalizedAndReplacesTheWikis()
    {
        ItemPageAnalysis analysis = Analyze(
            Captured(name: "Water Flask", merchantValue: "2 gold 4 silver 8 copper"), "Water Flask");

        FieldFinding finding = analysis.Find(ItemPageAnalyzer.MerchantValueField)!;
        Assert.Equal(FieldVerdict.Differs, finding.Verdict);
        Assert.Equal("2g 4s 8c", finding.Captured);
        Assert.Equal("1s", finding.OnWiki);
        Assert.Contains("maximum price", finding.Explanation);
    }

    [Fact]
    public void AMatchingMerchantValueIsNoChange()
    {
        ItemPageAnalysis analysis = Analyze(Captured(name: "Water Flask", merchantValue: "1 silver"), "Water Flask");

        Assert.Equal(FieldVerdict.Matches, analysis.Find(ItemPageAnalyzer.MerchantValueField)!.Verdict);
    }

    // ---- flags ----

    /// <summary>An item natively Attunable shows No Trade once attuned, so the page's author saw a state this
    /// capture cannot. Keep theirs, and say so (the user may later want this pair treated as simply matching).</summary>
    [Fact]
    public void TheWikisAttunableBeatsACapturedNoTrade()
    {
        ItemPageDocument page = ItemPageDocument.Parse(
            "{{Itempage\n|itemname = Attuned Thing\n|statsblock = \nAttunable<br>\nClass: ALL<br>\n}}")!;

        ItemPageAnalysis analysis = ItemPageAnalyzer.Analyze(
            Captured(name: "Attuned Thing", flags: ["No Trade"], classes: ["ALL"]), page, "Attuned Thing");

        FieldFinding finding = analysis.Find(ItemPageAnalyzer.FlagsField)!;
        Assert.Equal(FieldVerdict.Unverifiable, finding.Verdict);
        Assert.False(finding.IsChange);
        Assert.Contains("once attuned", finding.Explanation);
    }

    /// <summary>The devs keep adding flags (No Pet, Heirloom, Free Storage), so an unfamiliar one is ordinary data
    /// that gets copied through — never a warning, and never validated against a list.</summary>
    [Fact]
    public void AnUnfamiliarFlagIsCopiedThroughWithoutComplaint()
    {
        ItemPageDocument page = ItemPageDocument.Parse(
            "{{Itempage\n|itemname = Odd Thing\n|statsblock = \nLore Equipped<br>\n}}")!;

        ItemPageAnalysis analysis = ItemPageAnalyzer.Analyze(
            Captured(name: "Odd Thing", flags: ["Lore Equipped", "Heirloom", "No Pet"]), page, "Odd Thing");

        FieldFinding finding = analysis.Find(ItemPageAnalyzer.FlagsField)!;
        Assert.Equal(FieldVerdict.Differs, finding.Verdict);
        Assert.Contains("Heirloom", finding.Captured);
        Assert.Contains("No Pet", finding.Captured);
        Assert.DoesNotContain(analysis.Findings, f => f.Verdict == FieldVerdict.NeedsReview && f.Field == ItemPageAnalyzer.FlagsField);
    }

    /// <summary>
    /// **Flag order is not part of what a flags line means** (user, 2026-09-29, on `Brell's Girdle`): the page lists
    /// them alphabetically and the game in its own order. Calling that a difference would rewrite a correct line, and
    /// would teach the user that flag findings are noise.
    /// </summary>
    [Fact]
    public void FlagsInADifferentOrderStillMatch()
    {
        ItemPageDocument page = ItemPageDocument.Parse(
            "{{Itempage\n|itemname = Brell's Girdle\n|statsblock = \nAttunable, Lore Equipped<br>\n}}")!;

        ItemPageAnalysis analysis = ItemPageAnalyzer.Analyze(
            Captured(name: "Brell's Girdle", flags: ["Lore Equipped", "Attunable"]),
            page,
            "Brell's Girdle");

        Assert.Equal(FieldVerdict.Matches, analysis.Find(ItemPageAnalyzer.FlagsField)!.Verdict);
    }

    /// <summary>And matching means the line is left exactly as the page wrote it — whether alphabetical order is the
    /// house style is a formatting question, not a data one.</summary>
    [Fact]
    public void ReorderedFlagsProduceNoEdit()
    {
        const string wikitext =
            "{{Itempage\n|itemname = Brell's Girdle\n|statsblock = \nAttunable, Lore Equipped<br>\n" +
            "Class: ALL<br>\nRace: ALL<br>\n}}";
        ItemPageDocument page = ItemPageDocument.Parse(wikitext)!;
        ParsedItem captured = Captured(
            name: "Brell's Girdle",
            flags: ["Lore Equipped", "Attunable"],
            classes: ["ALL"],
            races: ["ALL"]);

        ProposedEdit edit = ItemPageEditor.BuildEdit(page, ItemPageAnalyzer.Analyze(captured, page, "Brell's Girdle"));

        Assert.Contains("Attunable, Lore Equipped<br>", edit.NewWikitext);
    }

    /// <summary>A genuinely repeated flag is still a difference — the comparison is a multiset, not a set, so a page
    /// defect does not hide behind order-insensitivity.</summary>
    [Fact]
    public void ARepeatedFlagIsStillADifference()
    {
        ItemPageDocument page = ItemPageDocument.Parse(
            "{{Itempage\n|itemname = Odd Thing\n|statsblock = \nNo Trade, No Trade<br>\n}}")!;

        ItemPageAnalysis analysis = ItemPageAnalyzer.Analyze(
            Captured(name: "Odd Thing", flags: ["No Trade"]), page, "Odd Thing");

        Assert.Equal(FieldVerdict.Differs, analysis.Find(ItemPageAnalyzer.FlagsField)!.Verdict);
    }

    /// <summary>Legacy flags are dropped rather than translated: LORE ITEM (carry one) is a different property from
    /// Lore Equipped (equip one), and MAGIC ITEM has no counterpart at all.</summary>
    [Fact]
    public void LegacyFlagsAreReportedAsDiscarded()
    {
        ItemPageAnalysis analysis = Analyze(
            Captured(name: "Cloak of Scales", flags: ["Lore Equipped", "No Trade"]), "Cloak of Scales");

        FieldFinding finding = analysis.Find(ItemPageAnalyzer.LegacyFlagsField)!;
        Assert.Contains("MAGIC ITEM", finding.OnWiki);
        Assert.Contains("no current equivalent", finding.Explanation);
    }

    /// <summary>EQL dropped these strings but they may still mean something the UI stopped exposing — in original
    /// EverQuest "This is a hearty meal!" meant the food lasted longer. Raised for the user; never moved, never
    /// dropped silently.</summary>
    [Fact]
    public void FoodProseOnTheFlagsLineIsRaisedButNeverActedOn()
    {
        ItemPageAnalysis analysis = Analyze(Captured(name: "Arctic Mussels"), "Arctic Mussels");

        FieldFinding finding = analysis.Find(ItemPageAnalyzer.FlagProseField)!;
        Assert.Equal(FieldVerdict.NeedsReview, finding.Verdict);
        Assert.Contains("This is a meal!", finding.OnWiki);
        Assert.False(finding.IsChange);
        Assert.Contains("will not move or discard", finding.Explanation);
    }

    // ---- stats ----

    [Fact]
    public void MappedStatsAreComparedUnderTheirWikiLabels()
    {
        ItemPageAnalysis analysis = Analyze(
            Captured(stats:
            [
                new("Strength", "+8"),
                new("Wisdom", "+8"),
                new("AC", "5"),
                new("Weight", "0.1"),
                new("Size", "TINY"),
            ]),
            "Earring of Bashing");

        Assert.Equal(FieldVerdict.Matches, analysis.Find("STR")!.Verdict);
        Assert.Equal(FieldVerdict.Matches, analysis.Find("WIS")!.Verdict);
        Assert.Equal(FieldVerdict.Matches, analysis.Find("AC")!.Verdict);
        Assert.Equal(FieldVerdict.Matches, analysis.Find("WT")!.Verdict);
        Assert.Equal(FieldVerdict.Matches, analysis.Find("Size")!.Verdict);
    }

    /// <summary>The wiki writes a bonus as "+8" and the game as "8"; neither is more correct. Everything else is
    /// compared exactly, since this is the comparison that decides whether a number on a public wiki is
    /// overwritten.</summary>
    [Theory]
    [InlineData("8", "+8", FieldVerdict.Matches)]
    [InlineData("+8", "8", FieldVerdict.Matches)]
    [InlineData("8", "9", FieldVerdict.Differs)]
    [InlineData("0.1", "0.10", FieldVerdict.Differs)]
    public void OnlyALeadingPlusIsIgnoredWhenComparingValues(string captured, string onWiki, FieldVerdict expected)
    {
        ItemPageDocument page = ItemPageDocument.Parse(
            $"{{{{Itempage\n|itemname = Thing\n|statsblock = \nSTR: {onWiki}<br>\n}}}}")!;

        ItemPageAnalysis analysis = ItemPageAnalyzer.Analyze(
            Captured(name: "Thing", stats: [new("Strength", captured)]), page, "Thing");

        Assert.Equal(expected, analysis.Find("STR")!.Verdict);
    }

    /// <summary>A stat the capture has and the page lacks is safe to add — which is also how EQL's new Void resist
    /// reaches legacy pages.</summary>
    [Fact]
    public void AStatMissingFromThePageIsAnAddition()
    {
        ItemPageAnalysis analysis = Analyze(
            Captured(stats: [new("SV. Void", "+7")]), "Earring of Bashing");

        FieldFinding finding = analysis.Find("SV Void")!;
        Assert.Equal(FieldVerdict.MissingOnWiki, finding.Verdict);
        Assert.True(finding.IsChange);
    }

    /// <summary>Ratio is Base Dmg over Delay, both of which the wiki stores, so recording the quotient would be a
    /// third value to keep consistent for no gain. Silently ignored — reporting it would be noise on every weapon.</summary>
    [Fact]
    public void ADerivedStatIsIgnoredEntirely()
    {
        ItemPageAnalysis analysis = Analyze(Captured(stats: [new("Ratio", "2.5")]), "Earring of Bashing");

        Assert.Null(analysis.Find("Ratio"));
        Assert.DoesNotContain(analysis.Findings, f => f.Captured == "2.5");
    }

    /// <summary>The wiki writes attributes and resists with an explicit sign (`STR: +5`) where the game says `5`.
    /// Measured: STR is signed on 126 pages against 4 plain, SV FIRE 68 against 0, while WT is plain on all 721.</summary>
    [Theory]
    [InlineData("Strength", "5", "+5")]
    [InlineData("SV. Fire", "10", "+10")]
    [InlineData("HP", "55", "+55")]
    [InlineData("Strength", "-3", "-3")]        // an already-signed value keeps its own sign
    [InlineData("AC", "43", "43")]              // never signed on the wiki
    [InlineData("Weight", "0.4", "0.4")]
    [InlineData("Size", "MEDIUM", "MEDIUM")]    // not a number at all
    public void SignedFieldsAreProposedWithASign(string gameLabel, string capturedValue, string expected)
    {
        ItemPageAnalysis analysis = Analyze(Captured(stats: [new(gameLabel, capturedValue)]), "Earring of Bashing");

        Assert.Contains(analysis.Findings, f => f.Captured == expected);
    }

    /// <summary>On its own, a sign-only difference is not an edit. `STR: 5` reads unambiguously, so rewriting it to
    /// `+5` is the incidental reformatting this tool must leave to the prettifier.</summary>
    [Fact]
    public void ASignOnlyDifferenceAloneIsNotAnEdit()
    {
        // The era banner and lucy_img_ID are here only so the compliance rules stay quiet and IsClean means what
        // this test is actually asserting.
        ItemPageDocument page = ItemPageDocument.Parse(
            "{{Classic Era}}\n<onlyinclude>{{Itempage\n|itemname = Thing\n|lucy_img_ID = 1\n" +
            "|statsblock = \nSTR: 8<br>\n}}</onlyinclude>")!;

        ItemPageAnalysis analysis = ItemPageAnalyzer.Analyze(
            Captured(name: "Thing", stats: [new("Strength", "8")]), page, "Thing");

        Assert.Equal(FieldVerdict.Matches, analysis.Find("STR")!.Verdict);
        Assert.True(analysis.IsClean);
    }

    /// <summary>But if the edit is already writing another signed stat, leaving this one unsigned produces a line
    /// the tool itself made inconsistent — so it gets normalized too, and only then (user, 2026-09-25).</summary>
    [Fact]
    public void ASignOnlyDifferenceIsCorrectedWhenTheEditWouldOtherwiseBeInconsistent()
    {
        ItemPageDocument page = ItemPageDocument.Parse(
            "{{Itempage\n|itemname = Thing\n|statsblock = \nSTR: 8  WIS: +3<br>\n}}")!;

        // WIS genuinely changed; STR differs only by its missing sign.
        ItemPageAnalysis analysis = ItemPageAnalyzer.Analyze(
            Captured(name: "Thing", stats: [new("Strength", "8"), new("Wisdom", "9")]), page, "Thing");

        Assert.Equal(FieldVerdict.Differs, analysis.Find("WIS")!.Verdict);

        FieldFinding str = analysis.Find("STR")!;
        Assert.Equal(FieldVerdict.Differs, str.Verdict);
        Assert.Equal("+8", str.Captured);
        Assert.Contains("already writing another signed stat", str.Explanation);
    }

    /// <summary>An unsigned field changing does not drag the signs along — only an inconsistency *between signed
    /// stats* justifies touching them.</summary>
    [Fact]
    public void AnUnsignedFieldChangingDoesNotTriggerSignNormalization()
    {
        ItemPageDocument page = ItemPageDocument.Parse(
            "{{Itempage\n|itemname = Thing\n|statsblock = \nSTR: 8  AC: 5<br>\n}}")!;

        ItemPageAnalysis analysis = ItemPageAnalyzer.Analyze(
            Captured(name: "Thing", stats: [new("Strength", "8"), new("AC", "6")]), page, "Thing");

        Assert.Equal(FieldVerdict.Differs, analysis.Find("AC")!.Verdict);
        Assert.Equal(FieldVerdict.Matches, analysis.Find("STR")!.Verdict);
    }

    /// <summary>The game writes `Weight Red: 100` and the wiki `Weight Reduction: 100%`. Identical data — and without
    /// the suffix the comparison called them different and would have stripped the % off every such page.</summary>
    [Fact]
    public void AUnitSuffixIsAppliedAndDoesNotCountAsADifference()
    {
        ItemPageDocument page = ItemPageDocument.Parse(
            "{{Itempage\n|itemname = Bag\n|statsblock = \nWeight Reduction: 100%<br>\n}}")!;

        ItemPageAnalysis analysis = ItemPageAnalyzer.Analyze(
            Captured(name: "Bag", stats: [new("Weight Red", "100")]), page, "Bag");

        FieldFinding finding = analysis.Find("Weight Reduction")!;
        Assert.Equal(FieldVerdict.Matches, finding.Verdict);
        Assert.Equal("100%", finding.Captured);
    }

    /// <summary>Container properties the user placed in the statsblock (2026-09-25): Type marks an item usable for
    /// bashing, Items restricts what a container may hold.</summary>
    [Theory]
    [InlineData("Type", "Shield")]
    [InlineData("Items", "Arrows")]
    public void ContainerPropertiesAreMappedIntoTheStatsBlock(string label, string value)
    {
        ItemPageAnalysis analysis = Analyze(Captured(stats: [new(label, value)]), "Earring of Bashing");

        FieldFinding finding = analysis.Find(label)!;
        Assert.Equal(FieldVerdict.MissingOnWiki, finding.Verdict);
        Assert.Equal(value, finding.Captured);
    }

    /// <summary>Open/closed is already implied by the container fields the wiki does store, so it adds nothing and is
    /// ignored rather than reported (user, 2026-09-25).</summary>
    [Fact]
    public void ContainerOpenOrClosedIsIgnored()
    {
        ItemPageAnalysis analysis = Analyze(Captured(stats: [new("Container", "CLOSED.")]), "Earring of Bashing");

        Assert.Null(analysis.Find("Container"));
        Assert.DoesNotContain(analysis.Findings, f => f.Captured == "CLOSED.");
    }

    /// <summary>A slash-separated wiki value means somebody combined several items onto one page — the two real cases
    /// are ammo pages holding three arrow variants. The user's call (2026-09-25) is to surface it as a genuine
    /// mismatch, since the right fix is usually splitting the page.</summary>
    [Fact]
    public void AWikiValueListingSeveralAlternativesIsAMismatch()
    {
        ItemPageDocument page = ItemPageDocument.Parse(
            "{{Itempage\n|itemname = Arrow\n|statsblock = \nRange: 50 / 75 / 100<br>\n}}")!;

        ItemPageAnalysis analysis = ItemPageAnalyzer.Analyze(
            Captured(name: "Arrow", stats: [new("Range", "50")]), page, "Arrow");

        FieldFinding finding = analysis.Find("Range")!;
        Assert.Equal(FieldVerdict.Differs, finding.Verdict);
        Assert.Contains("combined into one page", finding.Explanation);
    }

    /// <summary>The important one: a stat with no mapping is never dropped. An unmapped stat is how a game patch
    /// announces itself, and discarding it would lose real data with nobody the wiser.</summary>
    [Fact]
    public void AnUnmappedStatIsReportedRatherThanDropped()
    {
        ItemPageAnalysis analysis = Analyze(Captured(stats: [new("Sharpness", "+4")]), "Earring of Bashing");

        FieldFinding novel = analysis.Find("Sharpness")!;
        Assert.Equal(FieldVerdict.NeedsReview, novel.Verdict);
        Assert.Contains("possibly new", novel.Explanation);
        Assert.False(novel.IsChange);
    }

    /// <summary>Accuracy was added to the game the week of 2026-09-22 and is correct to write (user, 2026-09-25), so
    /// no legacy page has it and every item showing one is an addition. It was the last entry in
    /// <c>UnmappedGameLabels</c>, which is now empty — this pins that it really is mapped.</summary>
    [Fact]
    public void AccuracyIsWrittenRatherThanQueried()
    {
        ItemPageAnalysis analysis = Analyze(Captured(stats: [new("Accuracy", "+13.6%")]), "Earring of Bashing");

        FieldFinding finding = analysis.Find("Accuracy")!;
        Assert.Equal(FieldVerdict.MissingOnWiki, finding.Verdict);
        Assert.Equal("+13.6%", finding.Captured);
        Assert.True(finding.IsChange);
    }

    // ---- effects ----

    /// <summary>Golden Efreeti Boots really does carry `| focus_effect = Enhancement Haste II`.</summary>
    [Fact]
    public void AFocusEffectIsComparedAgainstItsOwnTemplateParameter()
    {
        ParsedItem captured = Captured(name: "Golden Efreeti Boots") with
        {
            Effects = [new EffectEntry("Focus", "Enhancement Haste II", [], [])],
        };

        ItemPageAnalysis analysis = Analyze(captured, "Golden Efreeti Boots");

        Assert.Equal(FieldVerdict.Matches, analysis.Find("Focus Effect")!.Verdict);
    }

    /// <summary>Fishbone Earring's page has the legacy bare link: `Effect:  [[Enduring Breath]] (Worn)`. The effect
    /// and its details are right, but the link gets no tooltip — so this is a functional correction, not a style
    /// one, which is the opposite call from the sign case above.</summary>
    [Fact]
    public void ALegacyEffectLinkIsAFunctionalCorrection()
    {
        ParsedItem captured = Captured(name: "Fishbone Earring") with
        {
            Effects = [new EffectEntry("Worn", "Enduring Breath", [], [])],
        };

        ItemPageAnalysis analysis = Analyze(captured, "Fishbone Earring");

        FieldFinding finding = analysis.Find("Worn Effect")!;
        Assert.Equal(FieldVerdict.Differs, finding.Verdict);
        Assert.Equal("Effect: [[Enduring Breath|<span class='itemeff'>Enduring Breath</span>]] (Worn)", finding.Captured);
        Assert.Contains("no tooltip", finding.Explanation);
        Assert.Contains("functional fix", finding.Explanation);
    }

    /// <summary>
    /// **The two sides of a finding must be the same kind of thing**, since a review screen puts them in adjacent
    /// columns for a human to compare. The wiki side of an effect used to be reported without its `Effect:` label
    /// while the captured side kept it, so the screen showed a missing label as though *that* were the difference
    /// and drew the eye away from the real one — a missing cooldown (user, 2026-09-28).
    /// </summary>
    [Fact]
    public void BothSidesOfAnEffectFindingAreWholeLines()
    {
        ParsedItem captured = Captured(name: "Fishbone Earring") with
        {
            Effects = [new EffectEntry("Worn", "Enduring Breath", [], [])],
        };

        FieldFinding finding = Analyze(captured, "Fishbone Earring").Find("Worn Effect")!;

        Assert.StartsWith("Effect: ", finding.Captured);
        Assert.StartsWith("Effect: ", finding.OnWiki);
    }

    [Fact]
    public void AnEffectAlreadyInTheModernFormMatches()
    {
        ItemPageDocument page = ItemPageDocument.Parse(
            "{{Itempage\n|itemname = Thing\n|statsblock = \n" +
            "Effect: [[Burn|<span class='itemeff'>Burn</span>]] (Combat) at Level 10<br>\n}}")!;

        ParsedItem captured = Captured(name: "Thing") with
        {
            Effects = [new EffectEntry("Combat", "Burn", [], [new("Required Level", "10")])],
        };

        ItemPageAnalysis analysis = ItemPageAnalyzer.Analyze(captured, page, "Thing");

        Assert.Equal(FieldVerdict.Matches, analysis.Find("Combat Effect")!.Verdict);
    }

    /// <summary>An effect the convention cannot express yet is refused rather than written incomplete — writing the
    /// line without its cooldown would look finished while having lost real game data.</summary>
    [Fact]
    public void AnEffectTheConventionCannotExpressIsRefused()
    {
        ParsedItem captured = Captured(name: "Bladestopper") with
        {
            Effects = [new EffectEntry("Click", "Rune IV", [], [new("Charges Remaining", "3")])],
        };

        ItemPageAnalysis analysis = Analyze(captured, "Bladestopper");

        FieldFinding finding = analysis.Find("Click Effect")!;
        Assert.Equal(FieldVerdict.NeedsReview, finding.Verdict);
        Assert.False(finding.IsChange);
        Assert.Contains("Charges Remaining", finding.Explanation);
    }

    /// <summary>A cooldown used to be unexpressible and is not any more (user, 2026-09-25), so Bladestopper's click
    /// effect now renders rather than being held back.</summary>
    [Fact]
    public void AnEffectWithACooldownNowRenders()
    {
        ParsedItem captured = Captured(name: "Bladestopper") with
        {
            Effects = [new EffectEntry("Click", "Rune IV", [], [new("Cast Time", "Instant"), new("Cooldown", "600 sec")])],
        };

        FieldFinding finding = Analyze(captured, "Bladestopper").Find("Click Effect")!;

        Assert.Equal(FieldVerdict.Differs, finding.Verdict);
        Assert.Equal(
            "Effect: [[Rune IV|<span class='itemeff'>Rune IV</span>]] (Clicky, Casting Time: Instant, Cooldown: 600 sec)",
            finding.Captured);
    }

    // ---- lists ----

    [Fact]
    public void ClassAndRaceListsAreComparedAsSets()
    {
        ItemPageAnalysis analysis = Analyze(
            Captured(classes: ["BER", "WAR", "SHD", "SHM", "BST"], races: ["ALL"]), "Earring of Bashing");

        Assert.Equal(FieldVerdict.Matches, analysis.Find(ItemPageAnalyzer.ClassesField)!.Verdict);
        Assert.Equal(FieldVerdict.Matches, analysis.Find(ItemPageAnalyzer.RacesField)!.Verdict);
    }

    [Fact]
    public void ASlotTheCaptureDidNotSeeIsKept()
    {
        ItemPageAnalysis analysis = Analyze(Captured(slots: []), "Earring of Bashing");

        FieldFinding finding = analysis.Find(ItemPageAnalyzer.SlotsField)!;
        Assert.Equal(FieldVerdict.Unverifiable, finding.Verdict);
        Assert.Equal("EAR", finding.OnWiki);
        Assert.False(finding.IsChange);
    }

    // ---- page title ----

    /// <summary>Any itemname/title mismatch visibly breaks the item box, so it always needs a human — and the tool
    /// proposes nothing, because the in-game item genuinely shares one name across its variants.</summary>
    [Fact]
    public void AnItemnameTitleMismatchAlwaysNeedsAHuman()
    {
        ItemPageDocument page = ItemPageDocument.Parse(
            "{{Itempage\n|itemname = Essence of Barbarian\n|statsblock = \nClass: ALL<br>\n}}")!;

        ItemPageAnalysis analysis = ItemPageAnalyzer.Analyze(
            Captured(name: "Essence of Barbarian", classes: ["ALL"]), page, "Essence of Barbarian (Wormwood)");

        FieldFinding finding = analysis.Find(ItemPageAnalyzer.PageTitleField)!;
        Assert.Equal(FieldVerdict.NeedsReview, finding.Verdict);
        Assert.True(finding.Blocks);
        Assert.Contains("breaks the item box", finding.Explanation);
    }

    // ---- real captures against real pages ----

    /// <summary>
    /// The honest test: real captured data from the verified corpus against the real wikitext of the same item. Six
    /// fixtures have a ground-truth capture. This does not assert everything matches — the whole point of the tool
    /// is that pages are often out of date — only that the analysis runs, produces findings for the fields it
    /// should, and never proposes a change for a field the capture could not see.
    /// </summary>
    [Theory]
    [InlineData("Earring of Bashing")]
    [InlineData("Water Flask")]
    [InlineData("Golden Efreeti Boots")]
    [InlineData("Cloak of Scales")]
    [InlineData("Bladestopper")]
    [InlineData("Shimmering Ruby Stiletto")]
    public void RealCapturesAnalyzeAgainstTheirRealPages(string itemName)
    {
        if (!File.Exists(RepoPaths.ExpectedItemsFile)) return;

        ExpectedWindow? window = ExpectedCorpus.Load(RepoPaths.ExpectedItemsFile).Samples
            .SelectMany(s => s.Windows)
            .FirstOrDefault(w => !w.Occluded && w.Name == itemName);
        Assert.NotNull(window);

        ParsedItem captured = new(
            window.Name!, window.Level, window.TitleContentNameMismatch,
            window.Flags, window.Classes, window.Races, window.Slots,
            [.. window.Stats.Select(s => new KeyValuePair<string, string>(s.Label, s.Value))],
            [], [], window.MerchantValue, window.Lore, []);

        ItemPageAnalysis analysis = ItemPageAnalyzer.Analyze(
            captured, ItemPageDocument.Parse(WikiFixtures.Load(itemName))!, itemName);

        Assert.NotEmpty(analysis.Findings);

        // The invariant that matters regardless of how stale a page is: nothing the capture could not see is ever
        // proposed as a change.
        Assert.All(analysis.Findings.Where(f => f.Verdict == FieldVerdict.Unverifiable),
            f => Assert.False(f.IsChange));

        // And an Unverifiable finding always has the wiki's value to preserve; otherwise there was nothing to keep
        // and it should not have been reported at all.
        Assert.All(analysis.Findings.Where(f => f.Verdict == FieldVerdict.Unverifiable),
            f => Assert.False(string.IsNullOrWhiteSpace(f.OnWiki)));
    }

    // ---- which compliance findings a field finding already speaks for ----

    /// <summary>
    /// `Bladestopper` carries `{{Item Lore Missing}}`, and a capture of it with lore makes the analyzer report that
    /// lore against the page's nothing. The compliance finding then says the same thing again — and says it wrongly,
    /// since its account of the game side is "no lore" and this item has some. The review screen shows one row, not
    /// two (user, 2026-09-29).
    /// </summary>
    [Fact]
    public void TheLorePlaceholderIsCoveredByACapturedLoreFinding()
    {
        ItemPageAnalysis analysis = Analyze(
            Captured(name: "Bladestopper", lore: "A blade that stops blades."), "Bladestopper");

        ComplianceFinding placeholder = Assert.Single(
            analysis.Compliance.Where(c => c.Rule == ComplianceChecker.LorePlaceholderRule));

        Assert.NotNull(analysis.Find(ItemPageAnalyzer.LoreField));
        Assert.True(analysis.IsAlreadyCoveredByAFieldFinding(placeholder));
    }

    /// <summary>With no lore captured there is no lore finding at all, so the placeholder is the only thing that
    /// says it is going — which is the row the user asked to see, reading "no lore" against the page's
    /// placeholder.</summary>
    [Fact]
    public void TheLorePlaceholderStandsAloneWhenNoLoreWasCaptured()
    {
        ItemPageAnalysis analysis = Analyze(Captured(name: "Bladestopper"), "Bladestopper");

        ComplianceFinding placeholder = Assert.Single(
            analysis.Compliance.Where(c => c.Rule == ComplianceChecker.LorePlaceholderRule));

        Assert.Null(analysis.Find(ItemPageAnalyzer.LoreField));
        Assert.False(analysis.IsAlreadyCoveredByAFieldFinding(placeholder));
    }

    /// <summary>The era banner is never covered by a field finding — nothing else reports it — so it always reads as
    /// a row of its own.</summary>
    [Fact]
    public void TheEraBannerIsNeverCoveredByAFieldFinding()
    {
        ItemPageAnalysis analysis = Analyze(
            Captured(name: "Bladestopper", lore: "A blade that stops blades."), "Bladestopper");

        ComplianceFinding era = Assert.Single(
            analysis.Compliance.Where(c => c.Rule == ComplianceChecker.EraTemplateRule));

        Assert.False(analysis.IsAlreadyCoveredByAFieldFinding(era));
    }
}
