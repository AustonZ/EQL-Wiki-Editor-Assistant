using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using EQLWikiEditorAssistant.Core.Items;
using EQLWikiEditorAssistant.Pipeline;
using EQLWikiEditorAssistant.TestSupport;
using EQLWikiEditorAssistant.Wiki.Analysis;
using EQLWikiEditorAssistant.Wiki.Formatting;
using EQLWikiEditorAssistant.Wiki.Mapping;
using EQLWikiEditorAssistant.Wiki.MediaWiki;
using EQLWikiEditorAssistant.Wiki.Wikitext;

// The measurement behind combining the edit and formatting steps (user, 2026-10-12). The design under test shows two
// diffs instead of one: the data changes as "formatted original -> formatted final", where both sides share one
// layout so only the data differs, and the reformatting as "original -> formatted original", collapsed. Whether that
// split is clean is a property of real pages, so it is measured here before any UI is built.
//
//   WikiSpike freshpages <count> [--seed N]   sample item pages nobody has edited within the wiki's recent-changes
//                                             window and that the existing cache does not hold, into
//                                             .local-data/wiki-pages-fresh
//   WikiSpike split --cached <dir> [--cached <dir>...] [--examples N] [--show <title>]

internal static class FreshPages
{
    public static async Task<int> RunAsync(Uri endpoint, string[] args)
    {
        int count = args.Length > 1 && int.TryParse(args[1], out int n) ? n : 400;
        int seedArg = Array.IndexOf(args, "--seed");
        int seed = seedArg >= 0 && seedArg + 1 < args.Length && int.TryParse(args[seedArg + 1], out int s) ? s : 11;

        using MediaWikiClient client = MediaWikiClient.Create(endpoint);
        IReadOnlyList<string> titles = await client.ListTransclusionsAsync("Template:Itempage");
        Console.WriteLine($"{titles.Count} pages transclude {{{{Itempage}}}}.");

        // Every main-namespace page edited or created within the recent-changes window. One request per 500 changes.
        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd(MediaWikiClient.UserAgent);
        var changed = new HashSet<string>(StringComparer.Ordinal);
        string? oldest = null, continuation = null;
        int requests = 0;
        do
        {
            string url = endpoint + "?action=query&format=json&formatversion=2&list=recentchanges&rcnamespace=0" +
                         "&rcprop=title%7Ctimestamp&rclimit=500&rctype=edit%7Cnew&maxlag=5" +
                         (continuation is null ? "" : "&rccontinue=" + Uri.EscapeDataString(continuation));
            using JsonDocument response = JsonDocument.Parse(await http.GetStringAsync(url));
            requests++;
            foreach (JsonElement change in response.RootElement.GetProperty("query").GetProperty("recentchanges").EnumerateArray())
            {
                changed.Add(change.GetProperty("title").GetString()!);
                oldest = change.GetProperty("timestamp").GetString();
            }
            continuation = response.RootElement.TryGetProperty("continue", out JsonElement cont) &&
                           cont.TryGetProperty("rccontinue", out JsonElement rc)
                ? rc.GetString()
                : null;
        } while (continuation is not null);
        Console.WriteLine($"{changed.Count} main-namespace pages changed since {oldest} ({requests} request(s)).");

        string oldCache = Path.Combine(RepoPaths.LocalDataDirectory, "wiki-pages");
        var cached = Directory.Exists(oldCache)
            ? Directory.EnumerateFiles(oldCache, "*.txt").Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.Ordinal)
            : [];

        List<string> candidates = [.. titles.Where(t => !changed.Contains(t) && !cached.Contains(Sanitize(t)))];
        Console.WriteLine($"{candidates.Count} item pages untouched in that window and not already cached; " +
                          $"sampling {Math.Min(count, candidates.Count)} with seed {seed}.");

