using EQLWikiAssistant.Core.Items;
using EQLWikiAssistant.TestSupport;
using EQLWikiAssistant.TestSupport.Accuracy;
using EQLWikiAssistant.Wiki.Analysis;
using EQLWikiAssistant.Wiki.Formatting;
using EQLWikiAssistant.Wiki.Mapping;
using EQLWikiAssistant.Wiki.Wikitext;

namespace EQLWikiAssistant.Tests.Wiki;

/// <summary>
/// Creating a page for an item the wiki has never heard of.
///
/// The property these tests exist for is that **creation decides nothing of its own**: it builds an empty blueprint
/// page and lets the ordinary analyzer and editor fill it, so everything about how item data is written stays
/// tested where it already was. What is genuinely new — and what is tested here — is the skeleton, the recomputed
/// gap list, and the claim that the result needs no second commit to be blueprint-shaped.
/// </summary>
public class ItemPageCreatorTests
{
    private static ParsedItem Captured(
        string name = "Shiverback-Hide Boots",
        IReadOnlyList<string>? flags = null,
        IReadOnlyList<string>? classes = null,
        IReadOnlyList<string>? races = null,
        IReadOnlyList<string>? slots = null,
        IReadOnlyList<KeyValuePair<string, string>>? stats = null,
        string? merchantValue = null,
        string? lore = null) =>
        new(name, 0, false, flags ?? [], classes ?? [], races ?? [], slots ?? [], stats ?? [], [], [],
            merchantValue, lore, []);

    private static ParsedItem ARealisticItem() => Captured(
        flags: ["Lore Equipped", "No Trade"],
        classes: ["WAR", "RNG"],
        races: ["ALL"],
        slots: ["Feet"],
        stats: [new("AC", "12"), new("Strength", "5"), new("SV. Fire", "10"), new("Weight", "1.5")],
        merchantValue: "2 gold 4 silver");

    /// <summary>The skeleton has to be a page the rest of the tool can read, or nothing downstream runs at all.
    /// It declares every blueprint parameter, which is what lets the data pass fill them in place.</summary>
    [Fact]
    public void TheSkeletonIsAReadableItemPageDeclaringEveryBlueprintParameter()
    {
        ItemPageDocument? page = ItemPageDocument.Parse(ItemPageCreator.Skeleton("Lightweight Bag"));

        Assert.NotNull(page);
        foreach (string parameter in WikiMapping.Default.ParameterOrder)
            Assert.NotNull(page.Template.Find(parameter));
        Assert.Empty(page.Warnings);
    }

    /// <summary>`itemname` is the one field the skeleton fills, because creating the page at that title is what
    /// makes it true. Left blank it would be reported as missing and never written — a name/title mismatch is
    /// reported rather than fixed on an existing page, and that rule is right there and wrong here.</summary>
    [Fact]
    public void TheSkeletonFillsInTheItemNameAndNothingElse()
    {
        ItemPageDocument page = ItemPageDocument.Parse(ItemPageCreator.Skeleton("Potion of Amnesia"))!;

        Assert.Equal("Potion of Amnesia", page.GetParameter("itemname"));
        Assert.All(
            WikiMapping.Default.ParameterOrder.Where(p => p != "itemname"),
            p => Assert.True(string.IsNullOrWhiteSpace(page.GetParameter(p))));
    }

    /// <summary>The whole point: everything the capture saw reaches the page, written by the same code that writes
    /// it to an existing one. The era banner and the categories come from the compliance and category rules, which
    /// is why the skeleton carries neither.</summary>
    [Fact]
    public void AGeneratedPageCarriesEverythingTheCaptureSaw()
    {
        ProposedPage page = ItemPageCreator.Build(ARealisticItem(), "Shiverback-Hide Boots")!;

        Assert.Contains("{{Classic Era}}", page.Wikitext);
        Assert.Contains("Shiverback-Hide Boots", page.Wikitext);
        Assert.Contains("Lore Equipped", page.Wikitext);
        Assert.Contains("No Trade", page.Wikitext);
        Assert.Contains("AC: 12", page.Wikitext);
        Assert.Contains("Slot: FEET", page.Wikitext);
        Assert.Contains("Class: WAR RNG", page.Wikitext);
        Assert.Contains("Race: ALL", page.Wikitext);

        // The game says "2 gold 4 silver"; the wiki's compact form is the mapping's job, not this class's.
        Assert.Contains("2g 4s", page.Wikitext);

        // A slotted item earns its class categories, and the slot category too.
        Assert.Contains("[[Category:Warrior Equipment]]", page.Wikitext);
        Assert.Contains("[[Category:Feet]]", page.Wikitext);
    }

