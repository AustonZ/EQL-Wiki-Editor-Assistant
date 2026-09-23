using EQLWikiAssistant.Core.Items;
using EQLWikiAssistant.Core.Locate;
using EQLWikiAssistant.Core.Ocr;
using EQLWikiAssistant.Ocr;
using EQLWikiAssistant.TestSupport;
using Xunit.Abstractions;

namespace EQLWikiAssistant.Tests.Items;

/// <summary>
/// Deterministic unit tests build an OcrLine list by hand from a real, verbatim <c>LocateSpike</c> capture (a
/// "Lustrous Russet Bracer +6" window — see the plan's milestone 2 writeup for the full dump) so the parser's
/// logic is covered without needing samples/ present. Golden tests below additionally run the real pipeline
/// end-to-end against real screenshots, same pattern as ItemWindowLocatorTests.
/// </summary>
public class ItemParserTests
{
    private readonly ITestOutputHelper _output;
    public ItemParserTests(ITestOutputHelper output) => _output = output;

    private static OcrLine L(string text, int x, int y) => new(text, new Rect(x, y, 10, 10), []);

    // Verbatim real capture of "Lustrous Russet Bracer +6", a native (removable) Focus Exaltation, and the
    // confirmed real OCR corruptions "Omamentation" (Ornamentation) and "Wom Exaltation" (Worn Exaltation).
    private static readonly OcrLine[] LustrousBracerLines =
    [
        L("Lustrous Russet Bracer +6 (Augmented)", 96, 0),
        L("Description", 170, 18),
        L("Lustrous Russet Bracer +6", 62, 49),
        L("No Trade", 61, 64),
        L("Class: WAR CLR PAL RNG SHD BRD ROG SHM BER", 61, 78),
        L("Race: ALL", 60, 95),
        L("Wrist", 61, 113),
        L("Merge", 19, 142),
        L("Place", 73, 140),
        L("Tier6 42/64", 117, 138),
        L("Item", 26, 158),
        L("Item", 74, 156),
        L("This item can be upgraded.", 118, 164),
        L("Size:", 10, 191),
        L("SMALL", 82, 191),
        L("AC:", 141, 193),
        L("15", 228, 193),
        L("Weight.", 10, 206),
        L("1.2", 105, 209),
        L("Strength:", 12, 241),
        L("9", 95, 242),
        L("SV. Magic:", 141, 239),
        L("13", 229, 241),
        L("Dexterity.", 13, 256),
        L("9", 95, 257),
        L("SV. Fire:", 142, 256),
        L("13", 230, 257),
        L("SV. Cold:", 142, 272),
        L("13", 229, 272),
        L("SV. Void:", 141, 288),
        L("6", 234, 289),
        L("Modified", 37, 322),
        L("Lustrous Russet Bracer +6", 129, 322),
        L("Omamentation: empty", 42, 351),
        L("Focus Exaltation: Runed Mithril Bracer (Exaltation)", 40, 372),
        L("Click Exaltation: empty", 40, 397),
        L("Wom Exaltation: empty", 40, 419),
        L("Proc Exaltation: empty", 39, 442),
        L("Focus Effect Reagent Conservation II", 12, 469),
    ];

    [Fact]
    public void Parse_RealBracerCapture_ExtractsCoreFields()
    {
        ParsedItem item = ItemParser.Parse(LustrousBracerLines);

        Assert.Equal("Lustrous Russet Bracer", item.Name);
        Assert.Equal(6, item.Level);
        Assert.False(item.TitleContentNameMismatch);
        Assert.Contains("No Trade", item.Flags);
        Assert.Contains("WAR", item.Classes);
        Assert.Contains("BER", item.Classes);
        Assert.Contains("ALL", item.Races);
        Assert.Equal("Wrist", item.Slot);
    }

