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
        Assert.Equal(["Wrist"], item.Slots);
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
        Assert.Equal("Reagent Conservation II", effect.Name);
        Assert.Empty(effect.Conditions);
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
    public void Parse_PunctuationOnlyJunkRow_DoesNotShiftTheHeader()
    {
        // Verbatim from a real capture: the window's own tab-bar corner, clipped at the crop's left edge, was
        // recognized as "()" and landed on its own row between the tab row and the content-area name. Because
        // the header is positional, that made the name parse as "()", pushed the real name row into the flags
        // field, and tripped the title-vs-content occlusion check on a completely clean capture.
        OcrLine[] lines =
        [
            L("Kavruul's Mystic Pouch", 134, 0),
            L("Description", 159, 18),
            L("()", 0, 36),
            L("Kavruul's Mystic Pouch", 58, 49),
            L("Class: ALL", 56, 79),
            L("Race: ALL", 55, 94),
            L("Size:", 6, 126),
            L("SMALL", 78, 126),
        ];

        ParsedItem item = ItemParser.Parse(lines);

        Assert.Equal("Kavruul's Mystic Pouch", item.Name);
        Assert.False(item.TitleContentNameMismatch);
        Assert.Empty(item.Flags);
        Assert.Equal(["ALL"], item.Classes);
        Assert.Contains(item.Stats, kv => kv.Key == "Size" && kv.Value == "SMALL");
    }

    [Fact]
    public void Parse_LongClassListWrappingToASecondRow_AbsorbsTheContinuation()
    {
        // Verbatim from a real capture: a class list too long for one row wraps onto an unlabeled second row.
        // That shifted the whole positional header by one — the class list came back truncated, races empty, and
        // the continuation row was consumed as the item's slot.
        OcrLine[] lines =
        [
            L("Turmoil Warts +5", 155, 0),
            L("Description", 163, 17),
            L("Turmoil Warts +5", 59, 50),
            L("Attunable, Quest, Placeable", 56, 65),
            L("Class: WAR RNG SHD MNK BRD ROG NEC WIZ MAG", 56, 81),
            L("ENC BST BER", 56, 97),
            L("Race: ALL", 54, 111),
            L("Range Ammo", 56, 129),
        ];

        ParsedItem item = ItemParser.Parse(lines);

        Assert.Equal(12, item.Classes.Count);
        Assert.Contains("WAR", item.Classes);
        Assert.Contains("BER", item.Classes); // from the wrapped row
        Assert.Equal(["ALL"], item.Races);
        Assert.Equal(["Range", "Ammo"], item.Slots); // mixed case, so never mistaken for a class continuation
    }

    [Theory]
    [InlineData("Worn Effect Enduring Breath", "Worn", "Enduring Breath")]
    [InlineData("Consumable Effect Flurry", "Consumable", "Flurry")]
    [InlineData("Charge Effect Word of Healing", "Charge", "Word of Healing")]
    public void Parse_EffectKind_IsRecognized(string effectLine, string expectedKind, string expectedName)
    {
        OcrLine[] lines =
        [
            L("Some Item", 100, 0),
            L("Description", 160, 17),
            L("Some Item", 60, 50),
            L("No Trade", 60, 65),
            L("Class: ALL", 60, 80),
            L("Race: ALL", 60, 96),
            L(effectLine, 10, 200),
        ];

        EffectEntry effect = Assert.Single(ItemParser.Parse(lines).Effects);
        Assert.Equal(expectedKind, effect.Kind);
        Assert.Equal(expectedName, effect.Name);
    }

    [Fact]
    public void Parse_LoreTabActive_TakesNameFromTitleBarAndCapturesTheLore()
    {
        // Verbatim from a real capture. The Lore view is a different layout, not a variant of the Description
        // one: no repeated content-area name, no stat block. So the name can only come from the title bar, and
        // there is nothing to reconcile it against.
        OcrLine[] lines =
        [
            L("Chilled Tundra Root", 139, 0),
            L("Description", 63, 17),
            L("Lore", 270, 18),
            L("Root frozen rock hard by the tundra", 0, 41),
        ];

        ParsedItem item = ItemParser.Parse(lines, ItemWindowTab.Lore);

        Assert.Equal("Chilled Tundra Root", item.Name);
        Assert.Equal("Root frozen rock hard by the tundra", item.Lore);
        Assert.False(item.TitleContentNameMismatch);
        Assert.Empty(item.Stats);
        Assert.Empty(item.Classes);
        Assert.Empty(item.Warnings);
    }

    [Fact]
    public void Parse_LoreTabActive_JoinsLoreWrappedAcrossRows()
    {
        // A long lore string wraps purely to fit the window, so the breaks aren't part of the text.
        OcrLine[] lines =
        [
            L("Some Item", 139, 0),
            L("Description", 63, 17),
            L("Lore", 270, 18),
            L("A blade forged in the deeps, said to have", 0, 41),
            L("drunk deeply of its maker's own regrets.", 0, 57),
        ];

        ParsedItem item = ItemParser.Parse(lines, ItemWindowTab.Lore);

        Assert.Equal("A blade forged in the deeps, said to have drunk deeply of its maker's own regrets.", item.Lore);
    }

    [Fact]
    public void Parse_DescriptionTab_LeavesLoreUnset()
    {
        Assert.Null(ItemParser.Parse(LustrousBracerLines).Lore);
    }

    [Theory]
    // A click effect carries its required level on its own sub-line below …
    [InlineData("Click Effect: Rune IV (Must Equip)", "Rune IV", "Must Equip")]
    [InlineData("Click Effect Haste (Can Equip)", "Haste", "Can Equip")]
    public void Parse_EffectConditions_AreSeparatedFromTheName(string effectLine, string expectedName, string expectedCondition)
    {
        OcrLine[] lines =
        [
            L("Some Item", 100, 0), L("Description", 160, 17), L("Some Item", 60, 50),
            L("No Trade", 60, 65), L("Class: ALL", 60, 80), L("Race: ALL", 60, 96),
            L(effectLine, 10, 200),
        ];

        EffectEntry effect = Assert.Single(ItemParser.Parse(lines).Effects);
        Assert.Equal(expectedName, effect.Name);
        Assert.Equal([expectedCondition], effect.Conditions);
    }

    [Fact]
    public void Parse_RequiredLevel_IsNormalizedAcrossBothFormsTheGameUses()
    {
        // … while a proc/combat effect folds it into the parenthetical instead. Both must end up in the same
        // place, so a consumer never has to know which style the game happened to use for a given effect.
        OcrLine[] lines =
        [
            L("Some Item", 100, 0), L("Description", 160, 17), L("Some Item", 60, 50),
            L("No Trade", 60, 65), L("Class: ALL", 60, 80), L("Race: ALL", 60, 96),
            L("Click Effect: Rune IV (Must Equip)", 10, 200),
            L("Required Level: 40", 24, 216),
            L("Combat Effect: Ykesha (Req Level 37)", 10, 240),
        ];

        var effects = ItemParser.Parse(lines).Effects;

        EffectEntry click = effects.Single(e => e.Kind == "Click");
        Assert.Equal("Rune IV", click.Name);
        Assert.Equal(["Must Equip"], click.Conditions);
        Assert.Contains(click.Modifiers, m => m.Key == "Required Level" && m.Value == "40");

        EffectEntry combat = effects.Single(e => e.Kind == "Combat");
        Assert.Equal("Ykesha", combat.Name);
        Assert.Empty(combat.Conditions); // hoisted out of the parenthetical, not left as a condition
        Assert.Contains(combat.Modifiers, m => m.Key == "Required Level" && m.Value == "37");
    }

    [Fact]
    public void Parse_LabelSeparatedByDotInsteadOfColon_IsStillPaired()
    {
        // OCR renders the separator as '.' on this UI often enough to matter ("Accuracy. +13.6%",
        // "Container. CLOSED."). Splitting on '.' unconditionally would cut decimal values in half, so it only
        // applies when the text before the dot is a label the lexicon knows — asserted by the Ratio case below,
        // where the value itself contains a dot.
        OcrLine[] lines =
        [
            L("Some Item", 100, 0),
            L("Description", 160, 17),
            L("Some Item", 60, 50),
            L("No Trade", 60, 65),
            L("Class: ALL", 60, 80),
            L("Race: ALL", 60, 96),
            L("Accuracy. +13.6%", 135, 159),
            L("Container. CLOSED.", 10, 180),
            L("Ratio: 0.542", 255, 200),
        ];

        var stats = ItemParser.Parse(lines).Stats.ToDictionary(kv => kv.Key, kv => kv.Value);
        Assert.Equal("+13.6%", stats["Accuracy"]);
        Assert.Equal("CLOSED.", stats["Container"]);
        Assert.Equal("0.542", stats["Ratio"]); // the decimal survives, not cut at its own dot
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
        Assert.Empty(item.Slots);
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
    public async Task Parse_ThreeSeparateWindows_AllParseWithTheirOwnNames()
    {
        if (await Load("07-three-distinct-items.png") is not { } l) return;
        using (l.Engine as IDisposable)
        {
            IReadOnlyList<LocatedWindow> windows = await ItemWindowLocator.LocateAsync(l.Image, l.Engine);
            Assert.All(windows, w => Assert.False(w.PossiblyOccluded));

            var items = windows.Select(w => ItemParser.Parse(w.Lines)).ToList();
            foreach (ParsedItem item in items)
                _output.WriteLine($"{item.Name} +{item.Level} mismatch={item.TitleContentNameMismatch}");

            Assert.Equal(3, items.Count);
            Assert.All(items, i => Assert.False(i.TitleContentNameMismatch));
            Assert.Single(items, i => i.Name == "Rod of the Protecting Winds");
            Assert.Single(items, i => i.Name == "Glassy Gauntlets");
            Assert.Single(items, i => i.Name == "Fruit");
        }
    }

    [Fact]
    public async Task Parse_SimpleConsumable_ExtractsNameFlagsAndMerchantValue()
    {
        if (await Load("03-single-item-noisy-background.png") is not { } l) return;
        using (l.Engine as IDisposable)
        {
            IReadOnlyList<LocatedWindow> windows = await ItemWindowLocator.LocateAsync(l.Image, l.Engine);
            LocatedWindow window = Assert.Single(windows);

            ParsedItem item = ItemParser.Parse(window.Lines);
            Assert.Equal("Water Flask", item.Name);
            Assert.Equal(0, item.Level);
            Assert.False(item.TitleContentNameMismatch);
            Assert.Contains("Quest", item.Flags);
            Assert.Equal("1 silver", item.MerchantValue);
            Assert.Empty(item.Slots); // a consumable has no slot row at all
            Assert.Empty(item.Warnings);
        }
    }

    [Fact]
    public async Task Parse_LeveledItem_ExtractsBaseNameAndLevel()
    {
        if (await Load("11-one-item-over-health-bar.png") is not { } l) return;
        using (l.Engine as IDisposable)
        {
            IReadOnlyList<LocatedWindow> windows = await ItemWindowLocator.LocateAsync(l.Image, l.Engine);
            LocatedWindow window = Assert.Single(windows);

            ParsedItem item = ItemParser.Parse(window.Lines);

            // The "+X" suffix is stripped from the name and kept separately — v1 only fully processes +0 items,
            // so the eligibility step needs the level, and the ledger/wiki lookup needs the base name.
            Assert.Equal("Crimson Ring of the Djinni", item.Name);
            Assert.Equal(6, item.Level);
            Assert.False(item.TitleContentNameMismatch);
            Assert.Equal(["Fingers"], item.Slots);
            Assert.Contains(item.Stats, kv => kv.Key == "AC" && kv.Value == "14");
        }
    }

    [Fact]
    public async Task Parse_WindowFlushAgainstOtherUi_IsIsolatedWithNoBleedIn()
    {
        if (await Load("04-single-item-over-bags-flush-with-inventory.png") is not { } l) return;
        using (l.Engine as IDisposable)
        {
            IReadOnlyList<LocatedWindow> windows = await ItemWindowLocator.LocateAsync(l.Image, l.Engine);
            LocatedWindow window = Assert.Single(windows);
            Assert.False(window.PossiblyOccluded);

            // This window is edge-to-edge with the inventory panel and has other dark UI directly above its
            // title bar. An earlier tracer ran past the real edge into neighbouring windows entirely (one sample
            // was traced 137px too far, parsing the neighbour's Class/Race/Size/Weight as this item's), so the
            // width bound and the single-occurrence field checks below are what catch that class of bug.
            Assert.InRange(window.Bounds.Width, 380, 430);

            ParsedItem item = ItemParser.Parse(window.Lines);
            Assert.Equal("Dark Cloak of the Sky", item.Name);
            Assert.False(item.TitleContentNameMismatch);
            Assert.Equal(["Back"], item.Slots);
            Assert.Single(item.Stats, kv => kv.Key == "Size");
            Assert.Single(item.Stats, kv => kv.Key == "Weight");
            Assert.Single(item.Classes);
        }
    }
}