    /// <summary>Signed stats are signed and the resist keeps the blueprint's spelling — asserted here because a
    /// creation writes every value rather than only the ones that changed, so it is the widest exercise of the
    /// mapping the tool has.</summary>
    [Fact]
    public void AGeneratedPageWritesStatsTheWayTheWikiSpellsThem()
    {
        ProposedPage page = ItemPageCreator.Build(ARealisticItem(), "Shiverback-Hide Boots")!;

        Assert.Contains("STR: +5", page.Wikitext);
        Assert.Contains("SV Fire: +10", page.Wikitext);
        Assert.Contains("WT: 1.5", page.Wikitext);
    }

    /// <summary>Lore from the second capture is written into the `notes` wrapper, the same way it is added to an
    /// existing page that has none.</summary>
    [Fact]
    public void CapturedLoreIsWrittenIntoTheNotesWrapper()
    {
        ProposedPage page = ItemPageCreator.Build(
            Captured(lore: "A sturdy pair of boots."), "Shiverback-Hide Boots")!;

        Assert.Contains("{{Item Lore|A sturdy pair of boots.}}", page.Wikitext);
    }

    /// <summary>The icon ID is the one thing no capture can supply, so it is the one gap an ordinary creation
    /// reports — which is what the review screen's warning is built on.</summary>
    [Fact]
    public void TheIconIdIsTheOnlyGapOnAnOrdinaryCreation()
    {
        ProposedPage page = ItemPageCreator.Build(ARealisticItem(), "Shiverback-Hide Boots")!;

        Assert.False(page.HasIconId);
        Assert.Contains("lucy_img_ID", Assert.Single(page.Gaps));
    }

    /// <summary>
    /// **`HasIconId` must be asked of the text about to be saved, not of the proposal** (bug found by the user,
    /// 2026-10-02: the save confirmation warned that no icon ID was set on a page where one had just been typed in).
    ///
    /// `ProposedPage.HasIconId` is false on every page this class generates, because no capture can read an icon ID
    /// off the game — so a caller that consults the proposal is asking a question whose answer is fixed, and warns
    /// unconditionally. The same rule as the review screen's one non-negotiable: what is on screen is what gets
    /// saved, so what is on screen is what gets judged. `CreateAsync` already reads the saved text to pick the
    /// ledger outcome; this is the function that lets the dialog agree with it instead of contradicting it.
    /// </summary>
    [Fact]
    public void AnIconIdTypedIntoTheGeneratedTextIsSeen()
    {
        ProposedPage page = ItemPageCreator.Build(ARealisticItem(), "Shiverback-Hide Boots")!;

        Assert.False(ItemPageCreator.HasIconId(page.Wikitext));

        string typed = page.Wikitext.Replace("|lucy_img_ID    =", "|lucy_img_ID    = 5797");

        Assert.NotEqual(page.Wikitext, typed);   // the replace actually matched, so the assertion below means something
        Assert.True(ItemPageCreator.HasIconId(typed));
        Assert.Empty(ItemPageCreator.GapsIn(typed));
    }

    /// <summary>
    /// **The negative control for the gap list, and a bug this caught rather than a hypothetical** (found by
    /// running the creator over a real capture of `Armor Ornamentation Token`, 2026-10-01). The data pass's own
    /// deferred list describes the *skeleton*, where every required parameter is missing by construction — so
    /// reporting it verbatim told the user that a page with a fully written statsblock was missing its statsblock.
    /// The gaps have to be recomputed against the finished text, and this is what says so.
    /// </summary>
    [Fact]
    public void AFilledStatsBlockIsNotReportedAsMissing()
    {
        ProposedPage page = ItemPageCreator.Build(ARealisticItem(), "Shiverback-Hide Boots")!;

        Assert.DoesNotContain(page.Gaps, g => g.Contains("statsblock", StringComparison.Ordinal));
        Assert.DoesNotContain(page.Gaps, g => g.Contains("itemname", StringComparison.Ordinal));
    }