    [Fact]
    public void Parse_RealBracerCapture_PairsSplitStatFragmentsAcrossTwoColumns()
    {
        ParsedItem item = ItemParser.Parse(LustrousBracerLines);
        var stats = item.Stats.ToDictionary(kv => kv.Key, kv => kv.Value);

        Assert.Equal("SMALL", stats["Size"]);
        Assert.Equal("15", stats["AC"]);
        Assert.Equal("1.2", stats["Weight"]); // "Weight." period, not colon — still corrected
        Assert.Equal("9", stats["Strength"]);
        Assert.Equal("13", stats["SV. Magic"]);
        Assert.Equal("9", stats["Dexterity"]); // "Dexterity." period, not colon
    }

    [Fact]
    public void Parse_RealBracerCapture_CorrectsKnownExaltationLabelCorruptions()
    {
        ParsedItem item = ItemParser.Parse(LustrousBracerLines);

        Assert.Contains(item.ExaltationSlots, e => e.Kind == ExaltationKind.Ornamentation && e.Name is null);
        Assert.Contains(item.ExaltationSlots, e => e.Kind == ExaltationKind.Worn && e.Name is null);
        Assert.Contains(item.ExaltationSlots, e => e.Kind == ExaltationKind.Click && e.Name is null);
        Assert.Contains(item.ExaltationSlots, e => e.Kind == ExaltationKind.Proc && e.Name is null);
    }

    [Fact]
    public void Parse_RealBracerCapture_ForeignExaltationDetected()
    {
        ParsedItem item = ItemParser.Parse(LustrousBracerLines);
        ExaltationSlot focus = item.ExaltationSlots.Single(e => e.Kind == ExaltationKind.Focus);

        Assert.Equal("Runed Mithril Bracer", focus.Name);
        Assert.True(ItemParser.IsForeignExaltation(focus, item.Name));
    }

    [Fact]
    public void Parse_RealBracerCapture_ExtractsFocusEffect()
    {
        ParsedItem item = ItemParser.Parse(LustrousBracerLines);
        EffectEntry effect = Assert.Single(item.Effects);

        Assert.Equal("Focus", effect.Kind);
        Assert.Equal("Reagent Conservation II", effect.Description);
    }

    // Verbatim real capture of "Bladestopper +7" — two effects (Focus, Click), where Cast Time/Required
    // Level/Cooldown belong specifically to the Click Effect (they follow it, not the Focus Effect above it).
    // Also has "SV. Void:" with no adjacent value at all (a real OCR-dropped value, not a grouping bug) and OCR
    // noise "? ×" trailing the title's "(Augmented)" from a nearby checkbox/close icon — confirmed real as the
    // Unicode multiplication sign U+00D7, NOT the ASCII letter 'x' (they're easy to conflate by eye; an earlier
    // version of this fixture used ASCII 'x' and so didn't actually exercise the real bug).
    private static readonly OcrLine[] BladestopperLines =
    [
        L("Bladestopper +7 (Augmented)", 125, 0),
        L("? ×", 378, 0),
        L("Description", 170, 18),
        L("Bladestopper +7", 61, 48),
        L("Lore Equipped, No Trade, Placeable", 62, 66),
        L("ClasS: WAR CLR PAL RNG SHD BRD ROG SHM", 63, 82),
        L("Race: ALL", 63, 97),
        L("Secondary", 62, 113),
        L("Merge", 20, 143),
        L("Place", 74, 142),
        L("Tier 7", 117, 139),
        L("61/128", 160, 139),
        L("Item", 26, 158),
        L("Item", 76, 157),
        L("This item can be upgraded.", 116, 162),
        L("Size:", 11, 193),
        L("MEDIUM", 74, 193),
        L("AC:", 141, 193),
        L("43", 227, 192),
        L("Weight.", 12, 208),
        L("2.4", 105, 209),
        L("HP:", 140, 208),
        L("87", 228, 209),
        L("Type:", 11, 224),
        L("Shield", 88, 224),
        L("Stamina:", 12, 257),
        L("26", 89, 257),
        L("SV. Void:", 141, 257),
        L("Modified", 37, 290),
        L("Bladestopper +7", 129, 290),
        L("Omamentation: empty", 41, 320),
        L("Focus Exaltation: Idol of the Underking (Exaltation)", 41, 342),
        L("Click Exaltation: Bladestopper (Exaltation)", 41, 365),
        L("Womn Exaltation: empty", 40, 387),
        L("Proc Exaltation: empty", 40, 411),
        L("Focus Effect Improved Healing III", 12, 437),
        L("Click Effect: Rune IV (Must Equip)", 13, 449),
        L("Cast Time: Instant", 24, 465),
        L("Required Level: 40", 24, 477),
        L("Cooldown: 600 seconds", 24, 493),
    ];

