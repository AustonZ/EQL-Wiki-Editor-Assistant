using EQLWikiEditorAssistant.Core.Glyphs;
using System.Diagnostics;
using EQLWikiEditorAssistant.Core.Items;
using EQLWikiEditorAssistant.TestSupport;
using EQLWikiEditorAssistant.TestSupport.Accuracy;

// Scores the real Locate -> Parse pipeline against the ground truth in
// tests/EQLWikiEditorAssistant.Tests/Accuracy/expected-items.json, so a change to the reader or the parser rules can be
// judged by a number instead of by eyeballing warning counts.
//
// Usage:
//   AccuracySpike                      score the corpus and print the summary
//   AccuracySpike --diff               also list every field that differs
//   AccuracySpike --bootstrap          write a candidate ground-truth file to .local-data/ for review
//   AccuracySpike --bootstrap --only <substring>   re-baseline just the matching sample(s)
//   AccuracySpike --json <path>        write the summary counts as JSON
//   AccuracySpike --expected <path>    score against a ground-truth file other than the tracked one
//                                      (e.g. a candidate still under review)
//   AccuracySpike --workers <n>        how many samples to run at once (default CorpusRunner.DefaultWorkers);
//                                      the report is identical at any count
//   AccuracySpike --font <name>        read the selected samples in this UI font (Arial, EqlWikiEditorAssistant) rather
//                                      than the one their ground truth or [font <name>] file-name marker names
//                                      (neither means Arial)
//
// --bootstrap never overwrites the tracked file; it writes a candidate for you to review and copy in. Every
// field the parser flagged is emitted as "?TODO" so the known misses become ground truth a human supplies,
// rather than today's bugs being frozen in as "correct".

bool diff = args.Contains("--diff");
bool bootstrap = args.Contains("--bootstrap");
string? only = ArgValue("--only");
string? jsonPath = ArgValue("--json");
string expectedPath = ArgValue("--expected") ?? RepoPaths.ExpectedItemsFile;

IReadOnlyList<string> files = CorpusRunner.EnumerateSampleFiles();
if (files.Count == 0)
{
    Console.Error.WriteLine($"No samples found in {RepoPaths.SamplesDirectory} (gitignored — nothing to do on a fresh clone).");
    return 1;
}
if (only is not null)
    files = [.. files.Where(f => Path.GetFileName(f).Contains(only, StringComparison.OrdinalIgnoreCase))];

int workers = int.TryParse(ArgValue("--workers"), out int w) && w > 0 ? w : CorpusRunner.DefaultWorkers;
Console.WriteLine($"Running {files.Count} sample(s) on {workers} worker(s)...");
var stopwatch = Stopwatch.StartNew();

ExpectedCorpus? truthForFonts = File.Exists(expectedPath) ? ExpectedCorpus.Load(expectedPath) : null;

// Each sample is read in its own font: --font, else its ground truth or file-name marker, else Arial (SampleFonts).
IReadOnlyList<CorpusSample> samples =
    await CorpusRunner.RunAllAsync(files, file => SampleFonts.For(file, args, truthForFonts), workers);
stopwatch.Stop();

foreach (CorpusSample sample in samples)
{
    string fontNote = sample.Font == UiFont.Arial ? "" : $" [{UiFonts.DisplayName(sample.Font)}]";
    string mismatch = sample.FontMismatches > 0 ? $"  !! {sample.FontMismatches} window(s) drawn in another font" : "";
    Console.WriteLine($"  {sample.File,-56} {sample.Windows.Count} window(s), {sample.Occluded} occluded, {sample.Warnings} warning(s){fontNote}{mismatch}");
}

if (bootstrap)
{
    ExpectedCorpus candidate = File.Exists(RepoPaths.ExpectedItemsFile)
        ? ExpectedCorpus.Load(RepoPaths.ExpectedItemsFile)
        : new ExpectedCorpus();

    foreach (CorpusSample sample in samples)
    {
        candidate.Samples.RemoveAll(s => string.Equals(s.File, sample.File, StringComparison.OrdinalIgnoreCase));
        candidate.Samples.Add(CorpusRunner.Bootstrap(sample));
    }

    string outPath = Path.Combine(RepoPaths.LocalDataDirectory, "accuracy", "expected-candidate.json");
    await candidate.SaveAsync(outPath);

    Console.WriteLine();
    Console.WriteLine($"Wrote candidate ground truth to {outPath}");
    Console.WriteLine($"  {candidate.Samples.Count} sample(s), {candidate.TodoCount} field(s) marked {ExpectedCorpus.TodoMarker} for you to fill in.");
    Console.WriteLine("  Review it against the screenshots, replace every ?TODO, set \"verified\": true, then copy to:");
    Console.WriteLine($"  {RepoPaths.ExpectedItemsFile}");
    return 0;
}

if (!File.Exists(expectedPath))
{
    Console.Error.WriteLine($"No ground truth at {expectedPath}. Run with --bootstrap first.");
    return 1;
}

ExpectedCorpus expected = ExpectedCorpus.Load(expectedPath);
AccuracyReport report = CorpusRunner.Score(samples, expected);

Console.WriteLine();
Console.WriteLine(report.Render(
    windowsLocated: samples.Sum(s => s.Windows.Count),
    occludedLocated: samples.Sum(s => s.Occluded),
    warnings: samples.Sum(s => s.Warnings),
    elapsed: stopwatch.Elapsed));

if (diff)
{
    Console.WriteLine(report.RenderDiff());
}

if (jsonPath is not null)
{
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(jsonPath))!);
    await File.WriteAllTextAsync(jsonPath, System.Text.Json.JsonSerializer.Serialize(new
    {
        structural = report.Structural,
        silentWrong = report.SilentWrong,
        wrong = report.Wrong,
        missing = report.Missing,
        extra = report.Extra,
        correct = report.Correct,
        warnings = samples.Sum(s => s.Warnings),
        windows = samples.Sum(s => s.Windows.Count),
        seconds = stopwatch.Elapsed.TotalSeconds,
    }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"Wrote summary to {jsonPath}");
}

// Non-zero exit on either hard gate, so this is usable from a script.
return report.Structural == 0 && report.SilentWrong == 0 ? 0 : 1;

string? ArgValue(string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
