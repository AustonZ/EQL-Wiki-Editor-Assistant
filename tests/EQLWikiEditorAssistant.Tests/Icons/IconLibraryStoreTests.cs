using EQLWikiEditorAssistant.Core.Icons;
using EQLWikiEditorAssistant.Core.Ocr;

namespace EQLWikiEditorAssistant.Tests.Icons;

/// <summary>
/// The cached fingerprint index, and the thing that actually matters about it: that it is discarded when the icon
/// folder changes.
///
/// **This is a silently-wrong risk, not a performance one.** Fingerprinting 11,592 PNGs takes ~8 seconds, so the
/// result is cached — but a cache of *which icons exist* that quietly went stale is the worst bug this feature could
/// have. The user adds newly extracted icons (which has already happened once in this repo's history), the index does
/// not know them, and every capture of one of those items is matched against the closest *older* icon and offered
/// confidently. That writes a wrong `lucy_img_ID` into a new page and uploads a wrong icon beside it.
///
/// The decoder is a fake so the files can be trivial: what is under test is the staleness rule, and decoding real
/// PNGs would be testing WinRT imaging.
/// </summary>
public class IconLibraryStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "eqlwiki-iconstore-" + Guid.NewGuid().ToString("N"));

    private string Folder => Path.Combine(_root, "icons");
    private string IndexFile => Path.Combine(_root, "item-icons.index");

    public IconLibraryStoreTests() => Directory.CreateDirectory(Folder);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* a temp dir, not worth failing */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>Turns the one byte in each file into a distinguishable little image, so different files fingerprint
    /// differently and the library's contents can be asserted on.</summary>
    private sealed class FakeDecoder : IImageDecoder
    {
        public int Decodes { get; private set; }

        public Task<CapturedImage> DecodeAsync(
            byte[] bytes, byte background = AlphaComposite.GameBackground,
            CancellationToken cancellationToken = default)
        {
            Decodes++;
            const int size = 16;
            var pixels = new byte[size * size * 4];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    int o = (y * size + x) * 4;
                    // Well clear of the ink floor, and varied enough to pass the contrast gate.
                    pixels[o] = (byte)((x * 16 + bytes[0] * 7) % 256);
                    pixels[o + 1] = (byte)((y * 16 + bytes[0] * 13) % 256);
                    pixels[o + 2] = (byte)((x * y + bytes[0] * 29) % 256);
                    pixels[o + 3] = 255;
                }
            return Task.FromResult(new CapturedImage(size, size, pixels));
        }
    }

    private void WriteIcon(string id, byte seed) => File.WriteAllBytes(Path.Combine(Folder, $"{id}.png"), [seed]);

    [Fact]
    public async Task TheIndexIsBuiltOnceAndReusedAfterwards()
    {
        WriteIcon("1", 1);
        WriteIcon("2", 2);
        var decoder = new FakeDecoder();

        (IconLibrary? first, bool rebuiltFirst) =
            await IconLibraryStore.LoadOrBuildAsync(Folder, IndexFile, decoder, null, force: false);
        int afterBuild = decoder.Decodes;

        (IconLibrary? second, bool rebuiltSecond) =
            await IconLibraryStore.LoadOrBuildAsync(Folder, IndexFile, decoder, null, force: false);

        Assert.True(rebuiltFirst);
        Assert.False(rebuiltSecond);
        Assert.Equal(2, first!.Count);
        Assert.Equal(2, second!.Count);
        // The whole point of the cache: the second load decodes nothing at all.
        Assert.Equal(afterBuild, decoder.Decodes);
    }

    /// <summary>
    /// **The negative control for the cache**, and the bug it exists to prevent: a new icon arrives and the index
    /// must notice. Without the stamp this test fails with a library of 2 — and the tool would then confidently
    /// match the new item against the closest old icon.
    /// </summary>
    [Fact]
    public async Task AddingAnIconRebuildsTheIndex()
    {
        WriteIcon("1", 1);
        WriteIcon("2", 2);
        var decoder = new FakeDecoder();
        await IconLibraryStore.LoadOrBuildAsync(Folder, IndexFile, decoder, null, force: false);

        WriteIcon("3", 3);

        (IconLibrary? library, bool rebuilt) =
            await IconLibraryStore.LoadOrBuildAsync(Folder, IndexFile, decoder, null, force: false);

        Assert.True(rebuilt);
        Assert.Equal(3, library!.Count);
        Assert.Contains("3", library.Icons.Select(i => i.IconId));
    }

    /// <summary>A removal has to invalidate it too, or the library keeps offering an icon the folder no longer has —
    /// which would then fail at the upload, after the id was already written into the page.</summary>
    [Fact]
    public async Task RemovingAnIconRebuildsTheIndex()
    {
        WriteIcon("1", 1);
        WriteIcon("2", 2);
        var decoder = new FakeDecoder();
        await IconLibraryStore.LoadOrBuildAsync(Folder, IndexFile, decoder, null, force: false);

        File.Delete(Path.Combine(Folder, "2.png"));

        (IconLibrary? library, bool rebuilt) =
            await IconLibraryStore.LoadOrBuildAsync(Folder, IndexFile, decoder, null, force: false);

        Assert.True(rebuilt);
        Assert.Equal(1, library!.Count);
    }

    /// <summary>A redrawn icon changes the file's size, and that rebuilds the index — the replacement the old
    /// date-based stamp caught, still caught without the dates.</summary>
    [Fact]
    public async Task ReplacingAnIconRebuildsTheIndex()
    {
        WriteIcon("1", 1);
        WriteIcon("2", 2);
        var decoder = new FakeDecoder();
        await IconLibraryStore.LoadOrBuildAsync(Folder, IndexFile, decoder, null, force: false);

        File.WriteAllBytes(Path.Combine(Folder, "2.png"), [9, 0]);

        (_, bool rebuilt) = await IconLibraryStore.LoadOrBuildAsync(Folder, IndexFile, decoder, null, force: false);

        Assert.True(rebuilt);
    }

    /// <summary>
    /// **The installer rewrites every icon's date and nothing else**, so a date alone must not rebuild the index
    /// (user, 2026-10-09). With the old date-based stamp this rebuilt — several seconds at start-up after every install
    /// and update, and after every switch between the developer's build and the installed copy, which share the index.
    /// </summary>
    [Fact]
    public async Task NewDatesAloneDoNotRebuildTheIndex()
    {
        WriteIcon("1", 1);
        WriteIcon("2", 2);
        var decoder = new FakeDecoder();
        await IconLibraryStore.LoadOrBuildAsync(Folder, IndexFile, decoder, null, force: false);
        int afterBuild = decoder.Decodes;

        foreach (string file in Directory.GetFiles(Folder))
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddHours(1));

        (_, bool rebuilt) = await IconLibraryStore.LoadOrBuildAsync(Folder, IndexFile, decoder, null, force: false);

        Assert.False(rebuilt);
        Assert.Equal(afterBuild, decoder.Decodes);
    }

    /// <summary>A damaged index costs eight seconds, not the feature — the same call the ledger makes when its JSON
    /// will not parse.</summary>
    [Fact]
    public async Task ACorruptIndexIsRebuiltRatherThanThrown()
    {
        WriteIcon("1", 1);
        var decoder = new FakeDecoder();
        await IconLibraryStore.LoadOrBuildAsync(Folder, IndexFile, decoder, null, force: false);

        await File.WriteAllTextAsync(IndexFile, "this is not an index");

        (IconLibrary? library, bool rebuilt) =
            await IconLibraryStore.LoadOrBuildAsync(Folder, IndexFile, decoder, null, force: false);

        Assert.True(rebuilt);
        Assert.Equal(1, library!.Count);
    }

    /// <summary>No folder is a normal outcome: the library is optional, and without it a generated page simply has a
    /// blank lucy_img_ID — the behaviour that existed before the library did.</summary>
    [Fact]
    public async Task AMissingFolderIsNotAnError()
    {
        (IconLibrary? library, bool rebuilt) = await IconLibraryStore.LoadOrBuildAsync(
            Path.Combine(_root, "nothing-here"), IndexFile, new FakeDecoder(), null, force: false);

        Assert.Null(library);
        Assert.False(rebuilt);
    }
}
