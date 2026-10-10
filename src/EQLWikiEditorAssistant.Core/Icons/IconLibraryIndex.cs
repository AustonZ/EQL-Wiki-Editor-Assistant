using System.Buffers.Binary;
using System.IO.Compression;

namespace EQLWikiEditorAssistant.Core.Icons;

/// <summary>
/// Reads and writes the fingerprint index for <see cref="IconLibrary"/>.
///
/// **It exists because fingerprinting the folder is far too slow to do at startup** — 11,592 PNG decodes — while the
/// fingerprints themselves are small and never change unless the game patches. So the index is built once by
/// <c>WikiSpike iconindex</c> and loaded from one file, the same arrangement as the glyph atlas: a generated artifact
/// with its generator kept, rather than a hand-maintained one.
///
/// **Deliberately not an embedded resource, unlike the glyph atlas**, and the difference is worth stating because the
/// atlas comment argues the opposite. The atlas is the only thing its reader needs, so a loose file could go missing
/// and break glyph reading outright. This index cannot stand alone: uploading an icon needs the actual PNG from the
/// asset folder, so the folder has to be present anyway and a missing index is recoverable by rebuilding it from
/// what is already there. Embedding 5MB into <c>Core</c> to protect a file that sits beside 46MB of its own inputs
/// would buy nothing.
/// </summary>
public static class IconLibraryIndex
{
    /// <summary>
    /// The name the index is built and loaded under. **The format's version is part of the name**, not only of the
    /// header: a developer build and an installed release share this folder, and with one file name a release on the
    /// old format and a build on the new one would each rebuild the other's index — seconds at start-up — on every
    /// switch. Version 2 (2026-10-10) is the whole-cell fingerprint.
    /// </summary>
    public const string FileName = "item-icons-v2.index";

    private const string Magic = "EQLICON2";

    /// <summary>
    /// Writes the index: a header, then one record per icon. Deflated, because the signatures are smooth colour data
    /// and compress to roughly half — worth it for a file that is read on every start.
    /// </summary>
    public static void Write(Stream stream, IReadOnlyList<LibraryIcon> icons)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(icons);

        using var deflate = new DeflateStream(stream, CompressionLevel.SmallestSize, leaveOpen: true);
        using var writer = new BinaryWriter(deflate, System.Text.Encoding.UTF8, leaveOpen: true);

        writer.Write(System.Text.Encoding.ASCII.GetBytes(Magic));
        writer.Write(icons.Count);
        // One signature length for the whole file: a mixed-grid index could not be compared at all, so it is
        // rejected at build time rather than discovered as a per-icon exception during a search.
        int signatureLength = icons.Count == 0 ? 0 : icons[0].Fingerprint.Signature.Length;
        writer.Write(signatureLength);

        foreach (LibraryIcon icon in icons)
        {
            if (icon.Fingerprint.Signature.Length != signatureLength)
                throw new ArgumentException(
                    $"Icon '{icon.IconId}' has a {icon.Fingerprint.Signature.Length}-byte signature where the rest " +
                    $"of the index uses {signatureLength}. A mixed index cannot be searched.", nameof(icons));

            writer.Write(icon.IconId);
            writer.Write(icon.Fingerprint.Signature);
        }
    }

    /// <summary>Reads an index written by <see cref="Write"/>.</summary>
    public static IReadOnlyList<LibraryIcon> Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var inflate = new DeflateStream(stream, CompressionMode.Decompress, leaveOpen: true);
        using var reader = new BinaryReader(inflate, System.Text.Encoding.UTF8, leaveOpen: true);

        string magic = System.Text.Encoding.ASCII.GetString(reader.ReadBytes(Magic.Length));
        if (magic != Magic)
            throw new InvalidDataException(
                $"This is not an icon index (expected '{Magic}'). Rebuild it with `WikiSpike iconindex`.");

        int count = reader.ReadInt32();
        int signatureLength = reader.ReadInt32();
        if (count < 0 || signatureLength < 0)
            throw new InvalidDataException("The icon index header is corrupt. Rebuild it with `WikiSpike iconindex`.");

        var icons = new List<LibraryIcon>(count);
        for (int i = 0; i < count; i++)
        {
            string id = reader.ReadString();
            byte[] signature = reader.ReadBytes(signatureLength);
            if (signature.Length != signatureLength)
                throw new InvalidDataException(
                    $"The icon index ends partway through '{id}'. Rebuild it with `WikiSpike iconindex`.");

            icons.Add(new LibraryIcon(id, new IconFingerprint(signature)));
        }

        return icons;
    }

    /// <summary>Convenience: load a library from an index file on disk.</summary>
    public static IconLibrary Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using FileStream file = File.OpenRead(path);
        return new IconLibrary(Read(file));
    }
}
