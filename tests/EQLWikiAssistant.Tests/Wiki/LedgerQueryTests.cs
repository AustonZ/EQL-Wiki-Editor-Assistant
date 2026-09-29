using EQLWikiAssistant.Wiki.Ledger;

namespace EQLWikiAssistant.Tests.Wiki;

/// <summary>
/// The ledger view's searching and filtering. It lives in <c>Wiki.Ledger</c> rather than in the WPF view model
/// precisely so it can be tested here — "which rows match" is a question about the ledger, not about a window.
/// </summary>
public class LedgerQueryTests
{
    private static LedgerEntry Entry(
        string name,
        CheckOutcome outcome,
        int daysAgo = 0,
        string? pageTitle = null) => new()
    {
        ItemName = name,
        EntityKind = CheckedItemsLedger.ItemKind,
        WikiPageTitle = pageTitle,
        Outcome = outcome,
        CheckedAt = new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero).AddDays(-daysAgo),
        Fingerprint = "fp-" + name,
        MappingVersion = 3,
    };

    private static readonly LedgerEntry[] Sample =
    [
        Entry("Ruby Crown", CheckOutcome.Edited, daysAgo: 0),
        Entry("Guise of the Deceived", CheckOutcome.Flagged, daysAgo: 1),
        Entry("Fruit", CheckOutcome.Matched, daysAgo: 2),
        Entry("Lake Pebble", CheckOutcome.NotOnWiki, daysAgo: 3),
        Entry("Water Flask", CheckOutcome.Skipped, daysAgo: 4),
        Entry("Halas 10lb Meat Pie", CheckOutcome.Matched, daysAgo: 5),
    ];

    [Fact]
    public void NewestCheckComesFirst()
    {
        IReadOnlyList<LedgerEntry> rows = LedgerQuery.Apply(Sample);

        Assert.Equal("Ruby Crown", rows[0].ItemName);
        Assert.Equal("Halas 10lb Meat Pie", rows[^1].ItemName);
    }

    /// <summary>The question the view exists to answer: what did I leave undone? All three not-done outcomes,
    /// nothing else.</summary>
    [Fact]
    public void NeedsAttentionIsEveryOutcomeThatIsNotDone()
    {
        IReadOnlyList<LedgerEntry> rows = LedgerQuery.Apply(Sample, filter: LedgerFilter.NeedsAttention);

        Assert.Equal(
            ["Guise of the Deceived", "Lake Pebble", "Water Flask"],
            rows.Select(r => r.ItemName).Order());
    }

    /// <summary>Pins the filter to the ledger's own rule rather than to a list of outcomes here, so a new outcome
    /// added later cannot be silently treated as settled by one of the two and unsettled by the other.</summary>
    [Fact]
    public void NeedsAttentionAgreesWithTheLedgersOwnDoneRule()
    {
        IReadOnlyList<LedgerEntry> attention = LedgerQuery.Apply(Sample, filter: LedgerFilter.NeedsAttention);

        foreach (CheckOutcome outcome in Enum.GetValues<CheckOutcome>())
        {
            bool inAttention = attention.Any(r => r.Outcome == outcome);
            Assert.Equal(!CheckedItemsLedger.MeansDone(outcome), inAttention);
        }
    }

    [Theory]
    [InlineData(LedgerFilter.Matched, 2)]
    [InlineData(LedgerFilter.Edited, 1)]
    [InlineData(LedgerFilter.Flagged, 1)]
    [InlineData(LedgerFilter.Skipped, 1)]
    [InlineData(LedgerFilter.NotOnWiki, 1)]
    [InlineData(LedgerFilter.All, 6)]
    public void EachFilterSelectsItsOwnOutcome(LedgerFilter filter, int expected) =>
        Assert.Equal(expected, LedgerQuery.Apply(Sample, filter: filter).Count);

    [Fact]
    public void SearchIsCaseInsensitiveAndMatchesPartOfAName()
    {
        IReadOnlyList<LedgerEntry> rows = LedgerQuery.Apply(Sample, search: "crown");

        Assert.Equal("Ruby Crown", Assert.Single(rows).ItemName);
    }

    /// <summary>An item whose in-game name cannot be a MediaWiki title lives under a name a human chose, and the user
    /// may remember either one — so both are searched.</summary>
    [Fact]
    public void SearchFindsAnItemByItsWikiPageTitle()
    {
        LedgerEntry[] entries = [Entry("Cell Key #5", CheckOutcome.Edited, pageTitle: "Cell Key No. 5")];

        Assert.Single(LedgerQuery.Apply(entries, search: "No. 5"));
        Assert.Single(LedgerQuery.Apply(entries, search: "#5"));
    }

    [Fact]
    public void SearchAndFilterApplyTogether()
    {
        IReadOnlyList<LedgerEntry> rows = LedgerQuery.Apply(Sample, search: "a", filter: LedgerFilter.Matched);

        Assert.Equal(["Halas 10lb Meat Pie"], rows.Select(r => r.ItemName));
    }

    [Fact]
    public void BlankSearchIsNoSearch() =>
        Assert.Equal(Sample.Length, LedgerQuery.Apply(Sample, search: "   ").Count);

    [Fact]
    public void SummaryCountsEveryOutcome()
    {
        LedgerSummary summary = LedgerSummary.Of(Sample);

        Assert.Equal(6, summary.Total);
        Assert.Equal(2, summary.Matched);
        Assert.Equal(1, summary.Edited);
        Assert.Equal(1, summary.Flagged);
        Assert.Equal(1, summary.Skipped);
        Assert.Equal(1, summary.NotOnWiki);
        Assert.Equal(3, summary.NeedsAttention);
    }

    [Fact]
    public void SummaryOfAnEmptyLedgerIsAllZero()
    {
        LedgerSummary summary = LedgerSummary.Of([]);

        Assert.Equal(0, summary.Total);
        Assert.Equal(0, summary.NeedsAttention);
    }

    [Fact]
    public void MeansDoneIsOnlyMatchedAndEdited()
    {
        Assert.True(CheckedItemsLedger.MeansDone(CheckOutcome.Matched));
        Assert.True(CheckedItemsLedger.MeansDone(CheckOutcome.Edited));
        Assert.False(CheckedItemsLedger.MeansDone(CheckOutcome.Flagged));
        Assert.False(CheckedItemsLedger.MeansDone(CheckOutcome.Skipped));
        Assert.False(CheckedItemsLedger.MeansDone(CheckOutcome.NotOnWiki));
    }
}
