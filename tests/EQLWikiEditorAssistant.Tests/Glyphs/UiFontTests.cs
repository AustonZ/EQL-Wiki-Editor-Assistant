using EQLWikiEditorAssistant.Core.Glyphs;
using EQLWikiEditorAssistant.Core.Locate;
using EQLWikiEditorAssistant.Core.Ocr;

namespace EQLWikiEditorAssistant.Tests.Glyphs;

/// <summary>
/// Reading the game in either of the two UI fonts the tool knows (user, 2026-10-05): Windows' Arial, and the user's
/// own modification of it, "EQL Wiki Editor Assistant", which gives the capital I serifs and the r a wider cell.
///
/// The two render pixel-identically except for those two characters, so the bundled atlas holds both with the
/// font-specific shapes tagged. These tests pin what that buys and what it must not cost: the corpus of Arial
/// captures is its own regression gate, but it only runs on demand, and every rule here would be invisible to it.
/// </summary>
public class UiFontTests
{
    private static GlyphAtlas Atlas => GlyphAtlas.Bundled;

    // ---- what the bundled atlas holds ----

    /// <summary>Exactly the shapes measured to differ, and no others. A merge that tagged a shared shape would make
    /// the reader treat an ordinary glyph as font evidence; one that missed a difference would read it as noise.</summary>
    [Fact]
    public void OnlyTheIAndTheRAreFontSpecific()
    {
        string Describe(AtlasEntry e) => $"{string.Concat(e.Labels)}@{e.Font}";

        Assert.Equal(
            ["I@EqlWikiEditorAssistant", "r@Arial", "r@EqlWikiEditorAssistant"],
            Atlas.Entries.Where(e => e.Font is not null).Select(Describe).Order(StringComparer.Ordinal));
    }

    /// <summary>The bare bar stays one shared entry that says "l or I". It is the same pixels in both fonts — what
    /// differs is only what it can mean, which is the reader's call from the configured font.</summary>
    [Fact]
    public void TheBareBarIsSharedAndStillAmbiguous()
    {
        AtlasEntry bar = Assert.Single(Atlas.Ambiguous);
        Assert.Equal(["l", "I"], bar.Labels);
        Assert.Null(bar.Font);
    }

    /// <summary>The new shapes' cells, learned from a real capture, are the widths the font was built to: 5px for
    /// both at the game's 12 px/em, where Arial's I is a 3px cell and its r 4px.</summary>
    [Fact]
    public void TheNewShapesHaveTheirOwnLearnedCells()
    {
        Assert.Equal(5, Entry("I", UiFont.EqlWikiEditorAssistant).Advance);
        Assert.Equal(5, Entry("r", UiFont.EqlWikiEditorAssistant).Advance);
        Assert.Equal(4, Entry("r", UiFont.Arial).Advance);
    }

    /// <summary>Two r's must stay two entries for anything keyed by entry. Keyed by label they collapsed into the
    /// smaller cell, and every wide r was followed by a phantom space.</summary>
    [Fact]
    public void FontSpecificEntriesHaveDistinctKeys()
    {
        Assert.NotEqual(Entry("r", UiFont.Arial).Key, Entry("r", UiFont.EqlWikiEditorAssistant).Key);
        Assert.Equal("o", Entry("o", UiFont.Arial).Key);   // a shared entry's key is just its label, as before
    }

    // ---- the atlas file ----

    /// <summary>The font column is written only when there is one, so the 87 shared lines keep exactly their old
    /// shape, and an atlas written before fonts existed still loads.</summary>
    [Fact]
    public void TheFontColumnRoundTripsAndIsOptional()
    {
        GlyphAtlas reloaded = GlyphAtlas.Parse(Atlas.Save());
        Assert.Equal(Atlas.Entries.Select(e => (e.Key, e.Advance)), reloaded.Entries.Select(e => (e.Key, e.Advance)));

        GlyphAtlas legacy = GlyphAtlas.Parse("o\t0\t2\t1\t66\t3\n");
        Assert.Null(Assert.Single(legacy.Entries).Font);
    }

    // ---- merging a second font's sheet ----

    private static readonly GlyphBitmap Bar = new(1, 2, [6, 6]);
    private static readonly GlyphBitmap Hook = new(2, 2, [6, 6, 6, 0]);
    private static readonly GlyphBitmap Serif = new(3, 2, [6, 6, 6, 0, 6, 0]);
    private static readonly GlyphBitmap Wide = new(2, 2, [6, 6, 6, 6]);

