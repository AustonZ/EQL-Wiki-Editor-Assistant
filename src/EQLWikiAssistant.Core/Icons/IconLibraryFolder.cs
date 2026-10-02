namespace EQLWikiAssistant.Core.Icons;

/// <summary>
/// The extracted game icons on disk, as files rather than as fingerprints.
///
/// **Separate from <see cref="IconLibrary"/> on purpose: matching and publishing need different things.** Identifying
/// a captured icon needs only the 432-byte fingerprints, which is why they are indexed into one small file that
/// loads instantly. Showing the match to the user and uploading it to the wiki need the actual PNG, which is only
/// ever a handful of files and is read on demand. Keeping them apart is what lets the index be loaded at startup
/// without 46MB of PNGs being touched.
///
/// The id is the filename, so this is a very thin wrapper — but the sanitization is not optional. An id reaches here
/// from a fingerprint match and is therefore trusted, yet it is also concatenated into a path, and this codebase
/// already sanitizes icon ids on the wiki-cache side for the same reason. One untrusted id away from a path
/// traversal is not a distance worth relying on.
/// </summary>
public sealed class IconLibraryFolder
{
    private readonly string _directory;

    public IconLibraryFolder(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = directory;
    }

    public string Directory => _directory;

    /// <summary>The wiki's file name for an icon id. MediaWiki capitalizes a title's first letter, so
    /// <c>item_5797.png</c> and <c>Item_5797.png</c> are the same page — this spells it the way the wiki's own
    /// 796 existing icon files are named.</summary>
    public static string WikiFileNameFor(string iconId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(iconId);
        return $"Item_{Sanitize(iconId)}.png";
    }

    /// <summary>Whether the library has this icon's file. False is a real answer — 30 of the library's icons are too
    /// blank to be indexed at all, and a folder may simply be missing.</summary>
    public bool Contains(string iconId) => File.Exists(PathFor(iconId));

    /// <summary>The icon's PNG bytes, or null when the library has no such file.</summary>
    public async Task<byte[]?> ReadAsync(string iconId, CancellationToken cancellationToken = default)
    {
        string path = PathFor(iconId);
        if (!File.Exists(path)) return null;
        return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
    }

    private string PathFor(string iconId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(iconId);
        return Path.Combine(_directory, $"{Sanitize(iconId)}.png");
    }

    /// <summary>Keeps an id to the characters a real icon id uses, so it can never climb out of the folder.</summary>
    private static string Sanitize(string iconId) =>
        new([.. iconId.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')]);
}
