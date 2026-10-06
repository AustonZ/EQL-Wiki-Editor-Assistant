using EQLWikiAssistant.Core.Glyphs;
using EQLWikiAssistant.Core.Ocr;
using EQLWikiAssistant.TestSupport.Accuracy;

namespace EQLWikiAssistant.TestSupport;

/// <summary>
/// Which UI font a screenshot should be read in, for the dev tools. One rule shared by every tool, so a sample
/// cannot be read as Arial by one and as EQL Wiki Assistant by another:
/// <list type="number">
/// <item>an explicit <c>--font &lt;name&gt;</c> on the command line;</item>
/// <item>else the sample's ground-truth entry — whose <c>font</c> field is absent for Arial, which is what every
/// sample captured before the custom font is;</item>
/// <item>else, for a screenshot the corpus has never seen, the app's own default, because a new capture is made
/// in whatever the game is set to now.</item>
/// </list>
/// </summary>
public static class SampleFonts
{
    private static readonly Lazy<ExpectedCorpus?> Tracked = new(() =>
        File.Exists(RepoPaths.ExpectedItemsFile) ? ExpectedCorpus.Load(RepoPaths.ExpectedItemsFile) : null);

    public static UiFont For(string path, IReadOnlyList<string> args, ExpectedCorpus? expected = null)
    {
        int i = args is string[] a ? Array.IndexOf(a, "--font") : args.ToList().IndexOf("--font");
        if (i >= 0 && i + 1 < args.Count) return UiFonts.Parse(args[i + 1]);

        ExpectedSample? entry = (expected ?? Tracked.Value)?.Find(Path.GetFileName(path));
        return entry is not null ? entry.FontOrArial : UiFonts.AppDefault;
    }

    /// <summary>The shipping configuration for one screenshot: RapidOCR to find the windows, the glyph atlas to
    /// read them, in that screenshot's font.</summary>
    public static IOcrEngine Engine(IOcrEngine fullFrame, string path, IReadOnlyList<string> args) =>
        Engine(fullFrame, For(path, args));

    public static IOcrEngine Engine(IOcrEngine fullFrame, UiFont font) =>
        new RoutingOcrEngine(fullFrame: fullFrame, windowCrop: new GlyphOcrEngine(font));
}