    [Fact]
    public void MergingKeepsSharedShapesAndTagsTheRest()
    {
        var arial = new GlyphAtlas([new AtlasEntry(["l", "I"], 0, Bar, 3), new AtlasEntry(["r"], 0, Hook, 4)]);
        var custom = new GlyphAtlas([new AtlasEntry(["l"], 0, Bar), new AtlasEntry(["I"], 0, Serif), new AtlasEntry(["r"], 0, Wide)]);

        GlyphAtlas merged = GlyphAtlas.MergeFont(arial, UiFont.Arial, custom, UiFont.EqlWikiEditorAssistant);

        AtlasEntry bar = merged.Entries.Single(e => e.Bitmap.Equals(Bar));
        Assert.Equal(["l", "I"], bar.Labels);       // keeps its labels...
        Assert.Equal(3, bar.Advance);               // ...and the advance learned for it
        Assert.Null(bar.Font);
        Assert.Equal(UiFont.Arial, merged.Entries.Single(e => e.Bitmap.Equals(Hook)).Font);
        Assert.Equal(UiFont.EqlWikiEditorAssistant, merged.Entries.Single(e => e.Bitmap.Equals(Serif)).Font);
        Assert.Equal(0, merged.Entries.Single(e => e.Bitmap.Equals(Wide)).Advance);   // not learned yet
    }

    /// <summary>A shared shape may not gain a label. If it could, a mislabelled sheet would silently teach the
    /// reader that two characters look alike in a font where nobody ever saw them do so.</summary>
    [Fact]
    public void MergingRefusesASharedShapeGainingALabel()
    {
        var arial = new GlyphAtlas([new AtlasEntry(["l", "I"], 0, Bar, 3)]);
        var mislabelled = new GlyphAtlas([new AtlasEntry(["1"], 0, Bar)]);

        Assert.Throws<ArgumentException>(() =>
            GlyphAtlas.MergeFont(arial, UiFont.Arial, mislabelled, UiFont.EqlWikiEditorAssistant));
    }

    // ---- what the bare bar means ----

    /// <summary>In EQL Wiki Editor Assistant the bar is always an l: that font's I has serifs, so there is nothing to
    /// decide — including the lore word Arial gets wrong.</summary>
    [Theory]
    [InlineData("\u0001ost items", "lost items")]
    [InlineData("B\u0001adestopper", "Bladestopper")]
    [InlineData("\u0001\u0001ama", "llama")]
    public void InEqlWikiEditorAssistantTheBarIsAlwaysAnL(string text, string expected) =>
        Assert.Equal(expected, GlyphReader.ResolveBar(text, UiFont.EqlWikiEditorAssistant));

    /// <summary>In Arial the word decides, and the dictionary where the word cannot: "lost" reads correctly since
    /// 2026-10-09, when the user reversed the decision to keep "Iost" as a known limitation.</summary>
    [Theory]
    [InlineData("\u0001ost items", "lost items")]
    [InlineData("B\u0001adestopper", "Bladestopper")]
    [InlineData("\u0001mproved", "Improved")]
    public void InArialTheWordDecides(string text, string expected) =>
        Assert.Equal(expected, GlyphReader.ResolveBar(text, UiFont.Arial));

    // ---- reading painted text end to end ----

    /// <summary>The whole point of the font: every character is its own shape, so a line full of I's and l's
    /// reads exactly, and says which font drew it.</summary>
    [Fact]
    public void EqlWikiEditorAssistantTextReadsExactly()
    {
        CapturedImage image = Paint(UiFont.EqlWikiEditorAssistant, "Iron", "lost");

        OcrLine line = Assert.Single(Read(image, UiFont.EqlWikiEditorAssistant));

        Assert.Equal("Iron lost", line.Text);
        Assert.Equal(UiFont.EqlWikiEditorAssistant, line.DrawnIn);
    }

    /// <summary>Arial reads both words right, though every l and I in them is the same bar: "Iron" by the Title Case
    /// rule, "lost" by the dictionary.</summary>
    [Fact]
    public void ArialTextReadsBothWordsRight()
    {
        CapturedImage image = Paint(UiFont.Arial, "Iron", "lost");

        OcrLine line = Assert.Single(Read(image, UiFont.Arial));

        Assert.Equal("Iron lost", line.Text);
        Assert.Equal(UiFont.Arial, line.DrawnIn);
    }

