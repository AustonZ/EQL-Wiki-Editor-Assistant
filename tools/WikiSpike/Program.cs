using EQLWikiAssistant.Capture;
using EQLWikiAssistant.TestSupport;
using EQLWikiAssistant.Wiki.MediaWiki;
using EQLWikiAssistant.Core.Icons;
using EQLWikiAssistant.Core.Items;
using EQLWikiAssistant.Core.Locate;
using EQLWikiAssistant.Core.Ocr;
using EQLWikiAssistant.Core.Glyphs;
using EQLWikiAssistant.Ocr;
using EQLWikiAssistant.TestSupport.Accuracy;
using EQLWikiAssistant.Wiki.Analysis;
using EQLWikiAssistant.Wiki.Formatting;
using EQLWikiAssistant.Wiki.Wikitext;

// The accuracy scorer has its own FieldVerdict (how well extraction did) which is a different question from the
// analyzer's (how the page compares to the capture). This tool bridges both, so it names the one it means.
using FieldVerdict = EQLWikiAssistant.Wiki.Analysis.FieldVerdict;

// Milestone 3 spike tool: exercise the wiki client and the wikitext layer against the real eqlwiki.com, the same
// way OcrSpike/LocateSpike/ParseSpike exercise the capture side against real screenshots. Keep using this rather
// than recreating ad hoc versions when tuning the statsblock grammar or debugging the API client.
//
// Usage:
//   WikiSpike fetch <page title>              print a page's wikitext and its parsed v1 fields
//   WikiSpike roundtrip <count> [--seed N]    fetch N random item pages and verify byte-for-byte round-trip
//   WikiSpike roundtrip --cached <dir>        same, against a directory of previously saved .txt pages
//   WikiSpike grammar <count> [--seed N]      report which statsblock lines the grammar cannot read
//   WikiSpike analyze [--detail]              diff every verified capture against its live wiki page
//   WikiSpike prettify <count>|--cached <dir>  run the formatting pass over real pages and report what it did
//   WikiSpike login                           prompt for a bot password and store it in Credential Manager
//   WikiSpike whoami                          confirm the credential logs in and may edit (writes nothing)
//   WikiSpike verified [<title>...]           the wiki's "Verified for EQLegends" list, and how much of the corpus
//                                             it covers (48 of 744 item pages when measured)
//   WikiSpike logout                          delete the stored credential
//   WikiSpike edit <User: page>               append a timestamped line, then revert it (userspace only)
//
// The round-trip check is the acceptance test for the wikitext layer: parse a real page, render it back, and
// require the result to be identical to the input. Anything less means an edit could silently reformat somebody
// else's work.

var endpoint = new Uri("https://eqlwiki.com/api.php");

if (args.Length == 0) { Usage(); return 1; }

switch (args[0])
{
    case "fetch": return await FetchAsync();
    case "roundtrip": return await RoundTripAsync(reportGrammar: false);
    case "grammar": return await RoundTripAsync(reportGrammar: true);
    case "analyze": return await AnalyzeCorpusAsync();
    case "prettify": return await PrettifyCorpusAsync();
    case "prettyshow": return ShowPrettified();
    case "icons": return args.Contains("--corpus") ? await MeasureIconsAcrossCorpusAsync() : await CompareIconsAsync();
    case "icondiff": return await DiffIconPixelsAsync();
    case "verified": return await VerifiedAsync();
    case "preview": return await PreviewEditsAsync();
    case "login": return Login();
    case "whoami": return await WhoAmIAsync();
    case "logout": return Logout();
    case "edit": return await EditAsync();
    default: Usage(); return 1;
}

void Usage()
{
    Console.Error.WriteLine("usage: WikiSpike fetch|roundtrip|grammar|login|whoami|logout|edit [...]");
    Console.Error.WriteLine("  WikiSpike fetch <title>");
    Console.Error.WriteLine("  WikiSpike roundtrip <count> [--seed N] | --cached <dir>");
    Console.Error.WriteLine("  WikiSpike grammar <count> [--seed N] | --cached <dir>");
    Console.Error.WriteLine("  WikiSpike verified [<title>...]");
    Console.Error.WriteLine("  WikiSpike login | whoami | logout");
    Console.Error.WriteLine("  WikiSpike edit \"User:<you>/sandbox\"   (two revisions, self-reverting, confirms first)");
}

async Task<int> FetchAsync()
{
    if (args.Length < 2) { Usage(); return 1; }

    using MediaWikiClient client = MediaWikiClient.Create(endpoint);
    WikiPage? page = await client.FetchPageAsync(args[1]);
    if (page is null) { Console.Error.WriteLine($"'{args[1]}' does not exist on the wiki."); return 1; }

    Console.WriteLine($"=== {page.Title}  (revision {page.RevisionId}, {page.Timestamp:u}) ===");
    Console.WriteLine(page.Wikitext);
    Console.WriteLine();

    ItemPageDocument? document = ItemPageDocument.Parse(page.Wikitext);
    if (document is null) { Console.WriteLine("(no {{Itempage}} call — not an item page)"); return 0; }

    Console.WriteLine("--- v1 fields ---");
    Console.WriteLine($"  itemname       : {document.ItemName}");
    Console.WriteLine($"  lucy_img_ID    : {document.IconId}");
    Console.WriteLine($"  focus_effect   : {document.FocusEffect ?? "(absent)"}");
    Console.WriteLine($"  merchant_value : {document.MerchantValue ?? "(absent)"}");
    Console.WriteLine($"  lore           : {document.Lore ?? "(none)"}");
    Console.WriteLine($"  lore placeholder present: {document.HasLoreMissingPlaceholder}");
    foreach (string warning in document.Warnings) Console.WriteLine($"  ! {warning}");

    Console.WriteLine("--- statsblock ---");
    StatsBlock? block = document.ReadStatsBlock();
    if (block is null) { Console.WriteLine("  (absent)"); return 0; }

    for (int i = 0; i < block.Lines.Count; i++)
    {
        StatsBlockLine line = block.Lines[i];
        if (line.Kind == StatsLineKind.Blank) continue;
        string detail = line.Kind switch
        {
            StatsLineKind.Fields => string.Join("  ", line.Fields.Select(f => $"[{f.Label}]=[{f.Value}]")),
            StatsLineKind.Flags => "flags: " + string.Join(" | ", line.Flags),
            _ => "UNPARSED",
        };
        if (line.Kind == StatsLineKind.Fields && line.Flags.Count > 0)
            detail = "flags: " + string.Join(" | ", line.Flags) + "  " + detail;
        Console.WriteLine($"  {i,2}  {detail}");
    }

    Console.WriteLine($"--- round-trip: {(block.Render() == document.Template.Find("statsblock")!.RawValue ? "exact" : "DIFFERS")}");
    return 0;
}

