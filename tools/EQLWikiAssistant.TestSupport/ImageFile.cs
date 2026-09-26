using System.Runtime.InteropServices.WindowsRuntime;
using EQLWikiAssistant.Capture;
using EQLWikiAssistant.Core.Ocr;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace EQLWikiAssistant.TestSupport;

/// <summary>
/// Loads a screenshot file (JPEG/PNG/etc.) from disk into a <see cref="CapturedImage"/>. Dev/test tooling
/// only — the real capture pipeline (EQLWikiAssistant.Capture) never reads from files, it captures the live
/// game window. Used by golden-file tests and the OcrSpike tool to feed real sample screenshots (from the
/// gitignored samples/ folder) through the same OCR engine the app uses.
/// </summary>
public static class ImageFile
{
    public static async Task<CapturedImage> LoadAsync(string path)
    {
        StorageFile file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
        using IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.Read);
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
        using SoftwareBitmap bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);

        int width = bitmap.PixelWidth;
        int height = bitmap.PixelHeight;
        var pixels = new byte[width * height * 4];
        bitmap.CopyToBuffer(pixels.AsBuffer());
        return new CapturedImage(width, height, pixels);
    }

    /// <summary>
    /// Loads an image that has transparency, compositing it over a flat background instead of discarding the alpha.
    ///
    /// <see cref="LoadAsync"/> decodes with <c>BitmapAlphaMode.Ignore</c>, which is right for screenshots (they have
    /// no alpha) and badly wrong for a wiki icon: a PNG's fully-transparent pixels keep whatever RGB the encoder
    /// left underneath, usually white, so the sprite's margin reads as bright and the whole 40x40 file looks like
    /// ink. That made every icon comparison come back as a mismatch at close to the random baseline.
    ///
    /// **This now delegates to the production decoder rather than repeating it.** The icon thresholds recorded in
    /// CLAUDE.md were all measured through this method, and they only transfer to the shipping tool if both sides
    /// decode identically — so the arithmetic lives once, in <see cref="AlphaComposite"/>, behind one decoder.
    /// </summary>
    public static async Task<CapturedImage> LoadOverBackgroundAsync(string path, byte background) =>
        await new WindowsImageDecoder().DecodeAsync(
            await File.ReadAllBytesAsync(Path.GetFullPath(path)), background);

    /// <summary>Saves a CapturedImage as a PNG, overwriting any existing file. Useful for eyeballing crops.</summary>
    public static async Task SavePngAsync(CapturedImage image, string path)
    {
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(fullPath)!);
        StorageFile file = await folder.CreateFileAsync(Path.GetFileName(fullPath), CreationCollisionOption.ReplaceExisting);

        using IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);

        using var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, image.Width, image.Height, BitmapAlphaMode.Ignore);
        bitmap.CopyFromBuffer(image.Pixels.AsBuffer());
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync();
    }
}
