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

    /// <summary>Charge and Consumable have no agreed token, so the renderer refuses rather than picking one. Writing
    /// a line without the kind would misrepresent when the effect applies.</summary>
    [Theory]
    [InlineData("Charge")]
    [InlineData("Consumable")]
    public void AnEffectKindWithNoAgreedTokenIsRefused(string kind)
    {
        EffectRender render = EffectLine.Render(Effect(kind, "Word of Healing"));

        Assert.False(render.IsComplete);
        Assert.Contains(render.Unsupported, u => u.Contains(kind) && u.Contains("no agreed token"));
    }

    /// <summary>A modifier with no place in the convention is reported, so the line is never written having quietly
    /// lost it. Cooldown is the real case: 10 corpus effects have one and the template's parenthetical does not
    /// mention it.</summary>
    [Theory]
    [InlineData("Cooldown", "240 seconds")]
    [InlineData("Cooldown Group", "Rune")]
    public void AModifierWithNoWikiRepresentationIsReported(string label, string value)
    {
        EffectRender render = EffectLine.Render(Effect("Combat", "Rune IV", modifiers: [new(label, value)]));

        Assert.False(render.IsComplete);
        Assert.Contains(render.Unsupported, u => u.Contains(label));
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
