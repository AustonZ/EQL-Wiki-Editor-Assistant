using EQLWikiAssistant.Core.Locate;
using EQLWikiAssistant.Core.Ocr;
using EQLWikiAssistant.Ocr;
using EQLWikiAssistant.TestSupport;

// Milestone 2 spike tool: run ItemWindowLocator (whole-screenshot OCR to find Description tabs, pixel border
// tracing to find each window's real bounds, occluded windows reported with no data) against a real
// screenshot. --save draws a rectangle around each found window (green = clean, red = occluded) for visual
// verification.
//
// --probe dumps raw pixel RGB along a ray instead of locating, which is how the window-chrome colour profile
// (interior / content-outline line / outer frame / world background) was actually measured rather than guessed
// at — keep using it before changing any threshold in WindowBoundsFinder.
//
// Usage: LocateSpike <imagePath> [--save pngPath] [--probe x,y,dx,dy,count]

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: LocateSpike <imagePath> [--save pngPath] [--probe x,y,dx,dy,count]");
    return 1;
}

string imagePath = args[0];
string? savePath = null;
int[]? probe = null;
for (int i = 1; i < args.Length; i++)
{
    if (args[i] == "--save") savePath = args[++i];
    else if (args[i] == "--probe") probe = args[++i].Split(',').Select(int.Parse).ToArray();
}

CapturedImage image = await ImageFile.LoadAsync(imagePath);
Console.WriteLine($"Loaded {imagePath} ({image.Width}x{image.Height})");

if (probe is not null)
{
    (int px, int py, int dx, int dy, int count) = (probe[0], probe[1], probe[2], probe[3], probe[4]);
    Console.WriteLine($"Probing from ({px},{py}) step ({dx},{dy}) x{count}:");
    for (int step = 0; step < count; step++)
    {
        int x = px + dx * step, y = py + dy * step;
        if (x < 0 || x >= image.Width || y < 0 || y >= image.Height) break;
        int idx = (y * image.Width + x) * 4;
        byte b = image.Pixels[idx], g = image.Pixels[idx + 1], r = image.Pixels[idx + 2];
        Console.WriteLine($"  ({x,5},{y,5}) R={r,3} G={g,3} B={b,3}  max={Math.Max(r, Math.Max(g, b)),3}");
    }
    return 0;
}

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
