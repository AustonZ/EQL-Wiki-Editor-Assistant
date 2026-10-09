using System.Runtime.InteropServices.WindowsRuntime;
using EQLWikiEditorAssistant.Core.Icons;
using EQLWikiEditorAssistant.Core.Ocr;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace EQLWikiEditorAssistant.Capture;

/// <summary>
/// The real <see cref="IImageDecoder"/>: WinRT's <see cref="BitmapDecoder"/>, reading from memory.
///
/// It lives in <c>Capture</c> rather than beside the icon code because this is the project whose job is turning
/// Windows pixels into a <see cref="CapturedImage"/> — and because WinRT imaging needs the SDK-versioned TFM, which
/// <c>Core</c> and <c>Wiki</c> deliberately do not use.
///
/// **Decoding is from bytes, never from a path.** The bytes come out of the icon cache, and an earlier file-based
/// route had a real failure mode worth not reintroducing: several items share one icon id (three corpus
/// breastplates all use 624), the decoder keeps a file mapped while it reads, and rewriting that file mid-run fails
/// outright.
/// </summary>
public sealed class WindowsImageDecoder : IImageDecoder
{
    public async Task<CapturedImage> DecodeAsync(
        byte[] bytes,
        byte background = AlphaComposite.GameBackground,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length == 0) throw new ArgumentException("No image data.", nameof(bytes));

        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(bytes.AsBuffer()).AsTask(cancellationToken).ConfigureAwait(false);
        stream.Seek(0);

        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken).ConfigureAwait(false);

        // Straight, not Ignore: the alpha is the whole point — see AlphaComposite.
        using SoftwareBitmap bitmap = await decoder
            .GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight)
            .AsTask(cancellationToken).ConfigureAwait(false);

        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyToBuffer(pixels.AsBuffer());
        AlphaComposite.Over(pixels, background);

        return new CapturedImage(bitmap.PixelWidth, bitmap.PixelHeight, pixels);
    }
}
