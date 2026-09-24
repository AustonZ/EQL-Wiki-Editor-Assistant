using EQLWikiAssistant.Core.Items;
using EQLWikiAssistant.TestSupport.Accuracy;

namespace EQLWikiAssistant.Tests.Accuracy;

/// <summary>Pure tests for the accuracy scorer — no samples, so these run on a fresh clone. The scorer is what
/// every tuning decision will be judged by, so it needs to be trustworthy in its own right.</summary>
public class ItemComparerTests
{
    private static ParsedItem Item(
        string name = "Widget",
        int level = 0,
        string? slot = "Ear",
        IReadOnlyList<KeyValuePair<string, string>>? stats = null,
        IReadOnlyList<string>? warnings = null) =>
        new(name, level, TitleContentNameMismatch: false,
            Flags: [], Classes: [], Races: [], Slot: slot,
            Stats: stats ?? [], ExaltationSlots: [], Effects: [],
            MerchantValue: null, Warnings: warnings ?? []);

    private static ExpectedWindow Expected(
        string name = "Widget",
        int level = 0,
        string? slot = "Ear",
        List<ExpectedField>? stats = null) =>
        new() { Name = name, Level = level, Slot = slot, Stats = stats ?? [] };

    private static FieldResult Field(IReadOnlyList<FieldResult> results, string field) =>
        results.Single(r => r.Field == field);

    [Fact]
    public void IdenticalItem_ScoresAllCorrect()
    {
        var results = ItemComparer.CompareWindow(Expected(), Item());
        Assert.All(results, r => Assert.Equal(FieldVerdict.Correct, r.Verdict));
    }

    [Fact]
    public void MissingValue_IsMissingNotWrong()
    {
        var results = ItemComparer.CompareWindow(
            Expected(stats: [new ExpectedField { Label = "AC", Value = "15" }]),
            Item(stats: []));

        Assert.Equal(FieldVerdict.Missing, Field(results, "stats[0]").Verdict);
    }

    [Fact]
    public void DifferentValue_IsWrong()
    {
        var results = ItemComparer.CompareWindow(Expected(name: "Widget"), Item(name: "Wodget"));
        Assert.Equal(FieldVerdict.Wrong, Field(results, "name").Verdict);
    }

    [Fact]
    public void ComparisonIsExact_NotFuzzy()
    {
        // Fuzzy matching is right at runtime (EditDistance, for wiki page-title lookup) and wrong here: an
        // edit-distance-tolerant scorer would pass "Tarnished" -> "Tamished" and hide an entire error class.
        var results = ItemComparer.CompareWindow(Expected(name: "Tarnished Sheer Blade"), Item(name: "Tamished Sheer Blade"));
        Assert.Equal(FieldVerdict.Wrong, Field(results, "name").Verdict);
    }

    [Fact]
    public void StatOrderMatters()
    {
        var results = ItemComparer.CompareWindow(
            Expected(stats:
            [
                new ExpectedField { Label = "Size", Value = "SMALL" },
                new ExpectedField { Label = "AC", Value = "15" },
            ]),
            Item(stats: [new("AC", "15"), new("Size", "SMALL")]));

        Assert.Equal(FieldVerdict.Wrong, Field(results, "stats[0]").Verdict);
        Assert.Equal(FieldVerdict.Wrong, Field(results, "stats[1]").Verdict);
    }

    [Fact]
    public void FailureOnAnItemWithNoWarnings_IsFlaggedSilent()
    {
        // The distinction the whole harness exists for: a gap the parser flagged is a review cost, a gap it did
        // not flag is a wiki-corruption risk.
        var silent = ItemComparer.CompareWindow(Expected(name: "Widget"), Item(name: "Wodget", warnings: []));
        Assert.False(Field(silent, "name").Flagged);

        var flagged = ItemComparer.CompareWindow(Expected(name: "Widget"), Item(name: "Wodget", warnings: ["something looked off"]));
        Assert.True(Field(flagged, "name").Flagged);
    }

    [Fact]
    public void OccludedAgreement_IsASinglePassNotACascade()
    {
        var results = ItemComparer.CompareWindow(new ExpectedWindow { Occluded = true }, actual: null);
        FieldResult only = Assert.Single(results);
        Assert.Equal("occluded", only.Field);
        Assert.Equal(FieldVerdict.Correct, only.Verdict);
    }

