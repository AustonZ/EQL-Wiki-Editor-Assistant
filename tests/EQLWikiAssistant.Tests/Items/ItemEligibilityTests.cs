using EQLWikiAssistant.Core.Items;
using EQLWikiAssistant.TestSupport;
using EQLWikiAssistant.TestSupport.Accuracy;

namespace EQLWikiAssistant.Tests.Items;

/// <summary>
/// Pipeline step 4b. The rule these mostly exist to protect is the ledger one: an ineligible item gets no row at
/// all, so the next capture — a pristine copy, or a future version that supports levelling — is treated as new. A
/// "skipped" row would make the item look handled and quietly exclude it from ever being checked properly.
/// </summary>
public class ItemEligibilityTests
{
    private static ParsedItem Item(
        string name = "Bloodmoon",
        int level = 0,
        IReadOnlyList<ExaltationSlot>? exaltations = null) =>
        new(name, level, false, [], [], [], [], [], exaltations ?? [], [], null, null, []);

    [Fact]
    public void APlainLevelZeroItemIsEligible()
    {
        ItemEligibility result = ItemEligibility.Check(Item());

        Assert.True(result.IsEligible);
        Assert.True(result.ShouldWriteLedgerEntry);
        Assert.Empty(result.Blockers);
    }

    /// <summary>An empty slot means "unlocked but nothing in it", which is ordinary.</summary>
    [Fact]
    public void EmptyExaltationSlotsDoNotBlock()
    {
        ItemEligibility result = ItemEligibility.Check(Item(exaltations:
        [
            new ExaltationSlot(ExaltationKind.Ornamentation, null),
            new ExaltationSlot(ExaltationKind.Focus, null),
        ]));

        Assert.True(result.IsEligible);
    }

    /// <summary>The item's own exaltation in its own slot is the "removable native exaltation" case — proceed
    /// normally. Verified against real data in ItemParserTests; this pins that eligibility agrees.</summary>
    [Fact]
    public void ANativeExaltationDoesNotBlock()
    {
        ItemEligibility result = ItemEligibility.Check(Item(exaltations:
            [new ExaltationSlot(ExaltationKind.Focus, "Bloodmoon")]));

        Assert.True(result.IsEligible);
    }

    [Fact]
    public void AForeignExaltationBlocksAndWritesNoLedgerRow()
    {
        ItemEligibility result = ItemEligibility.Check(Item(exaltations:
            [new ExaltationSlot(ExaltationKind.Click, "Golem Metal Wand")]));

        Assert.False(result.IsEligible);
        Assert.False(result.ShouldWriteLedgerEntry);
        Assert.True(result.Has(IneligibilityReason.ForeignExaltation));
        Assert.Contains("Golem Metal Wand", result.Blockers[0].Explanation);
    }

    /// <summary>Ornamentation is never native — it is only ever applied by a player — so a filled slot is foreign
    /// by its nature rather than because a name comparison said so (user, 2026-09-25).</summary>
    [Fact]
    public void AFilledOrnamentationSlotAlwaysBlocks()
    {
        ItemEligibility result = ItemEligibility.Check(Item(exaltations:
            [new ExaltationSlot(ExaltationKind.Ornamentation, "Anything At All")]));

        Assert.False(result.IsEligible);
        Assert.True(result.Has(IneligibilityReason.ForeignExaltation));
    }

    /// <summary>The case the name comparison alone would get wrong: an ornamentation named like the item itself.
    /// Every other slot would read that as native, and for Ornamentation that inference is knowably false.</summary>
    [Fact]
    public void AnOrnamentationNamedLikeTheItemStillBlocks()
    {
        ItemEligibility result = ItemEligibility.Check(Item(
            name: "Bloodmoon",
            exaltations: [new ExaltationSlot(ExaltationKind.Ornamentation, "Bloodmoon")]));

        Assert.False(result.IsEligible);
        Assert.Contains("never", result.Blockers[0].Explanation);
    }

    /// <summary>...and the explanation must not claim a name mismatch, which would be actively misleading for a
    /// slot that is foreign regardless of its name.</summary>
    [Fact]
    public void TheOrnamentationExplanationDoesNotClaimANameMismatch()
    {
        ItemEligibility result = ItemEligibility.Check(Item(exaltations:
            [new ExaltationSlot(ExaltationKind.Ornamentation, "Gaudy Hat")]));

        Assert.DoesNotContain("not this item's own", result.Blockers[0].Explanation);
    }