        var random = new Random(seed);
        string directory = Path.Combine(RepoPaths.LocalDataDirectory, "wiki-pages-fresh");
        Directory.CreateDirectory(directory);
        int fetched = 0;
        foreach (string title in candidates.OrderBy(_ => random.Next()).Take(count))
        {
            WikiPage? page = await client.FetchPageAsync(title);
            if (page is null) continue;
            await File.WriteAllTextAsync(Path.Combine(directory, Sanitize(page.Title) + ".txt"), page.Wikitext);
            fetched++;
            // Someone else's bandwidth: no hurry.
            await Task.Delay(100);
        }
        Console.WriteLine($"Cached {fetched} page(s) to {directory}");
        return 0;
    }

    internal static string Sanitize(string title) =>
        string.Concat(title.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}

internal static class SplitMeasurement
{
    private enum Layout { BothLaidOut, LegacyTransition, BothLeftAlone, FinalLeftAlone }

    private sealed record PageResult(
        string Title, Layout Layout, bool DataChanged, int DataLines, int ReformatLines,
        IReadOnlyList<string> Collateral, int BlankCollateral, string Summary);

    public static int Run(string[] args)
    {
        var directories = new List<string>();
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--cached") directories.Add(args[i + 1]);
        if (directories.Count == 0)
        {
            Console.Error.WriteLine("usage: WikiSpike split --cached <dir> [--cached <dir>...] [--examples N] [--show <title>]");
            return 1;
        }
        int examplesArg = Array.IndexOf(args, "--examples");
        int examples = examplesArg >= 0 && int.TryParse(args[examplesArg + 1], out int e) ? e : 12;
        int showArg = Array.IndexOf(args, "--show");
        string? show = showArg >= 0 ? args[showArg + 1] : null;

        WikiMapping mapping = WikiMapping.Default;
        var results = new List<PageResult>();
        int notItemPages = 0, refusedOriginal = 0, refusedFinal = 0;
        var blankExamples = new List<string>();

        foreach (string directory in directories)
        foreach (string file in Directory.EnumerateFiles(directory, "*.txt").Order(StringComparer.Ordinal))
        {
            string title = Path.GetFileNameWithoutExtension(file);
            string original = File.ReadAllText(file);
            ItemPageDocument? document = ItemPageDocument.Parse(original);
            if (document is null) { notItemPages++; continue; }

            ParsedItem capture = SyntheticCapture(document, title, mapping);
            ItemPageAnalysis analysis = ItemPageAnalyzer.Analyze(capture, document, title);
            ProposedEdit edit = ItemPageEditor.BuildEdit(document, analysis);
            string final = edit.NewWikitext;

            PrettifyResult formattedOriginal = ItemPagePrettifier.Format(original);
            PrettifyResult formattedFinal = ItemPagePrettifier.Format(final);
            if (!formattedOriginal.IsSafe) refusedOriginal++;
            if (!formattedFinal.IsSafe) refusedFinal++;
            string fo = formattedOriginal.IsSafe ? formattedOriginal.Formatted : original;
            string ff = formattedFinal.IsSafe ? formattedFinal.Formatted : final;

            bool originalLeftAlone = LeftAlone(formattedOriginal), finalLeftAlone = LeftAlone(formattedFinal);
            Layout layout = (originalLeftAlone, finalLeftAlone) switch
            {
                (false, false) => Layout.BothLaidOut,
                (true, false) => Layout.LegacyTransition,
                (true, true) => Layout.BothLeftAlone,
                (false, true) => Layout.FinalLeftAlone,
            };

            // What the data step touched, as keys: the raw diff it made, before any formatting.
            HashSet<string> touched = [.. Changed(original, final).SelectMany(l => Keys(l.Text, l.Owner))];

            List<(string Text, string? Owner)> dataDiff = Changed(fo, ff);
            // A line the data step changed shows as a removed and an added line; the half that only carries untouched
            // fields (`Haste` on a line `HP Regen` joined) is still part of that change, not collateral.
            List<HashSet<string>> dataLines = [.. dataDiff
                .Select(l => Keys(l.Text, l.Owner))
                .Where(k => k.Any(touched.Contains))
                .Select(k => k.ToHashSet())];
            var collateral = new List<string>();
            int blank = 0;
            foreach ((string text, string? owner) in dataDiff)
            {
                IReadOnlyList<string> keys = Keys(text, owner);
                if (text.Trim().Length == 0) { blank++; continue; }
                if (keys.Any(touched.Contains)) continue;
                if (dataLines.Any(d => keys.All(d.Contains))) continue;
                collateral.Add(text);
            }
            if (blank > 0 && blankExamples.Count < 6) blankExamples.Add(title);

            results.Add(new PageResult(
                title, layout, edit.HasChanges, dataDiff.Count, Changed(original, fo).Count, collateral, blank,
                edit.HasChanges ? edit.Summary : ""));

            if (show is not null && string.Equals(title, show, StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"=== {title} ({layout}) ===");
                Console.WriteLine("--- data diff: formatted original -> formatted final ---");
                Print(fo, ff);
                Console.WriteLine("--- reformatting: original -> formatted original ---");
                Print(original, fo);
                Console.WriteLine("--- what the data step alone wrote: original -> final ---");
                Print(original, final);
                Console.WriteLine();
            }
        }

        Report(results, notItemPages, refusedOriginal, refusedFinal, examples);
        Console.WriteLine();
        Console.WriteLine($"Pages whose data diff changes a blank line, e.g.: {string.Join(", ", blankExamples)}");

        string tsv = Path.Combine(RepoPaths.LocalDataDirectory, "split.tsv");
        File.WriteAllLines(tsv, results.Select(r =>
            $"{r.Title}\t{r.Layout}\t{r.DataLines}\t{r.ReformatLines}\t{r.Collateral.Count}\t{r.BlankCollateral}"));
        Console.WriteLine($"\nPer page: {tsv}");
        return 0;
    }

