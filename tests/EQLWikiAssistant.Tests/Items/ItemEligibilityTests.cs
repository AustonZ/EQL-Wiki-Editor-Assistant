using EQLWikiAssistant.Core.Items;

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
}