async Task<int> RoundTripAsync(bool reportGrammar)
{
    IReadOnlyList<(string Title, string Text)> pages = await LoadPagesAsync();
    if (pages.Count == 0) { Console.Error.WriteLine("No pages to check."); return 1; }

    int items = 0, roundTripFailures = 0, unparsedLines = 0, warned = 0;
    var unparsed = new List<(string Page, string Line)>();
    var labels = new List<(string Page, string Label, string Value)>();
    var flags = new List<(string Page, string Flag)>();

    foreach ((string title, string text) in pages)
    {
        ItemPageDocument? document = ItemPageDocument.Parse(text);
        if (document is null) continue;
        items++;

        // The whole-page guarantee: rewriting a parameter with the value it already has must be an exact no-op.
        // Every real edit is the same splice with a different string, so if this holds for every parameter of
        // every page, edits to those pages are safe — and if it fails anywhere the tool cannot be trusted to
        // touch a public wiki. Mirrors WikiRoundTripTests, which runs the same check offline on the fixtures.
        foreach (string name in document.Template.Parameters
                     .Where(p => p.Name is not null)
                     .Select(p => p.Name!)
                     .Distinct(StringComparer.Ordinal))
        {
            if (document.WithParameter(name, document.GetParameter(name)!).Wikitext == text) continue;
            roundTripFailures++;
            Console.WriteLine($"  ROUND-TRIP FAILED: {title} (rewriting |{name}= changed the page)");
        }

        StatsBlock? block = document.ReadStatsBlock();
        if (block is not null && document.WithStatsBlock(block).Wikitext != text)
        {
            roundTripFailures++;
            Console.WriteLine($"  ROUND-TRIP FAILED: {title} (statsblock)");
        }

        if (document.Warnings.Count > 0)
        {
            warned++;
            foreach (string w in document.Warnings) Console.WriteLine($"  ! {title}: {w}");
        }

        if (block is not null)
            foreach (StatsBlockLine line in block.Lines)
            {
                if (line.Kind == StatsLineKind.Unparsed)
                {
                    unparsedLines++;
                    unparsed.Add((title, line.Text));
                }

                foreach (StatsField field in line.Fields) labels.Add((title, field.Label, field.Value));
                foreach (string flag in line.Flags) flags.Add((title, flag));
            }
    }

    Console.WriteLine();
    Console.WriteLine($"item pages checked        : {items} (of {pages.Count} fetched)");
    Console.WriteLine($"round-trip failures       : {roundTripFailures}");
    Console.WriteLine($"pages with parse warnings : {warned}");
    Console.WriteLine($"unreadable statsblock rows: {unparsedLines}");

    if (reportGrammar && unparsed.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine("--- rows the grammar could not read (grouped) ---");
        foreach (var group in unparsed.GroupBy(u => u.Line).OrderByDescending(g => g.Count()))
            Console.WriteLine($"  {group.Count(),4}  {group.Key}   e.g. {group.First().Page}");
    }

    if (reportGrammar)
    {
        // "0 unreadable rows" is not evidence the split is *correct* — a bad boundary still produces a field, just
        // one with a nonsense label. The label census is what catches that, and it is how the single-spaced
        // "STR: +10 WIS: +10" bug was found after the unparsed count had already read zero.
        Console.WriteLine();
        Console.WriteLine("--- every distinct label the grammar produced (eyeball for nonsense) ---");
        foreach (var group in labels.GroupBy(l => l.Label).OrderByDescending(g => g.Count()))
            Console.WriteLine($"  {group.Count(),5}  {group.Key,-28} e.g. [{group.First().Value}] in {group.First().Page}");

        Console.WriteLine();
        Console.WriteLine("--- every distinct flag token (same reason) ---");
        foreach (var group in flags.GroupBy(f => f.Flag).OrderByDescending(g => g.Count()))
            Console.WriteLine($"  {group.Count(),5}  {group.Key}   e.g. {group.First().Page}");
    }

    return roundTripFailures == 0 ? 0 : 1;
}

async Task<IReadOnlyList<(string, string)>> LoadPagesAsync()
{
    int cached = Array.IndexOf(args, "--cached");
    if (cached >= 0 && cached + 1 < args.Length)
    {
        var fromDisk = new List<(string, string)>();
        foreach (string file in Directory.EnumerateFiles(args[cached + 1], "*.txt"))
            fromDisk.Add((Path.GetFileNameWithoutExtension(file), await File.ReadAllTextAsync(file)));
        Console.WriteLine($"Loaded {fromDisk.Count} cached page(s) from {args[cached + 1]}");
        return fromDisk;
    }

    int count = args.Length > 1 && int.TryParse(args[1], out int n) ? n : 50;
    int seedArg = Array.IndexOf(args, "--seed");
    int seed = seedArg >= 0 && seedArg + 1 < args.Length && int.TryParse(args[seedArg + 1], out int s) ? s : 7;

    using MediaWikiClient client = MediaWikiClient.Create(endpoint);
    IReadOnlyList<string> titles = await client.ListTransclusionsAsync("Template:Itempage");
    Console.WriteLine($"{titles.Count} pages transclude {{{{Itempage}}}}; sampling {count} with seed {seed}.");

    var random = new Random(seed);
    List<string> sample = [.. titles.OrderBy(_ => random.Next()).Take(count)];

    var pages = new List<(string, string)>();
    foreach (string title in sample)
    {
        WikiPage? page = await client.FetchPageAsync(title);
        if (page is not null) pages.Add((page.Title, page.Wikitext));
    }

    // Cache them so a grammar iteration does not re-fetch; this is read-only public wikitext, but it is still
    // someone else's bandwidth.
    string cacheDirectory = Path.Combine(RepoPaths.LocalDataDirectory, "wiki-pages");
    Directory.CreateDirectory(cacheDirectory);
    foreach ((string title, string text) in pages)
        await File.WriteAllTextAsync(Path.Combine(cacheDirectory, Sanitize(title) + ".txt"), text);
    Console.WriteLine($"Cached {pages.Count} page(s) to {cacheDirectory}");

    return pages;
}

