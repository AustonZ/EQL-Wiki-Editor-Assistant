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
        string? merchantValue = null) =>
        new(name, 0, false, flags ?? [], classes ?? [], races ?? [], slots ?? [],
            stats ?? [], [], [], merchantValue, null, []);

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

        FieldFinding finding = analysis.Find("SV VOID")!;
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

    /// <summary>But a sign-only difference is not an edit. `STR: 5` on a page reads unambiguously, so rewriting it to
    /// `+5` would be exactly the incidental reformatting this tool is not allowed to do — that belongs to the
    /// prettifier. The sign is applied only when the value is being written anyway.</summary>
    [Fact]
    public void ASignOnlyDifferenceIsNotAnEdit()
    {
        ItemPageDocument page = ItemPageDocument.Parse(
            "{{Itempage\n|itemname = Thing\n|statsblock = \nSTR: 8<br>\n}}")!;

        ItemPageAnalysis analysis = ItemPageAnalyzer.Analyze(
            Captured(name: "Thing", stats: [new("Strength", "8")]), page, "Thing");

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

    /// <summary>A real page has `Range: 50 / 75 / 100` against a captured `50`. Overwriting would discard the
    /// alternatives, so it is a human's call.</summary>
    [Fact]
    public void AWikiValueListingSeveralAlternativesNeedsReview()
    {
        ItemPageDocument page = ItemPageDocument.Parse(
            "{{Itempage\n|itemname = Arrow\n|statsblock = \nRange: 50 / 75 / 100<br>\n}}")!;

        ItemPageAnalysis analysis = ItemPageAnalyzer.Analyze(
            Captured(name: "Arrow", stats: [new("Range", "50")]), page, "Arrow");

        FieldFinding finding = analysis.Find("Range")!;
        Assert.Equal(FieldVerdict.NeedsReview, finding.Verdict);
        Assert.False(finding.IsChange);
    }

    /// <summary>The important one: a stat with no mapping is never dropped. An unmapped stat is how a game patch
    /// announces itself, and discarding it would lose real data with nobody the wiser.</summary>
    [Fact]
    public void AnUnmappedStatIsReportedRatherThanDropped()
    {
        ItemPageAnalysis analysis = Analyze(
            Captured(stats: [new("Accuracy", "+13.6%"), new("Sharpness", "+4")]), "Earring of Bashing");

        FieldFinding known = analysis.Find("Accuracy")!;
        Assert.Equal(FieldVerdict.NeedsReview, known.Verdict);
        Assert.Contains("no agreed field", known.Explanation);

        FieldFinding novel = analysis.Find("Sharpness")!;
        Assert.Equal(FieldVerdict.NeedsReview, novel.Verdict);
        Assert.Contains("possibly new", novel.Explanation);
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
}
