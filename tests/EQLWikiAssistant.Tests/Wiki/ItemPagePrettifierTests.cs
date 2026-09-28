using EQLWikiAssistant.Wiki.Formatting;
using EQLWikiAssistant.Wiki.Wikitext;

namespace EQLWikiAssistant.Tests.Wiki;

/// <summary>
/// The formatting pass. Its whole licence to rearrange a public wiki's pages is that it can *prove* it changed
/// nothing about what they say, so most of these tests are about what it refuses to do.
/// </summary>
public class ItemPagePrettifierTests
{
    private static string Page(string statsblock, string extra = "") =>
        "{{Classic Era}}\n<onlyinclude>{{Itempage\n|itemname = Thing\n|lucy_img_ID = 1\n|statsblock = \n" +
        statsblock + "\n" + extra + "}}</onlyinclude>\n\n[[Category:Waist]]";

    // --- what it lays out -------------------------------------------------------------------------------

    /// <summary>The blueprint's line order, which is the whole point: the data pass drops new content on its own
    /// line and trusts this to put it where it belongs.</summary>
    [Fact]
    public void LinesAreOrderedByTheBlueprint()
    {
        PrettifyResult result = ItemPagePrettifier.Format(Page(
            "Race: ALL<br>\nClass: PAL<br>\nAC: 10<br>\nSlot: WAIST<br>\nLore Equipped, No Trade<br>"));

        Assert.True(result.IsSafe);
        Assert.Contains(
            "Lore Equipped, No Trade<br>\nSlot: WAIST<br>\nAC: 10<br>\nClass: PAL<br>\nRace: ALL<br>",
            result.Formatted);
    }

    /// <summary>Fields sharing a line are written in the blueprint's order too, not the page's.</summary>
    [Fact]
    public void FieldsOnOneLineFollowTheBlueprintsOrder()
    {
        PrettifyResult result = ItemPagePrettifier.Format(Page("WT: 1.0  Size: SMALL<br>"));

        Assert.Contains("Size: SMALL  WT: 1.0<br>", result.Formatted);
    }

    [Fact]
    public void StraySpacingIsNormalized()
    {
        PrettifyResult result = ItemPagePrettifier.Format(Page("Haste: +41%  <br>"));

        Assert.Contains("Haste: +41%<br>", result.Formatted);
    }

    [Fact]
    public void ParametersAreOrderedAndAligned()
    {
        PrettifyResult result = ItemPagePrettifier.Format(
            "<onlyinclude>{{Itempage\n|notes = Something\n|itemname = Thing\n|lucy_img_ID = 1\n" +
            "|statsblock = \nAC: 10<br>\n}}</onlyinclude>");

        Assert.Contains("|itemname    = Thing\n|lucy_img_ID = 1\n|statsblock  = \nAC: 10<br>\n", result.Formatted);
        Assert.Contains("|notes       = Something\n", result.Formatted);
    }

    /// <summary>Everything outside the template call survives byte for byte — this pass never looks at it, and the
    /// era banner in particular belongs to the data pass because it is a statement about the item.</summary>
    [Fact]
    public void NothingOutsideTheTemplateCallIsTouched()
    {
        string original = Page("AC: 10<br>");
        PrettifyResult result = ItemPagePrettifier.Format(original);

        Assert.StartsWith("{{Classic Era}}\n<onlyinclude>", result.Formatted);
        Assert.EndsWith("</onlyinclude>\n\n[[Category:Waist]]", result.Formatted);
    }

    // --- the bugs the 744-page corpus sweep caught ------------------------------------------------------

    /// <summary>
    /// **A line can be a flag and a field at once** — the blueprint's own `EXPENDABLE  Charges: 10` — so reading
    /// flags only from flag-only lines dropped the flag on 14 real pages. The verification pass caught it, which is
    /// the entire reason it exists.
    /// </summary>
    [Fact]
    public void AFlagSharingALineWithAFieldIsNotLost()
    {
        PrettifyResult result = ItemPagePrettifier.Format(Page("Quest  Charges: 10<br>"));

        Assert.True(result.IsSafe);
        Assert.Contains("Quest<br>", result.Formatted);
        Assert.Contains("Charges: 10<br>", result.Formatted);
    }

    // --- legacy content stops it dead --------------------------------------------------------------------