    [Fact]
    public void Parse_RealBladestopperCapture_TitleTrailingOcrNoiseDoesNotCorruptNameOrLevel()
    {
        // Regression: an earlier version required "+X" to be the literal end of the title string, so the real
        // "? ×" noise after "(Augmented)" corrupted the whole name and hid the level (came back as +0).
        ParsedItem item = ItemParser.Parse(BladestopperLines);

        Assert.Equal("Bladestopper", item.Name);
        Assert.Equal(7, item.Level);
        Assert.False(item.TitleContentNameMismatch);
    }

    [Fact]
    public void Parse_RealCapture_TitleNoiseWithNoLevelOrAugmentedAnchor_StillStripped()
    {
        // Regression: verbatim real capture of "Drake-Hide Mask" (a +0, non-augmented item — no "+X" and no
        // "(Augmented)" for the noise-strip to anchor on). An earlier version only stripped this trailing
        // checkbox/close-icon noise relative to those tokens, so a plain level-0 title like this one passed
        // through with the junk still attached, corrupting the name and wrongly flagging a name mismatch against
        // the (clean) content-area name.
        OcrLine[] lines =
        [
            L("Drake-Hide Mask", 211, 0),
            L("? ×", 428, 0),
            L("Description", 221, 19),
            L("Drake-Hide Mask", 114, 51),
            L("Lore Equipped, No Trade", 113, 67),
            L("Class: DRU", 113, 80),
            L("Race: ALL", 113, 96),
        ];

        ParsedItem item = ItemParser.Parse(lines);
        Assert.Equal("Drake-Hide Mask", item.Name);
        Assert.Equal(0, item.Level);
        Assert.False(item.TitleContentNameMismatch);
    }

    [Fact]
    public void Parse_RealBladestopperCapture_EffectModifiersAttachToTheCorrectEffectNotStats()
    {
        ParsedItem item = ItemParser.Parse(BladestopperLines);

        EffectEntry focusEffect = item.Effects.Single(e => e.Kind == "Focus");
        Assert.Empty(focusEffect.Modifiers);

        EffectEntry clickEffect = item.Effects.Single(e => e.Kind == "Click");
        var modifiers = clickEffect.Modifiers.ToDictionary(m => m.Key, m => m.Value);
        Assert.Equal("Instant", modifiers["Cast Time"]);
        Assert.Equal("40", modifiers["Required Level"]);
        Assert.Equal("600 seconds", modifiers["Cooldown"]);

        // These must NOT also appear in the generic stats bag.
        Assert.DoesNotContain(item.Stats, kv => kv.Key is "Cast Time" or "Required Level" or "Cooldown");
    }

    [Fact]
    public void Parse_RealBladestopperCapture_OrphanedLabelWithDroppedValueIsWarnedNotMispaired()
    {
        ParsedItem item = ItemParser.Parse(BladestopperLines);

        // "SV. Void:" has no adjacent value fragment in the real capture (OCR dropped it) — must be flagged,
        // never silently paired with an unrelated neighboring label/value.
        Assert.DoesNotContain(item.Stats, kv => kv.Key == "SV. Void");
        Assert.Contains(item.Warnings, w => w.Contains("SV. Void"));
    }

