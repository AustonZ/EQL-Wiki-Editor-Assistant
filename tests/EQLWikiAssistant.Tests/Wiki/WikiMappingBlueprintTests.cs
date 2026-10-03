using EQLWikiAssistant.Wiki.Formatting;
using EQLWikiAssistant.Wiki.Mapping;

namespace EQLWikiAssistant.Tests.Wiki;

/// <summary>
/// Pins the parts of <see cref="WikiMapping.Default"/> that are copied from the Item Page Blueprint on
/// `Help:Contents`, so a drift between the two is a failing test rather than a wrong edit on a public wiki.
///
/// **Checked against the blueprint's 2026-09-30 revision** (oldid 179818), which is the one that made the sign
/// convention explicit. Before it the blueprint wrote `STR: ?` and this set rested on a frequency census; now it
/// writes `STR: +?` and the two agree. If the blueprint changes again, these are the assertions to re-read it
/// against.
/// </summary>
public class WikiMappingBlueprintTests
{
    /// <summary>
    /// Every attribute and every resist, which the blueprint writes as `+?`.
    ///
    /// Spelled as game labels, because that is how <see cref="WikiMapping.Stats"/> is keyed — the game's
    /// `SV. Fire` against the wiki's `SV Fire`, and its `End`/`Strength` against `END`/`STR`.
    /// </summary>
    private static readonly string[] BlueprintSignsThese =
    [
        "Strength", "Dexterity", "Stamina", "Charisma", "Wisdom", "Intelligence", "Agility", "HP", "Mana", "End",
        "SV. Fire", "SV. Disease", "SV. Cold", "SV. Magic", "SV. Poison", "SV. Void",
    ];

    /// <summary>
    /// The blueprint leaves these plain. `END Regen` is the one worth a test of its own: it sits one line below
    /// `END`, which *is* signed, and the blueprint's 2026-09-30 revision touched both lines at once — signing the
    /// attributes while deliberately leaving this line alone.
    /// </summary>
    private static readonly string[] BlueprintLeavesThesePlain =
    [
        "HP Regen", "Mana Regen", "End Regen", "Haste",
        "AC", "Weight", "Base Dmg", "Dmg Bon", "Backstab Dmg", "Delay", "Range", "Capacity", "Weight Red",
    ];

    [Fact]
    public void EveryAttributeAndResistIsSigned()
    {
        foreach (string gameLabel in BlueprintSignsThese)
        {
            StatMapping? stat = WikiMapping.Default.FindStat(gameLabel);
            Assert.NotNull(stat);
            Assert.True(stat.Signed, $"The blueprint writes {stat.WikiLabel}: +? but the mapping leaves it plain.");
        }
    }

    [Fact]
    public void NothingElseIsSigned()
    {
        foreach (string gameLabel in BlueprintLeavesThesePlain)
        {
            StatMapping? stat = WikiMapping.Default.FindStat(gameLabel);
            Assert.NotNull(stat);
            Assert.False(stat.Signed, $"The blueprint writes {stat.WikiLabel}: ? but the mapping signs it.");
        }
    }

    /// <summary>
    /// The negative control on the two tests above: they would both pass against a mapping that signed *everything*
    /// or *nothing*, so this asserts the split actually falls between the two lists rather than collapsing to one
    /// side. `END` versus `END Regen` is the pair that makes it a real split.
    /// </summary>
    [Fact]
    public void TheSignedSetIsAProperSplitAndNotAllOrNothing()
    {
        IReadOnlyList<StatMapping> signed =
            [.. WikiMapping.Default.Stats.Values.Where(s => s.Signed).DistinctBy(s => s.GameLabel)];

        Assert.Equal(BlueprintSignsThese.Length, signed.Count);
        Assert.True(WikiMapping.Default.FindStat("End")!.Signed);
        Assert.False(WikiMapping.Default.FindStat("End Regen")!.Signed);
    }

