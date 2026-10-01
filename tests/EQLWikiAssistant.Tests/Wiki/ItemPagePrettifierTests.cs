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

    // --- signing a positive stat value ------------------------------------------------------------------

    /// <summary>
    /// The one value this pass changes (user, 2026-09-30): *"Adding a `+` in front of a stat value that is already
    /// positive is just data formatting in my book, not a data change."* Before this, nothing applied it — the data
    /// pass signs a stat only when rewriting that line anyway, so an unsigned value on an otherwise correct page
    /// stayed unsigned for good.
    /// </summary>
    [Theory]
    [InlineData("STR: 5<br>", "STR: +5<br>")]
    [InlineData("MANA: 10<br>", "MANA: +10<br>")]
    [InlineData("SV FIRE: 7<br>", "SV FIRE: +7<br>")]
    // Case is the page's business, not a reason to skip it: real pages write both `SV FIRE` and `SV Fire`.
    [InlineData("SV Cold: 4<br>", "SV Cold: +4<br>")]
    // A decimal is still a positive number.
    [InlineData("HP: 2.5<br>", "HP: +2.5<br>")]
    public void APositiveSignedStatGainsItsSign(string before, string after)
    {
        PrettifyResult result = ItemPagePrettifier.Format(Page(before));

        Assert.True(result.IsSafe, string.Join("; ", result.Refusals));
        Assert.Contains(after, result.Formatted);
    }

    /// <summary>
    /// What must survive untouched. The negative case is real rather than defensive on both sides: 29 values across
    /// 1,183 pages are negative, and the game emits them too (`Earthshaker` shows `Dexterity: -1`,
    /// `Adamantite Band` shows `SV. Magic: -10`).
    /// </summary>
    [Theory]
    [InlineData("CHA: -5<br>")]
    [InlineData("SV DISEASE: -20<br>")]
    [InlineData("STR: +7<br>")]
    // Not signed by the blueprint, so not signed here. `END Regen` is the one worth pinning: it sits one line below
    // `END`, which *is* signed (user confirmed 2026-09-30 that it stays plain).
    [InlineData("END Regen: 3<br>")]
    [InlineData("AC: 10<br>")]
    [InlineData("Haste: 10<br>")]
    [InlineData("WT: 1.0<br>")]
    // Not a number at all.
    [InlineData("Size: SMALL<br>")]
    public void EverythingElseKeepsItsValueExactly(string line)
    {
        PrettifyResult result = ItemPagePrettifier.Format(Page(line));

        Assert.True(result.IsSafe, string.Join("; ", result.Refusals));
        Assert.Contains(line, result.Formatted);
    }

    /// <summary>
    /// `END` signed and `END Regen` plain, on one page — the pair the blueprint makes easiest to get backwards,
    /// since the two sit on adjacent lines and its 2026-09-30 revision touched both at once.
    /// </summary>
    [Fact]
    public void EndIsSignedAndEndRegenIsNotOnTheSamePage()
    {
        PrettifyResult result = ItemPagePrettifier.Format(Page("END: 12<br>\nEND Regen: 3<br>"));

        Assert.True(result.IsSafe, string.Join("; ", result.Refusals));
        Assert.Contains("END: +12<br>", result.Formatted);
        Assert.Contains("END Regen: 3<br>", result.Formatted);
    }

    /// <summary>
    /// A block the formatter refuses to reorder keeps its unsigned values, because that path returns the raw text
    /// untouched. **That is the rule rather than a gap**: a pass which has just said it does not understand a block
    /// has no business editing values inside it. It does mean the 469 legacy-flag pages get no signs until the data
    /// pass modernizes them first, which is the settled data-before-formatting order working as intended.
    /// </summary>
    [Fact]
    public void ABlockTheFormatterWillNotReorderKeepsItsUnsignedValues()
    {
        PrettifyResult result = ItemPagePrettifier.Format(Page("MAGIC ITEM  LORE ITEM<br>\nSTR: 5<br>"));

        Assert.True(result.IsSafe);
        Assert.Contains("STR: 5<br>", result.Formatted);
        Assert.DoesNotContain("STR: +5", result.Formatted);
        Assert.Contains(result.Notes, n => n.Contains("left exactly as it was", StringComparison.Ordinal));
    }

    /// <summary>
    /// **The negative control on the verification allowance.** `CanonicalField` deliberately treats `STR: 5` and
    /// `STR: +5` as the same content, which is a hole cut in an otherwise byte-exact check — so this proves the hole
    /// is only that wide, by feeding the comparison the differences it must still reject.
    ///
    /// Driven through <see cref="ItemPagePrettifier.WouldVerify"/> rather than through a deliberately broken
    /// formatter, because the thing under test is the comparison, and a test that needed a sabotaged writer to
    /// reach it would be testing the sabotage.
    /// </summary>
    [Theory]
    // A changed number, which is the failure the whole check exists for.
    [InlineData("STR: 5<br>", "STR: 7<br>", false)]
    [InlineData("STR: +5<br>", "STR: +7<br>", false)]
    // A sign flipped off a negative — the allowance strips a leading `+`, never a `-`, so these stay different.
    [InlineData("CHA: -5<br>", "CHA: +5<br>", false)]
    [InlineData("CHA: -5<br>", "CHA: 5<br>", false)]
    // A dropped or invented field.
    [InlineData("STR: 5<br>\nAC: 2<br>", "STR: +5<br>", false)]
    [InlineData("STR: 5<br>", "STR: +5<br>\nAC: 2<br>", false)]
    // A label the mapping does not mark signed gets no allowance at all.
    [InlineData("AC: 5<br>", "AC: +5<br>", false)]
    [InlineData("END Regen: 5<br>", "END Regen: +5<br>", false)]
    // And the one difference that is allowed.
    [InlineData("STR: 5<br>", "STR: +5<br>", true)]
    [InlineData("SV FIRE: 5<br>", "SV FIRE: +5<br>", true)]
    public void TheVerificationAllowanceIsOnlyAsWideAsTheSign(string was, string now, bool shouldPass)
    {
        bool verified = ItemPagePrettifier.WouldVerify(Page(was), Page(now));

        Assert.Equal(shouldPass, verified);
    }


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

    // --- the trailing section ---------------------------------------------------------------------------

    /// <summary>
    /// **`Mount Speed` and `Pet Illusion` go at the bottom, below a blank line** (user, 2026-09-28, after seeing
    /// them rendered): they describe something the item summons or affects — the horse, your pet — not the item
    /// itself. Which is also, now understood, why the game puts them below the effects rather than among the stats.
    /// </summary>
    [Fact]
    public void PropertiesOfWhatTheItemSummonsGoBelowABlankLineAtTheBottom()
    {
        PrettifyResult result = ItemPagePrettifier.Format(Page(
            "Mount Speed: Fast<br>\nSlot: AMMO<br>\nClass: ALL<br>\nRace: ALL<br>"));

        Assert.True(result.IsSafe);
        Assert.Contains("Slot: AMMO<br>\nClass: ALL<br>\nRace: ALL<br>\n\nMount Speed: Fast<br>", result.Formatted);
    }

    /// <summary>The separator is dropped when nothing follows it, so an ordinary item never ends in a stray blank
    /// line.</summary>
    [Fact]
    public void AnItemWithNoTrailingSectionGetsNoBlankLine()
    {
        PrettifyResult result = ItemPagePrettifier.Format(Page("Slot: AMMO<br>\nClass: ALL<br>"));

        Assert.DoesNotContain("<br>\n\n", result.Formatted);
        Assert.EndsWith("Class: ALL<br>\n}}</onlyinclude>\n\n[[Category:Waist]]", result.Formatted);
    }

    /// <summary>
    /// **The formatter must not be frozen by its own blank line.** A blank line normally stops it reordering a
    /// block, so without an exception for the separator it writes itself, formatting a page once would mean never
    /// being able to format it again.
    /// </summary>
    [Fact]
    public void TheSeparatorItWritesDoesNotStopItFormattingAgain()
    {
        PrettifyResult once = ItemPagePrettifier.Format(Page(
            "Mount Speed: Fast<br>\nRace: ALL<br>\nSlot: AMMO<br>\nClass: ALL<br>"));
        Assert.True(once.IsSafe);
        Assert.Contains("\n\nMount Speed: Fast<br>", once.Formatted);

        // Re-formatting the result must still reorder — here, by having nothing left to do — rather than refusing.
        PrettifyResult twice = ItemPagePrettifier.Format(once.Formatted);
        Assert.True(twice.IsSafe);
        Assert.Equal(once.Formatted, twice.Formatted);
        Assert.DoesNotContain(twice.Notes, n => n.Contains("blank line"));
    }

    /// <summary>...but a blank line anywhere else still stops it, since that one may be doing visible work.</summary>
    [Fact]
    public void ABlankLineElsewhereStillStopsIt()
    {
        PrettifyResult result = ItemPagePrettifier.Format(Page("Slot: AMMO<br>\n\nAC: 10<br>\nClass: ALL<br>"));

        Assert.Contains(result.Notes, n => n.Contains("blank line"));
    }

    public static TheoryData<string> Pages => WikiFixtures.AllTitles();
}
