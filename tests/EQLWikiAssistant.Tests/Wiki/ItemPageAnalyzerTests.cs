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

    // ---- a legacy flags line is removed even when the capture has no flags ----

    /// <summary>
    /// **The reported bug** (user, 2026-10-01, on `Treasure Hunter`s Satchel`): a page whose flags line is nothing
    /// but legacy, against an item EQL gives no flags at all. The tool reported the legacy flag and then left it on
    /// the page.
    ///
    /// The cause was that the comparison ran against the legacy-*filtered* flag list, so "the page has no current
    /// flags" and "the capture has no flags" matched — and a match tells the editor to leave the line exactly as the
    /// page wrote it. `Golden Efreeti Boots` carries the same `MAGIC ITEM<br>` shape, so the real page needed no new
    /// fixture.
    /// </summary>
    [Fact]
    public void ALegacyOnlyFlagsLineIsRemovedWhenTheCaptureHasNoFlags()
    {
        ItemPageAnalysis analysis = Analyze(
            Captured(name: "Golden Efreeti Boots", flags: []), "Golden Efreeti Boots");

        FieldFinding finding = analysis.Find(ItemPageAnalyzer.FlagsField)!;
        Assert.True(finding.IsChange, "the legacy line has to come off, so this is a change rather than a match");
        Assert.Equal("", finding.Captured);
        Assert.Equal("MAGIC ITEM", finding.OnWiki);
    }

    /// <summary>
    /// The general form of the same bug, which the reported case is one instance of: legacy tokens *beside* current
    /// ones. The filtered comparison called this a match too, so `MAGIC ITEM` survived on a page whose real flags
    /// were already right.
    /// </summary>
    [Fact]
    public void ALegacyFlagBesideAMatchingCurrentOneIsStillRemoved()
    {
        const string page =
            "<onlyinclude>{{Itempage\n|itemname = Thing\n|statsblock = \n" +
            "MAGIC ITEM  Lore Equipped<br>\nClass: ALL<br>\nRace: ALL<br>\n}}</onlyinclude>";

        ItemPageAnalysis analysis = ItemPageAnalyzer.Analyze(
            Captured(name: "Thing", flags: ["Lore Equipped"]), ItemPageDocument.Parse(page)!, "Thing");

        FieldFinding finding = analysis.Find(ItemPageAnalyzer.FlagsField)!;
        Assert.True(finding.IsChange);
        Assert.Equal("Lore Equipped", finding.Captured);
    }

    /// <summary>
    /// **A legacy flag spelled like a current one must not match it** (bug found by the user, 2026-10-02, on
    /// `Prickly Pear`, whose line is `QUEST` against a captured `Quest`). Case is the only thing that tells the two
    /// dialects apart, so comparing case-insensitively threw away the signal the whole question turns on: the page
    /// matched, the item was recorded done, and the legacy token would have stayed forever — the user caught it
    /// only by checking the wiki by hand.
    /// </summary>
    [Theory]
    [InlineData("QUEST", "Quest")]
    [InlineData("NO TRADE", "No Trade")]
    [InlineData("LORE EQUIPPED", "Lore Equipped")]
    public void ALegacyFlagSpelledLikeACurrentOneIsStillADifference(string onPage, string captured)
    {
        string page = """
            <onlyinclude>{{Itempage
            |itemname = Thing
            |statsblock =
            FLAGS<br>
            Class: ALL<br>
            Race: ALL<br>
            }}</onlyinclude>
            """.Replace("FLAGS", onPage, StringComparison.Ordinal);

        ItemPageAnalysis analysis = ItemPageAnalyzer.Analyze(
            Captured(name: "Thing", flags: [captured]), ItemPageDocument.Parse(page)!, "Thing");

        FieldFinding finding = analysis.Find(ItemPageAnalyzer.FlagsField)!;
        Assert.True(finding.IsChange, $"'{onPage}' is not what the tool would write, so the line has to be rewritten");
        Assert.Equal(captured, finding.Captured);
        Assert.Equal(onPage, finding.OnWiki);
    }

    /// <summary>
    /// The negative control, and the reason this is not simply "always rewrite the line": a page whose flags line is
    /// already exactly right must still be left alone, or every flag finding becomes noise and the diff rewrites a
    /// correct line. Order is not part of what a flags line means, so the reversed order still matches.
    /// </summary>
    [Theory]
    [InlineData("Lore Equipped, No Trade")]
    [InlineData("No Trade, Lore Equipped")]
    public void ACorrectFlagsLineIsStillLeftExactlyAsItIs(string wikiFlags)
    {
        string page =
            "<onlyinclude>{{Itempage\n|itemname = Thing\n|statsblock = \n" +
            wikiFlags.Replace(", ", "  ") + "<br>\nClass: ALL<br>\nRace: ALL<br>\n}}</onlyinclude>";

        ItemPageAnalysis analysis = ItemPageAnalyzer.Analyze(
            Captured(name: "Thing", flags: ["Lore Equipped", "No Trade"]), ItemPageDocument.Parse(page)!, "Thing");

        FieldFinding finding = analysis.Find(ItemPageAnalyzer.FlagsField)!;
        Assert.Equal(FieldVerdict.Matches, finding.Verdict);
        Assert.False(finding.IsChange);
    }

    /// <summary>A page with no flags line and a capture with no flags has nothing to do — this must not become a
    /// phantom change now that the comparison reads the raw line.</summary>
    [Fact]
    public void NoFlagsOnEitherSideIsNotAChange()
    {
        const string page =
            "<onlyinclude>{{Itempage\n|itemname = Thing\n|statsblock = \nClass: ALL<br>\nRace: ALL<br>\n}}" +
            "</onlyinclude>";

        ItemPageAnalysis analysis = ItemPageAnalyzer.Analyze(
            Captured(name: "Thing", flags: []), ItemPageDocument.Parse(page)!, "Thing");

        Assert.False(analysis.Find(ItemPageAnalyzer.FlagsField)?.IsChange ?? false);
    }

    /// <summary>
    /// **Prose on the flags line blocks the rewrite**, which this fix had to settle rather than inherit: the tool is
    /// forbidden to move or discard `This is a meal!` (user, 2026-09-24), and the flags line is regenerated *whole*,
    /// so rewriting it at all would silently delete the prose. Widening when the line gets rewritten — which is what
    /// fixing the legacy bug did — would have made that worse, so it is guarded instead, and the prose finding is
    /// what reaches the user.
    ///
    /// `Arctic Mussels` is the real fixture for this: its whole flags line is `This is a meal!`.
    /// </summary>
    [Fact]
    public void ProseOnTheFlagsLineIsNeverRewrittenAway()
    {
        ItemPageDocument page = ItemPageDocument.Parse(WikiFixtures.Load("Arctic Mussels"))!;
        ItemPageAnalysis analysis = ItemPageAnalyzer.Analyze(
            Captured(name: "Arctic Mussels", flags: ["Lore Equipped"]), page, "Arctic Mussels");

        Assert.False(analysis.Find(ItemPageAnalyzer.FlagsField)?.IsChange ?? false);
        Assert.NotNull(analysis.Find(ItemPageAnalyzer.FlagProseField));

        // And the edit really does leave it there, which is the half that matters on a public wiki.
        Assert.Contains("This is a meal!", ItemPageEditor.BuildEdit(page, analysis).NewWikitext);
    }

    /// <summary>
    /// End to end on the reported bug: the analyzer calls it a change *and* the editor takes the line off. Worth
    /// asserting together, because `ReplaceFlagsLine` already handled an empty flag set correctly — the whole defect
    /// was that nothing ever asked it to.
    /// </summary>
    [Fact]
    public void TheEditRemovesALegacyOnlyFlagsLineForAnItemWithNoFlags()
    {
        ItemPageDocument page = ItemPageDocument.Parse(WikiFixtures.Load("Golden Efreeti Boots"))!;
        ItemPageAnalysis analysis = ItemPageAnalyzer.Analyze(
            Captured(name: "Golden Efreeti Boots", flags: []), page, "Golden Efreeti Boots");

        ProposedEdit edit = ItemPageEditor.BuildEdit(page, analysis);

        Assert.DoesNotContain("MAGIC ITEM", edit.NewWikitext);
        // Surgical: the line goes and nothing around it moves.
        Assert.Contains("Slot: FEET<br>", edit.NewWikitext);
        Assert.Contains("WIS: +9  INT: +9 SV POISON: +1<br>", edit.NewWikitext);
        Assert.Contains("removed", edit.Summary);
    }

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

    /// <summary>
    /// Legacy flags are dropped rather than translated — LORE ITEM (carry one) is a different property from Lore
    /// Equipped (equip one), and MAGIC ITEM has no counterpart at all — and they are reported **once**, on the
    /// ordinary `flags` row (user, 2026-10-02, on `Drake-Hide Mask`: the table showed two rows for one line).
    ///
    /// The count is the assertion that matters and is the negative control against the duplicate: the row showing
    /// the whole line as the page wrote it already says everything a separate legacy row said.
    /// </summary>
    [Fact]
    public void LegacyFlagsAreReportedOnceOnTheOrdinaryFlagsRow()
    {
        ItemPageAnalysis analysis = Analyze(
            Captured(name: "Cloak of Scales", flags: ["Lore Equipped", "No Trade"]), "Cloak of Scales");

        Assert.Equal(1, analysis.Findings.Count(f => f.Field.StartsWith("flags", StringComparison.Ordinal)));

        FieldFinding finding = analysis.Find(ItemPageAnalyzer.FlagsField)!;
        Assert.True(finding.IsChange);
        Assert.Contains("MAGIC ITEM", finding.OnWiki);
        Assert.Equal("Lore Equipped, No Trade", finding.Captured);
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
        Assert.Contains("Move it into notes by hand", finding.Explanation);
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
        Assert.Contains("no tooltip", finding.Explanation);
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

    // ---- an effect whose page is linked under a qualified title (user, 2026-10-03, `Rain Caller`) ----

    /// <summary>`Rain Caller`'s statsblock, verbatim from the live page. Its effect links the *disambiguated* page
    /// and displays the plain name, which is the shape the matching used to miss.</summary>
    private static ItemPageDocument RainCaller() => ItemPageDocument.Parse(
        "{{Itempage\n|itemname    = Rain Caller\n|lucy_img_ID = 1024\n|statsblock  = \n" +
        "Lore Equipped, Attunable, Placeable<br>\n" +
        "Slot: RANGE<br>\n" +
        "Skill: Archery  Atk Delay: 45<br>\n" +
        "DMG: 20<br>\n" +
        "Effect:  [[Firestrike_(Effect)|Firestrike]] (Must Equip, Casting Time: Instant, Cooldown: 120s) at Level 40<br>\n" +
        "Class: RNG<br>\n" +
        "Race: ALL<br>\n}}")!;

    private static ParsedItem RainCallerCapture() => Captured(name: "Rain Caller") with
    {
        Effects =
        [
            new EffectEntry("Click", "Firestrike", ["Must Equip"],
                [new("Cast Time", "Instant"), new("Cooldown", "120 seconds"), new("Required Level", "40")]),
        ],
    };

    /// <summary>
    /// **The reported bug** (user, 2026-10-03): the tool added its own effect line and left the page's in place, so
    /// one effect ended up stated twice.
    ///
    /// The cause was that an effect was matched by the page it *links to* rather than the name it *displays*. The
    /// game shows `Firestrike`; the page links `Firestrike_(Effect)`; so nothing matched, the analyzer called the
    /// effect `MissingOnWiki`, and the editor inserted a second line. The line-count assertion is what fails against
    /// the old behaviour — the finding's verdict alone would have read as a plausible "the page is missing this".
    /// </summary>
    [Fact]
    public void AnEffectLinkedUnderAQualifiedTitleIsStillTheSameEffect()
    {
        ItemPageDocument page = RainCaller();
        ItemPageAnalysis analysis = ItemPageAnalyzer.Analyze(RainCallerCapture(), page, "Rain Caller");
        ProposedEdit edit = ItemPageEditor.BuildEdit(page, analysis);

        FieldFinding finding = analysis.Find("Click Effect")!;
        Assert.Equal(FieldVerdict.Differs, finding.Verdict);
        Assert.Null(analysis.Find(ItemPageAnalyzer.OrphanEffectField));

        Assert.Equal(1, Occurrences(edit.NewWikitext, "Effect:"));
        Assert.Equal(1, Occurrences(edit.NewWikitext, "[[Firestrike"));
    }

    /// <summary>
    /// The write side, and the reason the match had to be the *displayed* name rather than either half being
    /// "close enough": `Firestrike` and `Firestrike (Effect)` are both real pages and they are different spells —
    /// 422 damage for 138 mana against 302 for none, the second's own page reading "None; this spell is found on
    /// weapons". Normalizing the target to the effect's name repoints the item at the wrong figures with nothing
    /// visible on the rendered page to say so.
    /// </summary>
    [Fact]
    public void TheQualifiedLinkTargetSurvivesTheRewrite()
    {
        ItemPageDocument page = RainCaller();
        ItemPageAnalysis analysis = ItemPageAnalyzer.Analyze(RainCallerCapture(), page, "Rain Caller");
        ProposedEdit edit = ItemPageEditor.BuildEdit(page, analysis);

        Assert.Contains(
            "Effect: [[Firestrike_(Effect)|<span class='itemeff'>Firestrike</span>]] " +
            "(Clicky, Must Equip, Casting Time: Instant, Cooldown: 120 seconds) at Level 40",
            edit.NewWikitext);
        Assert.DoesNotContain("[[Firestrike|", edit.NewWikitext);
    }

    /// <summary>A link the tool kept rather than chose is reported, so the user can confirm it points at the right
    /// page (user, 2026-10-03). Only on a line being written — see ItemPageAnalyzer.LinkNote.</summary>
    [Fact]
    public void ALinkTheToolDidNotChooseIsReported()
    {
        FieldFinding finding = ItemPageAnalyzer
            .Analyze(RainCallerCapture(), RainCaller(), "Rain Caller").Find("Click Effect")!;

        Assert.Contains("Firestrike_(Effect)", finding.Explanation);
    }

    /// <summary>The control for that note: an ordinary page, whose link target *is* the effect's name, says nothing
    /// about it. Without this a perfectly ordinary effect would carry the bar on every capture.</summary>
    [Fact]
    public void AnOrdinaryEffectLinkIsNotRemarkedOn()
    {
        ItemPageDocument page = ItemPageDocument.Parse(
            "{{Itempage\n|itemname = Thing\n|statsblock = \n" +
            "Effect: [[Burn|<span class='itemeff'>Burn</span>]] (Combat)<br>\n}}")!;

        ParsedItem captured = Captured(name: "Thing") with
        {
            Effects = [new EffectEntry("Combat", "Burn", [], [new("Required Level", "10")])],
        };

        FieldFinding finding = ItemPageAnalyzer.Analyze(captured, page, "Thing").Find("Combat Effect")!;

        Assert.Equal(FieldVerdict.Differs, finding.Verdict);   // the level is genuinely new
        Assert.Null(finding.Explanation);
    }

    // ---- an effect line the capture does not account for ----

    /// <summary>
    /// The other half of the duplicate-line bug (user, 2026-10-03). A line naming an effect the window does not
    /// show was ignored outright, so the tool could add its own beside it and say nothing at all. It is reported
    /// and left exactly as the page wrote it: only a human can tell a stale page from a page describing something
    /// the window cannot display.
    /// </summary>
    [Fact]
    public void AnEffectLineTheCaptureDoesNotShowIsReportedAndLeftAlone()
    {
        ItemPageDocument page = ItemPageDocument.Parse(
            "{{Itempage\n|itemname = Thing\n|statsblock = \n" +
            "Effect: [[Burn|<span class='itemeff'>Burn</span>]] (Combat)<br>\n}}")!;

        ParsedItem captured = Captured(name: "Thing") with
        {
            Effects = [new EffectEntry("Combat", "Chill", [], [])],
        };

        ItemPageAnalysis analysis = ItemPageAnalyzer.Analyze(captured, page, "Thing");
        FieldFinding orphan = analysis.Find(ItemPageAnalyzer.OrphanEffectField)!;

        Assert.Equal(FieldVerdict.NeedsReview, orphan.Verdict);
        Assert.False(orphan.IsChange);
        Assert.True(orphan.Blocks, "nobody has judged this line, so the item must not settle as done");
        Assert.Contains("[[Burn", orphan.OnWiki);

        // Left alone means left alone: the page's line is still there, beside the captured effect's new one.
        string edited = ItemPageEditor.BuildEdit(page, analysis).NewWikitext;
        Assert.Contains("[[Burn", edited);
        Assert.Contains("[[Chill", edited);
    }

    /// <summary>A *legacy* page can write a focus effect as a statsblock line, where the modern convention gives it
    /// its own parameter. The tool sets the parameter and reports the line rather than deleting it — and the message
    /// says which of the two it is, since "an effect the window does not show" would be plainly untrue here.</summary>
    [Fact]
    public void AFocusEffectWrittenAsALineIsReportedAsThat()
    {
        ItemPageDocument page = ItemPageDocument.Parse(
            "{{Itempage\n|itemname = Thing\n|statsblock = \n" +
            "Effect: [[Improved Healing III|<span class='itemeff'>Improved Healing III</span>]] (Worn)<br>\n}}")!;

        ParsedItem captured = Captured(name: "Thing") with
        {
            Effects = [new EffectEntry("Focus", "Improved Healing III", [], [])],
        };

        ItemPageAnalysis analysis = ItemPageAnalyzer.Analyze(captured, page, "Thing");

        FieldFinding orphan = analysis.Find(ItemPageAnalyzer.OrphanEffectField)!;
        Assert.Contains("focus_effect", orphan.Explanation);
        Assert.Equal(FieldVerdict.MissingOnWiki, analysis.Find("Focus Effect")!.Verdict);
    }

    /// <summary>The control: every effect line accounted for produces no orphan at all.</summary>
    [Fact]
    public void AnEffectLineTheCaptureMatchesIsNoOrphan()
    {
        ItemPageAnalysis analysis = ItemPageAnalyzer.Analyze(
            RainCallerCapture(), RainCaller(), "Rain Caller");

        Assert.Null(analysis.Find(ItemPageAnalyzer.OrphanEffectField));
    }

    // ---- which effects need a link target resolved ----

    /// <summary>The page has the line, so its target is preserved and nothing is looked up — which is what keeps an
    /// ordinary check from spending a request on a question already answered.</summary>
    [Fact]
    public void AnEffectThePageAlreadyCarriesNeedsNoLinkLookup() =>
        Assert.Empty(ItemPageAnalyzer.EffectsNeedingALinkTarget(RainCallerCapture(), RainCaller()));

    /// <summary>A page with no line for the effect is the one case where the tool picks the target itself, so that
    /// is the case worth asking the wiki about.</summary>
    [Fact]
    public void AnEffectThePageIsMissingNeedsOne()
    {
        ItemPageDocument page = ItemPageDocument.Parse(
            "{{Itempage\n|itemname = Rain Caller\n|statsblock = \nDMG: 20<br>\n}}")!;

        Assert.Equal(
            ["Firestrike"], ItemPageAnalyzer.EffectsNeedingALinkTarget(RainCallerCapture(), page));
    }

    /// <summary>A focus effect has no line and so no link: it goes in its own parameter as a bare name.</summary>
    [Fact]
    public void AFocusEffectNeedsNoLinkTarget()
    {
        ParsedItem captured = Captured(name: "Thing") with
        {
            Effects = [new EffectEntry("Focus", "Improved Healing III", [], [])],
        };

        Assert.Empty(ItemPageAnalyzer.EffectsNeedingALinkTarget(captured, null));
    }

    /// <summary>A resolved target is used for a line the tool writes from scratch, which is the only place it can
    /// be: a page that has the line has a target of its own to keep.</summary>
    [Fact]
    public void AResolvedTargetIsUsedForALineTheToolWritesItself()
    {
        ItemPageDocument page = ItemPageDocument.Parse(
            "{{Itempage\n|itemname = Rain Caller\n|statsblock = \nDMG: 20<br>\n}}")!;

        ItemPageAnalysis analysis = ItemPageAnalyzer.Analyze(
            RainCallerCapture(), page, "Rain Caller",
            effectLinkTargets: new Dictionary<string, string> { ["Firestrike"] = "Firestrike (Effect)" });

        FieldFinding finding = analysis.Find("Click Effect")!;
        Assert.Equal(FieldVerdict.MissingOnWiki, finding.Verdict);
        Assert.Contains("[[Firestrike (Effect)|<span class='itemeff'>Firestrike</span>]]", finding.Captured);
        Assert.Contains("Firestrike (Effect)", finding.Explanation);
    }

    /// <summary>The control: with nothing resolved the effect's own name is linked, exactly as before this
    /// existed.</summary>
    [Fact]
    public void WithNothingResolvedTheEffectsOwnNameIsLinked()
    {
        ItemPageDocument page = ItemPageDocument.Parse(
            "{{Itempage\n|itemname = Rain Caller\n|statsblock = \nDMG: 20<br>\n}}")!;

        FieldFinding finding = ItemPageAnalyzer
            .Analyze(RainCallerCapture(), page, "Rain Caller").Find("Click Effect")!;

        Assert.Contains("[[Firestrike|<span class='itemeff'>Firestrike</span>]]", finding.Captured);
        Assert.Null(finding.Explanation);
    }

    private static int Occurrences(string text, string value) => text.Split(value).Length - 1;
}
