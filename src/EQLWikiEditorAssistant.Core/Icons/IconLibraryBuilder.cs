using EQLWikiEditorAssistant.Core.Ocr;

namespace EQLWikiEditorAssistant.Core.Icons;

/// <summary>
/// Fingerprints a folder of extracted game icons into an <see cref="IconLibrary"/>.
///
/// The folder is the game's own asset export, one PNG per icon, named by its id — so the filename *is* the answer the
/// search is looking for and nothing has to map between the two.
///
/// **Icons are composited over the game's background, not decoded raw**, which is the same correction the wiki-icon
/// path needed and for the same reason: a transparent PNG margin carries whatever RGB the encoder left behind
/// (usually white), so decoding without compositing makes the whole 40x40 read as ink and every fingerprint
/// meaningless. Both sides of a comparison must be the same sprite on the same backdrop.
/// </summary>
public static class IconLibraryBuilder
{
    /// <summary>
    /// Builds the library, reporting progress because this takes long enough to need it.
    /// </summary>
    /// <param name="skipped">Icons that could not be fingerprinted — in practice the handful that are nearly blank.
    /// Reported rather than silently dropped: a library quietly missing entries would answer searches with the
    /// *second* best icon and look like it was working.</param>
    public static async Task<IconLibrary> BuildAsync(
        string folder,
        IImageDecoder decoder,
        Action<int, int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentNullException.ThrowIfNull(decoder);

        (IconLibrary library, _) = await BuildAsync(folder, decoder, progress, reportSkipped: true, cancellationToken)
            .ConfigureAwait(false);
        return library;
    }

    /// <inheritdoc cref="BuildAsync(string, IImageDecoder, Action{int, int}?, CancellationToken)"/>
    public static async Task<(IconLibrary Library, IReadOnlyList<string> Skipped)> BuildAsync(
        string folder,
        IImageDecoder decoder,
        Action<int, int>? progress,
        bool reportSkipped,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentNullException.ThrowIfNull(decoder);

        if (!Directory.Exists(folder))
            throw new DirectoryNotFoundException($"No icon library at '{folder}'.");

        string[] files = Directory.GetFiles(folder, "*.png");
        // Numeric order, so the index reads in id order and a diff of two builds is legible. Non-numeric names sort
        // after, rather than being rejected: an extra file in the folder is not a reason to refuse to build.
        Array.Sort(files, (a, b) =>
        {
            bool na = int.TryParse(Path.GetFileNameWithoutExtension(a), out int ia);
            bool nb = int.TryParse(Path.GetFileNameWithoutExtension(b), out int ib);
            return na && nb ? ia.CompareTo(ib)
                : na != nb ? (na ? -1 : 1)
                : string.CompareOrdinal(a, b);
        });

        var icons = new List<LibraryIcon>(files.Length);
        var skipped = new List<string>();

        for (int i = 0; i < files.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string id = Path.GetFileNameWithoutExtension(files[i]);
            byte[] bytes = await File.ReadAllBytesAsync(files[i], cancellationToken).ConfigureAwait(false);
            CapturedImage image = await decoder
                .DecodeAsync(bytes, AlphaComposite.GameBackground, cancellationToken).ConfigureAwait(false);

            if (IconHasher.TryFingerprint(image, new Rect(0, 0, image.Width, image.Height), out IconFingerprint fp))
                icons.Add(new LibraryIcon(id, fp));
            else if (reportSkipped)
                skipped.Add(id);

            progress?.Invoke(i + 1, files.Length);
        }

        return (new IconLibrary(icons), skipped);
    }
}
