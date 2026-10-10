using EQLWikiEditorAssistant.Core.Glyphs;
using EQLWikiEditorAssistant.Core.Items;
using EQLWikiEditorAssistant.Core.Locate;
using EQLWikiEditorAssistant.Core.Imaging;
using EQLWikiEditorAssistant.TestSupport;

// Milestone 2 spike tool: run the full Locate -> Parse pipeline against a real screenshot and dump each item's
// parsed fields, for eyeballing against the real window during parser tuning. Mirrors LocateSpike —
// keep using this rather than recreating ad hoc versions.
//
// Usage: ParseSpike <imagePath>

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: ParseSpike <imagePath>");
    return 1;
}

CapturedImage image = await ImageFile.LoadAsync(args[0]);
Console.WriteLine($"Loaded {args[0]} ({image.Width}x{image.Height})");

UiFont font = SampleFonts.For(args[0], args);
Console.WriteLine($"Reading as {UiFonts.DisplayName(font)} (--font to override)");

IReadOnlyList<LocatedWindow> windows = ItemWindowLocator.Locate(image, new GlyphTextReader(font));
Console.WriteLine($"Located {windows.Count} window(s):");

foreach (LocatedWindow window in windows)
{
    Console.WriteLine();
    Console.WriteLine($"--- {window.Bounds} PossiblyOccluded={window.PossiblyOccluded} ---");
    if (window.PossiblyOccluded)
    {
        Console.WriteLine("  (occluded, not parsed)");
        continue;
    }

    ParsedItem item = ItemParser.Parse(window.Lines, window.ActiveTab);
    Console.WriteLine($"  Name: {item.Name}  Level: +{item.Level}  Tab: {window.ActiveTab}  TitleContentNameMismatch: {item.TitleContentNameMismatch}");
    if (item.Lore is not null) Console.WriteLine($"  Lore: {item.Lore}");
    Console.WriteLine($"  Flags: {string.Join(", ", item.Flags)}");
    Console.WriteLine($"  Classes: {string.Join(" ", item.Classes)}");
    Console.WriteLine($"  Races: {string.Join(" ", item.Races)}");
    Console.WriteLine($"  Slots: {(item.Slots.Count == 0 ? "(none)" : string.Join(" ", item.Slots))}");
    Console.WriteLine("  Stats:");
    foreach (var kv in item.Stats)
        Console.WriteLine($"    {kv.Key}: {kv.Value}");
    Console.WriteLine("  Exaltations:");
    foreach (var ex in item.ExaltationSlots)
        Console.WriteLine($"    {ex.Kind}: {ex.Name ?? "empty"} {(ex.Name is not null && ItemParser.IsForeignExaltation(ex, item.Name) ? "[FOREIGN]" : "")}");
    Console.WriteLine("  Effects:");
    foreach (var fx in item.Effects)
    {
        string conditions = fx.Conditions.Count == 0 ? "" : $"  conditions: {string.Join(", ", fx.Conditions)}";
        Console.WriteLine($"    {fx.Kind} Effect: {fx.Name}{conditions}");
        foreach (var mod in fx.Modifiers)
            Console.WriteLine($"      {mod.Key}: {mod.Value}");
    }
    Console.WriteLine($"  MerchantValue: {item.MerchantValue ?? "(none)"}");
    if (item.Warnings.Count > 0)
    {
        Console.WriteLine("  Warnings:");
        foreach (string w in item.Warnings)
            Console.WriteLine($"    ! {w}");
    }
}

return 0;