static string Sanitize(string title) =>
    string.Concat(title.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

// Runs the real analyzer over every verified capture in the corpus against that item's live wiki page, so the diff
// rules can be judged against ~100 real items instead of hand-written cases. Read-only; writes nothing.
async Task<int> AnalyzeCorpusAsync()
{
    if (!File.Exists(RepoPaths.ExpectedItemsFile))
    {
        Console.Error.WriteLine($"No ground truth at {RepoPaths.ExpectedItemsFile}.");
        return 1;
    }

    bool detail = args.Contains("--detail");
    ExpectedCorpus corpus = ExpectedCorpus.Load(RepoPaths.ExpectedItemsFile);

    // One entry per distinct item; the corpus captures several items more than once.
    var items = corpus.Samples
        .SelectMany(s => s.Windows)
        .Where(w => !w.Occluded && !string.IsNullOrWhiteSpace(w.Name) && w.Name != ExpectedCorpus.TodoMarker)
        .GroupBy(w => w.Name!, StringComparer.Ordinal)
        .Select(g => g.First())
        .OrderBy(w => w.Name, StringComparer.Ordinal)
        .ToList();

    // Eligibility (step 4b) runs before analysis in the real pipeline, and it must run here too or the numbers
    // lie: a levelled item's stats are legitimately higher than the wiki's level-0 figures, so including one
    // reports a stale page where there is none. Bladestopper +7 shows AC 43 against the page's 25 for exactly that
    // reason. Foreign exaltations are excluded for the same kind of reason — the window includes another item's
    // contribution.
    var ineligible = new List<(string Name, string Reason)>();
    var eligible = new List<ExpectedWindow>();
    foreach (ExpectedWindow window in items)
    {
        ParsedItem probe = new(
            window.Name!, window.Level, false, window.Flags, [], [], [], [],
            [.. window.Exaltations.Select(e => new ExaltationSlot(Enum.Parse<ExaltationKind>(e.Kind, true), e.Name))],
            [], null, null, []);

        ItemEligibility check = ItemEligibility.Check(probe);
        if (check.IsEligible) eligible.Add(window);
        else ineligible.Add((window.Name!, check.Blockers[0].Reason.ToString()));
    }

    Console.WriteLine($"{items.Count} distinct captured items; {eligible.Count} eligible, {ineligible.Count} skipped:");
    foreach (var group in ineligible.GroupBy(i => i.Reason))
        Console.WriteLine($"  {group.Count(),3} {group.Key}  e.g. {group.First().Name}");
    Console.WriteLine($"Analyzing {eligible.Count} eligible items against the live wiki...");
    items = eligible;

    using MediaWikiClient client = MediaWikiClient.Create(endpoint);
    var verdictCounts = new Dictionary<FieldVerdict, int>();
    var needsReview = new List<(string Item, FieldFinding Finding)>();
    var compliance = new List<(string Item, ComplianceFinding Finding)>();
    int notItemPages = 0, missing = 0, misnamed = 0, unusable = 0, clean = 0, changed = 0;

    foreach (ExpectedWindow window in items)
    {
        ItemPageLookupResult lookup = await ItemPageLookup.FindAsync(client, window.Name!);
        if (lookup.Outcome == LookupOutcome.NameUnusable) { unusable++; Console.WriteLine($"  [unusable name] {window.Name}"); continue; }
        if (lookup.Outcome == LookupOutcome.FoundMisnamedCandidate) { misnamed++; Console.WriteLine($"  [misnamed?] {window.Name} -> {lookup.Page!.Title}"); continue; }
        if (lookup.Outcome == LookupOutcome.NotFound) { missing++; continue; }

        ItemPageDocument? page = ItemPageDocument.Parse(lookup.Page!.Wikitext);
        if (page is null) { notItemPages++; continue; }

        ParsedItem captured = new(
            window.Name!, window.Level, window.TitleContentNameMismatch,
            window.Flags, window.Classes, window.Races, window.Slots,
            [.. window.Stats.Select(s => new KeyValuePair<string, string>(s.Label, s.Value))],
            [.. window.Exaltations.Select(e => new ExaltationSlot(Enum.Parse<ExaltationKind>(e.Kind, true), e.Name))],
            [.. window.Effects.Select(e => new EffectEntry(
                e.Kind, e.Name, e.Conditions,
                [.. e.Modifiers.Select(m => new KeyValuePair<string, string>(m.Label, m.Value))]))],
            window.MerchantValue, window.Lore, []);

        ItemPageAnalysis analysis = ItemPageAnalyzer.Analyze(captured, page, lookup.Page!.Title);
        foreach (ComplianceFinding c in analysis.Compliance)
            compliance.Add((window.Name!, c));
        foreach (FieldFinding finding in analysis.Findings)
        {
            verdictCounts[finding.Verdict] = verdictCounts.GetValueOrDefault(finding.Verdict) + 1;
            if (finding.Blocks) needsReview.Add((window.Name!, finding));
        }

        if (analysis.IsClean) clean++; else changed++;

        if (detail)
        {
            Console.WriteLine();
            Console.WriteLine($"--- {lookup.Page!.Title}");
            foreach (FieldFinding f in analysis.Findings.Where(f => f.Verdict != FieldVerdict.Matches))
                Console.WriteLine($"    {f.Verdict,-14} {f.Field,-22} captured=[{f.Captured}] wiki=[{f.OnWiki}]");
        }
    }

    Console.WriteLine();
    Console.WriteLine("=== lookup ===");
    Console.WriteLine($"  pages analyzed          : {clean + changed}  ({clean} already correct, {changed} would change)");
    Console.WriteLine($"  no page on the wiki     : {missing}");
    Console.WriteLine($"  page exists, not an item: {notItemPages}   (spell/effect names in the corpus)");
    Console.WriteLine($"  probably misnamed       : {misnamed}");
    Console.WriteLine($"  name unusable as a title: {unusable}");

    Console.WriteLine();
    Console.WriteLine("=== field verdicts ===");
    foreach (FieldVerdict verdict in Enum.GetValues<FieldVerdict>())
        Console.WriteLine($"  {verdict,-16} {verdictCounts.GetValueOrDefault(verdict)}");

    Console.WriteLine();
    Console.WriteLine($"=== template compliance ({compliance.Count} finding(s)) ===");
    foreach (var group in compliance.GroupBy(c => (c.Finding.Rule, c.Finding.ToolWillFix)).OrderByDescending(g => g.Count()))
        Console.WriteLine($"  {group.Count(),4}  {group.Key.Rule,-24} " +
                          $"{(group.Key.ToolWillFix ? "tool fixes" : "needs a human"),-14} e.g. {group.First().Item}");

    Console.WriteLine();
    Console.WriteLine($"=== needs review ({needsReview.Count}) ===");
    foreach (var group in needsReview.GroupBy(r => r.Finding.Field).OrderByDescending(g => g.Count()))
        Console.WriteLine($"  {group.Count(),4}  {group.Key,-24} e.g. {group.First().Item}");

    return 0;
}

// Reads each item icon out of a real screenshot and compares it against the icon the item's wiki page points at.
// This is the only way to know whether the perceptual hash actually survives the two renderings being different
// sizes — the wiki stores 40x40 PNGs and the game draws the same sprite about 1.2x larger.
async Task<int> CompareIconsAsync()
{
    if (args.Length < 2) { Console.Error.WriteLine("usage: WikiSpike icons <screenshot>"); return 1; }

    CapturedImage image = await ImageFile.LoadAsync(args[1]);
    using var rapid = new RapidOcrEngine();
    IOcrEngine ocr = new RoutingOcrEngine(fullFrame: rapid, windowCrop: new GlyphOcrEngine());
    IReadOnlyList<LocatedWindow> windows = await ItemWindowLocator.LocateAsync(image, ocr);

    using MediaWikiClient client = MediaWikiClient.Create(endpoint);
    using var http = new HttpClient();
    http.DefaultRequestHeaders.Add("User-Agent", MediaWikiClient.UserAgent);
    var cache = new IconCache(
        Path.Combine(RepoPaths.LocalDataDirectory, "icon-cache"), new WikiIconSource(http, endpoint));

    string scratch = Path.Combine(RepoPaths.LocalDataDirectory, "icon-scratch");
    Directory.CreateDirectory(scratch);

    var fingerprints = new List<(string Name, IconFingerprint Captured, string IconId)>();
    var wikiIcons = new List<(string Name, IconFingerprint OnWiki, string IconId)>();

    Console.WriteLine($"{windows.Count} window(s) in {Path.GetFileName(args[1])}");
    foreach (LocatedWindow window in windows)
    {
        if (!ItemIconReader.TryRead(image, window, out IconFingerprint captured))
        {
            Console.WriteLine("  (no readable icon — occluded, a Lore capture, or too little ink)");
            continue;
        }

        ParsedItem item = ItemParser.Parse(window.Lines, window.ActiveTab);
        ItemPageLookupResult lookup = await ItemPageLookup.FindAsync(client, item.Name);
        if (lookup.Outcome != LookupOutcome.Found)
        {
            Console.WriteLine($"  {item.Name,-34} captured icon {captured.InkWidth}x{captured.InkHeight}, no page");
            continue;
        }

        ItemPageDocument? page = ItemPageDocument.Parse(lookup.Page!.Wikitext);
        if (page?.IconId is not { Length: > 0 } iconId)
        {
            Console.WriteLine($"  {item.Name,-34} page has no lucy_img_ID");
            continue;
        }

        byte[]? bytes = await cache.GetAsync(iconId);
        if (bytes is null)
        {
            Console.WriteLine($"  {item.Name,-34} wiki has no File:item_{iconId}.png");
            continue;
        }

        // Written once per icon id. Several items routinely share one (12d has three breastplates on id 624), and
        // the decoder keeps the file mapped, so rewriting it mid-run fails outright. Production will decode from
        // the bytes directly and never touch a file — see the note in CLAUDE.md about needing a decoder port.
        string file = Path.Combine(scratch, $"item_{new string([.. iconId.Where(char.IsLetterOrDigit)])}.png");
        if (!File.Exists(file)) await File.WriteAllBytesAsync(file, bytes);
        // Composited over the game's own background grey, so both sides are the same sprite on the same backdrop.
        CapturedImage wikiIcon = await ImageFile.LoadOverBackgroundAsync(file, background: 16);

        if (!IconHasher.TryFingerprint(wikiIcon, new Rect(0, 0, wikiIcon.Width, wikiIcon.Height), out IconFingerprint onWiki))
        {
            Console.WriteLine($"  {item.Name,-34} the wiki's icon {iconId} has no readable ink");
            continue;
        }

        fingerprints.Add((item.Name, captured, iconId));
        wikiIcons.Add((item.Name, onWiki, iconId));

        double distance = captured.DistanceTo(onWiki);
        Console.WriteLine(
            $"  {item.Name,-34} id {iconId,-5} captured {captured.InkWidth}x{captured.InkHeight} " +
            $"vs wiki {onWiki.InkWidth}x{onWiki.InkHeight}  distance {distance,6:F3} corr {captured.CorrelationDistanceTo(onWiki),6:F3} contrast {captured.Contrast,5:F3}/{onWiki.Contrast,5:F3}  " +
            $"{(captured.LooksLike(onWiki) ? "match" : "MISMATCH")}");
    }

    // The negative control, and the only thing that makes the "match" results above mean anything: a check that
    // says yes to everything is worthless. Every captured icon is also compared against the *other* items' wiki
    // icons, which should land far outside the threshold.
    if (fingerprints.Count > 1)
    {
        Console.WriteLine("\n--- control: each capture against the other items' wiki icons ---");
        foreach ((string name, IconFingerprint captured, string iconId) in fingerprints)
            foreach ((string otherName, IconFingerprint onWiki, string otherId) in wikiIcons)
            {
                if (otherId == iconId) continue;
                double distance = captured.DistanceTo(onWiki);
                Console.WriteLine(
                    $"  {name,-30} vs {otherName}'s icon {otherId,-5} distance {distance,6:F3} corr {captured.CorrelationDistanceTo(onWiki),6:F3} contrast {captured.Contrast,5:F3}/{onWiki.Contrast,5:F3}  " +
                    $"{(captured.LooksLike(onWiki) ? "FALSE MATCH" : "correctly rejected")}");
            }
    }

    Console.WriteLine($"\nicon downloads this run: {cache.Downloads}");
    return 0;
}

// Tests whether the game renders icons losslessly from the same asset the wiki files came from. If it does, the
// comparison can be an exact pixel diff instead of a perceptual one, and every alert would be a real defect.
// The discriminating question is whether the game *interpolates* when it scales: nearest-neighbour reuses the
// source colours exactly, while any smoothing invents new ones.
async Task<int> DiffIconPixelsAsync()
{
    if (args.Length < 2) { Console.Error.WriteLine("usage: WikiSpike icondiff <screenshot>"); return 1; }

    CapturedImage image = await ImageFile.LoadAsync(args[1]);
    using var rapid = new RapidOcrEngine();
    IOcrEngine ocr = new RoutingOcrEngine(fullFrame: rapid, windowCrop: new GlyphOcrEngine());
    IReadOnlyList<LocatedWindow> windows = await ItemWindowLocator.LocateAsync(image, ocr);

    using MediaWikiClient client = MediaWikiClient.Create(endpoint);
    using var http = new HttpClient();
    http.DefaultRequestHeaders.Add("User-Agent", MediaWikiClient.UserAgent);
    var cache = new IconCache(Path.Combine(RepoPaths.LocalDataDirectory, "icon-cache"), new WikiIconSource(http, endpoint));
    string scratch = Path.Combine(RepoPaths.LocalDataDirectory, "icon-scratch");
    Directory.CreateDirectory(scratch);

    foreach (LocatedWindow window in windows)
    {
        if (window.PossiblyOccluded || window.ActiveTab != ItemWindowTab.Description) continue;

        ParsedItem item = ItemParser.Parse(window.Lines, window.ActiveTab);
        ItemPageLookupResult lookup = await ItemPageLookup.FindAsync(client, item.Name);
        if (lookup.Outcome != LookupOutcome.Found) continue;
        if (ItemPageDocument.Parse(lookup.Page!.Wikitext)?.IconId is not { Length: > 0 } iconId) continue;
        if (await cache.GetAsync(iconId) is not { } bytes) continue;

        string file = Path.Combine(scratch, $"item_{new string([.. iconId.Where(char.IsLetterOrDigit)])}.png");
        if (!File.Exists(file)) await File.WriteAllBytesAsync(file, bytes);
        CapturedImage wiki = await ImageFile.LoadOverBackgroundAsync(file, background: 16);

        // Ink boxes on both sides, using the same floor, so the two are measured identically.
        Rect captured = InkBox(image, new Rect(
            window.Bounds.X + ItemIconReader.IconStrip.X, window.Bounds.Y + ItemIconReader.IconStrip.Y,
            ItemIconReader.IconStrip.Width, ItemIconReader.IconStrip.Height));
        Rect onWiki = InkBox(wiki, new Rect(0, 0, wiki.Width, wiki.Height));
        if (captured.Width == 0 || onWiki.Width == 0) continue;

        var wikiColours = new HashSet<int>();
        for (int y = onWiki.Y; y < onWiki.Y + onWiki.Height; y++)
            for (int x = onWiki.X; x < onWiki.X + onWiki.Width; x++)
                wikiColours.Add(ColourAt(wiki, x, y));

        var capturedColours = new HashSet<int>();
        for (int y = captured.Y; y < captured.Y + captured.Height; y++)
            for (int x = captured.X; x < captured.X + captured.Width; x++)
                capturedColours.Add(ColourAt(image, x, y));

        int shared = capturedColours.Count(c => wikiColours.Contains(c));

        Console.WriteLine($"--- {item.Name} (icon {iconId})");
        Console.WriteLine($"    captured ink {captured.Width}x{captured.Height} at window-rel " +
                          $"({captured.X - window.Bounds.X},{captured.Y - window.Bounds.Y})   " +
                          $"wiki ink {onWiki.Width}x{onWiki.Height} at ({onWiki.X},{onWiki.Y})");
        Console.WriteLine($"    scale x {(double)captured.Width / onWiki.Width:F3}  y {(double)captured.Height / onWiki.Height:F3}");
        Console.WriteLine($"    distinct colours: captured {capturedColours.Count}, wiki {wikiColours.Count}, " +
                          $"captured-also-in-wiki {shared} ({100.0 * shared / capturedColours.Count:F0}%)");
    }

    return 0;

    static int ColourAt(CapturedImage img, int x, int y)
    {
        int o = (y * img.Width + x) * 4;
        return (img.Pixels[o + 2] << 16) | (img.Pixels[o + 1] << 8) | img.Pixels[o];
    }

    static Rect InkBox(CapturedImage img, Rect region)
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (int y = Math.Max(0, region.Y); y < Math.Min(region.Y + region.Height, img.Height); y++)
            for (int x = Math.Max(0, region.X); x < Math.Min(region.X + region.Width, img.Width); x++)
            {
                int o = (y * img.Width + x) * 4;
                if (Math.Max(img.Pixels[o + 2], Math.Max(img.Pixels[o + 1], img.Pixels[o])) <= IconHasher.InkFloor) continue;
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            }

        return maxX < 0 ? new Rect(0, 0, 0, 0) : new Rect(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }
}

// The whole pipeline end to end against a real screenshot: locate, parse, check eligibility, find the page,
// analyze, and build the edit — printing the proposed wikitext as a line diff. This is the closest thing to what
// the review UI will show, and the point of having it before the UI exists is that the edit can be judged on real
// pages without one.
async Task<int> PreviewEditsAsync()
{
    if (args.Length < 2) { Console.Error.WriteLine("usage: WikiSpike preview <screenshot>"); return 1; }

    CapturedImage image = await ImageFile.LoadAsync(args[1]);
    using var rapid = new RapidOcrEngine();
    IOcrEngine ocr = new RoutingOcrEngine(fullFrame: rapid, windowCrop: new GlyphOcrEngine());
    IReadOnlyList<LocatedWindow> windows = await ItemWindowLocator.LocateAsync(image, ocr);
    using MediaWikiClient client = MediaWikiClient.Create(endpoint);

    foreach (LocatedWindow window in windows)
    {
        if (window.PossiblyOccluded) { Console.WriteLine("--- (occluded window, skipped)"); continue; }

        ParsedItem item = ItemParser.Parse(window.Lines, window.ActiveTab);
        Console.WriteLine();
        Console.WriteLine($"=== {item.Name}");

        ItemEligibility eligibility = ItemEligibility.Check(item);
        if (!eligibility.IsEligible)
        {
            // No ledger row is written for these either — see ItemEligibility.
            foreach (IneligibilityDetail blocker in eligibility.Blockers)
                Console.WriteLine($"    ineligible: {blocker.Explanation}");
            continue;
        }

        ItemPageLookupResult lookup = await ItemPageLookup.FindAsync(client, item.Name);
        if (lookup.Outcome != LookupOutcome.Found)
        {
            Console.WriteLine($"    {lookup.Outcome}{(lookup.Warning is null ? "" : ": " + lookup.Warning)}");
            continue;
        }

        ItemPageDocument? page = ItemPageDocument.Parse(lookup.Page!.Wikitext);
        if (page is null) { Console.WriteLine("    the page is not an item page"); continue; }

        ItemPageAnalysis analysis = ItemPageAnalyzer.Analyze(item, page, lookup.Page!.Title);
        ProposedEdit edit = ItemPageEditor.BuildEdit(page, analysis);

        if (!edit.HasChanges) { Console.WriteLine("    already correct — no edit"); }
        else
        {
            Console.WriteLine($"    summary: {edit.Summary}");
            Console.WriteLine($"    needs reformatting afterwards: {edit.NeedsReformatting}");
            PrintLineDiff(edit.OriginalWikitext, edit.NewWikitext);
        }

        foreach (string deferred in edit.Deferred) Console.WriteLine($"    ! {deferred}");
        foreach (FieldFinding finding in analysis.Findings.Where(f => f.Blocks))
            Console.WriteLine($"    ? {finding.Field}: {finding.Explanation ?? "needs review"}");
    }

    return 0;

    // A crude line diff — enough to eyeball whether the edit is surgical, which is the whole question.
    static void PrintLineDiff(string before, string after)
    {
        string[] a = before.Replace("\r\n", "\n").Split('\n');
        string[] b = after.Replace("\r\n", "\n").Split('\n');
        var removed = new List<string>(a);
        var added = new List<string>(b);

        foreach (string line in a.Intersect(b).ToList())
        {
            removed.RemoveAll(l => l == line);
            added.RemoveAll(l => l == line);
        }

        foreach (string line in removed) Console.WriteLine($"      - {line}");
        foreach (string line in added) Console.WriteLine($"      + {line}");
    }
}

int Login()
{
    // Read interactively so the secret never reaches a file, a command line (which lands in shell history and in
    // the process list) or this repo. It goes straight from the console into Windows Credential Manager.
    Console.WriteLine("Create a bot password at https://eqlwiki.com/Special:BotPasswords");
    Console.WriteLine("Grant it at least the \"Edit existing pages\" right.");
    Console.Write("Bot username (the full User@BotName form): ");
    string? user = Console.ReadLine();
    if (string.IsNullOrWhiteSpace(user)) { Console.Error.WriteLine("No username given."); return 1; }

    Console.Write("Bot password (not echoed): ");
    string password = ReadHidden();
    if (password.Length == 0) { Console.Error.WriteLine("No password given."); return 1; }

    new WindowsCredentialStore().Write(new BotCredentials(user.Trim(), password));
    Console.WriteLine($"Stored under '{WindowsCredentialStore.DefaultTargetName}' in Windows Credential Manager.");
    Console.WriteLine("Verify with: WikiSpike whoami");
    return 0;
}

static string ReadHidden()
{
    var chars = new List<char>();
    while (true)
    {
        ConsoleKeyInfo key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); return new string([.. chars]); }
        if (key.Key == ConsoleKey.Backspace) { if (chars.Count > 0) chars.RemoveAt(chars.Count - 1); continue; }
        if (!char.IsControl(key.KeyChar)) chars.Add(key.KeyChar);
    }
}