    private static void Report(
        List<PageResult> results, int notItemPages, int refusedOriginal, int refusedFinal, int examples)
    {
        Console.WriteLine();
        Console.WriteLine($"=== {results.Count} item pages ({notItemPages} not item pages skipped) ===");
        Console.WriteLine($"  data step changed the page : {results.Count(r => r.DataChanged)}");
        Console.WriteLine($"  formatter refused original : {refusedOriginal}");
        Console.WriteLine($"  formatter refused final    : {refusedFinal}");

        Console.WriteLine();
        Console.WriteLine("--- statsblock layout (original -> final) ---");
        foreach (Layout layout in Enum.GetValues<Layout>())
        {
            List<PageResult> group = [.. results.Where(r => r.Layout == layout)];
            Console.WriteLine($"  {layout,-18} {group.Count,5} pages, {group.Count(r => r.Collateral.Count > 0),5} with collateral");
        }

        Console.WriteLine();
        Console.WriteLine("--- diff sizes (changed lines) ---");
        Console.WriteLine($"  data diff    median {Median(results.Select(r => r.DataLines))}, max {results.Max(r => r.DataLines)}");
        Console.WriteLine($"  reformatting median {Median(results.Select(r => r.ReformatLines))}, max {results.Max(r => r.ReformatLines)}");
        Console.WriteLine($"  blank lines in the data diff: {results.Sum(r => r.BlankCollateral)} on " +
                          $"{results.Count(r => r.BlankCollateral > 0)} pages");

        Console.WriteLine();
        Console.WriteLine("--- collateral: data-diff lines carrying nothing the data step touched ---");
        foreach (var group in results.SelectMany(r => r.Collateral.Select(c => (r.Layout, Kind: KindOf(c))))
                     .GroupBy(x => x).OrderByDescending(g => g.Count()))
            Console.WriteLine($"  {group.Count(),5}  {group.Key.Layout,-18} {group.Key.Kind}");

        foreach (Layout layout in Enum.GetValues<Layout>())
        {
            List<PageResult> withCollateral = [.. results.Where(r => r.Layout == layout && r.Collateral.Count > 0)];
            if (withCollateral.Count == 0) continue;
            Console.WriteLine();
            Console.WriteLine($"--- examples, {layout} ---");
            foreach (PageResult r in withCollateral.Take(examples))
            {
                Console.WriteLine($"  {r.Title}");
                foreach (string line in r.Collateral.Take(4)) Console.WriteLine($"      {Shorten(line)}");
            }
        }

        Console.WriteLine();
        Console.WriteLine("--- what the simulated data edits did (the commonest summaries) ---");
        foreach (var group in results.Where(r => r.Summary.Length > 0).GroupBy(r => r.Summary)
                     .OrderByDescending(g => g.Count()).Take(8))
            Console.WriteLine($"  {group.Count(),5}  {group.Key}");
    }

