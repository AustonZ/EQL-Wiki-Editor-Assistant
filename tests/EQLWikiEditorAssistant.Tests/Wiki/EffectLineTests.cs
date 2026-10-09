using EQLWikiEditorAssistant.Core.Items;
using EQLWikiEditorAssistant.Wiki.Wikitext;

namespace EQLWikiEditorAssistant.Tests.Wiki;

/// <summary>
/// The four worked examples the user gave (2026-09-25) are the first four tests, verbatim. They are the contract.
/// </summary>
public class EffectLineTests
{
    private static EffectEntry Effect(
        string kind,
        string name,
        IReadOnlyList<string>? conditions = null,
        IReadOnlyList<KeyValuePair<string, string>>? modifiers = null) =>
        new(kind, name, conditions ?? [], modifiers ?? []);

    /// <summary>"A combat proc: Effect: [[Burn|&lt;span class='itemeff'&gt;Burn&lt;/span&gt;]] (Combat) at Level 10"</summary>
    [Fact]
    public void ACombatProcRendersTheUsersExample() =>
        Assert.Equal(
            "Effect: [[Burn|<span class='itemeff'>Burn</span>]] (Combat) at Level 10",
            EffectLine.Render(Effect("Combat", "Burn", modifiers: [new("Required Level", "10")])).Line);

    /// <summary>"A click effect with 'Can Equip' and no req level"</summary>
    [Fact]
    public void AClickEffectRendersTheUsersExample() =>
        Assert.Equal(
            "Effect: [[Haste|<span class='itemeff'>Haste</span>]] (Clicky, Can Equip, Casting Time: Instant)",
            EffectLine.Render(Effect("Click", "Haste", ["Can Equip"], [new("Cast Time", "Instant")])).Line);

    /// <summary>"A worn effect"</summary>
    [Fact]
    public void AWornEffectRendersTheUsersExample() =>
        Assert.Equal(
            "Effect: [[See Invisible|<span class='itemeff'>See Invisible</span>]] (Worn)",
            EffectLine.Render(Effect("Worn", "See Invisible")).Line);

    /// <summary>A focus effect has no line at all — the wiki gives it its own template parameter.</summary>
    [Fact]
    public void AFocusEffectIsNotALine()
    {
        EffectRender render = EffectLine.Render(Effect("Focus", "Improved Vampirism III"));

        Assert.Null(render.Line);
        Assert.False(render.IsComplete);
        Assert.Contains("focus_effect", render.Unsupported[0]);
    }

    /// <summary>Both halves of the link are the name, and the span is what carries the tooltip.</summary>
    [Fact]
    public void TheLinkNamesTheEffectTwice() =>
        Assert.Equal("[[Rune IV|<span class='itemeff'>Rune IV</span>]]", EffectLine.Link("Rune IV"));

    [Fact]
    public void EveryApplicablePartIsCommaSeparatedInOrder() =>
        Assert.Equal(
            "Effect: [[Ykesha|<span class='itemeff'>Ykesha</span>]] (Combat, Must Equip, Casting Time: Instant) at Level 37",
            EffectLine.Render(Effect("Combat", "Ykesha", ["Must Equip"], [new("Cast Time", "Instant"), new("Required Level", "37")])).Line);

    /// <summary>An effect with nothing applicable gets no empty parentheses.</summary>
    [Fact]
    public void AnEffectWithNoApplicablePartsHasNoParentheses() =>
        Assert.Equal(
            "Effect: [[Mystery|<span class='itemeff'>Mystery</span>]] (Worn)",
            EffectLine.Render(Effect("Worn", "Mystery")).Line);

    /// <summary>Charge and Consumable have no template wording of their own; the user's interim choice (2026-09-25)
    /// is to treat both as clickies until the community formalizes it.</summary>
    [Theory]
    [InlineData("Charge", "Charge Clicky")]
    [InlineData("Consumable", "Consumable Clicky")]
    public void ChargeAndConsumableRenderAsClickies(string kind, string token) =>
        Assert.Equal(
            $"Effect: [[Word of Healing|<span class='itemeff'>Word of Healing</span>]] ({token})",
            EffectLine.Render(Effect(kind, "Word of Healing")).Line);

    /// <summary>Cooldowns go at the end of the parenthetical (user, 2026-09-25), which is also where the one real
    /// page carrying one puts it: `Alter Plane: Sky` reads "(Any Slot/Can Equip, Casting Time: Instant,
    /// Cooldown: 120 seconds) at Level 45".</summary>
    [Fact]
    public void CooldownsGoAtTheEndOfTheParenthetical() =>
        Assert.Equal(
            "Effect: [[Rune IV|<span class='itemeff'>Rune IV</span>]] " +
            "(Clicky, Can Equip, Casting Time: Instant, Cooldown: 240 seconds, Cooldown Group: Rune) at Level 45",
            EffectLine.Render(Effect("Click", "Rune IV", ["Can Equip"],
            [
                new("Cooldown Group", "Rune"),
                new("Required Level", "45"),
                new("Cast Time", "Instant"),
                new("Cooldown", "240 seconds"),
            ])).Line);

    /// <summary>Modifier order in the rendered line does not depend on the order the window happened to list them,
    /// which the test above already relies on — the input there is deliberately scrambled.</summary>
    [Fact]
    public void ModifierOrderDoesNotDependOnTheCaptureOrder()
    {
        string? forward = EffectLine.Render(Effect("Click", "X", [],
            [new("Cast Time", "Instant"), new("Cooldown", "10 seconds")])).Line;
        string? reversed = EffectLine.Render(Effect("Click", "X", [],
            [new("Cooldown", "10 seconds"), new("Cast Time", "Instant")])).Line;

        Assert.Equal(forward, reversed);
    }