int Logout()
{
    bool removed = new WindowsCredentialStore().Delete();
    Console.WriteLine(removed ? "Credential deleted." : "There was no stored credential.");
    return 0;
}

async Task<int> WhoAmIAsync()
{
    BotCredentials? credentials = new WindowsCredentialStore().Read();
    if (credentials is null)
    {
        Console.Error.WriteLine("No stored credential. Run: WikiSpike login");
        return 1;
    }

    Console.WriteLine($"Stored credential for {credentials.UserName} (account {credentials.AccountName}).");

    using MediaWikiClient client = MediaWikiClient.Create(endpoint);
    try
    {
        await client.LoginAsync(credentials);
        Console.WriteLine("Login succeeded.");
    }
    catch (MediaWikiException ex)
    {
        Console.Error.WriteLine($"Login failed [{ex.Code}]: {ex.Message}");
        return 1;
    }

    // Ask the wiki what this bot password may do, rather than finding out by attempting an edit. A failed edit
    // would still be a permanent revision in some page's history, and on this wiki the user cannot delete one.
    UserInfo info = await client.GetUserInfoAsync();
    Console.WriteLine($"The wiki sees: {info.Name}{(info.IsAnonymous ? " (ANONYMOUS — the login did not stick)" : "")}");
    Console.WriteLine($"  edit existing pages : {(info.CanEdit ? "yes" : "NO — re-create the bot password with that grant")}");
    Console.WriteLine($"  create new pages    : {(info.CanCreate ? "yes" : "no")} (v1 never creates a page, so this is optional)");

    if (info.IsAnonymous || !info.CanEdit) return 1;

    Console.WriteLine();
    Console.WriteLine("Credential verified. Nothing was written — this check is entirely read-only.");
    return 0;
}