    /// <summary>
    /// **A legacy flag leaves the whole block exactly as it was** (user, 2026-09-28): the formatting pass has no
    /// business understanding obsolete flags, and discarding them is the *data* pass's decision, made against a live
    /// capture. Real usage aims at items already updated for EQL; an old page gets reported, not half-modernized.
    /// </summary>
    [Fact]
    public void ALegacyFlagLeavesTheStatsblockExactlyAsItWas()
    {
        PrettifyResult result = ItemPagePrettifier.Format(Page("MAGIC ITEM  LORE ITEM  NO DROP<br>\nRace: ALL<br>\nAC: 10<br>"));

        Assert.True(result.IsSafe);
        Assert.Contains("MAGIC ITEM  LORE ITEM  NO DROP<br>\nRace: ALL<br>\nAC: 10<br>", result.Formatted);
        Assert.Contains(result.Notes, n => n.Contains("not a current EQL flag"));
    }

    /// <summary>...including the legacy separators. Nothing about that line is touched, not even its spacing.</summary>
    [Fact]
    public void LegacyFlagSeparatorsAreNotNormalized()
    {
        PrettifyResult result = ItemPagePrettifier.Format(Page("MAGIC ITEM  LORE ITEM<br>"));

        Assert.DoesNotContain("MAGIC ITEM, LORE ITEM", result.Formatted);
    }

    /// <summary>
    /// The shape rule, rather than a vocabulary list — the same reason the capture side keeps no known-flags list:
    /// the devs keep adding flags, and a list would reject exactly the rare items most worth recording. All three
    /// categories here are real, from a census of 1,183 pages.
    /// </summary>
    [Theory]
    [InlineData("Lore Equipped", true)]
    [InlineData("No Trade", true)]
    [InlineData("Attunable", true)]
    [InlineData("Placeable", true)]
    [InlineData("Quest", true)]
    [InlineData("Heirloom", true)]           // never seen on the wiki, but current — a list would have rejected it
    [InlineData("MAGIC ITEM", false)]        // legacy
    [InlineData("NODROP", false)]
    [InlineData("EXPENDABLE", false)]
    [InlineData("NO RENT", false)]
    [InlineData("This is a meal!", false)]   // prose that landed on the flags line
    [InlineData("The Book is closed.", false)]
    [InlineData("Required level of 55.", false)]
    [InlineData("Class:CLR DRU SHM", false)] // a mis-parsed Class line
    public void CurrentFlagsAreToldFromLegacyOnesByShape(string flag, bool current)
    {
        PrettifyResult result = ItemPagePrettifier.Format(Page($"{flag}<br>\nRace: ALL<br>\nAC: 10<br>"));

        Assert.Equal(current, !result.Notes.Any(n => n.Contains("not a current EQL flag")));
    }

    /// <summary>
    /// **A `*` is a bullet only at the start of a line.** Folding a one-line `|relatedquests = * [[Quest]]` onto the
    /// parameter's own line leaves the value string identical while turning a list into a literal asterisk — a
    /// rendering change the content comparison cannot see, so the layout rule has to be right rather than checked.
    /// </summary>
    [Fact]
    public void AValueStartingWithLineSensitiveMarkupKeepsItsOwnLine()
    {
        PrettifyResult result = ItemPagePrettifier.Format(
            "<onlyinclude>{{Itempage\n|itemname = Thing\n|statsblock = \nAC: 10<br>\n" +
            "|relatedquests = \n\n* [[Some Quest]]\n\n}}</onlyinclude>");

        Assert.True(result.IsSafe);
        Assert.Contains("|relatedquests = \n* [[Some Quest]]\n", result.Formatted);
        Assert.DoesNotContain("= * [[Some Quest]]", result.Formatted);
    }

    /// <summary>...while an ordinary value stays on the parameter's line. A template call is not line-sensitive.</summary>
    [Fact]
    public void AnOrdinaryValueStaysOnTheParameterLine()
    {
        PrettifyResult result = ItemPagePrettifier.Format(
            "<onlyinclude>{{Itempage\n|itemname = Thing\n|statsblock = \nAC: 10<br>\n" +
            "|notes = {{Item Lore|Words.}}\n}}</onlyinclude>");

        Assert.Contains("|notes      = {{Item Lore|Words.}}\n", result.Formatted);
    }

    // --- what it refuses to rearrange --------------------------------------------------------------------

    /// <summary>
    /// The real page that writes its resists with no colons at all survives intact.
    ///
    /// Note what actually happens to it, because it is not what one would guess: the grammar reads a colon-less line
    /// as a single unrecognized *flag* token rather than as an unparsed line, so it ends up on the flags line. That
    /// is why the corpus sweep reports no unparsed lines at all across 744 pages — the `Unparsed` kind is nearly
    /// unreachable. Either way the content is preserved, which is the property that matters.
    /// </summary>
    [Fact]
    public void ALineWithNoColonsSurvivesIntact()
    {
        PrettifyResult result = ItemPagePrettifier.Format(Page("SV FIRE +5 SV COLD +5<br>\nAC: 10<br>\nSlot: WAIST<br>"));

        Assert.True(result.IsSafe);
        // Read as one ALL-CAPS token, so it trips the legacy rule and the whole block is left untouched — which is
        // the right answer twice over for a line nobody could parse properly in the first place.
        Assert.Contains("SV FIRE +5 SV COLD +5<br>\nAC: 10<br>\nSlot: WAIST<br>", result.Formatted);
    }

