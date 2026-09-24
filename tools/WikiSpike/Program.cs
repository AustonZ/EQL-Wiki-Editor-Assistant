using EQLWikiAssistant.TestSupport;
using EQLWikiAssistant.Wiki.MediaWiki;
using EQLWikiAssistant.Wiki.Wikitext;

// Milestone 3 spike tool: exercise the wiki client and the wikitext layer against the real eqlwiki.com, the same
// way OcrSpike/LocateSpike/ParseSpike exercise the capture side against real screenshots. Keep using this rather
// than recreating ad hoc versions when tuning the statsblock grammar or debugging the API client.
//
// Usage:
//   WikiSpike fetch <page title>              print a page's wikitext and its parsed v1 fields
//   WikiSpike roundtrip <count> [--seed N]    fetch N random item pages and verify byte-for-byte round-trip
//   WikiSpike roundtrip --cached <dir>        same, against a directory of previously saved .txt pages
//   WikiSpike grammar <count> [--seed N]      report which statsblock lines the grammar cannot read
//   WikiSpike login                           prompt for a bot password and store it in Credential Manager
//   WikiSpike whoami                          confirm the credential logs in and may edit (writes nothing)
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