async Task<int> EditAsync()
{
    if (args.Length < 2) { Usage(); return 1; }
    string title = args[1];

    // This writes to a live public wiki, and on eqlwiki.com an ordinary editor cannot delete a page or a revision
    // — so anything this does is permanent. It therefore only touches the user's own userspace, where two extra
    // history entries on a personal scratch page are unremarkable and need nobody's help to live with. The real
    // pipeline edits real articles, but only behind the review step; a spike tool has no such gate and a mistyped
    // title here would land in somebody's article history for good.
    if (!title.StartsWith("User:", StringComparison.OrdinalIgnoreCase) && !args.Contains("--yes-really"))
    {
        Console.Error.WriteLine($"'{title}' is not in your userspace. Use a User:<you>/<something> page.");
        Console.Error.WriteLine("Nothing here can be deleted afterwards, so --yes-really is needed to go elsewhere.");
        return 1;
    }

    BotCredentials? credentials = new WindowsCredentialStore().Read();
    if (credentials is null) { Console.Error.WriteLine("No stored credential. Run: WikiSpike login"); return 1; }

    using MediaWikiClient client = MediaWikiClient.Create(endpoint);
    await client.LoginAsync(credentials);

    WikiPage? page = await client.FetchPageAsync(title);
    if (page is null)
    {
        // nocreate=1 is set on every edit, so this could not create the page even if it wanted to. Creating one is
        // a separate decision with the same no-take-backs property, and belongs to the user, not to a spike tool.
        Console.Error.WriteLine($"'{title}' does not exist. Create it in the browser first — this tool never creates pages.");
        return 1;
    }

    string marker = $"EQLWikiAssistant write-path test {DateTimeOffset.UtcNow:u}";

    Console.WriteLine($"Fetched '{page.Title}' revision {page.RevisionId} ({page.Wikitext.Length} bytes).");
    Console.WriteLine();
    Console.WriteLine("This will make exactly two revisions to that page:");
    Console.WriteLine($"  1. append a line reading \"{marker}\"");
    Console.WriteLine("  2. restore the current text byte for byte");
    Console.WriteLine("The page ends up exactly as it is now. The two history entries are permanent.");
    Console.WriteLine();
    Console.Write("Type 'yes' to proceed: ");
    if (!string.Equals(Console.ReadLine()?.Trim(), "yes", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine("Nothing was written.");
        return 1;
    }

    string edited = page.Wikitext.TrimEnd() + "\n\n" + marker + "\n";
    EditResult result = await client.EditAsync(page.Title, edited, "Testing the editor assistant's write path", page.Timestamp);
    Console.WriteLine($"Edit accepted: revision {result.NewRevisionId} (nochange={result.NoChange}).");

    // Restore the original text. If this fails the marker line stays, which is why it says what it is and why the
    // failure is reported loudly rather than swallowed.
    WikiPage? after = await client.FetchPageAsync(page.Title);
    if (after is null) { Console.Error.WriteLine("The page vanished after editing; NOT reverted."); return 1; }

    try
    {
        EditResult revert = await client.EditAsync(
            after.Title, page.Wikitext, "Reverting the editor assistant's write-path test", after.Timestamp);
        Console.WriteLine($"Reverted: revision {revert.NewRevisionId}. The page's text is back to what it was.");
    }
    catch (MediaWikiException ex)
    {
        Console.Error.WriteLine($"REVERT FAILED [{ex.Code}]: {ex.Message}");
        Console.Error.WriteLine($"The page still ends with the line: {marker}");
        Console.Error.WriteLine("Remove it by hand, or re-run this command to try again.");
        return 1;
    }

    Console.WriteLine("Write path verified end to end: token, conflict guard, session assertion and revert.");
    return 0;
}

// Runs the formatting pass over real item pages and reports what it did — the same methodology as `grammar` and
// `analyze`, and for the same reason: a rule that rearranges a public wiki's pages has to be judged against the
// pages it will actually meet, not against hand-written examples. Writes nothing.
async Task<int> PrettifyCorpusAsync()
{
    IReadOnlyList<(string Title, string Wikitext)> pages = await LoadPagesAsync();

    int changed = 0, unchanged = 0, refused = 0, keptOrder = 0, notApplicable = 0;
    var refusalReasons = new List<(string Page, string Reason)>();
    var orderReasons = new List<(string Page, string Reason)>();
    var unknownLabels = new List<(string Page, string Note)>();
    var growth = new List<(string Page, int Delta)>();

    foreach ((string title, string wikitext) in pages)
    {
        PrettifyResult result = ItemPagePrettifier.Format(wikitext);

        if (!result.IsSafe)
        {
            // Not an item page is "not applicable", not a refusal: the cache holds monster and zone pages too.
            if (result.Refusals.Any(r => r.Contains("not an item page"))) { notApplicable++; continue; }
            refused++;
            foreach (string reason in result.Refusals) refusalReasons.Add((title, reason));
            continue;
        }

        if (result.Changed) changed++; else unchanged++;

        foreach (string note in result.Notes)
        {
            if (note.StartsWith("The statsblock was left", StringComparison.Ordinal))
            {
                keptOrder++;
                orderReasons.Add((title, note["The statsblock was left exactly as it was: ".Length..]));
            }
            else if (note.StartsWith("The blueprint has no place", StringComparison.Ordinal))
            {
                unknownLabels.Add((title, note));
            }
        }

        growth.Add((title, result.Formatted.Length - wikitext.Length));
    }

    Console.WriteLine();
    Console.WriteLine($"=== formatting {pages.Count} page(s) ===");
    Console.WriteLine($"  not item pages          : {notApplicable}");
    Console.WriteLine($"  reformatted             : {changed}");
    Console.WriteLine($"  already correct         : {unchanged}");
    Console.WriteLine($"  REFUSED (content moved) : {refused}");
    Console.WriteLine($"  statsblock left alone   : {keptOrder}");

    if (refusalReasons.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine("--- refusals: the verification pass found the result saying something different ---");
        foreach (var group in refusalReasons.GroupBy(r => Generalize(r.Reason)).OrderByDescending(g => g.Count()))
            Console.WriteLine($"  {group.Count(),5}  {group.Key}\n           e.g. {group.First().Page}: {group.First().Reason}");
    }

    if (orderReasons.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine("--- statsblocks left exactly as they were ---");
        foreach (var group in orderReasons.GroupBy(r => Generalize(r.Reason)).OrderByDescending(g => g.Count()))
            Console.WriteLine($"  {group.Count(),5}  {group.Key}   e.g. {group.First().Page}");
    }

    if (unknownLabels.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine("--- labels the blueprint has no place for ---");
        foreach (var group in unknownLabels.GroupBy(u => u.Note).OrderByDescending(g => g.Count()))
            Console.WriteLine($"  {group.Count(),5}  {group.Key}   e.g. {group.First().Page}");
    }

    if (growth.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine("--- size change (a formatter should not be inventing or eating content) ---");
        Console.WriteLine($"  median {growth.OrderBy(g => g.Delta).ElementAt(growth.Count / 2).Delta:+#;-#;0} bytes, " +
                          $"largest shrink {growth.Min(g => g.Delta)}, largest growth {growth.Max(g => g.Delta)}");
        foreach ((string page, int delta) in growth.OrderBy(g => g.Delta).Take(3))
            Console.WriteLine($"    {delta,6}  {page}");
    }

    return refused == 0 ? 0 : 1;

    // Groups reasons that differ only by the page's own values, so the census shows classes rather than instances.
    static string Generalize(string reason)
    {
        int quote = reason.IndexOf('\'');
        if (quote < 0) return reason;
        int close = reason.IndexOf('\'', quote + 1);
        return close < 0 ? reason : reason[..quote] + "'...'" + reason[(close + 1)..];
    }
}

// Writes one page's formatted text to a file so it can be diffed against the cached original by eye. A census
// tells you nothing about whether the layout is any good.
int ShowPrettified()
{
    if (args.Length < 3) { Console.Error.WriteLine("usage: WikiSpike prettyshow <cached file> <out file>"); return 1; }
    PrettifyResult result = ItemPagePrettifier.Format(File.ReadAllText(args[1]));
    File.WriteAllText(args[2], result.Formatted);
    foreach (string note in result.Notes) Console.WriteLine($"note: {note}");
    foreach (string refusal in result.Refusals) Console.WriteLine($"REFUSED: {refusal}");
    return 0;
}

// Measures the icon comparison across every sample, with the contrast gate *off*, printing one row per judged pair
// so a threshold can be chosen from data rather than from a single example. Same-item pairs should score low and
// control pairs high; the gate and the match threshold are then whatever separates them with zero false matches.
async Task<int> MeasureIconsAcrossCorpusAsync()
{
    using var rapid = new RapidOcrEngine();
    IOcrEngine ocr = new RoutingOcrEngine(fullFrame: rapid, windowCrop: new GlyphOcrEngine());
    using MediaWikiClient client = MediaWikiClient.Create(endpoint);
    using var http = new HttpClient();
    http.DefaultRequestHeaders.Add("User-Agent", MediaWikiClient.UserAgent);
    var cache = new IconCache(
        Path.Combine(RepoPaths.LocalDataDirectory, "icon-cache"), new WikiIconSource(http, endpoint));
    var decoder = new WindowsImageDecoder();

    // One entry per distinct item: the corpus captures several of them more than once.
    var captured = new Dictionary<string, (IconFingerprint Icon, string IconId)>(StringComparer.Ordinal);
    var wikiIcons = new Dictionary<string, IconFingerprint>(StringComparer.Ordinal);

    foreach (string file in Directory.EnumerateFiles(RepoPaths.SamplesDirectory, "*.png").OrderBy(f => f))
    {
        CapturedImage image = await ImageFile.LoadAsync(file);
        foreach (LocatedWindow window in await ItemWindowLocator.LocateAsync(image, ocr))
        {
            if (window.PossiblyOccluded || window.ActiveTab != ItemWindowTab.Description) continue;

            // Deliberately *not* ItemIconReader.TryRead: that applies the contrast gate this run exists to measure.
            var region = new Rect(
                window.Bounds.X + ItemIconReader.IconStrip.X, window.Bounds.Y + ItemIconReader.IconStrip.Y,
                ItemIconReader.IconStrip.Width, ItemIconReader.IconStrip.Height);
            if (!IconHasher.TryFingerprint(image, region, out IconFingerprint icon)) continue;

            ParsedItem item = ItemParser.Parse(window.Lines, window.ActiveTab);
            if (captured.ContainsKey(item.Name)) continue;

            ItemPageLookupResult lookup = await ItemPageLookup.FindAsync(client, item.Name);
            if (lookup.Outcome != LookupOutcome.Found) continue;
            if (ItemPageDocument.Parse(lookup.Page!.Wikitext)?.IconId is not { Length: > 0 } iconId) continue;
            if (await cache.GetAsync(iconId) is not { } bytes) continue;

            CapturedImage wikiIcon = await decoder.DecodeAsync(bytes);
            if (!IconHasher.TryFingerprint(wikiIcon, new Rect(0, 0, wikiIcon.Width, wikiIcon.Height), out IconFingerprint onWiki))
                continue;

            captured[item.Name] = (icon, iconId);
            wikiIcons[iconId] = onWiki;
        }
    }

    Console.WriteLine();
    Console.WriteLine("pair\tkind\tcapturedContrast\twikiContrast\tcorrelation");
    foreach ((string name, (IconFingerprint icon, string iconId)) in captured)
        foreach ((string otherId, IconFingerprint other) in wikiIcons)
            Console.WriteLine(
                $"{name} vs {otherId}\t{(otherId == iconId ? "same" : "control")}\t" +
                $"{icon.Contrast:F4}\t{other.Contrast:F4}\t{icon.CorrelationDistanceTo(other):F4}");

    Console.WriteLine();
    Console.WriteLine($"{captured.Count} item(s) with both icons readable; {wikiIcons.Count} distinct wiki icon(s).");
    return 0;
}

/// <summary>
/// The wiki's "Verified for EQLegends" list, measured against the corpus.
///
/// Exists for the same reason `analyze` and `prettify` do: a rule about real pages is worth a number before it is
/// worth code. It is what established that only 48 of 744 cached item pages are verified — the measurement that
/// settled verification as a warning rather than a blocker.
/// </summary>
async Task<int> VerifiedAsync()
{
    using MediaWikiClient client = MediaWikiClient.Create(endpoint);
    var verified = new VerifiedPages(client, Path.Combine(Path.GetTempPath(), "wikispike-verified.json"));
    await verified.RefreshAsync();

    if (!verified.IsKnown) { Console.Error.WriteLine("The verified-pages list could not be read."); return 1; }
    Console.WriteLine($"{VerifiedPages.ListPageTitle}: {verified.Count} title(s), revision {verified.RevisionId}");

    if (args.Length > 1)
    {
        foreach (string title in args.Skip(1))
            Console.WriteLine($"  {(verified.IsVerified(title) == true ? "VERIFIED  " : "unverified")}  {title}");
        return 0;
    }

    string cached = Path.Combine(RepoPaths.LocalDataDirectory, "wiki-pages");
    if (!Directory.Exists(cached)) return 0;

    int items = 0, done = 0;
    foreach (string file in Directory.EnumerateFiles(cached, "*.txt"))
    {
        if (!File.ReadAllText(file).Contains("{{Itempage", StringComparison.Ordinal)) continue;
        items++;
        if (verified.IsVerified(Path.GetFileNameWithoutExtension(file)) == true) done++;
    }

    Console.WriteLine($"cached item pages: {items}, verified {done} ({(items == 0 ? 0 : 100.0 * done / items):F1}%)");
    return 0;
}
