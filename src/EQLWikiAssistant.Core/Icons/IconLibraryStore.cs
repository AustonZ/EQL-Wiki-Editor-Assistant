namespace EQLWikiAssistant.Core.Icons;

/// <summary>
/// Loads the icon library's fingerprint index, rebuilding it whenever the icon folder has changed.
///
/// **The staleness check is the point of this type, not an optimization.** Fingerprinting 11,592 PNGs takes about 8
/// seconds — tolerable once, far too slow on every start — so the result is cached. But a cache of *what icons exist*
/// that silently went stale would be the worst kind of bug this feature could have: the user adds newly extracted
/// icons (which has already happened once in this repo's history), the index does not know about them, and every
/// capture of one of those items is then matched against the closest *older* icon and offered confidently. A wrong
/// `lucy_img_ID` written into a new page, and a wrong icon uploaded beside it, from a cache nobody thought about.
///
/// So the index records what it was built from and is discarded when that no longer matches. The stamp is the file
/// count plus the newest write time, which catches an addition, a removal and a replacement — everything short of an
/// edit that preserves both, which is not a thing an asset re-export does.
/// </summary>
public static class IconLibraryStore
{
    /// <summary>
    /// The library, from the cached index when it is still valid, otherwise rebuilt and re-cached.
    /// </summary>
    /// <param name="folder">The extracted icon PNGs.</param>
    /// <param name="indexFile">Where the fingerprint index is cached.</param>
    /// <param name="rebuilt">True when the index had to be built rather than loaded — worth surfacing, because it is
    /// the difference between a fast start and an eight-second one.</param>
    public static async Task<IconLibrary?> LoadOrBuildAsync(
        string folder,
        string indexFile,
        IImageDecoder decoder,
        Action<int, int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        (IconLibrary? library, _) = await LoadOrBuildAsync(
            folder, indexFile, decoder, progress, force: false, cancellationToken).ConfigureAwait(false);
        return library;
    }

    /// <inheritdoc cref="LoadOrBuildAsync(string, string, IImageDecoder, Action{int, int}?, CancellationToken)"/>
    public static async Task<(IconLibrary? Library, bool Rebuilt)> LoadOrBuildAsync(
        string folder,
        string indexFile,
        IImageDecoder decoder,
        Action<int, int>? progress,
        bool force,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentException.ThrowIfNullOrWhiteSpace(indexFile);
        ArgumentNullException.ThrowIfNull(decoder);

        // No folder is a normal outcome, not an error: the library is optional and the tool simply leaves
        // lucy_img_ID blank without it, exactly as it did before the library existed.
        if (!Directory.Exists(folder)) return (null, false);

        string stamp = StampOf(folder);
        string stampFile = indexFile + ".stamp";

        if (!force && File.Exists(indexFile) && File.Exists(stampFile))
        {
            try
            {
                if (string.Equals(await File.ReadAllTextAsync(stampFile, cancellationToken).ConfigureAwait(false),
                        stamp, StringComparison.Ordinal))
                    return (IconLibraryIndex.Load(indexFile), false);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                // A damaged index is rebuildable from the folder beside it, so losing it costs eight seconds rather
                // than the feature. Same reasoning as the ledger loading empty rather than refusing to start.
            }
        }

        IconLibrary library = await IconLibraryBuilder
            .BuildAsync(folder, decoder, progress, cancellationToken).ConfigureAwait(false);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(indexFile))!);
            // Written through a temporary file, like the ledger: an interrupted write must not leave a truncated
            // index that then loads as a *shorter* library, which would look like working software.
            string temporary = indexFile + ".tmp";
            using (FileStream file = File.Create(temporary)) IconLibraryIndex.Write(file, library.Icons);
            File.Move(temporary, indexFile, overwrite: true);
            await File.WriteAllTextAsync(stampFile, stamp, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Failing to cache is not failing: the library in memory is good, this start was just slow.
        }

        return (library, true);
    }

    /// <summary>What the index was built from: how many icon files there were and the newest write time among them.
    /// </summary>
    private static string StampOf(string folder)
    {
        string[] files = Directory.GetFiles(folder, "*.png");
        long newest = 0;
        foreach (string file in files)
        {
            long ticks = File.GetLastWriteTimeUtc(file).Ticks;
            if (ticks > newest) newest = ticks;
        }
        return $"{files.Length}:{newest}";
    }
}
