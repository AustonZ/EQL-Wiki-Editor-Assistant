using EQLWikiEditorAssistant.Core.Items;
using EQLWikiEditorAssistant.Core.Locate;
using EQLWikiEditorAssistant.Core.Ocr;

namespace EQLWikiEditorAssistant.TestSupport.Accuracy;

/// <summary>Everything one screenshot produced, so callers can score it, bootstrap from it, or print it without
/// each re-running the (slow) pipeline in its own way. <see cref="Font"/> is the font it was read in.</summary>
public sealed record CorpusSample(
    string File, IReadOnlyList<LocatedWindow> Windows, IReadOnlyList<ParsedItem?> Items, UiFont Font = UiFont.Arial)
{
    public int Occluded => Windows.Count(w => w.PossiblyOccluded);
    public int Warnings => Items.Sum(i => i?.Warnings.Count ?? 0);

    /// <summary>Windows whose pixels say they were drawn in a different font from the one the sample was read in.
    /// Always a defect in the corpus rather than in the reader: the sample's ground truth names the wrong font, so
    /// its I's and l's were read by the wrong rule.</summary>
    public int FontMismatches => Windows.Count(w => w.DrawnIn is { } drawn && drawn != Font);
}

/// <summary>
/// Runs the real Locate -> Parse pipeline over the samples corpus. Shared by <c>tools/AccuracySpike</c> and the
/// corpus regression test so both measure exactly the same thing — if the tool and the test could drift, the
/// number the test gates on would stop meaning what the tool reported.
/// </summary>
public static class CorpusRunner
{
    /// <summary>Sample screenshots on disk, in stable filename order. Empty on a fresh clone (samples/ is
    /// gitignored), which every caller must treat as "skip", never "fail".</summary>
    public static IReadOnlyList<string> EnumerateSampleFiles()
    {
        if (!Directory.Exists(RepoPaths.SamplesDirectory)) return [];
        return [.. Directory.EnumerateFiles(RepoPaths.SamplesDirectory, "*.png")
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)];
    }

    /// <summary>Locates and parses one screenshot. Items are index-aligned with windows; an occluded window
    /// yields a null item, because it is deliberately never parsed.</summary>
    public static async Task<CorpusSample> RunAsync(
        string path, IOcrEngine engine, UiFont font, CancellationToken cancellationToken = default)
    {
        CapturedImage image = await ImageFile.LoadAsync(path);
        IReadOnlyList<LocatedWindow> windows = await ItemWindowLocator.LocateAsync(image, engine, cancellationToken);

        var items = windows
            .Select(w => w.PossiblyOccluded ? null : ItemParser.Parse(w.Lines, w.ActiveTab))
            .ToList();

        return new CorpusSample(Path.GetFileName(path), windows, items, font);
    }

    /// <summary>
    /// Runs every sample, <paramref name="workers"/> at a time (user, 2026-10-09: the corpus took four minutes on a
    /// 32-thread machine, one screenshot after another). Results come back in <paramref name="files"/>' order whatever
    /// finished first, so a report is identical at any worker count.
    ///
    /// **Each worker gets an engine of its own** from <paramref name="createEngine"/>, rather than sharing one, because
    /// RapidOCR is not written to be called from two threads at once. That costs a set of loaded models per worker,
    /// which is why the count is a parameter rather than "every core". **The factory is told how many threads its
    /// engine may use** — the machine's share per worker — because an engine left to size itself takes the whole
    /// machine, and several of them then fight: measured, 4 and 8 such workers were no faster than one. <paramref name="configure"/> picks the engine and
    /// font for one sample given its worker's engine, as <see cref="SampleFonts"/> does.
    /// </summary>
    public static async Task<IReadOnlyList<CorpusSample>> RunAllAsync(
        IReadOnlyList<string> files,
        Func<int, IOcrEngine> createEngine,
        Func<string, IOcrEngine, (IOcrEngine Engine, UiFont Font)> configure,
        int workers = 0,
        CancellationToken cancellationToken = default)
    {
        if (workers <= 0) workers = DefaultWorkers;
        var results = new CorpusSample[files.Count];
        int next = -1;
        int threads = Math.Max(1, Environment.ProcessorCount / workers);

        await Task.WhenAll(Enumerable.Range(0, Math.Clamp(workers, 1, Math.Max(files.Count, 1))).Select(_ =>
            Task.Run(async () =>
            {
                IOcrEngine own = createEngine(threads);
                try
                {
                    for (int i = Interlocked.Increment(ref next); i < files.Count; i = Interlocked.Increment(ref next))
                    {
                        (IOcrEngine engine, UiFont font) = configure(files[i], own);
                        results[i] = await RunAsync(files[i], engine, font, cancellationToken);
                    }
                }
                finally
                {
                    (own as IDisposable)?.Dispose();
                }
            }, cancellationToken)));

        return results;
    }

    /// <summary>How many samples run at once when the caller does not say. Measured: see CLAUDE.md, "Measuring
    /// extraction accuracy".</summary>
    public static int DefaultWorkers => Math.Clamp(Environment.ProcessorCount / 4, 1, 8);

    /// <summary>Scores a run against ground truth. Samples on disk with no expected entry are reported as
    /// unscored rather than skipped silently, so a new capture batch is visible immediately.</summary>
    public static AccuracyReport Score(IReadOnlyList<CorpusSample> samples, ExpectedCorpus expected)
    {
        var report = new AccuracyReport();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (CorpusSample sample in samples)
        {
            ExpectedSample? want = expected.Find(sample.File);
            if (want is null)
            {
                report.Unscored.Add(sample.File);
                continue;
            }
            seen.Add(sample.File);

            bool structural = want.Windows.Count != sample.Windows.Count;
            var fields = new List<FieldResult>();
            if (!structural)
            {
                for (int i = 0; i < want.Windows.Count; i++)
                    fields.AddRange(ItemComparer.CompareWindow(want.Windows[i], sample.Items[i]));
            }

            report.Add(new SampleResult(
                sample.File, want.Verified, structural, want.Windows.Count, sample.Windows.Count, fields));
        }

        foreach (ExpectedSample want in expected.Samples.Where(s => !seen.Contains(s.File)))
            report.Skipped.Add(want.File);

        return report;
    }

    /// <summary>Builds a candidate ground-truth entry from what the pipeline currently produces. Every field the
    /// parser flagged is emitted as <see cref="ExpectedCorpus.TodoMarker"/> rather than as its (absent) value —
    /// that is what converts the known misses from an unmeasurable absence into ground truth a human must supply,
    /// instead of freezing today's bugs in as "correct".</summary>
    public static ExpectedSample Bootstrap(CorpusSample sample)
    {
        var entry = new ExpectedSample
        {
            File = sample.File,
            Verified = false,
            Font = sample.Font == UiFont.Arial ? null : sample.Font,
        };

        foreach ((LocatedWindow window, ParsedItem? item) in sample.Windows.Zip(sample.Items))
        {
            if (item is null)
            {
                entry.Windows.Add(new ExpectedWindow { Occluded = true });
                continue;
            }

            var w = new ExpectedWindow
            {
                Name = item.Name.Length == 0 ? ExpectedCorpus.TodoMarker : item.Name,
                Level = item.Level,
                TitleContentNameMismatch = item.TitleContentNameMismatch,
                Flags = [.. item.Flags],
                Classes = [.. item.Classes],
                Races = [.. item.Races],
                Slots = [.. item.Slots],
                MerchantValue = item.MerchantValue,
                Lore = item.Lore,
                WarningCount = item.Warnings.Count,
                Stats = [.. item.Stats.Select(s => new ExpectedField { Label = s.Key, Value = s.Value })],
                Exaltations = [.. item.ExaltationSlots.Select(e => new ExpectedExaltation { Kind = e.Kind.ToString(), Name = e.Name })],
                Effects = [.. item.Effects.Select(e => new ExpectedEffect
                {
                    Kind = e.Kind,
                    Name = e.Name,
                    Conditions = [.. e.Conditions],
                    Modifiers = [.. e.Modifiers.Select(m => new ExpectedField { Label = m.Key, Value = m.Value })],
                })],
            };

            // An orphaned-label warning means the parser saw a field whose value OCR never produced. Record the
            // label with a TODO so the reviewer has to read the real value off the screenshot.
            foreach (string label in OrphanedLabels(item))
                w.Stats.Add(new ExpectedField { Label = label, Value = ExpectedCorpus.TodoMarker });

            entry.Windows.Add(w);
        }

        return entry;
    }

    /// <summary>Extracts the label out of an orphaned-label warning — i.e. a field whose value OCR dropped.
    ///
    /// Only fragments that actually look like a field label qualify: one the lexicon knows, or one carrying the
    /// bare-label shape (a trailing ':' or OCR's '.'). The stat block also strands genuine junk — a stray "0", a
    /// wrapped "(Can Equip)" — and emitting those as TODO fields would hand the reviewer rows to *delete* rather
    /// than values to *read*, which is friction that invites mistakes in exactly the file that has to be right.</summary>
    private static IEnumerable<string> OrphanedLabels(ParsedItem item)
    {
        const string prefix = "Unparsed line in stat block: \"";
        foreach (string warning in item.Warnings)
        {
            int start = warning.IndexOf(prefix, StringComparison.Ordinal);
            if (start < 0) continue;

            int from = start + prefix.Length;
            int end = warning.IndexOf('"', from);
            if (end < 0) continue;

            string raw = warning[from..end];
            string label = raw.TrimEnd(':', '.', ' ');
            if (label.Length == 0) continue;

            bool looksLikeLabel = raw.EndsWith(':') || raw.EndsWith('.') || FieldLabelLexicon.IsKnownLabel(label);
            if (looksLikeLabel) yield return label;
        }
    }
}