    /// <summary>
    /// The page's own data as if the game had shown it, plus three edits the Assistant commonly makes: one stat value
    /// changed, one stat the page lacks added, and a merchant value. Legacy flags become what the game would show
    /// (`NO DROP` as `No Trade`, the rest dropped), so a legacy page goes through the same modernization a real capture
    /// gives it. Effects and lore are left out; the analyzer leaves a page's own effect lines and lore alone then.
    /// </summary>
    private static ParsedItem SyntheticCapture(ItemPageDocument document, string title, WikiMapping mapping)
    {
        var flags = new List<string>();
        var classes = new List<string>();
        var races = new List<string>();
        var slots = new List<string>();
        var stats = new List<KeyValuePair<string, string>>();
        Dictionary<string, string> slotsBack = mapping.Slots.ToDictionary(p => p.Value, p => p.Key, StringComparer.OrdinalIgnoreCase);

        if (document.ReadStatsBlock() is { } block)
        {
            foreach (StatsBlockLine line in block.Lines)
            {
                foreach (string flag in line.Flags)
                {
                    if (FlagDialect.IsCurrent(flag)) flags.Add(flag);
                    else if (flag is "NO DROP" or "NODROP" or "NO TRADE") flags.Add("No Trade");
                }

                foreach (StatsField field in line.Fields)
                {
                    string[] words = field.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    switch (field.Label.ToUpperInvariant())
                    {
                        case "CLASS": classes.AddRange(words); continue;
                        case "RACE": races.AddRange(words); continue;
                        case "SLOT":
                            slots.AddRange(words.Select(w => slotsBack.TryGetValue(w, out string? game)
                                ? game
                                : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(w.ToLowerInvariant())));
                            continue;
                    }

                    if (mapping.FindStatByWikiLabel(field.Label) is not { Disposition: StatDisposition.Stored } stat ||
                        stat.Format != StatValueFormat.AsCaptured)
                        continue;
                    string value = field.Value.Trim();
                    if (stat.Signed && value.StartsWith('+')) value = value[1..];
                    if (stat.WikiSuffix is { } suffix && value.EndsWith(suffix, StringComparison.Ordinal)) value = value[..^suffix.Length];
                    stats.Add(new(stat.GameLabel, value));
                }
            }
        }

        // One value changed: the first whole-number stat that is not a name-like field.
        int changeAt = stats.FindIndex(p => int.TryParse(p.Value, out _) && p.Key is not ("Size" or "Skill"));
        if (changeAt >= 0) stats[changeAt] = new(stats[changeAt].Key, (int.Parse(stats[changeAt].Value) + 1).ToString());
        // One stat added that the page does not have, which the formatter has to find a place for.
        if (!stats.Any(p => p.Key == "HP Regen")) stats.Add(new("HP Regen", "2"));

        return new ParsedItem(
            document.ItemName ?? title, 0, false, flags, classes, races, slots, stats, [], [],
            "3 platinum 5 gold", null, []);
    }

    private static bool LeftAlone(PrettifyResult result) =>
        !result.IsSafe || result.Notes.Any(n => n.StartsWith("The statsblock was left", StringComparison.Ordinal));

    /// <summary>The changed lines of a diff, each with the template parameter it sits inside, if any — a line of a
    /// multi-line value (an old merchant value's <c>&lt;ul&gt;</c>) says nothing about which parameter it belongs to on
    /// its own.</summary>
    private static List<(string Text, string? Owner)> Changed(string before, string after)
    {
        string?[] beforeOwners = Owners(before), afterOwners = Owners(after);
        return [.. WikitextDiff.Compute(before, after, context: 0)
            .Where(l => l.Kind is DiffLineKind.Added or DiffLineKind.Removed)
            .Select(l => (l.Text, l.Kind == DiffLineKind.Removed ? beforeOwners[l.BeforeLine!.Value - 1] : afterOwners[l.AfterLine!.Value - 1]))];
    }

