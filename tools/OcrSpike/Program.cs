using EQLWikiEditorAssistant.Core.Ocr;
using EQLWikiEditorAssistant.Ocr;
using EQLWikiEditorAssistant.TestSupport;

// Spike tool: run the real OCR engine against a screenshot (optionally cropped and scaled) and dump what it
// recognizes, so accuracy can be judged by eye when tuning parse logic against new samples.
//
// Usage:
//   OcrSpike <imagePath> [--crop x,y,w,h] [--scale factor] [--save pngPath]
//
// --crop restricts OCR to a region of the image (original-resolution coordinates).
// --scale bilinearly rescales (applied after crop). Note RapidOCR wants NATIVE resolution — upscaling measurably
//   hurt it in testing — so this is mainly for saving zoomed crops to eyeball window chrome, not for OCR itself.
// --save writes the (cropped+scaled) image actually fed to OCR, so the region/scale can be eyeballed.

string? imagePath = null;
Rect? crop = null;
double scale = 1.0;
string? savePath = null;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--crop":
            int[] p = args[++i].Split(',').Select(int.Parse).ToArray();
            crop = new Rect(p[0], p[1], p[2], p[3]);
            break;
        case "--scale":
            scale = double.Parse(args[++i]);
            break;
        case "--save":
            savePath = args[++i];
            break;
        default:
            imagePath = args[i];
            break;
    }
}

if (imagePath is null)
{
    Console.Error.WriteLine("usage: OcrSpike <imagePath> [--crop x,y,w,h] [--scale factor] [--save pngPath]");
    return 1;
}

CapturedImage image = await ImageFile.LoadAsync(imagePath);
Console.WriteLine($"Loaded {imagePath} ({image.Width}x{image.Height})");

if (crop is { } region)
{
    image = image.Crop(region);
    Console.WriteLine($"Cropped to {region} -> {image.Width}x{image.Height}");
}

if (scale != 1.0)
{
    var newSize = (Width: (int)Math.Round(image.Width * scale), Height: (int)Math.Round(image.Height * scale));
    image = image.Resize(newSize.Width, newSize.Height);
    Console.WriteLine($"Scaled x{scale} -> {image.Width}x{image.Height}");
}

if (savePath is not null)
{
    await ImageFile.SavePngAsync(image, savePath);
    Console.WriteLine($"Saved to {savePath}");
}

IOcrEngine engine = new RapidOcrEngine();
try
{
    IReadOnlyList<OcrLine> lines = await engine.RecognizeAsync(image);

    Console.WriteLine();
    Console.WriteLine($"--- {lines.Count} line(s) recognized ---");
    foreach (OcrLine line in lines)
    {
        Console.WriteLine($"[{line.BoundingBox.X,4},{line.BoundingBox.Y,4} {line.BoundingBox.Width,4}x{line.BoundingBox.Height,3}] {line.Text}");
    }

    return 0;
}
finally
{
    (engine as IDisposable)?.Dispose();
}