    /// <summary>
    /// **The one-commit goal** (user, 2026-10-01): the generated text is already laid out, so the formatting prompt
    /// that follows the save finds nothing to do. Asserted by running the formatter over the result and requiring
    /// no change — the same property the formatter's own format-twice test pins, and what makes a creation land a
    /// finished page in a single revision.
    /// </summary>
    [Fact]
    public void AGeneratedPageIsAlreadyFormatted()
    {
        ProposedPage page = ItemPageCreator.Build(ARealisticItem(), "Shiverback-Hide Boots")!;

        PrettifyResult again = ItemPagePrettifier.Format(page.Wikitext);

        Assert.Empty(page.FormattingRefusals);
        Assert.Empty(again.Refusals);
        Assert.False(again.Changed);
    }

    /// <summary>An item with no flags gets no flags line, rather than an empty one leaving a bare `&lt;br&gt;`
    /// behind — the same rule the edit path follows, which 20 of the 101 verified windows need.</summary>
    [Fact]
    public void AnItemWithNoFlagsGetsNoFlagsLine()
    {
        ProposedPage page = ItemPageCreator.Build(
            Captured(classes: ["ALL"], races: ["ALL"]), "Token of Reclamation")!;

        Assert.DoesNotContain("<br>\n<br>", page.Wikitext);
        Assert.Contains("Class: ALL", page.Wikitext);
    }

    /// <summary>
    /// The census over every real capture the corpus holds — the wikitext-side equivalent of the accuracy corpus,
    /// and the only thing here that would catch a field whose generated form is malformed on some item type nobody
    /// wrote a unit test for. Three properties, each a real failure mode: the result is readable as an item page,
    /// it actually has a statsblock, and the formatter both accepts it and finds nothing further to do.
    /// </summary>
    [Fact]
    public void EveryRealCaptureGeneratesAFinishedPage()
    {
        if (!File.Exists(RepoPaths.ExpectedItemsFile)) return;

        var windows = ExpectedCorpus.Load(RepoPaths.ExpectedItemsFile).Samples
            .SelectMany(s => s.Windows)
            .Where(w => !w.Occluded && !string.IsNullOrWhiteSpace(w.Name) && w.Name != ExpectedCorpus.TodoMarker)
            // A Lore-tab capture carries the name and the prose and nothing else, and the pipeline never generates
            // a page from one (it returns LoreRecorded first). Token of Reclamation is in the corpus only that way
            // (15c), so without this the test demanded a statsblock from a view that has none.
            .Where(w => !IsLoreTabView(w))
            .GroupBy(w => w.Name!, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();

        Assert.NotEmpty(windows);

        foreach (ExpectedWindow window in windows)
        {
            ParsedItem captured = new(
                window.Name!, window.Level, false,
                window.Flags, window.Classes, window.Races, window.Slots,
                [.. window.Stats.Select(s => new KeyValuePair<string, string>(s.Label, s.Value))],
                [], [], window.MerchantValue, window.Lore, []);

            ProposedPage? page = ItemPageCreator.Build(captured, window.Name!);
            Assert.NotNull(page);

            ItemPageDocument? reread = ItemPageDocument.Parse(page.Wikitext);
            Assert.NotNull(reread);
            Assert.False(
                string.IsNullOrWhiteSpace(reread.GetParameter("statsblock")),
                $"{window.Name} generated a page with no statsblock.");

            PrettifyResult again = ItemPagePrettifier.Format(page.Wikitext);
            Assert.Empty(page.FormattingRefusals);
            Assert.False(again.Changed, $"{window.Name} generated a page the formatter would change again.");
        }
    }

    /// <summary>The shape a Lore-tab capture has in the ground truth: lore, and no Description-tab data at all.
    /// Every Description capture in the corpus has at least a class list or a stat.</summary>
    private static bool IsLoreTabView(ExpectedWindow window) =>
        window.Lore is not null && window.Stats.Count == 0 && window.Classes.Count == 0
        && window.Races.Count == 0 && window.Flags.Count == 0;
}