    private static readonly Regex ParameterStart = new(@"^\|\s*([A-Za-z_]+)\s*=");

    private static string?[] Owners(string text)
    {
        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        var owners = new string?[lines.Length];
        string? current = null;
        bool inTemplate = false;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.Contains("{{Itempage", StringComparison.OrdinalIgnoreCase)) inTemplate = true;
            if (inTemplate && ParameterStart.Match(line) is { Success: true } start) current = start.Groups[1].Value.ToLowerInvariant();
            owners[i] = inTemplate ? current : null;
            if (inTemplate && line.TrimStart().StartsWith("}}</onlyinclude>", StringComparison.Ordinal)) { inTemplate = false; current = null; }
        }
        return owners;
    }

    private static readonly Regex Category = new(@"^\[\[\s*Category\s*:\s*([^\]|]+)", RegexOptions.IgnoreCase);

    /// <summary>What a line is about: the parameter it opens, the statsblock fields or flags it holds, the category it
    /// links, or failing those its own text. A data-diff line is data when it shares a key with what the data step
    /// touched.</summary>
    private static IReadOnlyList<string> Keys(string line, string? owner)
    {
        string text = line.Trim();
        if (text.Length == 0) return ["<blank>"];

        var keys = new List<string>();
        // Any line inside a parameter other than the statsblock belongs to that parameter.
        if (owner is not null && owner != "statsblock") keys.Add("param:" + owner);
        if (text.StartsWith('|') && text.IndexOf('=') is > 0 and int equals)
        {
            keys.Add("param:" + text[1..equals].Trim().ToLowerInvariant());
            text = text[(equals + 1)..].Trim();
            if (text.Length == 0) return keys;
        }

        if (Category.Match(text) is { Success: true } category)
        {
            string name = category.Groups[1].Value.Replace('_', ' ').Trim();
            keys.Add("cat:" + char.ToUpperInvariant(name[0]) + name[1..]);
            return keys;
        }

        // One physical line can hold several statsblock lines (`HP Regen: 2<br>Class: ALL<br>`), so all of them count.
        foreach (StatsBlockLine parsed in StatsBlock.Parse(text).Lines)
        {
            switch (parsed.Kind)
            {
                case StatsLineKind.Flags:
                    keys.Add("flags");
                    // A flags line can carry a field too (`EXPENDABLE  Charges: 10`).
                    keys.AddRange(parsed.Fields.Select(f => "stat:" + f.Label.ToLowerInvariant()));
                    break;
                case StatsLineKind.Fields:
                    keys.AddRange(parsed.Fields.Select(f => "stat:" + f.Label.ToLowerInvariant()));
                    break;
                case StatsLineKind.Unparsed:
                    keys.Add("text:" + parsed.Text);
                    break;
            }
        }
        if (keys.Count == 0) keys.Add("text:" + text);
        return keys;
    }

    private static string KindOf(string line)
    {
        IReadOnlyList<string> keys = Keys(line, null);
        string first = keys[0];
        if (first.StartsWith("text:", StringComparison.Ordinal)) return "other text";
        int colon = first.IndexOf(':');
        return colon > 0 ? first[..colon] : first;
    }

    private static void Print(string before, string after)
    {
        foreach (DiffLine line in WikitextDiff.Compute(before, after))
        {
            string marker = line.Kind switch { DiffLineKind.Added => "+ ", DiffLineKind.Removed => "- ", DiffLineKind.Gap => "  ", _ => "  " };
            Console.WriteLine(marker + line.Text);
        }
    }

    private static int Median(IEnumerable<int> values)
    {
        int[] sorted = [.. values.Order()];
        return sorted.Length == 0 ? 0 : sorted[sorted.Length / 2];
    }

    private static string Shorten(string line) => line.Length <= 110 ? line : line[..107] + "...";
}
