using System.Net.Http;
using System.Text.Json;
using EQLWikiEditorAssistant.TestSupport;
using EQLWikiEditorAssistant.Wiki.MediaWiki;
using EQLWikiEditorAssistant.Wiki.Wikitext;

// The census behind recognizing the legacy statsblock vocabulary by name (user, 2026-10-12): the Project1999-era
// flags (`MAGIC ITEM`, `NO DROP`, ...) and the food and drink lines (`This is a Hearty Meal!`). Both are fixed lists,
// so every page is read once to find the whole of each rather than trusting a sample.
//
//   WikiSpike allpages                  every page that transcludes {{Itempage}}, 50 per request, into
//                                       .local-data/wiki-pages-all (pages already there are not fetched again)
//   WikiSpike flagcensus --cached <dir> every flags-line token that is not a current flag, and every statsblock line
//                                       the grammar could not read, with counts and an example page

internal static class AllPages
{
    public static async Task<int> RunAsync(Uri endpoint)
    {
        string directory = Path.Combine(RepoPaths.LocalDataDirectory, "wiki-pages-all");
        Directory.CreateDirectory(directory);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(MediaWikiClient.UserAgent);

        string? continuation = null;
        int requests = 0, saved = 0;
        do
        {
            string url = endpoint + "?action=query&format=json&formatversion=2&maxlag=5" +
                         "&generator=embeddedin&geititle=Template:Itempage&geinamespace=0&geilimit=50" +
                         "&prop=revisions&rvprop=content&rvslots=main" + continuation;
            using JsonDocument response = JsonDocument.Parse(await http.GetStringAsync(url));
            requests++;

            if (response.RootElement.TryGetProperty("error", out JsonElement error))
            {
                // maxlag: the wiki is busy, so wait as it asks rather than adding to it.
                Console.WriteLine($"  wiki said {error.GetProperty("code").GetString()}; waiting 10 s");
                await Task.Delay(TimeSpan.FromSeconds(10));
                continue;
            }

            if (response.RootElement.TryGetProperty("query", out JsonElement query))
                foreach (JsonElement page in query.GetProperty("pages").EnumerateArray())
                {
                    if (!page.TryGetProperty("revisions", out JsonElement revisions)) continue;
                    string title = page.GetProperty("title").GetString()!;
                    string text = revisions[0].GetProperty("slots").GetProperty("main").GetProperty("content").GetString() ?? "";
                    await File.WriteAllTextAsync(Path.Combine(directory, FreshPages.Sanitize(title) + ".txt"), text);
                    saved++;
                }

            // The API's continuation is every parameter in "continue", passed back as is.
            continuation = response.RootElement.TryGetProperty("continue", out JsonElement cont)
                ? string.Concat(cont.EnumerateObject().Select(p => "&" + p.Name + "=" + Uri.EscapeDataString(p.Value.GetString()!)))
                : null;

            if (requests % 25 == 0) Console.WriteLine($"  {requests} requests, {saved} pages");
            await Task.Delay(250);
        } while (continuation is not null);

        Console.WriteLine($"Saved {saved} page(s) to {directory} in {requests} request(s).");
        return 0;
    }
}

internal static class FlagCensus
{
    public static int Run(string[] args)
    {
        int at = Array.IndexOf(args, "--cached");
        if (at < 0 || at + 1 >= args.Length) { Console.Error.WriteLine("usage: WikiSpike flagcensus --cached <dir>"); return 1; }

        var tokens = new Dictionary<string, (int Count, string Example)>(StringComparer.Ordinal);
        var unreadable = new Dictionary<string, (int Count, string Example)>(StringComparer.Ordinal);
        int itemPages = 0, withLegacy = 0;
        int multipleFlagLines = 0;
        var current = new Dictionary<string, (int Count, string Example)>(StringComparer.Ordinal);
        var laterFlagLines = new Dictionary<string, (int Count, string Example)>(StringComparer.Ordinal);

        foreach (string file in Directory.EnumerateFiles(args[at + 1], "*.txt"))
        {
            ItemPageDocument? document = ItemPageDocument.Parse(File.ReadAllText(file));
            if (document?.ReadStatsBlock() is not { } block) continue;
            itemPages++;
            string title = Path.GetFileNameWithoutExtension(file);
            bool legacy = false;

            foreach (StatsBlockLine line in block.Lines)
            {
                foreach (string flag in line.Flags.Where(FlagDialect.IsCurrent))
                    Count(current, flag, title);
                foreach (string flag in line.Flags.Where(f => !FlagDialect.IsCurrent(f)))
                {
                    legacy = true;
                    Count(tokens, flag, title);
                }
                if (line.Kind == StatsLineKind.Unparsed) Count(unreadable, line.Text, title);
            }
            if (legacy) withLegacy++;

            List<StatsBlockLine> flagLines = [.. block.Lines.Where(l => l.Flags.Count > 0)];
            if (flagLines.Count > 1)
            {
                multipleFlagLines++;
                foreach (StatsBlockLine extra in flagLines.Skip(1))
                    Count(laterFlagLines, string.Join(" | ", extra.Flags) + (extra.Fields.Count > 0 ? "  (+fields)" : ""), title);
            }
        }

        Console.WriteLine($"{multipleFlagLines} item pages carry flags on more than one statsblock line. The later lines:");
        foreach (var (text, (count, example)) in laterFlagLines.OrderByDescending(t => t.Value.Count).Take(40))
            Console.WriteLine($"  {count,6}  {(text.Length > 70 ? text[..67] + "..." : text),-70} e.g. {example}");
        Console.WriteLine();

        Console.WriteLine($"--- current flags ({current.Count} distinct) ---");
        foreach (var (token, (count, example)) in current.OrderByDescending(t => t.Value.Count))
            Console.WriteLine($"  {count,6}  {token,-40} e.g. {example}");
        Console.WriteLine();
        Console.WriteLine($"{itemPages} item pages; {withLegacy} carry a flags-line token that is not a current flag.");
        Console.WriteLine();
        Console.WriteLine($"--- flags-line tokens that are not current flags ({tokens.Count} distinct) ---");
        foreach (var (token, (count, example)) in tokens.OrderByDescending(t => t.Value.Count))
            Console.WriteLine($"  {count,6}  {token,-40} e.g. {example}");

        Console.WriteLine();
        Console.WriteLine($"--- statsblock lines the grammar could not read ({unreadable.Count} distinct, top 60) ---");
        foreach (var (text, (count, example)) in unreadable.OrderByDescending(t => t.Value.Count).Take(60))
            Console.WriteLine($"  {count,6}  {(text.Length > 70 ? text[..67] + "..." : text),-70} e.g. {example}");
        return 0;

        static void Count(Dictionary<string, (int, string)> into, string key, string example) =>
            into[key] = into.TryGetValue(key, out var seen) ? (seen.Item1 + 1, seen.Item2) : (1, example);
    }
}