    /// <summary>A blank line mid-block is a paragraph break that may be doing visible work, so the block is left
    /// alone rather than silently re-rendered. 14 real pages have one.</summary>
    [Fact]
    public void AStatsblockWithAnInteriorBlankLineKeepsItsOrder()
    {
        PrettifyResult result = ItemPagePrettifier.Format(Page("Slot: WAIST<br>\n\nAC: 10<br>"));

        Assert.Contains(result.Notes, n => n.Contains("blank line"));
        Assert.Contains("Slot: WAIST<br>\n\nAC: 10<br>", result.Formatted);
    }

    /// <summary>Two lines carrying flags would have to be merged, and merging is a bigger claim than laying
    /// out.</summary>
    [Fact]
    public void FlagsOnTwoLinesAreLeftAlone()
    {
        PrettifyResult result = ItemPagePrettifier.Format(Page("Lore Equipped<br>\nAC: 10<br>\nNo Trade<br>"));

        Assert.Contains(result.Notes, n => n.Contains("more than one line"));
        Assert.True(result.IsSafe);
    }

    /// <summary>A label appearing twice with different values is a page defect for a human, not a layout to
    /// tidy — and tidying it would have to pick a winner.</summary>
    [Fact]
    public void ADuplicateFieldWithConflictingValuesIsLeftAlone()
    {
        PrettifyResult result = ItemPagePrettifier.Format(Page("AC: 10<br>\nAC: 12<br>"));

        Assert.Contains(result.Notes, n => n.Contains("more than once"));
        Assert.Contains("AC: 10<br>\nAC: 12<br>", result.Formatted);
    }

    /// <summary>A label the blueprint has no place for — `Deity` on 25 real pages, `Range` on 15 — is kept, on its
    /// own line before Class, and reported. Dropping it would lose real data; guessing a slot would invent a
    /// convention the editors have not agreed.</summary>
    [Fact]
    public void AnUnknownLabelIsKeptAndReported()
    {
        PrettifyResult result = ItemPagePrettifier.Format(Page("Class: PAL<br>\nDeity: Brell<br>\nAC: 10<br>"));

        Assert.True(result.IsSafe);
        Assert.Contains("Deity: Brell<br>\nClass: PAL<br>", result.Formatted);
        Assert.Contains(result.Notes, n => n.Contains("Deity"));
    }

    [Fact]
    public void APageThatIsNotAnItemPageIsLeftAlone()
    {
        PrettifyResult result = ItemPagePrettifier.Format("Just some prose about a goblin.");

        Assert.False(result.IsSafe);
        Assert.False(result.Changed);
    }

    /// <summary>Formatting is idempotent — running it twice must not keep changing the page, or the follow-up
    /// prompt would never stop appearing.</summary>
    [Theory]
    [MemberData(nameof(Pages))]
    public void FormattingIsIdempotent(string title)
    {
        PrettifyResult once = ItemPagePrettifier.Format(WikiFixtures.Load(title));
        if (!once.IsSafe) return;

        PrettifyResult twice = ItemPagePrettifier.Format(once.Formatted);

        Assert.True(twice.IsSafe);
        Assert.Equal(once.Formatted, twice.Formatted);
    }

    /// <summary>Every fixture keeps every field it had. This is the property the whole design rests on, asserted
    /// against real pages rather than constructed ones.</summary>
    [Theory]
    [MemberData(nameof(Pages))]
    public void EveryFixtureKeepsAllItsContent(string title)
    {
        string original = WikiFixtures.Load(title);
        PrettifyResult result = ItemPagePrettifier.Format(original);
        if (!result.IsSafe) return;

        ItemPageDocument before = ItemPageDocument.Parse(original)!;
        ItemPageDocument after = ItemPageDocument.Parse(result.Formatted)!;

        Assert.Equal(before.ItemName, after.ItemName);
        Assert.Equal(before.IconId, after.IconId);
        Assert.Equal(before.Lore, after.Lore);
        Assert.Equal(before.MerchantValue, after.MerchantValue);

        StatsBlock? beforeBlock = before.ReadStatsBlock();
        StatsBlock? afterBlock = after.ReadStatsBlock();
        Assert.Equal(
            beforeBlock?.AllFields().Select(f => $"{f.Label}={f.Value}").Order(),
            afterBlock?.AllFields().Select(f => $"{f.Label}={f.Value}").Order());
    }

    public static TheoryData<string> Pages => WikiFixtures.AllTitles();
}