    [Fact]
    public void ALevelledItemBlocksAndWritesNoLedgerRow()
    {
        ItemEligibility result = ItemEligibility.Check(Item(level: 7));

        Assert.False(result.IsEligible);
        Assert.False(result.ShouldWriteLedgerEntry);
        Assert.True(result.Has(IneligibilityReason.UnsupportedLevel));
        Assert.Contains("+7", result.Blockers[0].Explanation);
    }

    /// <summary>All blockers at once, not just the first: fixing one problem and being told about the next is a
    /// worse experience than seeing both up front.</summary>
    [Fact]
    public void EveryBlockerIsReportedTogether()
    {
        ItemEligibility result = ItemEligibility.Check(Item(
            level: 7,
            exaltations:
            [
                new ExaltationSlot(ExaltationKind.Click, "Golem Metal Wand"),
                new ExaltationSlot(ExaltationKind.Ornamentation, "Gaudy Hat"),
            ]));

        Assert.Equal(3, result.Blockers.Count);
        Assert.True(result.Has(IneligibilityReason.UnsupportedLevel));
        Assert.Equal(2, result.Blockers.Count(b => b.Reason == IneligibilityReason.ForeignExaltation));
    }

    /// <summary>
    /// The rule against every real window in the verified corpus, which holds about twenty levelled items with filled
    /// exaltation slots, foreign and native both. The unit tests above use names chosen to make the point; these are
    /// the names the game actually produces, long and punctuated ones included (`Staff of Elemental Mastery: Earth`),
    /// which is where a fuzzy name comparison would misjudge if it ever does.
    ///
    /// **The expectation is not the rule restated.** The ground truth is human-verified and exact, so native means
    /// the slot names the window's own item exactly, and anything else filled — or any filled Ornamentation slot — is
    /// foreign. The rule under test uses an edit-distance comparison to survive misreads; agreement here shows that on
    /// the names the game really produces it neither calls a different item native nor its own item foreign.
    ///
    /// **What it cannot catch**, measured rather than assumed: the real foreign names are far from their items, so a
    /// threshold loosened from a sixth of the name to half of it still passes. Treating every slot as native, or every
    /// slot as foreign, fails. The threshold's fine tuning is the unit tests' job, not this one's.
    ///
    /// A foreign exaltation on an unlevelled item cannot exist in the game (user, 2026-10-07: an exaltation can only be
    /// added to a levelled item), so every foreign case here is also levelled, and both blockers must be reported.
    /// </summary>
    [Fact]
    public void EveryRealWindowIsJudgedAsItsGroundTruthSays()
    {
        if (!File.Exists(RepoPaths.ExpectedItemsFile)) return;

        var windows = ExpectedCorpus.Load(RepoPaths.ExpectedItemsFile).Samples
            .SelectMany(s => s.Windows)
            .Where(w => !w.Occluded && !string.IsNullOrWhiteSpace(w.Name) && w.Name != ExpectedCorpus.TodoMarker)
            .ToList();

        int natives = 0, foreigns = 0, levelled = 0;
        foreach (ExpectedWindow window in windows)
        {
            List<ExaltationSlot> slots =
            [
                .. window.Exaltations.Select(e => new ExaltationSlot(Enum.Parse<ExaltationKind>(e.Kind), e.Name)),
            ];
            int expectedForeign = slots.Count(slot => slot.Name is not null &&
                (slot.Kind == ExaltationKind.Ornamentation || slot.Name != window.Name));
            natives += slots.Count(slot => slot.Name is not null) - expectedForeign;
            foreigns += expectedForeign;
            if (window.Level > 0) levelled++;

            ItemEligibility result = ItemEligibility.Check(Item(window.Name!, window.Level, slots));

            Assert.True(
                expectedForeign == result.Blockers.Count(b => b.Reason == IneligibilityReason.ForeignExaltation),
                $"{window.Name} +{window.Level}: expected {expectedForeign} foreign exaltation(s), the rule reported " +
                $"{result.Blockers.Count(b => b.Reason == IneligibilityReason.ForeignExaltation)}.");
            Assert.Equal(window.Level > 0, result.Has(IneligibilityReason.UnsupportedLevel));
            Assert.Equal(expectedForeign == 0 && window.Level == 0, result.IsEligible);
        }

        // Not vacuous: the corpus really does exercise both sides. Measured 2026-10-07; a smaller corpus would mean
        // this test had quietly stopped covering the case it exists for.
        Assert.True(natives >= 8, $"Only {natives} native exaltations in the corpus.");
        Assert.True(foreigns >= 20, $"Only {foreigns} foreign exaltations in the corpus.");
        Assert.True(levelled >= 40, $"Only {levelled} levelled windows in the corpus.");
    }
}
