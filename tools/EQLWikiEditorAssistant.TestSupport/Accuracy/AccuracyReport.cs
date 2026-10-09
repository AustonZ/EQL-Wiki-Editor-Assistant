using System.Text;

namespace EQLWikiEditorAssistant.TestSupport.Accuracy;

/// <summary>One sample's outcome. <see cref="Structural"/> means the window count didn't match, which is reported
/// as a single failure rather than absorbed by clever re-matching: a lost or invented window is the most serious
/// regression class there is, and it must not hide behind a pile of field errors.</summary>
public sealed record SampleResult(
    string File,
    bool Verified,
    bool Structural,
    int ExpectedWindows,
    int ActualWindows,
    IReadOnlyList<FieldResult> Fields)
{
    public IEnumerable<FieldResult> Failures => Fields.Where(f => f.Verdict != FieldVerdict.Correct);
}

/// <summary>
/// Aggregates sample results into the numbers that decide whether a change is an improvement.
///
/// Compare configurations **lexicographically**, not by a weighted score: a weighted total lets a tuner buy five
/// recovered digits with one corrupted value, which is precisely the trade this project must never make.
/// Order: <see cref="Structural"/> = 0, then <see cref="SilentWrong"/> = 0 (both hard gates), then
/// <see cref="Wrong"/>, then <see cref="Missing"/>, then warnings, then speed.
/// </summary>
public sealed class AccuracyReport
{
    private readonly List<SampleResult> _samples = [];

    public IReadOnlyList<SampleResult> Samples => _samples;
    public void Add(SampleResult result) => _samples.Add(result);

    /// <summary>Samples present on disk but absent from the expected file — new captures nobody has scored yet.</summary>
    public List<string> Unscored { get; } = [];

    /// <summary>Samples in the expected file but absent from disk — the fresh-clone case; skipped, never failed.</summary>
    public List<string> Skipped { get; } = [];

    public int Structural => _samples.Count(s => s.Structural);
    public int Correct => CountVerdict(FieldVerdict.Correct);
    public int Missing => CountVerdict(FieldVerdict.Missing);
    public int Wrong => CountVerdict(FieldVerdict.Wrong);
    public int Extra => CountVerdict(FieldVerdict.Extra);

    /// <summary>The number that must never move off zero: a field we got wrong (or dropped) on an item the parser
    /// did *not* flag. Everything else is a review cost; this is a wiki-corruption risk.</summary>
    public int SilentWrong => _samples
        .SelectMany(s => s.Failures)
        .Count(f => !f.Flagged && f.Verdict is FieldVerdict.Wrong or FieldVerdict.Missing or FieldVerdict.Extra);

    public int VerifiedSamples => _samples.Count(s => s.Verified);

    public string Render(int windowsLocated, int occludedLocated, int warnings, TimeSpan elapsed)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"corpus:       {_samples.Count} samples scored ({VerifiedSamples} verified), " +
                      $"{windowsLocated} windows located ({occludedLocated} occluded)");
        sb.AppendLine($"structural:   {Structural,5}  window-count mismatches      <- hard gate");
        sb.AppendLine($"silent-wrong: {SilentWrong,5}  fields wrong with no warning <- hard gate");
        sb.AppendLine($"wrong:        {Wrong,5}  fields");
        sb.AppendLine($"missing:      {Missing,5}  fields");
        sb.AppendLine($"extra:        {Extra,5}  fields");
        sb.AppendLine($"correct:      {Correct,5}  fields");
        sb.AppendLine($"warnings:     {warnings,5}");
        sb.AppendLine($"time:         {elapsed.TotalSeconds:F1}s total");

        if (Unscored.Count > 0)
            sb.AppendLine($"unscored:     {Unscored.Count} sample(s) on disk with no ground truth: {string.Join(", ", Unscored)}");
        if (Skipped.Count > 0)
            sb.AppendLine($"skipped:      {Skipped.Count} sample(s) in ground truth but not on disk");

        return sb.ToString();
    }

    public string RenderDiff()
    {
        var sb = new StringBuilder();
        foreach (SampleResult sample in _samples.Where(s => s.Structural || s.Failures.Any()))
        {
            sb.AppendLine($"### {sample.File}{(sample.Verified ? "" : "  (unverified)")}");
            if (sample.Structural)
                sb.AppendLine($"    STRUCTURAL: expected {sample.ExpectedWindows} window(s), got {sample.ActualWindows}");

            foreach (FieldResult f in sample.Failures)
                sb.AppendLine($"    {f.Verdict,-7} {f.Field,-26} expected={Show(f.Expected),-30} actual={Show(f.Actual),-30} {(f.Flagged ? "" : "<- SILENT")}");
        }
        return sb.Length == 0 ? "(no field differences)\n" : sb.ToString();
    }

    private static string Show(string? v) => v is null ? "(none)" : $"\"{v}\"";

    private int CountVerdict(FieldVerdict verdict) => _samples.Sum(s => s.Fields.Count(f => f.Verdict == verdict));
}