    [Fact]
    public void IsForeignExaltation_NameMatchesItemBaseName_IsNative()
    {
        var native = new ExaltationSlot(ExaltationKind.Focus, "Bloodmoon");
        Assert.False(ItemParser.IsForeignExaltation(native, "Bloodmoon"));
    }

    [Fact]
    public void IsForeignExaltation_EmptySlot_IsNeverForeign()
    {
        var empty = new ExaltationSlot(ExaltationKind.Focus, null);
        Assert.False(ItemParser.IsForeignExaltation(empty, "Anything"));
    }

    [Fact]
    public void Parse_TitleAndContentNamesReconcileWithMinorOcrNoise_NoMismatchFlagged()
    {
        OcrLine[] lines =
        [
            L("Watar Flask", 171, 0), // single-char OCR noise in the title only
            L("Description", 164, 17),
            L("Water Flask", 62, 48),
            L("Quest", 63, 64),
            L("Class: ALL", 61, 79),
            L("Race: ALL", 61, 96),
        ];

        ParsedItem item = ItemParser.Parse(lines);
        Assert.False(item.TitleContentNameMismatch);
        Assert.Equal("Water Flask", item.Name);
    }

    [Fact]
    public void Parse_TitleTruncatedByPartialOcclusion_MismatchFlagged()
    {
        // Reproduces the plan's documented residual Locate gap: a small occluder truncates only the title bar,
        // geometry reports clean bounds, and Parse's title-vs-content reconciliation must be what catches it.
        OcrLine[] lines =
        [
            L("s Russet Bracer +6 (Augmented)", 96, 0),
            L("Description", 170, 18),
            L("Lustrous Russet Bracer +6", 62, 49),
            L("No Trade", 61, 64),
            L("Class: WAR", 61, 78),
            L("Race: ALL", 60, 95),
        ];

        ParsedItem item = ItemParser.Parse(lines);
        Assert.True(item.TitleContentNameMismatch);
        Assert.Contains(item.Warnings, w => w.Contains("possible partial occlusion"));
    }

    [Fact]
    public void Parse_ConsumableWithNoSlotOrExaltations_LeavesSlotNull()
    {
        OcrLine[] lines =
        [
            L("Water Flask", 171, 0),
            L("Description", 164, 17),
            L("Water Flask", 62, 48),
            L("Quest", 63, 64),
            L("Class: ALL", 61, 79),
            L("Race: ALL", 61, 96),
            L("Size:", 11, 128),
            L("SMALL", 80, 123),
            L("Weight:", 10, 143),
            L("0.4", 105, 143),
            L("Modified", 37, 192),
            L("Value: 1 silver", 11, 216),
        ];

        ParsedItem item = ItemParser.Parse(lines);
        Assert.Null(item.Slot);
        Assert.Equal("1 silver", item.MerchantValue);
        Assert.Empty(item.ExaltationSlots);
    }

    // --- Golden tests against real samples (see ItemWindowLocatorTests for the skip-if-missing rationale) ---

    private async Task<(CapturedImage Image, IOcrEngine Engine)?> Load(string fileName)
    {
        string path = Path.Combine(RepoPaths.SamplesDirectory, fileName);
        if (!File.Exists(path))
        {
            _output.WriteLine($"Skipping: {path} not present (samples/ is gitignored, personal data).");
            return null;
        }
        return (await ImageFile.LoadAsync(path), new RapidOcrEngine());
    }

