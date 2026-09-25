using EQLWikiAssistant.Core.Items;
using EQLWikiAssistant.Wiki.Wikitext;

namespace EQLWikiAssistant.Tests.Wiki;

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

    [Theory]
    [InlineData("[[Burn|<span class='itemeff'>Burn</span>]] (Combat) at Level 10", "Burn")]
    [InlineData("[[Enduring Breath]] (Worn)", "Enduring Breath")]                      // legacy bare link
    [InlineData("[[Cold Awareness II (Spell)|Cold Awareness II]] (Any Slot)", "Cold Awareness II (Spell)")]
    [InlineData(" [[Null Aura]] (Any Slot, Casting Time: 4.0)", "Null Aura")]
    [InlineData("no link at all", null)]
    [InlineData("[[unterminated", null)]
    public void TryReadName_FindsTheLinkTargetHoweverItIsWritten(string value, string? expected) =>
        Assert.Equal(expected, EffectLine.TryReadName(value));

    /// <summary>Distinguishing the two link forms is what separates a functional fix from a cosmetic one.</summary>
    [Theory]
    [InlineData("[[Burn|<span class='itemeff'>Burn</span>]] (Combat)", true)]
    [InlineData("[[Burn|<span class=\"itemeff\">Burn</span>]] (Combat)", true)]
    [InlineData("[[Burn]] (Combat)", false)]
    [InlineData("[[Burn|Burn]] (Combat)", false)]
    public void HasTooltipLink_DetectsTheModernForm(string value, bool expected) =>
        Assert.Equal(expected, EffectLine.HasTooltipLink(value));
}
