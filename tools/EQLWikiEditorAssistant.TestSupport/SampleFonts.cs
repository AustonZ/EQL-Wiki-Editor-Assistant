using EQLWikiEditorAssistant.Core.Glyphs;
using EQLWikiEditorAssistant.TestSupport.Accuracy;

namespace EQLWikiEditorAssistant.TestSupport;

/// <summary>
/// Which UI font a screenshot should be read in, for the dev tools. One rule shared by every tool, so a sample
/// cannot be read as Arial by one and as EQL Wiki Editor Assistant by another. **Arial unless something says
/// otherwise** (user, 2026-10-10) — the game's default and the Assistant's, so a contributor who has never heard of the
/// other font never needs to:
/// <list type="number">
/// <item>an explicit <c>--font &lt;name&gt;</c> on the command line;</item>
/// <item>else the sample's ground-truth entry, whose <c>font</c> field is absent for Arial;</item>
/// <item>else the file name's <c>[font &lt;name&gt;]</c> marker (<see cref="UiFonts.FileNameMarker"/>), which a saved
/// capture carries when the Assistant was set to another font and a sample keeps when copied from one;</item>
/// <item>else Arial.</item>
/// </list>
/// A ground-truth entry and a marker that disagree are a corpus defect and throw, rather than one quietly winning.
/// </summary>
public static class SampleFonts
{
    private static readonly Lazy<ExpectedCorpus?> Tracked = new(() =>
        File.Exists(RepoPaths.ExpectedItemsFile) ? ExpectedCorpus.Load(RepoPaths.ExpectedItemsFile) : null);

    public static UiFont For(string path, IReadOnlyList<string> args, ExpectedCorpus? expected = null)
    {
        int i = args is string[] a ? Array.IndexOf(a, "--font") : args.ToList().IndexOf("--font");
        if (i >= 0 && i + 1 < args.Count) return UiFonts.Parse(args[i + 1]);

        UiFont? marked = UiFonts.FromFileName(path);
        ExpectedSample? entry = (expected ?? Tracked.Value)?.Find(Path.GetFileName(path));
        if (entry is null) return marked ?? UiFont.Arial;

        if (marked is { } m && m != entry.FontOrArial)
            throw new InvalidOperationException(
                $"{Path.GetFileName(path)} is marked {UiFonts.DisplayName(m)} but its ground truth says " +
                $"{UiFonts.DisplayName(entry.FontOrArial)}.");
        return entry.FontOrArial;
    }

    /// <summary>A glyph reader for one screenshot, in the font <see cref="For"/> gives it.</summary>
    public static GlyphTextReader Reader(string path, IReadOnlyList<string> args, ExpectedCorpus? expected = null) =>
        new(For(path, args, expected));
}