    [Fact]
    public async Task Parse_ThreeAdjacentWindows_AllThreeParseWithCorrectNamesAndForeignExaltations()
    {
        if (await Load("screen capture 3 item windows.png") is not { } l) return;
        using (l.Engine as IDisposable)
        {
            IReadOnlyList<LocatedWindow> windows = await ItemWindowLocator.LocateAsync(l.Image, l.Engine);
            Assert.All(windows, w => Assert.False(w.PossiblyOccluded));

            var items = windows.Select(w => ItemParser.Parse(w.Lines)).ToList();
            foreach (var item in items)
                _output.WriteLine($"{item.Name} +{item.Level} mismatch={item.TitleContentNameMismatch}");

            ParsedItem bracer = items.Single(i => i.Name.Contains("Lustrous"));
            Assert.Equal(6, bracer.Level);
            Assert.False(bracer.TitleContentNameMismatch);
            Assert.True(ItemParser.IsForeignExaltation(
                bracer.ExaltationSlots.Single(e => e.Kind == ExaltationKind.Focus), bracer.Name));

            ParsedItem bloodmoon = items.Single(i => i.Name.Contains("Bloodmoon"));
            Assert.Equal(10, bloodmoon.Level);
            // Bloodmoon's own Focus Exaltation is itself ("Bloodmoon") — native, not foreign.
            Assert.False(ItemParser.IsForeignExaltation(
                bloodmoon.ExaltationSlots.Single(e => e.Kind == ExaltationKind.Focus), bloodmoon.Name));
            // Its Click/Proc exaltations are genuinely foreign items.
            Assert.True(ItemParser.IsForeignExaltation(
                bloodmoon.ExaltationSlots.Single(e => e.Kind == ExaltationKind.Click), bloodmoon.Name));
        }
    }

    [Fact]
    public async Task Parse_SimpleConsumable_ExtractsNameFlagsAndMerchantValue()
    {
        if (await Load("simple 1 item.jpg") is not { } l) return;
        using (l.Engine as IDisposable)
        {
            IReadOnlyList<LocatedWindow> windows = await ItemWindowLocator.LocateAsync(l.Image, l.Engine);
            Assert.Single(windows);

            ParsedItem item = ItemParser.Parse(windows[0].Lines);
            Assert.Equal("Water Flask", item.Name);
            Assert.Equal(0, item.Level);
            Assert.False(item.TitleContentNameMismatch);
            Assert.Contains("Quest", item.Flags);
            Assert.Equal("1 silver", item.MerchantValue);
        }
    }

    [Fact]
    public async Task Parse_WindowDirectlyAgainstAnotherWindow_IsIsolatedWithNoBleedIn()
    {
        if (await Load("1 item occluded by another.png") is not { } l) return;
        using (l.Engine as IDisposable)
        {
            IReadOnlyList<LocatedWindow> windows = await ItemWindowLocator.LocateAsync(l.Image, l.Engine);
            LocatedWindow window = Assert.Single(windows);
            Assert.False(window.PossiblyOccluded);

            // This sample is the one that drove the switch to tracing the content outline. The item window here
            // sits directly on top of an unrelated "Fish Rolls" window, and the previous brightness-transition
            // tracer ran ~137px past the real left edge into it, so the neighbour's own Class:/Race:/Size:/
            // Weight:/Value: rows were parsed as if they belonged to this item. (It also produced a nonsense
            // title, which is where the since-retired "partial title occlusion" note came from — the title was
            // never occluded, the bounds were just wrong.) Tracing the outline isolates the window exactly.
            Assert.InRange(window.Bounds.Width, 380, 430);

            ParsedItem item = ItemParser.Parse(window.Lines);
            Assert.Equal("Lustrous Russet Bracer", item.Name);
            Assert.Equal(6, item.Level);
            Assert.False(item.TitleContentNameMismatch);
            Assert.Empty(item.Warnings);

            // Nothing from the neighbouring window may appear, and each labelled field may appear exactly once.
            Assert.DoesNotContain(window.Lines, x => x.Text.Contains("Fish Rolls"));
            Assert.Single(item.Stats, kv => kv.Key == "Size");
            Assert.Single(item.Stats, kv => kv.Key == "Weight");
        }
    }
}
