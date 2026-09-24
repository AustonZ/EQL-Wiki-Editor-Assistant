using EQLWikiAssistant.Ocr;
using EQLWikiAssistant.TestSupport;
using EQLWikiAssistant.TestSupport.Accuracy;
using Xunit.Abstractions;

namespace EQLWikiAssistant.Tests.Accuracy;

/// <summary>
/// Scores the whole samples corpus against tracked ground truth. This is where the accuracy numbers get an
/// enforceable home — <c>tools/AccuracySpike</c> is the everyday driver, but a number nothing asserts on will
/// drift.
///
/// **Gated behind <c>EQLWIKI_ACCURACY=1</c>** (precedent: <c>EQLWIKI_LOCATE_DIAG</c>). A corpus pass runs the real
/// pipeline over every screenshot and takes ~3 minutes; in the default <c>dotnet test</c> path that would get
/// muted within a week, which is worse than an opt-in gate that people actually run.
///
///     EQLWIKI_ACCURACY=1 dotnet test --filter FullyQualifiedName~CorpusAccuracyTests
/// </summary>
public class CorpusAccuracyTests
{
    // Ratchets. Lower these in the same commit as any improvement that earns it; they exist so a regression is a
    // failing test rather than a number someone notices later. They may only ever go down.
    //
    // These were 0/0 while the corpus was unverified, which meant nothing — an unverified entry records whatever
    // the pipeline produced, so everything scored correct by construction. Once the user verified all 43 samples
    // against the screenshots, the real numbers appeared. Every one of them is a *glyph-level* OCR failure, not a
    // parser defect:
    //   - missing: isolated stat digits the RapidOCR detector never finds (the documented dominant gap);
    //   - wrong/silent-wrong: the "rn"->"m" cluster in payload names (Tarnished->Tamished, Burn->Bum), a roman
    //     numeral losing a stroke (III->II), a grave accent read as an apostrophe, and an item icon recognized as
    //     a stray letter joining the flags row.
    // Driving all three to 0 is exactly what the glyph-matching engine (plan stages 2-3) is for; they are ratchets
    // rather than 0 so that a *parser* regression still fails the build in the meantime.
    private const int MaxMissingFields = 24;
    private const int MaxWrongFields = 15;
    private const int MaxSilentWrongFields = 13;

    private readonly ITestOutputHelper _output;
    public CorpusAccuracyTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Corpus_MeetsAccuracyBaseline()
    {
        if (Environment.GetEnvironmentVariable("EQLWIKI_ACCURACY") != "1")
        {
            _output.WriteLine("Skipping: set EQLWIKI_ACCURACY=1 to run the corpus pass (~3 minutes).");
            return;
        }

        IReadOnlyList<string> files = CorpusRunner.EnumerateSampleFiles();
        if (files.Count == 0)
        {
            _output.WriteLine($"Skipping: no samples in {RepoPaths.SamplesDirectory} (gitignored, personal data).");
            return;
        }
        if (!File.Exists(RepoPaths.ExpectedItemsFile))
        {
            _output.WriteLine($"Skipping: no ground truth at {RepoPaths.ExpectedItemsFile}. Run AccuracySpike --bootstrap.");
            return;
        }

        using var engine = new RapidOcrEngine();
        var samples = new List<CorpusSample>();
        foreach (string file in files)
            samples.Add(await CorpusRunner.RunAsync(file, engine));

        ExpectedCorpus expected = ExpectedCorpus.Load(RepoPaths.ExpectedItemsFile);
        AccuracyReport report = CorpusRunner.Score(samples, expected);

        _output.WriteLine(report.Render(
            samples.Sum(s => s.Windows.Count), samples.Sum(s => s.Occluded), samples.Sum(s => s.Warnings), TimeSpan.Zero));
        _output.WriteLine(report.RenderDiff());

        // Hard gates, in the order a config should be judged by. A lost or invented window first: it is the most
        // serious regression class and must not hide behind field counts.
        Assert.Equal(0, report.Structural);

        // Then the one that matters most: a field we got wrong on an item the parser did NOT flag. Everything
        // else here is a manual-review cost; this is a wiki-corruption risk, so it gets its own named gate.
        Assert.True(report.SilentWrong <= MaxSilentWrongFields,
            $"silent-wrong fields {report.SilentWrong} exceeds baseline {MaxSilentWrongFields}");

        Assert.True(report.Wrong <= MaxWrongFields,
            $"wrong fields {report.Wrong} exceeds baseline {MaxWrongFields}");
        Assert.True(report.Missing <= MaxMissingFields,
            $"missing fields {report.Missing} exceeds baseline {MaxMissingFields}");
    }
}
