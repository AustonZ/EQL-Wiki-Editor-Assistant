using EQLWikiAssistant.Core.Items;
using EQLWikiAssistant.Core.Ocr;

namespace EQLWikiAssistant.Tests.Items;

/// <summary>
/// The item window's trailing region — below the effects, where the game mixes real fields with developer help
/// text because there was nowhere else to put either.
///
/// **Rules by text, not by position**, and the measurement is why: on real captures the gap between an exaltation
/// row and an effect row is 26px while a one-blank-line separator is 28px, and the separator is not even consistent
/// (a pet illusion's text sits two blank lines below its effect, a mount's one). No geometric threshold survives
/// that, so each known line is recognized by what it says and everything else warns.
/// </summary>
public class TrailingTextTests
{
    private static OcrLine L(string text, int x, int y) => new(text, new Rect(x, y, 10, 10), []);

    /// <summary>Verbatim from `Black Chain Bridle`: one blank line after the effect, the mount speed, one more blank
    /// line, then the sentence explaining the Placeable flag.</summary>
    private static ParsedItem ParseBridle() =>
        ItemParser.Parse(
        [
            L("Black Chain Bridle", 1379, 798),
            L("Description", 1298, 819),
            L("Black Chain Bridle", 1289, 850),
            L("No Trade, Placeable", 1289, 866),
            L("Class: ALL", 1289, 882),
            L("Race: ALL", 1289, 898),
            L("Ammo", 1289, 914),
            L("Size:", 1239, 945), L("TINY", 1323, 945),
            L("Weight:", 1239, 961), L("0.1", 1332, 961),
            L("Click Effect: Summon Horse", 1239, 1033),
            L("Cast Time: 3.0 seconds", 1251, 1047),
            L("Mount Speed: Fast", 1239, 1075),
            L("This item is placeable in yards, guild yards, houses and guild halls.", 1239, 1103),
        ]);

    /// <summary>`Mount Speed` is real data the wiki wants, and it is down there with the prose (user,
    /// 2026-09-28) — which is what makes the region a mixture rather than something to skip wholesale.</summary>
    [Fact]
    public void AMountSpeedIsKeptAsAField()
    {
        ParsedItem item = ParseBridle();

        Assert.Equal("Fast", item.Stats.Single(s => s.Key == "Mount Speed").Value);
    }

    /// <summary>The sentence explaining what `Placeable` means is not data, and appears on every placeable item, so
    /// warning about it every time would be noise the user learns to ignore.</summary>
    [Fact]
    public void ThePlaceableExplanationIsDroppedSilently()
    {
        ParsedItem item = ParseBridle();

        Assert.DoesNotContain(item.Stats, s => s.Key.Contains("placeable", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(item.Warnings);
    }

    /// <summary>The same for a pet illusion's description, which sits two blank lines below its effect rather than
    /// one — the inconsistency that rules out reading this region by position.</summary>
    [Fact]
    public void ThePetIllusionExplanationIsDroppedSilently()
    {
        ParsedItem item = ItemParser.Parse(
        [
            L("Guise of the Deceived", 876, 318),
            L("Description", 899, 339),
            L("Guise of the Deceived", 791, 370),
            L("No Trade", 791, 386),
            L("Class: None", 791, 402),
            L("Race: None", 791, 418),
            L("Face", 791, 434),
            L("Size:", 741, 465), L("SMALL", 813, 465),
            L("Effect: Pet Illusion: Dark Elf (Casting Time: 6.0)", 741, 614),
            L("Pet Illusion: Dark Elf", 741, 656),
            L("Changes your pet to Iook Iike a Dark Elf.", 741, 670),
        ]);

        Assert.Equal("Dark Elf", item.Stats.Single(s => s.Key == ItemParser.PetIllusionLabel).Value);
        Assert.Empty(item.Warnings);
    }

    /// <summary>
    /// **Matched on the opening words, because the tail is where the reader is least reliable.** That real capture
    /// reads "Changes your pet to Iook Iike a Dark Elf." — the `l`/`I` glyphs are identical in this font and the
    /// reader resolves them from context, which works on Title Case UI text and not on prose. Anchoring the rule on
    /// the clean opening keeps it working anyway.
    /// </summary>
    [Fact]
    public void TheRuleSurvivesTheReadersProseMisreads()
    {
        ParsedItem item = ItemParser.Parse(
        [
            L("Thing", 96, 0), L("Description", 170, 18), L("Thing", 62, 49),
            L("Class: ALL", 61, 78), L("Race: ALL", 60, 95),
            L("Changes your pet to Iook Iike a Murderbee", 10, 240),
        ]);

        Assert.Empty(item.Warnings);
    }

    /// <summary>
    /// **An unrecognized trailing line still warns**, which is the whole point of doing this by rule: a new kind of
    /// developer text reaches the user rather than being quietly dropped or quietly written. `Convert to Guise of
    /// the Deceiver` is a real one — and the editors do record conversions, by hand, in `notes`.
    /// </summary>
    [Fact]
    public void AnUnrecognizedTrailingLineStillReachesTheUser()
    {
        // Verbatim from `Guise of the Deceived`, which really does show this above its effect.
        ParsedItem item = ItemParser.Parse(
        [
            L("Guise of the Deceived", 876, 318),
            L("Description", 899, 339),
            L("Guise of the Deceived", 791, 370),
            L("No Trade", 791, 386),
            L("Class: None", 791, 402),
            L("Race: None", 791, 418),
            L("Face", 791, 434),
            L("Size:", 741, 465), L("SMALL", 813, 465),
            L("Convert to Guise of the Deceiver", 766, 530),
        ]);

        Assert.Contains(item.Warnings, w => w.Contains("Convert to Guise of the Deceiver"));
    }
}