    [Fact]
    public void ExpectedOccludedButParsed_IsOneLoudFailure()
    {
        var results = ItemComparer.CompareWindow(new ExpectedWindow { Occluded = true }, Item());
        FieldResult only = Assert.Single(results);
        Assert.Equal("occluded", only.Field);
        Assert.NotEqual(FieldVerdict.Correct, only.Verdict);
    }

    [Fact]
    public void WindowCountMismatch_IsStructural_NotAPileOfFieldErrors()
    {
        var expected = new ExpectedCorpus
        {
            Samples = [new ExpectedSample { File = "a.png", Verified = true, Windows = [Expected(), Expected()] }],
        };
        var sample = new CorpusSample("a.png", Windows: [], Items: [])
        {
        };

        AccuracyReport report = CorpusRunner.Score([sample], expected);

        Assert.Equal(1, report.Structural);
        Assert.Empty(report.Samples.Single().Fields); // no field noise behind the structural failure
    }

    [Fact]
    public void SampleOnDiskWithNoGroundTruth_IsUnscoredNotFailed()
    {
        AccuracyReport report = CorpusRunner.Score(
            [new CorpusSample("new.png", Windows: [], Items: [])],
            new ExpectedCorpus());

        Assert.Equal(["new.png"], report.Unscored);
        Assert.Equal(0, report.Structural);
    }

    [Fact]
    public void GroundTruthWithNoSampleOnDisk_IsSkipped()
    {
        // The fresh-clone case: samples/ is gitignored, so everything skips and nothing fails.
        AccuracyReport report = CorpusRunner.Score(
            [],
            new ExpectedCorpus { Samples = [new ExpectedSample { File = "gone.png" }] });

        Assert.Equal(["gone.png"], report.Skipped);
        Assert.Equal(0, report.Structural);
    }

    [Fact]
    public async Task Corpus_RoundTripsThroughJson()
    {
        var corpus = new ExpectedCorpus
        {
            Samples =
            [
                new ExpectedSample
                {
                    File = "a.png",
                    Verified = true,
                    Windows =
                    [
                        new ExpectedWindow
                        {
                            Name = "Widget", Level = 3, Slot = "Ear",
                            Flags = ["No Trade"], Classes = ["WAR"], Races = ["ALL"],
                            Stats = [new ExpectedField { Label = "AC", Value = "15" }],
                            Exaltations = [new ExpectedExaltation { Kind = "Focus", Name = null }],
                            Effects =
                            [
                                new ExpectedEffect
                                {
                                    Kind = "Click", Description = "Rune IV",
                                    Modifiers = [new ExpectedField { Label = "Cast Time", Value = "Instant" }],
                                },
                            ],
                            MerchantValue = "1 silver",
                            WarningCount = 2,
                        },
                    ],
                },
            ],
        };

        string path = Path.Combine(Path.GetTempPath(), $"eqlwiki-corpus-{Guid.NewGuid():N}.json");
        try
        {
            await corpus.SaveAsync(path);
            ExpectedCorpus loaded = ExpectedCorpus.Load(path);

            ExpectedWindow window = loaded.Samples.Single().Windows.Single();
            Assert.Equal("Widget", window.Name);
            Assert.Equal(3, window.Level);
            Assert.Equal("AC", window.Stats.Single().Label);
            Assert.Equal("Focus", window.Exaltations.Single().Kind);
            Assert.Null(window.Exaltations.Single().Name);
            Assert.Equal("Instant", window.Effects.Single().Modifiers.Single().Value);
            Assert.Equal(2, window.WarningCount);
            Assert.True(loaded.Samples.Single().Verified);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TodoMarkerIsCounted_SoBootstrapCannotBeMistakenForComplete()
    {
        var corpus = new ExpectedCorpus
        {
            Samples =
            [
                new ExpectedSample
                {
                    File = "a.png",
                    Windows =
                    [
                        new ExpectedWindow
                        {
                            Stats =
                            [
                                new ExpectedField { Label = "AC", Value = "15" },
                                new ExpectedField { Label = "SV. Void", Value = ExpectedCorpus.TodoMarker },
                            ],
                        },
                    ],
                },
            ],
        };

        Assert.Equal(1, corpus.TodoCount);
    }
}
