using System.Runtime.InteropServices.WindowsRuntime;
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
