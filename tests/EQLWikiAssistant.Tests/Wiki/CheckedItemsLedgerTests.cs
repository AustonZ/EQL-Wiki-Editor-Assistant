using EQLWikiAssistant.Core.Items;
using EQLWikiAssistant.Wiki.Ledger;
using EQLWikiAssistant.Wiki.MediaWiki;

namespace EQLWikiAssistant.Tests.Wiki;

/// <summary>
/// The ledger exists to avoid network calls, so the tests the plan asks for by name are about request *counts*, not
/// just return values — hence the counting fake client at the bottom.
/// </summary>
public class CheckedItemsLedgerTests
{
    private const int Mapping = 1;

    private static LedgerEntry Entry(
        string name = "Water Flask",
        CheckOutcome outcome = CheckOutcome.Matched,
        string fingerprint = "FP1",
        int mappingVersion = Mapping,
        DateTimeOffset? checkedAt = null,
        string? wikiPageTitle = null) =>
        new()
        {
            ItemName = name,
            EntityKind = CheckedItemsLedger.ItemKind,
            Outcome = outcome,
            Fingerprint = fingerprint,
            MappingVersion = mappingVersion,
            CheckedAt = checkedAt ?? DateTimeOffset.UtcNow,
            WikiPageTitle = wikiPageTitle,
        };

    [Fact]
    public void AnItemNeverSeenIsNotChecked() =>
        Assert.Equal(
            LedgerVerdict.NotChecked,
            new CheckedItemsLedger().Consult("Water Flask", "FP1", Mapping));

    [Theory]
    [InlineData(CheckOutcome.Matched)]
    [InlineData(CheckOutcome.Edited)]
    public void ASettledUnchangedItemNeedsNoWikiCall(CheckOutcome outcome)
    {
        var ledger = new CheckedItemsLedger();
        ledger.Record(Entry(outcome: outcome));

        Assert.Equal(LedgerVerdict.AlreadyDone, ledger.Consult("Water Flask", "FP1", Mapping));
        Assert.True(ledger.CanSkipWikiFetch("Water Flask", "FP1", Mapping));
    }

    /// <summary>The plan's rule, and the one most likely to be got wrong: these outcomes left something undone, so
    /// they are never treated as done however recent or unchanged they are.</summary>
    [Theory]
    [InlineData(CheckOutcome.Flagged)]
    [InlineData(CheckOutcome.Skipped)]
    [InlineData(CheckOutcome.NotOnWiki)]
    public void AnUnresolvedOutcomeIsNeverTreatedAsDone(CheckOutcome outcome)
    {
        var ledger = new CheckedItemsLedger();
        ledger.Record(Entry(outcome: outcome));

        // Same fingerprint, same mapping, checked a moment ago — and still not done.
        Assert.Equal(LedgerVerdict.Unresolved, ledger.Consult("Water Flask", "FP1", Mapping));
        Assert.False(ledger.CanSkipWikiFetch("Water Flask", "FP1", Mapping));
    }

    [Fact]
    public void ChangedItemDataForcesARecheck()
    {
        var ledger = new CheckedItemsLedger();
        ledger.Record(Entry(fingerprint: "FP1"));

        Assert.Equal(LedgerVerdict.DataChanged, ledger.Consult("Water Flask", "FP2", Mapping));
    }

    /// <summary>A mapping change can change what the tool would write, so a row checked under the old one is no
    /// longer evidence the page is right.</summary>
    [Fact]
    public void AChangedMappingVersionForcesARecheck()
    {
        var ledger = new CheckedItemsLedger();
        ledger.Record(Entry(mappingVersion: 1));

        Assert.Equal(LedgerVerdict.MappingChanged, ledger.Consult("Water Flask", "FP1", mappingVersion: 2));
    }

    [Fact]
    public void ReCheckAnywayOverridesASettledRow()
    {
        var ledger = new CheckedItemsLedger();
        ledger.Record(Entry());

        Assert.Equal(
            LedgerVerdict.DataChanged,
            ledger.Consult("Water Flask", "FP1", Mapping, reCheckAnyway: true));
    }

    /// <summary>No expiry by default: a checked item does not drift on its own, and the fingerprint and mapping
    /// version are what actually invalidate a row.</summary>
    [Fact]
    public void RowsDoNotExpireUnlessAMaximumAgeIsSet()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var ledger = new CheckedItemsLedger(clock);
        ledger.Record(Entry(checkedAt: clock.GetUtcNow()));

        clock.Advance(TimeSpan.FromDays(3650));