    /// <summary>
    /// The hazard the font evidence exists for. Arial text read with the reader set to EQL Wiki Editor Assistant turns
    /// every capital I into an l — and it does read, plausibly, as "lron". Nothing in the text says it is wrong.
    /// What does is the line's own r, which is Arial's: that is how the pipeline knows to refuse the window.
    /// </summary>
    [Fact]
    public void ArialTextReadInTheWrongFontStillSaysWhichFontDrewIt()
    {
        CapturedImage image = Paint(UiFont.Arial, "Iron", "lost");

        OcrLine line = Assert.Single(Read(image, UiFont.EqlWikiEditorAssistant));

        Assert.Equal("lron lost", line.Text);
        Assert.Equal(UiFont.Arial, line.DrawnIn);
    }

    /// <summary>A line made only of shared shapes says nothing about its font, which is most lines — the verdict
    /// comes from the few with an r or a serifed I, and every window's "Description" tab has an r.</summary>
    [Fact]
    public void ALineOfSharedShapesClaimsNoFont()
    {
        CapturedImage image = Paint(UiFont.EqlWikiEditorAssistant, "Hoop");

        Assert.Null(Assert.Single(Read(image, UiFont.EqlWikiEditorAssistant)).DrawnIn);
    }

    // ---- the window's verdict ----

    [Fact]
    public void AWindowTakesTheFontItsLinesAgreeOn()
    {
        OcrLine Line(UiFont? font) => new("x", new Rect(0, 0, 1, 1), [], font);

        Assert.Equal(UiFont.Arial, ItemWindowLocator.DrawnIn([Line(null), Line(UiFont.Arial), Line(UiFont.Arial)]));
        Assert.Null(ItemWindowLocator.DrawnIn([Line(null), Line(null)]));
        // Lines that disagree mean a misread, not a window in two fonts, so the window claims nothing.
        Assert.Null(ItemWindowLocator.DrawnIn([Line(UiFont.Arial), Line(UiFont.EqlWikiEditorAssistant)]));
    }

    // ---- helpers ----

    private static IReadOnlyList<OcrLine> Read(CapturedImage image, UiFont font) =>
        GlyphReader.Read(image, new Rect(0, 0, image.Width, image.Height), Atlas, font);

    /// <summary>The atlas entry a font draws a character with: its own shape if it has one, else the shared one.</summary>
    private static AtlasEntry Entry(string label, UiFont font) =>
        Atlas.Entries.FirstOrDefault(e => e.Labels.Contains(label) && e.Font == font)
        ?? Atlas.Entries.Single(e => e.Labels.Contains(label) && e.Font is null);

    /// <summary>
    /// Paints words onto the game's content-area grey exactly as the game draws them: each glyph's ramp levels as
    /// white text, glyphs one learned cell apart, words a space apart. The atlas shapes are the real ones, measured
    /// from in-game captures, so this is the game's own pixels with only the layout made up.
    /// </summary>
    private static CapturedImage Paint(UiFont font, params string[] words)
    {
        const int background = 16, peak = 255, baseline = 20, width = 200, height = 32;
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = pixels[i + 1] = pixels[i + 2] = background;
            pixels[i + 3] = 255;
        }

        int pen = 4;
        foreach (string word in words)
        {
            foreach (char c in word)
            {
                AtlasEntry entry = Entry(c.ToString(), font);
                GlyphBitmap bitmap = entry.Bitmap;
                int top = baseline - (bitmap.Height - 1) + entry.BaselineOffset;
                for (int y = 0; y < bitmap.Height; y++)
                    for (int x = 0; x < bitmap.Width; x++)
                    {
                        int value = background + (int)Math.Round(
                            (peak - background) * GlyphRamp.Fifteenths[bitmap[x, y]] / 15.0);
                        int o = ((top + y) * width + pen + x) * 4;
                        pixels[o] = pixels[o + 1] = pixels[o + 2] = (byte)value;
                    }
                pen += entry.HasAdvance ? entry.Advance : bitmap.Width + 1;
            }
            pen += GlyphReader.MinSpaceAdvance + 1;
        }
        return new CapturedImage(width, height, pixels);
    }
}