    /// <summary>
    /// The blueprint's 2026-09-30 revision removed its `Recommended level of ? Required level of ?` line, so the
    /// recorded line order has no entry for those labels either.
    ///
    /// Safe to drop because neither side ever produces the label: the game has never emitted one across 109
    /// captured windows, and of 1,183 real pages exactly one mentions a required level — as the prose
    /// `Required level of 55.`, which is not `Label: Value` at all. A page that somehow did carry the field is
    /// still not silently dropped; see <see cref="AnUnnamedLabelStillHasAHome"/>.
    /// </summary>
    [Fact]
    public void TheLineOrderHasNoRecommendedOrRequiredLevelEntry()
    {
        IEnumerable<string> allLabels = WikiMapping.Default.StatsBlockLineOrder.SelectMany(line => line);

        Assert.DoesNotContain("Recommended level", allLabels, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("Required level", allLabels, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// What makes removing a line-order entry safe in general, rather than only for this one: a label the order does
    /// not name keeps its own line before `Class:` and is reported, so dropping an entry can lose a field's
    /// *position* but never the field.
    /// </summary>
    [Fact]
    public void AnUnnamedLabelStillHasAHome()
    {
        // `Range` is a live example — real game data the blueprint has never had a place for.
        Assert.DoesNotContain(
            "Range",
            WikiMapping.Default.StatsBlockLineOrder.SelectMany(line => line),
            StringComparer.OrdinalIgnoreCase);

        const string page =
            "<onlyinclude>{{Itempage\n|itemname = Thing\n|statsblock = Range: 50<br>\nClass: ALL<br>\n" +
            "Race: ALL<br>\n}}</onlyinclude>";

        PrettifyResult result = ItemPagePrettifier.Format(page);

        Assert.Contains("Range: 50", result.Formatted);
        Assert.Contains(result.Notes, n => n.Contains("Range", StringComparison.Ordinal));
    }

    // ---- Skill Mod ----

    /// <summary>
    /// **A field the blueprint documents must be mapped, however rarely a capture produces it** (user, 2026-10-02,
    /// on `Collapsible Fishing Pole`): the tool reported "no mapping at all — possibly new" for a line the template
    /// has always had. It was in <see cref="WikiMapping.StatsBlockLineOrder"/> and nowhere else, which is the gap
    /// this pins — the order knows where the line goes, the mapping knows the value belongs on the page at all.
    /// </summary>
    [Fact]
    public void SkillModIsMappedBecauseTheBlueprintHasIt()
    {
        Assert.Contains(
            "Skill Mod",
            WikiMapping.Default.StatsBlockLineOrder.SelectMany(line => line),
            StringComparer.Ordinal);

        StatMapping? mapping = WikiMapping.Default.FindStat("Skill Mod");

        Assert.NotNull(mapping);
        Assert.Equal("Skill Mod", mapping.WikiLabel);
        Assert.Equal(StatDisposition.Stored, mapping.Disposition);
    }

    /// <summary>
    /// The game writes `Fishing 5 % (10 Max)` and the wiki's one page with this field writes `Fishing +5%`, so the
    /// value needs a sign and the space closed. The cap is kept, by the user's decision (2026-10-02): it is real
    /// game data and nothing else on the page records it.
    /// </summary>
    [Theory]
    [InlineData("Fishing 5 % (10 Max)", "Fishing +5% (10 Max)")]
    [InlineData("Fishing 5 %", "Fishing +5%")]
    [InlineData("Fishing 5%", "Fishing +5%")]
    [InlineData("Blacksmithing 12.5 % (20 Max)", "Blacksmithing +12.5% (20 Max)")]
    // Idempotent, which matters because this value is compared against a page that already carries it.
    [InlineData("Fishing +5% (10 Max)", "Fishing +5% (10 Max)")]
    // A negative keeps its own sign, the same rule every signed stat follows.
    [InlineData("Fishing -5 %", "Fishing -5%")]
    // Two-word skills exist, and the skill name is not part of what gets rewritten.
    [InlineData("Sense Heading 5 % (10 Max)", "Sense Heading +5% (10 Max)")]
    public void ASkillModifierIsSignedAndItsPercentClosedUp(string captured, string expected) =>
        Assert.Equal(expected, WikiMapping.Default.FindStat("Skill Mod")!.ToWikiValue(captured));

    /// <summary>
    /// A value the format does not recognize comes back untouched. **This is the control that keeps the transform
    /// from being a licence to reshape anything** — a value this does not understand is one to leave alone, not one
    /// to tidy into something that reads well and says something else.
    /// </summary>
    [Theory]
    [InlineData("Fishing")]
    [InlineData("5")]
    [InlineData("Fishing five percent")]
    public void ASkillModifierItCannotReadIsLeftExactlyAsCaptured(string captured) =>
        Assert.Equal(captured, WikiMapping.Default.FindStat("Skill Mod")!.ToWikiValue(captured));

    /// <summary>And no other stat is reshaped by it: the format is opt-in per mapping, not a rule about values that
    /// happen to contain a percent sign.</summary>
    [Fact]
    public void NoOtherStatIsTouchedByTheSkillModifierFormat()
    {
        Assert.Equal("13.6 % (10 Max)", WikiMapping.Default.FindStat("Accuracy")!.ToWikiValue("13.6 % (10 Max)"));
        Assert.All(
            WikiMapping.Default.Stats.Values.Where(s => s.GameLabel != "Skill Mod"),
            s => Assert.Equal(StatValueFormat.AsCaptured, s.Format));
    }
}
