using EQLWikiEditorAssistant.Core.Icons;
using EQLWikiEditorAssistant.Core.Ocr;

namespace EQLWikiEditorAssistant.Tests.Icons;

/// <summary>
/// Identifying a captured icon among the whole library — a different question from the icon *check*, which only ever
/// compares one capture against one known file.
///
/// **These are synthetic fingerprints on purpose.** Whether the search picks the right icon out of 11,562 real ones is
/// a corpus question and is measured by `WikiSpike iconsearch` (87/90 top-1, which is where
/// <see cref="IconLibrary.ConfidentMargin"/> comes from); what belongs here is the behaviour around that — how a tie
/// is reported, that a duplicate does not steal the margin, that an uncomparable fingerprint is refused rather than
/// answered. Those are rules, and a rule tested against real artwork would be testing the artwork.
/// </summary>
public class IconLibraryTests
{
    private const int SignatureLength = 12 * 12 * 3;

    /// <summary>A fingerprint whose signature is a smooth ramp offset by <paramref name="seed"/>, so two different
    /// seeds correlate imperfectly and the same seed correlates exactly.</summary>
    private static IconFingerprint Fingerprint(int seed, double skew = 0)
    {
        var signature = new byte[SignatureLength];
        for (int i = 0; i < signature.Length; i++)
        {
            double value = (i * 7 + seed * 53) % 256;
            value += skew * Math.Sin(i / 9.0) * 40;
            signature[i] = (byte)Math.Clamp(value, 0, 255);
        }
        return new IconFingerprint(signature, 40, 40);
    }

    /// <summary>A copy perturbed in every other cell starting at <paramref name="from"/>, so two different starting
    /// offsets give two copies that are equally far from the original but not from each other.</summary>
    private static IconFingerprint Nudge(IconFingerprint source, int from)
    {
        var signature = (byte[])source.Signature.Clone();
        for (int i = from; i < signature.Length; i += 2)
            signature[i] = (byte)Math.Clamp(signature[i] + 18, 0, 255);
        return source with { Signature = signature };
    }

    private static IconLibrary LibraryOf(params (string Id, IconFingerprint Fingerprint)[] icons) =>
        new(icons.Select(i => new LibraryIcon(i.Id, i.Fingerprint)));

    [Fact]
    public void TheMatchingIconRanksFirst()
    {
        IconLibrary library = LibraryOf(
            ("100", Fingerprint(1)), ("200", Fingerprint(2)), ("300", Fingerprint(3)));

        IReadOnlyList<IconMatch> matches = library.Search(Fingerprint(2), take: 3);

        Assert.Equal("200", matches[0].IconId);
        Assert.Equal(0, matches[0].Distance, 6);
    }

    /// <summary>A clear winner is written into the page; this is the ordinary creation path.</summary>
    [Fact]
    public void AClearWinnerIsConfident()
    {
        IconLibrary library = LibraryOf(("100", Fingerprint(1)), ("200", Fingerprint(2)));

        IconIdentification found = library.Identify(Fingerprint(2))!;

        Assert.Equal("200", found.IconId);
        Assert.True(found.IsConfident);
        Assert.True(found.Margin >= IconLibrary.ConfidentMargin);
    }

    /// <summary>
    /// **The case the whole gate exists for.** Two icons almost equally close means the tool does not know which it
    /// is, and on a brand-new page a wrong `lucy_img_ID` is published alongside a wrong uploaded icon under a name
    /// nobody on this wiki can delete. It still reports its best guess and the shortlist — the user picks — but
    /// nothing is written automatically.
    /// </summary>
    [Fact]
    public void TwoNearlyIdenticalCandidatesAreNotConfident()
    {
        // Two rivals perturbed from the captured icon by the same amount in *different* cells, so each is about as
        // close as the other — which is what a real near-tie looks like, and what a single skew value could not
        // produce reliably.
        IconFingerprint captured = Fingerprint(2);
        IconLibrary library = LibraryOf(
            ("200", Nudge(captured, from: 0)),
            ("201", Nudge(captured, from: 1)),
            ("300", Fingerprint(3)));

        IconIdentification found = library.Identify(captured)!;

        Assert.False(found.IsConfident);
        Assert.True(found.Margin < IconLibrary.ConfidentMargin, $"margin was {found.Margin}");
        // The shortlist is what the user chooses from, so it has to carry the rival rather than only the winner.
        Assert.Contains("200", found.Candidates.Select(c => c.IconId));
        Assert.Contains("201", found.Candidates.Select(c => c.IconId));
    }

