using EQLWikiAssistant.Core.Locate;
using EQLWikiAssistant.Core.Ocr;
using EQLWikiAssistant.Ocr;
using EQLWikiAssistant.TestSupport;

// Milestone 2 spike tool: run ItemWindowLocator (whole-screenshot OCR to find Description tabs, pixel border
// tracing to find each window's real bounds, occluded windows reported with no data) against a real
// screenshot. --save draws a rectangle around each found window (green = clean, red = occluded) for visual
// verification.
//
// Usage: LocateSpike <imagePath> [--save pngPath]

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: LocateSpike <imagePath> [--save pngPath]");
    return 1;
}

string imagePath = args[0];
string? savePath = null;
for (int i = 1; i < args.Length; i++)
{
    if (args[i] == "--save") savePath = args[++i];
}

CapturedImage image = await ImageFile.LoadAsync(imagePath);
Console.WriteLine($"Loaded {imagePath} ({image.Width}x{image.Height})");

using var engine = new RapidOcrEngine();
var sw = System.Diagnostics.Stopwatch.StartNew();
IReadOnlyList<LocatedWindow> windows = await ItemWindowLocator.LocateAsync(image, engine);
Console.WriteLine($"Located {windows.Count} window(s) in {sw.ElapsedMilliseconds}ms:");

for (int i = 0; i < windows.Count; i++)
{
    LocatedWindow w = windows[i];
    Console.WriteLine();
    Console.WriteLine($"--- Window {i}: {w.Bounds}, {w.Lines.Count} line(s), " +
        $"HasLoreTab={w.HasLoreTab}, PossiblyOccluded={w.PossiblyOccluded} ---");
    foreach (var line in w.Lines)
        Console.WriteLine($"  [{line.BoundingBox.X,4},{line.BoundingBox.Y,4}] {line.Text}");

    if (savePath is not null)
        DebugDraw.DrawRectOutline(image, w.Bounds, r: w.PossiblyOccluded ? (byte)255 : (byte)0, g: w.PossiblyOccluded ? (byte)0 : (byte)255, b: 0);
}

if (savePath is not null)
{
    await ImageFile.SavePngAsync(image, savePath);
    Console.WriteLine($"\nSaved to {savePath}");
}

return 0;