    /// <summary>A modifier with no place at all in the convention is still reported, so a line is never written
    /// having quietly lost one.</summary>
    [Fact]
    public void AModifierWithNoWikiRepresentationIsReported()
    {
        EffectRender render = EffectLine.Render(
            Effect("Combat", "Rune IV", modifiers: [new("Charges Remaining", "3")]));

        Assert.False(render.IsComplete);
        Assert.Contains(render.Unsupported, u => u.Contains("Charges Remaining"));
    }

    /// <summary>The wiki writes a cast time as a bare number; the game says "12.0 seconds". Measured across the
    /// corpus, no page carries a unit. Found by the live corpus run, which flagged `Careless Lightning` as differing
    /// when only the unit did.</summary>
    [Theory]
    [InlineData("12.0 seconds", "Casting Time: 12.0")]
    [InlineData("4.0 seconds", "Casting Time: 4.0")]
    [InlineData("2.0 sec", "Casting Time: 2.0")]
    [InlineData("4.0", "Casting Time: 4.0")]
    [InlineData("Instant", "Casting Time: Instant")]
    public void ACastTimeIsWrittenWithoutItsUnit(string captured, string expected) =>
        Assert.Contains(expected, EffectLine.Render(Effect("Worn", "X", modifiers: [new("Cast Time", captured)])).Line);

    /// <summary>
    /// The name an existing line *displays*, which is the only half a capture can compare against — the window
    /// shows `Firestrike`, never the page the editor chose to link.
    ///
    /// The `(Spell)` and `(Effect)` rows are the regression: this used to return the link target, so a page writing
    /// a qualified title matched nothing and the editor added a second effect line beside the one already there
    /// (`Rain Caller`, 2026-10-03). Both fail against the old behaviour.
    /// </summary>
    [Theory]
    [InlineData("[[Burn|<span class='itemeff'>Burn</span>]] (Combat) at Level 10", "Burn")]
    [InlineData("[[Enduring Breath]] (Worn)", "Enduring Breath")]                      // legacy bare link
    [InlineData("[[Cold Awareness II (Spell)|Cold Awareness II]] (Any Slot)", "Cold Awareness II")]
    [InlineData("[[Firestrike_(Effect)|Firestrike]] (Must Equip) at Level 40", "Firestrike")]
    [InlineData(" [[Null Aura]] (Any Slot, Casting Time: 4.0)", "Null Aura")]
    [InlineData("no link at all", null)]
    [InlineData("[[unterminated", null)]
    public void TryReadName_FindsTheDisplayedNameHoweverTheLineIsWritten(string value, string? expected) =>
        Assert.Equal(expected, EffectLine.TryReadName(value));

    /// <summary>The other half, kept apart from the name because it is preserved rather than compared.</summary>
    [Theory]
    [InlineData("[[Firestrike_(Effect)|Firestrike]] (Must Equip)", "Firestrike_(Effect)")]
    [InlineData("[[Burn|<span class='itemeff'>Burn</span>]] (Combat)", "Burn")]
    [InlineData("[[Enduring Breath]] (Worn)", "Enduring Breath")]
    [InlineData("no link at all", null)]
    public void TryReadTarget_FindsThePageTheLineLinksTo(string value, string? expected) =>
        Assert.Equal(expected, EffectLine.TryReadTarget(value));

    /// <summary>An underscore is a space in a MediaWiki title, so the two spellings are one page.</summary>
    [Theory]
    [InlineData("Firestrike_(Effect)", "Firestrike", true)]
    [InlineData("Firestrike", "Firestrike", false)]
    [InlineData("Null_Aura", "Null Aura", false)]
    [InlineData(null, "Firestrike", false)]
    public void PointsElsewhere_AsksWhetherTheTargetIsThisEffectsOwnPage(
        string? target, string name, bool expected) =>
        Assert.Equal(expected, EffectLine.PointsElsewhere(target, name));

    /// <summary>
    /// A preserved target keeps the page's link while the tool still modernizes the markup around it.
    ///
    /// This is the write side of the `Rain Caller` bug: `Firestrike` and `Firestrike (Effect)` are different spells
    /// (422 damage against 302), so normalizing the target would repoint the effect at the wrong numbers with
    /// nothing visible on the rendered page to say so.
    /// </summary>
    [Fact]
    public void APreservedLinkTargetSurvivesTheRewrite() =>
        Assert.Equal(
            "Effect: [[Firestrike_(Effect)|<span class='itemeff'>Firestrike</span>]] (Combat) at Level 40",
            EffectLine.Render(
                Effect("Combat", "Firestrike", modifiers: [new("Required Level", "40")]),
                linkTarget: "Firestrike_(Effect)").Line);

    /// <summary>The control: with no target supplied the name fills both halves, exactly as before.</summary>
    [Fact]
    public void WithNoTargetTheEffectsOwnNameIsLinked() =>
        Assert.Equal(
            "Effect: [[Firestrike|<span class='itemeff'>Firestrike</span>]] (Combat)",
            EffectLine.Render(Effect("Combat", "Firestrike")).Line);

    /// <summary>Distinguishing the two link forms is what separates a functional fix from a cosmetic one.</summary>
    [Theory]
    [InlineData("[[Burn|<span class='itemeff'>Burn</span>]] (Combat)", true)]
    [InlineData("[[Burn|<span class=\"itemeff\">Burn</span>]] (Combat)", true)]
    [InlineData("[[Burn]] (Combat)", false)]
    [InlineData("[[Burn|Burn]] (Combat)", false)]
    public void HasTooltipLink_DetectsTheModernForm(string value, bool expected) =>
        Assert.Equal(expected, EffectLine.HasTooltipLink(value));
}