        Assert.Equal(LedgerVerdict.AlreadyDone, ledger.Consult("Water Flask", "FP1", Mapping));

        ledger.MaximumAge = TimeSpan.FromDays(30);
        Assert.Equal(LedgerVerdict.Stale, ledger.Consult("Water Flask", "FP1", Mapping));
    }

    /// <summary>An item whose in-game name cannot be a MediaWiki title lives at a name a human chose. Without
    /// recording that, every future capture looks unhandled.</summary>
    [Fact]
    public void TheWikiPageTitleIsRememberedWhenItDiffersFromTheItemName()
    {
        var ledger = new CheckedItemsLedger();
        ledger.Record(Entry(name: "Cell Key #5", wikiPageTitle: "Cell Key No. 5"));

        LedgerEntry found = ledger.Find("Cell Key #5")!;

        Assert.Equal("Cell Key No. 5", found.WikiPageTitle);
        Assert.Equal(LedgerVerdict.AlreadyDone, ledger.Consult("Cell Key #5", "FP1", Mapping));
    }

    [Fact]
    public void RemoveMakesAnItemNewAgain()
    {
        var ledger = new CheckedItemsLedger();
        ledger.Record(Entry());

        Assert.True(ledger.Remove("Water Flask"));
        Assert.Equal(LedgerVerdict.NotChecked, ledger.Consult("Water Flask", "FP1", Mapping));
    }

    /// <summary>Entity kind is part of the key, because a spell can share a name with an item and the plan has them
    /// sharing this store.</summary>
    [Fact]
    public void TheSameNameUnderADifferentKindIsADifferentRow()
    {
        var ledger = new CheckedItemsLedger();
        ledger.Record(Entry(name: "Haste"));

        Assert.Equal(LedgerVerdict.NotChecked, ledger.Consult("Haste", "FP1", Mapping, entityKind: "spell"));
    }

    [Fact]
    public async Task ALedgerRoundTripsThroughDisk()
    {
        string path = Path.Combine(Path.GetTempPath(), $"eqlwiki-ledger-{Guid.NewGuid():N}.json");
        try
        {
            var ledger = new CheckedItemsLedger();
            ledger.Record(Entry(name: "Cell Key #5", wikiPageTitle: "Cell Key No. 5", outcome: CheckOutcome.Edited));
            await ledger.SaveAsync(path);

            CheckedItemsLedger reloaded = CheckedItemsLedger.Load(path);
            LedgerEntry entry = reloaded.Find("Cell Key #5")!;

            Assert.Equal(CheckOutcome.Edited, entry.Outcome);
            Assert.Equal("Cell Key No. 5", entry.WikiPageTitle);
            Assert.Equal(LedgerVerdict.AlreadyDone, reloaded.Consult("Cell Key #5", "FP1", Mapping));
        }
        finally { File.Delete(path); }
    }

    /// <summary>A corrupt ledger costs re-checks, not correctness — every row is reconstructible by capturing the
    /// item again — so losing it beats refusing to start.</summary>
    [Fact]
    public void ACorruptLedgerLoadsAsEmptyRatherThanThrowing()
    {
        string path = Path.Combine(Path.GetTempPath(), $"eqlwiki-ledger-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{ this is not json");
            Assert.Equal(0, CheckedItemsLedger.Load(path).Count);
        }
        finally { File.Delete(path); }
    }

    // ---- the request-count tests the plan asks for by name ----

    /// <summary>"A second capture of an unchanged, already-matched item makes zero wiki requests." Asserted against
    /// a client that counts them, because that is the property — not the return value.</summary>
    [Fact]
    public async Task ASecondCaptureOfAnUnchangedMatchedItemMakesNoWikiRequests()
    {
        var client = new CountingClient();
        var ledger = new CheckedItemsLedger();

        await CheckAsync(ledger, client, "Water Flask", "FP1");   // first time: fetches
        Assert.Equal(1, client.Fetches);

        await CheckAsync(ledger, client, "Water Flask", "FP1");   // unchanged: must not
        Assert.Equal(1, client.Fetches);
    }

    [Fact]
    public async Task AChangedFingerprintForcesAFetch()
    {
        var client = new CountingClient();
        var ledger = new CheckedItemsLedger();

        await CheckAsync(ledger, client, "Water Flask", "FP1");
        await CheckAsync(ledger, client, "Water Flask", "FP2");

        Assert.Equal(2, client.Fetches);
    }

    [Fact]
    public async Task AFlaggedItemIsFetchedEveryTime()
    {
        var client = new CountingClient();
        var ledger = new CheckedItemsLedger();

        await CheckAsync(ledger, client, "Water Flask", "FP1", CheckOutcome.Flagged);
        await CheckAsync(ledger, client, "Water Flask", "FP1", CheckOutcome.Flagged);
        await CheckAsync(ledger, client, "Water Flask", "FP1", CheckOutcome.Flagged);

        Assert.Equal(3, client.Fetches);
    }

    /// <summary>The pipeline step in miniature: consult first, fetch only if needed, record afterwards.</summary>
    private static async Task CheckAsync(
        CheckedItemsLedger ledger,
        CountingClient client,
        string itemName,
        string fingerprint,
        CheckOutcome outcome = CheckOutcome.Matched)
    {
        if (ledger.Consult(itemName, fingerprint, Mapping) == LedgerVerdict.AlreadyDone) return;

        WikiPage? page = await client.FetchPageAsync(itemName);
        ledger.Record(Entry(name: itemName, outcome: outcome, fingerprint: fingerprint) with
        {
            WikiRevisionId = page?.RevisionId,
        });
    }

    private sealed class CountingClient : IMediaWikiClient
    {
        public int Fetches { get; private set; }

        public Task<WikiPage?> FetchPageAsync(string title, CancellationToken cancellationToken = default)
        {
            Fetches++;
            return Task.FromResult<WikiPage?>(new WikiPage(title, "{{Itempage}}", 1, DateTimeOffset.UnixEpoch));
        }

        public Task LoginAsync(BotCredentials credentials, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<EditResult> EditAsync(
            string title, string newWikitext, string summary, DateTimeOffset baseTimestamp,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}

/// <summary>The fingerprint's contract: it must change when anything the tool would write changes, and not
/// otherwise.</summary>
public class ItemFingerprintTests
{
    private static ParsedItem Item(
        string name = "Water Flask",
        int level = 0,
        IReadOnlyList<string>? flags = null,
        IReadOnlyList<string>? classes = null,
        IReadOnlyList<KeyValuePair<string, string>>? stats = null,
        string? merchantValue = null,
        IReadOnlyList<string>? warnings = null) =>
        new(name, level, false, flags ?? [], classes ?? [], [], [], stats ?? [], [], [], merchantValue, null,
            warnings ?? []);

    [Fact]
    public void TheSameItemFingerprintsTheSame() =>
        Assert.Equal(ItemFingerprint.Compute(Item()), ItemFingerprint.Compute(Item()));

    [Fact]
    public void ADifferentStatValueChangesTheFingerprint() =>
        Assert.NotEqual(
            ItemFingerprint.Compute(Item(stats: [new("AC", "5")])),
            ItemFingerprint.Compute(Item(stats: [new("AC", "6")])));

    /// <summary>Order is not meaning: a class list is a set, and the window's emission order must not invalidate a
    /// ledger row.</summary>
    [Fact]
    public void ListOrderDoesNotChangeTheFingerprint() =>
        Assert.Equal(
            ItemFingerprint.Compute(Item(classes: ["WAR", "CLR"])),
            ItemFingerprint.Compute(Item(classes: ["CLR", "WAR"])));

    /// <summary>Warnings quote OCR fragments and churn with every tuning change, so a fingerprint that included them
    /// would expire every row on every release.</summary>
    [Fact]
    public void WarningsDoNotChangeTheFingerprint() =>
        Assert.Equal(
            ItemFingerprint.Compute(Item()),
            ItemFingerprint.Compute(Item(warnings: ["couldn't read the merchant value"])));

    /// <summary>Only `+0` items are processed today, so a levelled capture never reaches the ledger — but including
    /// the level means a future version that does process them can never confuse the two.</summary>
    [Fact]
    public void TheCapturedLevelIsPartOfTheFingerprint() =>
        Assert.NotEqual(ItemFingerprint.Compute(Item(level: 0)), ItemFingerprint.Compute(Item(level: 7)));

    [Fact]
    public void LoreChangesTheFingerprint() =>
        Assert.NotEqual(
            ItemFingerprint.Compute(Item(), lore: "For those that like to bash"),
            ItemFingerprint.Compute(Item(), lore: "Something else entirely"));

    [Fact]
    public void FlagsAndMerchantValueChangeTheFingerprint()
    {
        Assert.NotEqual(
            ItemFingerprint.Compute(Item(flags: ["No Trade"])),
            ItemFingerprint.Compute(Item(flags: ["Attunable"])));
        Assert.NotEqual(
            ItemFingerprint.Compute(Item(merchantValue: "1 silver")),
            ItemFingerprint.Compute(Item(merchantValue: "2 silver")));
    }
}