    /// <summary>
    /// **A duplicate must not steal the margin.** The library holds 4 groups of byte-identical artwork (15 ids); for
    /// those, the runner-up is the *same picture* under another id, so a naive second-place gap is zero and the icon
    /// becomes permanently unidentifiable. The margin is measured against the nearest *different* artwork instead.
    /// </summary>
    [Fact]
    public void AnIdenticalDuplicateDoesNotDestroyConfidence()
    {
        IconFingerprint shared = Fingerprint(2);
        IconLibrary library = LibraryOf(("200", shared), ("999", shared), ("300", Fingerprint(3)));

        IconIdentification found = library.Identify(shared)!;

        Assert.True(found.IsConfident);
        Assert.Contains(found.IconId, new[] { "200", "999" });
    }

    /// <summary>
    /// A near-uniform signature correlates with almost anything, and against 11,562 candidates "almost anything"
    /// always contains a winner. Refusing is the same call <see cref="ItemIconReader"/> makes, for the same reason.
    /// </summary>
    [Fact]
    public void AnIconTooFlatToCompareIsRefusedRatherThanAnswered()
    {
        var flat = new IconFingerprint(new byte[SignatureLength], 40, 40);
        IconLibrary library = LibraryOf(("100", Fingerprint(1)), ("200", Fingerprint(2)));

        Assert.False(flat.IsComparable);
        Assert.Empty(library.Search(flat));
        Assert.Null(library.Identify(flat));
    }

    /// <summary>The wiki's own 796 icon files are named this way; MediaWiki capitalizes a title's first letter, so
    /// this and `item_<id>.png` are the same page.</summary>
    [Fact]
    public void TheWikiFileNameMatchesTheWikisOwnConvention()
    {
        Assert.Equal("Item_5797.png", IconLibraryFolder.WikiFileNameFor("5797"));
        // An id reaches a path, so it is kept to the characters a real one uses.
        Assert.Equal("Item_123.png", IconLibraryFolder.WikiFileNameFor("../123"));
    }

    [Fact]
    public void TheIndexRoundTripsExactly()
    {
        LibraryIcon[] icons =
        [
            new("100", Fingerprint(1)),
            new("2000", Fingerprint(2)),
            new("30", Fingerprint(3)),
        ];

        using var stream = new MemoryStream();
        IconLibraryIndex.Write(stream, icons);
        stream.Position = 0;
        IReadOnlyList<LibraryIcon> read = IconLibraryIndex.Read(stream);

        Assert.Equal(icons.Length, read.Count);
        for (int i = 0; i < icons.Length; i++)
        {
            Assert.Equal(icons[i].IconId, read[i].IconId);
            Assert.Equal(icons[i].Fingerprint.Signature, read[i].Fingerprint.Signature);
            Assert.Equal(icons[i].Fingerprint.InkWidth, read[i].Fingerprint.InkWidth);
        }
    }

    /// <summary>A file that is not an index says so rather than producing a library of nonsense.</summary>
    [Fact]
    public void ANonIndexFileIsRejected()
    {
        using var stream = new MemoryStream();
        using (var deflate = new System.IO.Compression.DeflateStream(
                   stream, System.IO.Compression.CompressionMode.Compress, leaveOpen: true))
            deflate.Write("not an icon index at all"u8);
        stream.Position = 0;

        Assert.Throws<InvalidDataException>(() => IconLibraryIndex.Read(stream));
    }
}
