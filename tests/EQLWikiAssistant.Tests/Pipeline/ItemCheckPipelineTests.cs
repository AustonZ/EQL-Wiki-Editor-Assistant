using EQLWikiAssistant.Core.Locate;
using EQLWikiAssistant.Core.Icons;
using EQLWikiAssistant.Core.Ocr;
using EQLWikiAssistant.Pipeline;
using EQLWikiAssistant.Tests.Wiki;
using EQLWikiAssistant.Wiki.Ledger;
using EQLWikiAssistant.Wiki.MediaWiki;

namespace EQLWikiAssistant.Tests.Pipeline;

/// <summary>
/// The pipeline's *sequencing*, which is where the rules that matter live: the ledger is consulted before the wiki,
/// an item nobody checked gets no ledger row, and a commit refuses a page that has moved on.
///
/// **Everything here is asserted on wiki request counts and ledger contents, not on return values.** "An unchanged,
/// already-matched item costs zero wiki traffic" is the actual property the ledger exists for, and only a counting
/// fake can state it. The pixel-level work upstream (tracing a window, reading its glyphs) keeps its own golden
/// tests against real screenshots; this stands in a fake locator instead, because a frame synthesized to satisfy a
/// dozen measured pixel thresholds would test those thresholds rather than this sequence.
/// </summary>
public class ItemCheckPipelineTests
{
    private static OcrLine L(string text, int x, int y) => new(text, new Rect(x, y, 10, 10), []);

    /// <summary>A verbatim-shaped Description capture of `Earring of Bashing`, the page also used as a wikitext
    /// fixture — so the analysis run here is against real page text.</summary>
    private static readonly OcrLine[] EarringLines =
    [
        L("Earring of Bashing", 96, 0),
        L("Description", 170, 18),
        L("Earring of Bashing", 62, 49),
        L("Lore Equipped, No Trade", 61, 64),
        L("Class: WAR SHD SHM BST BER", 61, 78),
        L("Race: ALL", 60, 95),
        L("Ear", 61, 113),
        L("Size:", 10, 191),
        L("TINY", 82, 191),
        L("AC:", 141, 193),
        L("5", 228, 193),
        L("Weight:", 10, 206),
        L("0.1", 105, 209),
        L("Strength:", 12, 241),
        L("8", 95, 242),
        L("Wisdom:", 141, 239),
        L("8", 229, 241),
    ];

    /// <summary>The same item with a levelled name — ineligible, because the wiki stores level-0 data only.</summary>
    private static readonly OcrLine[] LevelledLines =
        [.. EarringLines.Select(l => new OcrLine(l.Text.Replace("Earring of Bashing", "Earring of Bashing +3"), l.BoundingBox, l.Words))];

    private static readonly OcrLine[] LoreTabLines =
    [
        L("Earring of Bashing", 96, 0),
        L("Lore", 170, 18),
        L("A trophy of the first bashing.", 20, 49),
    ];

    /// <summary>A frame only has to exist for cropping; the fake locator supplies the bounds and the lines, and a
    /// blank frame simply has no icon to read, which is an outcome the pipeline already handles.</summary>
    private static CapturedImage BlankFrame(int width = 500, int height = 700) =>
        new(width, height, new byte[width * height * 4]);

    /// <summary>
    /// A frame with readable artwork where the icon sits, so <see cref="ItemCheckResult.CapturedIcon"/> is not null.
    ///
    /// **Needed because a blank frame makes fingerprint bugs invisible**: with no icon, a fingerprint computed with
    /// one and a fingerprint computed without one are identical, so a test on <see cref="BlankFrame"/> passes whether
    /// or not the icon is included. That is exactly how the ledger bug of 2026-09-29 went uncaught. The pattern only
    /// has to clear the ink floor and carry enough variation to be comparable; it is not meant to resemble an item.
    /// </summary>
    private static CapturedImage FrameWithIcon(int width = 500, int height = 700)
    {
        var pixels = new byte[width * height * 4];
        Rect strip = ItemIconReader.IconStrip;

        for (int y = 0; y < strip.Height; y++)
            for (int x = 0; x < strip.Width; x++)
            {
                int offset = ((strip.Y + y) * width + strip.X + x) * 4;
                pixels[offset] = (byte)(40 + x * 4 % 200);       // B
                pixels[offset + 1] = (byte)(60 + y * 3 % 180);   // G
                pixels[offset + 2] = (byte)(80 + (x + y) % 160); // R
                pixels[offset + 3] = 255;
            }

        return new CapturedImage(width, height, pixels);
    }

    private static LocatedWindow Window(
        IReadOnlyList<OcrLine> lines,
        bool occluded = false,
        bool hasLoreTab = false,
        ItemWindowTab tab = ItemWindowTab.Description) =>
        new(new Rect(0, 0, 400, 600), lines, hasLoreTab, occluded, tab);

    private static (ItemCheckPipeline Pipeline, FakeWiki Wiki, CheckedItemsLedger Ledger) Build(
        LocatedWindow window, string? pageWikitext = null, string pageTitle = "Earring of Bashing")
    {
        var wiki = new FakeWiki();
        if (pageWikitext is not null) wiki.Pages[pageTitle] = new WikiPage(pageTitle, pageWikitext, 100, DateTimeOffset.UnixEpoch);

        var ledger = new CheckedItemsLedger();
        return (new ItemCheckPipeline(wiki, new FakeLocator(window), ledger), wiki, ledger);
    }

    private static string EarringPage() => WikiFixtures.Load("Earring of Bashing");

    /// <summary>A pipeline that also knows the wiki's verified-pages list, holding exactly the titles given.</summary>
    private static (ItemCheckPipeline Pipeline, CheckedItemsLedger Ledger) BuildWithVerifiedList(
        LocatedWindow window, string pageWikitext, params string[] verifiedTitles)
    {
        var wiki = new FakeWiki();
        wiki.Pages["Earring of Bashing"] =
            new WikiPage("Earring of Bashing", pageWikitext, 100, DateTimeOffset.UnixEpoch);
        wiki.Pages[VerifiedPages.ListPageTitle] = new WikiPage(
            VerifiedPages.ListPageTitle, string.Join('\n', verifiedTitles), 179774, DateTimeOffset.UnixEpoch);

        var ledger = new CheckedItemsLedger();
        var verified = new VerifiedPages(
            wiki, Path.Combine(Path.GetTempPath(), $"verified-{Guid.NewGuid():N}.json"));

        return (new ItemCheckPipeline(wiki, new FakeLocator(window), ledger, verified: verified), ledger);
    }

    /// <summary>
    /// **The captured icon survives an item that had no page when it was first checked** (user, 2026-09-29, on
    /// `Lake Pebble`). The chain that lost it: the first capture found no wiki page, and the `NotOnWiki` branch
    /// dropped the icon; the user then created the page and re-captured with the game still on the Lore tab, so
    /// `ReanalyzeAsync` rebuilt the result from the previous one — carrying that null forward even though the page
    /// now existed. The review screen then showed the wiki's icon beside an empty box, which its near-black
    /// background rendered as a small dark rectangle indistinguishable from corrupt artwork.
    /// </summary>
    [Fact]
    public async Task TheCapturedIconSurvivesAPageBeingCreatedAfterTheFirstCheck()
    {
        var wiki = new FakeWiki();
        var pipeline = new ItemCheckPipeline(wiki, new FakeLocator(Window(EarringLines)), new CheckedItemsLedger());

        // No page yet.
        IReadOnlyList<ItemCheckResult> first = await pipeline.CheckAsync(BlankFrame());
        Assert.Equal(ItemCheckStatus.NotOnWiki, first[0].Status);

        // Somebody creates it, and the item is re-analyzed from that earlier result rather than re-read.
        wiki.Pages["Earring of Bashing"] =
            new WikiPage("Earring of Bashing", EarringPage(), 100, DateTimeOffset.UnixEpoch);

        ItemCheckResult again = await pipeline.ReanalyzeAsync(first[0]);

        Assert.NotEqual(ItemCheckStatus.NotOnWiki, again.Status);

        // Asserted non-null on both sides: comparing two nulls is exactly the bug passing itself off as a fix.
        Assert.NotNull(first[0].CapturedIconImage);
        Assert.Same(first[0].CapturedIconImage, again.CapturedIconImage);
    }

    /// <summary>
    /// **The round trip the ledger exists for, through a user action** (bug found by the user, 2026-09-29). Settling
    /// an item by hand must make the next capture of that unchanged item skip the wiki — and it did not, because the
    /// fingerprint written by the action omitted the captured icon while the one computed by the next capture
    /// included it, so the two could never match. Every hand-settled item came back forever.
    ///
    /// The existing ledger tests all went through the automatic-match path, which computes its fingerprint
    /// differently, so none of them covered this.
    /// </summary>
    [Fact]
    public async Task AnItemSettledByHandIsNotCheckedAgain()
    {
        (ItemCheckPipeline pipeline, FakeWiki wiki, CheckedItemsLedger ledger) =
            Build(Window(EarringLines), UntidyPage());

        IReadOnlyList<ItemCheckResult> first = await pipeline.CheckAsync(FrameWithIcon());
        Assert.NotNull(first[0].CapturedIcon);   // or this test cannot see the bug it exists for
        pipeline.RecordCheckedByHand(first[0]);
        Assert.Equal(CheckOutcome.Matched, ledger.Find("Earring of Bashing")!.Outcome);

        int after = wiki.Fetches;
        IReadOnlyList<ItemCheckResult> again = await pipeline.CheckAsync(FrameWithIcon());

        Assert.Equal(ItemCheckStatus.AlreadyChecked, again[0].Status);
        Assert.Equal(after, wiki.Fetches);
    }

    /// <summary>The same round trip through a commit, which shared the same broken fingerprint.</summary>
    [Fact]
    public async Task AnItemCommittedIsNotCheckedAgain()
    {
        (ItemCheckPipeline pipeline, FakeWiki wiki, CheckedItemsLedger ledger) =
            Build(Window(EarringLines), UntidyPage());

        IReadOnlyList<ItemCheckResult> first = await pipeline.CheckAsync(FrameWithIcon());
        Assert.NotNull(first[0].CapturedIcon);   // or this test cannot see the bug it exists for
        await pipeline.CommitAsync(first[0], first[0].Edit!.NewWikitext, "test");
        Assert.Equal(CheckOutcome.Edited, ledger.Find("Earring of Bashing")!.Outcome);

        int after = wiki.Fetches;
        IReadOnlyList<ItemCheckResult> again = await pipeline.CheckAsync(FrameWithIcon());

        Assert.Equal(ItemCheckStatus.AlreadyChecked, again[0].Status);
        Assert.Equal(after, wiki.Fetches);
    }

    // --- formatting offered without a data edit ---------------------------------------------------------

    /// <summary>
    /// **A page that already agrees still gets its formatting checked** (user, 2026-09-29). There is otherwise no
    /// moment at which a user would be shown that a page whose data is right is laid out wrongly. It costs no
    /// request: the page was just fetched and nothing has changed it since.
    /// </summary>
    [Fact]
    public async Task APageThatAlreadyMatchesIsStillOfferedItsFormatting()
    {
        // The real `Earring of Bashing` fixture: its data matches this capture exactly (the ledger test above
        // relies on that), and the prettifier still has work to do on it — notes out of blueprint order, Size and
        // WT the wrong way round, padding unaligned. Exactly the case this feature exists for.
        (ItemCheckPipeline pipeline, FakeWiki wiki, _) =
            Build(Window(EarringLines), EarringPage());

        int before = wiki.Fetches;
        IReadOnlyList<ItemCheckResult> results = await pipeline.CheckAsync(BlankFrame());

        Assert.Equal(ItemCheckStatus.AlreadyCorrect, results[0].Status);
        Assert.NotNull(results[0].Formatting);
        Assert.Equal(before + 1, wiki.Fetches); // the page's own fetch, and nothing more
    }

    /// <summary>A page the tool wants to change does not get one yet: that edit rebuilds the layout anyway, and
    /// offering both at once would bury the data diff under a reflow.</summary>
    [Fact]
    public async Task APageWithAProposedEditIsNotOfferedFormattingYet()
    {
        (ItemCheckPipeline pipeline, _, _) = Build(Window(EarringLines), UntidyPage());

        IReadOnlyList<ItemCheckResult> results = await pipeline.CheckAsync(BlankFrame());

        Assert.Equal(ItemCheckStatus.EditProposed, results[0].Status);
        Assert.Null(results[0].Formatting);
    }

    /// <summary>Formatting a page already in hand asks the wiki for nothing — the whole reason it can be offered on
    /// a match without making captures slower.</summary>
    [Fact]
    public async Task PreparingFormattingFromAPageInHandMakesNoRequest()
    {
        (ItemCheckPipeline pipeline, FakeWiki wiki, _) = Build(Window(EarringLines), EarringPage());
        IReadOnlyList<ItemCheckResult> results = await pipeline.CheckAsync(BlankFrame());

        int before = wiki.Fetches;
        (FormattingProposal? proposal, _) = pipeline.PrepareFormatting(results[0].Page!);

        Assert.NotNull(proposal);
        Assert.Equal(before, wiki.Fetches);
    }

    /// <summary>**Skipping never offers formatting**, and that is the data-before-formatting rule rather than an
    /// oversight: skipping leaves the data question unanswered, and the formatter must never be asked to lay out a
    /// page the data pass has not modernized (user, 2026-09-29).</summary>
    [Fact]
    public async Task SkippingLeavesTheFormattingAlone()
    {
        (ItemCheckPipeline pipeline, _, CheckedItemsLedger ledger) = Build(Window(EarringLines), UntidyPage());
        IReadOnlyList<ItemCheckResult> results = await pipeline.CheckAsync(BlankFrame());

        pipeline.RecordSkipped(results[0]);

        Assert.Equal(CheckOutcome.Skipped, ledger.Find("Earring of Bashing")!.Outcome);
        Assert.Null(results[0].Formatting);
    }

    // --- verification (reported, never enforced) --------------------------------------------------------

    /// <summary>
    /// **Unverified warns and nothing more** (user, 2026-09-29). Verification attests that a whole page is accurate,
    /// including the drops and quests this tool never reads, so it cannot be a blocker — and with only 48 of 744
    /// sampled item pages verified, making it one would leave nearly every item permanently unsettled.
    /// </summary>
    [Fact]
    public async Task AnUnverifiedPageIsWarnedAboutButStillCountsAsDone()
    {
        (ItemCheckPipeline pipeline, CheckedItemsLedger ledger) =
            BuildWithVerifiedList(Window(EarringLines), EarringPage(), "Some_Other_Page");

        IReadOnlyList<ItemCheckResult> results = await pipeline.CheckAsync(BlankFrame());

        Assert.Contains(results[0].Warnings, w => w.Contains("not verified for EQL", StringComparison.Ordinal));
        Assert.False(results[0].NeedsAttention);
        Assert.Equal(CheckOutcome.Matched, ledger.Find("Earring of Bashing")!.Outcome);
    }

    /// <summary>The other branch says nothing at all. An alert on every page — either "not yet verified" or "already
    /// verified" — is an alert everyone learns to ignore (user, 2026-09-29).</summary>
    [Fact]
    public async Task AVerifiedPageSaysNothingAboutVerification()
    {
        (ItemCheckPipeline pipeline, _) =
            BuildWithVerifiedList(Window(EarringLines), EarringPage(), "Earring_of_Bashing");

        IReadOnlyList<ItemCheckResult> results = await pipeline.CheckAsync(BlankFrame());

        Assert.DoesNotContain(results[0].Warnings, w => w.Contains("Verified", StringComparison.Ordinal));
    }

    /// <summary>With no list configured the tool is silent, which is also what an unreachable wiki produces: a false
    /// "unverified" would send the user to re-verify a page that is already done.</summary>
    [Fact]
    public async Task NothingIsSaidWhenTheListIsNotAvailable()
    {
        (ItemCheckPipeline pipeline, _, _) = Build(Window(EarringLines), EarringPage());

        IReadOnlyList<ItemCheckResult> results = await pipeline.CheckAsync(BlankFrame());

        Assert.DoesNotContain(results[0].Warnings, w => w.Contains("Verified", StringComparison.Ordinal));
    }

    /// <summary>The ledger-skip path answers it too, from the local list and with no wiki request — and that is the
    /// item most likely to still be waiting on verification.</summary>
    [Fact]
    public async Task AnAlreadyCheckedItemIsStillToldItsPageIsUnverified()
    {
        (ItemCheckPipeline pipeline, _) =
            BuildWithVerifiedList(Window(EarringLines), EarringPage(), "Some_Other_Page");

        await pipeline.CheckAsync(BlankFrame());
        IReadOnlyList<ItemCheckResult> again = await pipeline.CheckAsync(BlankFrame());

        Assert.Equal(ItemCheckStatus.AlreadyChecked, again[0].Status);
        Assert.Contains(again[0].Warnings, w => w.Contains("not verified for EQL", StringComparison.Ordinal));
    }

    /// <summary>A page whose data agrees but which carries a defect the tool will not fix — here the missing
    /// <c>&lt;onlyinclude&gt;</c> wrapper, which needs a human to decide what to enclose.</summary>
    private static string AgreeingPageWithSomethingTheToolWillNotFix() =>
        EarringPage().Replace("<onlyinclude>", "").Replace("</onlyinclude>", "");

    // --- the ledger rules -------------------------------------------------------------------------------

    /// <summary>
    /// **A page that agrees but left something undone is not done** (user, 2026-09-29, on `Dragon Bone Bracelet`).
    ///
    /// The review screen already listed the deferred item as "Not done" in its warning strip while the ledger
    /// recorded `Matched` — the two saying opposite things about the same item, and the ledger's answer being the one
    /// that decides whether it is ever raised again. Anything the tool declines has to keep coming back.
    /// </summary>
    [Fact]
    public async Task APageThatAgreesButHasSomethingDeferredIsFlaggedNotMatched()
    {
        (ItemCheckPipeline pipeline, _, CheckedItemsLedger ledger) =
            Build(Window(EarringLines), AgreeingPageWithSomethingTheToolWillNotFix());

        IReadOnlyList<ItemCheckResult> results = await pipeline.CheckAsync(BlankFrame());

        Assert.Equal(ItemCheckStatus.AlreadyCorrect, results[0].Status);
        Assert.NotEmpty(results[0].Edit!.Deferred);
        Assert.True(results[0].NeedsAttention);
        Assert.Equal(CheckOutcome.Flagged, ledger.Find("Earring of Bashing")!.Outcome);
    }

    /// <summary>And being flagged means the next capture still goes to the wiki, which is the whole consequence of
    /// getting the outcome right.</summary>
    [Fact]
    public async Task ADeferredItemIsStillFetchedOnTheNextCapture()
    {
        (ItemCheckPipeline pipeline, FakeWiki wiki, _) =
            Build(Window(EarringLines), AgreeingPageWithSomethingTheToolWillNotFix());

        await pipeline.CheckAsync(BlankFrame());
        int after = wiki.Fetches;

        await pipeline.CheckAsync(BlankFrame());

        Assert.True(wiki.Fetches > after);
    }


    /// <summary>The headline property. A page that already agrees is recorded as matched, and capturing the same
    /// item again then costs **no wiki requests at all** — which is the only reason the ledger exists.</summary>
    [Fact]
    public async Task ASecondCaptureOfAnUnchangedMatchedItemMakesNoWikiRequests()
    {
        (ItemCheckPipeline pipeline, FakeWiki wiki, CheckedItemsLedger ledger) =
            Build(Window(EarringLines), EarringPage());

        IReadOnlyList<ItemCheckResult> first = await pipeline.CheckAsync(BlankFrame());
        Assert.Equal(ItemCheckStatus.AlreadyCorrect, first[0].Status);
        Assert.Equal(CheckOutcome.Matched, ledger.Find("Earring of Bashing")!.Outcome);
        Assert.True(wiki.Fetches > 0);

        int after = wiki.Fetches;
        IReadOnlyList<ItemCheckResult> second = await pipeline.CheckAsync(BlankFrame());

        Assert.Equal(ItemCheckStatus.AlreadyChecked, second[0].Status);
        Assert.Equal(after, wiki.Fetches);
    }

    /// <summary>"Re-check anyway" must reach the wiki however settled the row is — otherwise the override does
    /// nothing, and the user has no way to act on a page somebody else changed.</summary>
    [Fact]
    public async Task ReCheckAnywayForcesAFetch()
    {
        (ItemCheckPipeline pipeline, FakeWiki wiki, _) = Build(Window(EarringLines), EarringPage());
        await pipeline.CheckAsync(BlankFrame());

        int after = wiki.Fetches;
        pipeline.ReCheckAnyway = true;
        IReadOnlyList<ItemCheckResult> again = await pipeline.CheckAsync(BlankFrame());

        Assert.True(wiki.Fetches > after);
        Assert.Equal(ItemCheckStatus.AlreadyCorrect, again[0].Status);
    }

    /// <summary>The rule most likely to be got backwards, and the most damaging to get wrong: an ineligible item was
    /// never actually checked, so any row — even `Skipped` — would make it look handled forever.</summary>
    [Fact]
    public async Task AnIneligibleItemWritesNoLedgerRowAndNeverReachesTheWiki()
    {
        (ItemCheckPipeline pipeline, FakeWiki wiki, CheckedItemsLedger ledger) = Build(Window(LevelledLines));

        IReadOnlyList<ItemCheckResult> results = await pipeline.CheckAsync(BlankFrame());

        Assert.Equal(ItemCheckStatus.Ineligible, results[0].Status);
        Assert.False(results[0].Eligibility!.IsEligible);
        Assert.Equal(0, ledger.Count);
        Assert.Equal(0, wiki.Fetches);
    }

    /// <summary>...and the user's "skip" action must not sneak one in either.</summary>
    [Fact]
    public async Task SkippingAnIneligibleItemStillWritesNoRow()
    {
        (ItemCheckPipeline pipeline, _, CheckedItemsLedger ledger) = Build(Window(LevelledLines));
        IReadOnlyList<ItemCheckResult> results = await pipeline.CheckAsync(BlankFrame());

        pipeline.RecordSkipped(results[0]);

        Assert.Equal(0, ledger.Count);
    }

    /// <summary>An occluded window is the same case for the same reason — nothing was read, so nothing was
    /// checked.</summary>
    [Fact]
    public async Task AnOccludedWindowIsReportedAndWritesNoRow()
    {
        (ItemCheckPipeline pipeline, FakeWiki wiki, CheckedItemsLedger ledger) =
            Build(Window(EarringLines, occluded: true));

        IReadOnlyList<ItemCheckResult> results = await pipeline.CheckAsync(BlankFrame());

        Assert.Equal(ItemCheckStatus.Occluded, results[0].Status);
        Assert.NotEmpty(results[0].Warnings);
        Assert.Equal(0, ledger.Count);
        Assert.Equal(0, wiki.Fetches);
    }

    /// <summary>A page that agrees but still needs a human is `Flagged`, not `Matched` — and `Flagged` never lets a
    /// later capture skip the wiki, which is exactly the point.</summary>
    [Fact]
    public async Task APageThatAgreesButNeedsAHumanIsFlaggedRatherThanMatched()
    {
        // The title bar and the body disagree — the parser's occlusion safety net — which the pipeline must not
        // paper over. Truncated past the parser's length-scaled tolerance, since ordinary OCR noise reconciles.
        OcrLine[] lines = [.. EarringLines];
        lines[0] = L("of Bashing", 96, 0);

        (ItemCheckPipeline pipeline, FakeWiki wiki, CheckedItemsLedger ledger) = Build(Window(lines), EarringPage());

        IReadOnlyList<ItemCheckResult> results = await pipeline.CheckAsync(BlankFrame());

        Assert.True(results[0].NeedsAttention);
        Assert.Equal(CheckOutcome.Flagged, ledger.Find("Earring of Bashing")!.Outcome);

        int after = wiki.Fetches;
        await pipeline.CheckAsync(BlankFrame());
        Assert.True(wiki.Fetches > after);
    }

    /// <summary>An item whose lore has not been captured is not done either, however well the rest of the page
    /// agrees — recording it as matched would mean never looking at its lore again.</summary>
    [Fact]
    public async Task APageThatAgreesButHasUncapturedLoreIsFlagged()
    {
        (ItemCheckPipeline pipeline, _, CheckedItemsLedger ledger) =
            Build(Window(EarringLines, hasLoreTab: true), EarringPage());

        ItemCheckResult result = (await pipeline.CheckAsync(BlankFrame()))[0];

        Assert.Equal(ItemCheckStatus.AlreadyCorrect, result.Status);
        Assert.Equal(CheckOutcome.Flagged, ledger.Find("Earring of Bashing")!.Outcome);
    }

    /// <summary>An item with no page is recorded, but as `NotOnWiki` — never as done, because somebody may create
    /// the page tomorrow.</summary>
    [Fact]
    public async Task AnItemWithNoPageIsRecordedButNeverCountsAsDone()
    {
        (ItemCheckPipeline pipeline, FakeWiki wiki, CheckedItemsLedger ledger) = Build(Window(EarringLines));

        IReadOnlyList<ItemCheckResult> results = await pipeline.CheckAsync(BlankFrame());

        Assert.Equal(ItemCheckStatus.NotOnWiki, results[0].Status);
        Assert.Equal(CheckOutcome.NotOnWiki, ledger.Find("Earring of Bashing")!.Outcome);

        int after = wiki.Fetches;
        await pipeline.CheckAsync(BlankFrame());
        Assert.True(wiki.Fetches > after);
    }

    /// <summary>A page that exists but carries no Itempage call has nothing to compare, and that is a page defect
    /// worth a human rather than an error.</summary>
    [Fact]
    public async Task APageThatIsNotAnItemPageIsFlagged()
    {
        (ItemCheckPipeline pipeline, _, CheckedItemsLedger ledger) =
            Build(Window(EarringLines), "A redirect-ish page with no template at all.");

        IReadOnlyList<ItemCheckResult> results = await pipeline.CheckAsync(BlankFrame());

        Assert.Equal(ItemCheckStatus.NotAnItemPage, results[0].Status);
        Assert.Equal(CheckOutcome.Flagged, ledger.Find("Earring of Bashing")!.Outcome);
    }

    /// <summary>
    /// **An unreachable wiki aborts the whole frame** (user, 2026-09-29). This tool exists to compare captures
    /// against the wiki, so without it there is nothing for the remaining windows to be checked against — and
    /// degrading into one identical failure per item reads like a tool malfunction rather than an outage. It
    /// reaches the caller, which says so once and stops.
    /// </summary>
    [Fact]
    public async Task AnUnreachableWikiAbortsTheFrameRatherThanFailingEachWindow()
    {
        var wiki = new FakeWiki { FailFetches = true };
        var ledger = new CheckedItemsLedger();
        var pipeline = new ItemCheckPipeline(wiki, new FakeLocator(Window(EarringLines)), ledger);

        await Assert.ThrowsAsync<WikiUnavailableException>(() => pipeline.CheckAsync(BlankFrame()));

        // Nothing was checked, so nothing is recorded — re-capturing once the wiki is back loses nothing.
        Assert.Equal(0, ledger.Count);
    }

    /// <summary>
    /// The other half of the rule: an error *about one page* is still per-window. The wiki answered, so the rest of
    /// the frame has somewhere to be checked against and carries on — and still writes no row, because that window
    /// was not checked either.
    /// </summary>
    [Fact]
    public async Task AnErrorAboutOnePageStillOnlyFailsThatWindow()
    {
        var wiki = new FakeWiki { FetchError = new MediaWikiException("protectedpage", "That page is protected.") };
        var ledger = new CheckedItemsLedger();
        var pipeline = new ItemCheckPipeline(wiki, new FakeLocator(Window(EarringLines)), ledger);

        IReadOnlyList<ItemCheckResult> results = await pipeline.CheckAsync(BlankFrame());

        Assert.Equal(ItemCheckStatus.Failed, results[0].Status);
        Assert.NotNull(results[0].Error);
        Assert.Equal(0, ledger.Count);
    }

    // --- the edit and the commit ------------------------------------------------------------------------

    /// <summary>A stale page produces a reviewable edit and **writes nothing** — checking is read-only, so the user
    /// sees every window in a frame before anything is committed.</summary>
    [Fact]
    public async Task AStalePageProducesAReviewableEditAndWritesNothing()
    {
        OcrLine[] lines = [.. EarringLines];
        lines[Array.IndexOf(lines, lines.First(l => l.Text == "5"))] = L("6", 228, 193);

        (ItemCheckPipeline pipeline, FakeWiki wiki, CheckedItemsLedger ledger) = Build(Window(lines), EarringPage());

        IReadOnlyList<ItemCheckResult> results = await pipeline.CheckAsync(BlankFrame());

        Assert.Equal(ItemCheckStatus.EditProposed, results[0].Status);
        Assert.True(results[0].CanCommit);
        Assert.Contains("AC: 6<br>", results[0].Edit!.NewWikitext);
        Assert.Equal(0, wiki.Edits);
        Assert.Equal(0, ledger.Count);
    }

    [Fact]
    public async Task CommittingWritesThePageAndRecordsTheEdit()
    {
        OcrLine[] lines = [.. EarringLines];
        lines[Array.IndexOf(lines, lines.First(l => l.Text == "5"))] = L("6", 228, 193);

        (ItemCheckPipeline pipeline, FakeWiki wiki, CheckedItemsLedger ledger) = Build(Window(lines), EarringPage());
        ItemCheckResult result = (await pipeline.CheckAsync(BlankFrame()))[0];

        CommitResult commit = await pipeline.CommitAsync(result, result.Edit!.NewWikitext, result.Edit.Summary);

        Assert.Equal(CommitStatus.Committed, commit.Status);
        Assert.Equal(1, wiki.Edits);
        Assert.Contains("AC: 6<br>", wiki.Pages["Earring of Bashing"].Wikitext);

        LedgerEntry entry = ledger.Find("Earring of Bashing")!;
        Assert.Equal(CheckOutcome.Edited, entry.Outcome);
        Assert.Equal("Earring of Bashing", entry.WikiPageTitle);
    }

    /// <summary>
    /// The guard that matters more than `basetimestamp` does. MediaWiki merges what it can, and this tool's edits
    /// are wholesale parameter replacements — exactly the shape that merges cleanly while discarding somebody's
    /// work. So a page that changed between the check and the commit is refused outright.
    /// </summary>
    [Fact]
    public async Task CommittingRefusesAPageSomebodyElseEditedSinceTheCheck()
    {
        OcrLine[] lines = [.. EarringLines];
        lines[Array.IndexOf(lines, lines.First(l => l.Text == "5"))] = L("6", 228, 193);

        (ItemCheckPipeline pipeline, FakeWiki wiki, CheckedItemsLedger ledger) = Build(Window(lines), EarringPage());
        ItemCheckResult result = (await pipeline.CheckAsync(BlankFrame()))[0];

        // Somebody else saves a change while the user is looking at the diff.
        wiki.Pages["Earring of Bashing"] = wiki.Pages["Earring of Bashing"] with
        {
            Wikitext = wiki.Pages["Earring of Bashing"].Wikitext + "\n[[Category:Somebody's addition]]",
            RevisionId = 101,
        };

        CommitResult commit = await pipeline.CommitAsync(result, result.Edit!.NewWikitext, result.Edit.Summary);

        Assert.Equal(CommitStatus.PageChangedSinceCheck, commit.Status);
        Assert.Equal(0, wiki.Edits);
        Assert.Equal(0, ledger.Count);
        Assert.Contains("101", commit.Error);
    }

    /// <summary>What the user sees is what gets written — the review screen lets them amend the wikitext, and an
    /// edit that silently saved something else would make the review meaningless.</summary>
    [Fact]
    public async Task CommittingWritesTheUsersOwnTextWhenTheyAmendedIt()
    {
        OcrLine[] lines = [.. EarringLines];
        lines[Array.IndexOf(lines, lines.First(l => l.Text == "5"))] = L("6", 228, 193);

        (ItemCheckPipeline pipeline, FakeWiki wiki, _) = Build(Window(lines), EarringPage());
        ItemCheckResult result = (await pipeline.CheckAsync(BlankFrame()))[0];

        string amended = result.Edit!.NewWikitext.Replace("AC: 6<br>", "AC: 7<br>");
        await pipeline.CommitAsync(result, amended, "hand-corrected");

        Assert.Contains("AC: 7<br>", wiki.Pages["Earring of Bashing"].Wikitext);
        Assert.Equal("hand-corrected", wiki.LastSummary);
    }

    /// <summary>
    /// **Committing a stat fix must not quietly settle a judgement the tool declined to make.** Writing a corrected
    /// AC says nothing about the lore difference the user was warned of on the same screen, and recording `Edited`
    /// would count the whole item as done and never raise it again. The outcome must not depend on whether some
    /// unrelated stat happened to change too.
    /// </summary>
    [Fact]
    public async Task CommittingAnItemThatStillNeedsAHumanRecordsItAsFlagged()
    {
        OcrLine[] lines = [.. EarringLines];
        lines[Array.IndexOf(lines, lines.First(l => l.Text == "5"))] = L("6", 228, 193);

        // A Lore tab that has not been captured is an outstanding judgement, and the edit is real and unrelated.
        (ItemCheckPipeline pipeline, FakeWiki wiki, CheckedItemsLedger ledger) =
            Build(Window(lines, hasLoreTab: true), EarringPage());
        ItemCheckResult result = (await pipeline.CheckAsync(BlankFrame()))[0];

        CommitResult commit = await pipeline.CommitAsync(result, result.Edit!.NewWikitext, result.Edit.Summary);

        Assert.Equal(CommitStatus.Committed, commit.Status);
        Assert.Equal(CheckOutcome.Flagged, ledger.Find("Earring of Bashing")!.Outcome);

        int after = wiki.Fetches;
        await pipeline.CheckAsync(BlankFrame());
        Assert.True(wiki.Fetches > after);
    }

    /// <summary>
    /// The user's answer to a judgement the tool refused to make — most often "the page's lore is the better
    /// wording". It has to exist: without it such an item re-fetches on every capture forever, and the only other
    /// escape would be overwriting the very thing the user just approved.
    /// </summary>
    [Fact]
    public async Task MarkingAFlaggedItemAsCheckedByHandSettlesIt()
    {
        (ItemCheckPipeline pipeline, FakeWiki wiki, CheckedItemsLedger ledger) =
            Build(Window(EarringLines, hasLoreTab: true), EarringPage());
        ItemCheckResult result = (await pipeline.CheckAsync(BlankFrame()))[0];
        Assert.Equal(CheckOutcome.Flagged, ledger.Find("Earring of Bashing")!.Outcome);

        pipeline.RecordCheckedByHand(result, "the page's lore is better than the window's");

        Assert.Equal(CheckOutcome.Matched, ledger.Find("Earring of Bashing")!.Outcome);

        int after = wiki.Fetches;
        await pipeline.CheckAsync(BlankFrame());
        Assert.Equal(after, wiki.Fetches);
    }

    /// <summary>...but it must not conjure a row for a window nothing was read from, whatever the user clicks.</summary>
    [Fact]
    public async Task MarkingAnIneligibleItemAsCheckedStillWritesNoRow()
    {
        (ItemCheckPipeline pipeline, _, CheckedItemsLedger ledger) = Build(Window(LevelledLines));
        ItemCheckResult result = (await pipeline.CheckAsync(BlankFrame()))[0];

        pipeline.RecordCheckedByHand(result);

        Assert.Equal(0, ledger.Count);
    }

    /// <summary>Skipping records that the user looked and declined — as `Skipped`, which never counts as done, so
    /// the item comes back next time.</summary>
    [Fact]
    public async Task SkippingRecordsAnUnresolvedRow()
    {
        OcrLine[] lines = [.. EarringLines];
        lines[Array.IndexOf(lines, lines.First(l => l.Text == "5"))] = L("6", 228, 193);

        (ItemCheckPipeline pipeline, FakeWiki wiki, CheckedItemsLedger ledger) = Build(Window(lines), EarringPage());
        ItemCheckResult result = (await pipeline.CheckAsync(BlankFrame()))[0];

        pipeline.RecordSkipped(result, "waiting on a template change");

        Assert.Equal(CheckOutcome.Skipped, ledger.Find("Earring of Bashing")!.Outcome);
        Assert.Equal(0, wiki.Edits);

        int after = wiki.Fetches;
        await pipeline.CheckAsync(BlankFrame());
        Assert.True(wiki.Fetches > after);
    }

    // --- the formatting follow-up ----------------------------------------------------------------------

    /// <summary>
    /// **The formatting is offered, never folded in.** Mixing it into the data edit would bury a one-value
    /// correction under a whole-page reflow, which is the review problem this tool exists to solve — so the data
    /// commit writes exactly once and hands back a separate proposal.
    /// </summary>
    [Fact]
    public async Task CommittingOffersTheFormattingAsASecondEditWithoutWritingIt()
    {
        (ItemCheckPipeline pipeline, FakeWiki wiki, _) = Build(Window(EarringLines), UntidyPage());
        ItemCheckResult result = (await pipeline.CheckAsync(BlankFrame()))[0];

        CommitResult commit = await pipeline.CommitAsync(result, result.Edit!.NewWikitext, result.Edit.Summary);

        Assert.Equal(CommitStatus.Committed, commit.Status);
        Assert.Equal(1, wiki.Edits);
        Assert.NotNull(commit.Formatting);
        Assert.DoesNotContain("Reformatted", wiki.LastSummary);
    }

    [Fact]
    public async Task TheFormattingEditIsWrittenSeparatelyAndSaysSo()
    {
        (ItemCheckPipeline pipeline, FakeWiki wiki, _) = Build(Window(EarringLines), UntidyPage());
        ItemCheckResult result = (await pipeline.CheckAsync(BlankFrame()))[0];
        CommitResult commit = await pipeline.CommitAsync(result, result.Edit!.NewWikitext, result.Edit.Summary);

        CommitResult formatting = await pipeline.CommitFormattingAsync(commit.Formatting!);

        Assert.Equal(CommitStatus.Committed, formatting.Status);
        Assert.Equal(2, wiki.Edits);
        Assert.Contains("no data changes", wiki.LastSummary);
    }

    /// <summary>A page already laid out offers nothing — the prompt has to be silent when there is nothing to do, or
    /// it becomes noise the user learns to dismiss.</summary>
    [Fact]
    public async Task ATidyPageOffersNoFormatting()
    {
        (ItemCheckPipeline pipeline, _, _) = Build(Window(EarringLines), UntidyPage());
        ItemCheckResult result = (await pipeline.CheckAsync(BlankFrame()))[0];
        CommitResult first = await pipeline.CommitAsync(result, result.Edit!.NewWikitext, result.Edit.Summary);
        await pipeline.CommitFormattingAsync(first.Formatting!);

        (FormattingProposal? again, _) = await pipeline.PrepareFormattingAsync("Earring of Bashing");

        Assert.Null(again);
    }

    /// <summary>Same conflict rule as the data commit: a reflow saved over somebody else's change would be the worst
    /// kind of edit to review.</summary>
    [Fact]
    public async Task TheFormattingEditRefusesAPageThatMovedOn()
    {
        (ItemCheckPipeline pipeline, FakeWiki wiki, _) = Build(Window(EarringLines), UntidyPage());
        ItemCheckResult result = (await pipeline.CheckAsync(BlankFrame()))[0];
        CommitResult commit = await pipeline.CommitAsync(result, result.Edit!.NewWikitext, result.Edit.Summary);

        wiki.Pages["Earring of Bashing"] = wiki.Pages["Earring of Bashing"] with
        {
            Wikitext = wiki.Pages["Earring of Bashing"].Wikitext + "\n[[Category:Somebody's addition]]",
        };

        CommitResult formatting = await pipeline.CommitFormattingAsync(commit.Formatting!);

        Assert.Equal(CommitStatus.PageChangedSinceCheck, formatting.Status);
        Assert.Equal(1, wiki.Edits);
    }

    /// <summary>
    /// **The two passes compose the right way round on a legacy page**, which is the clearest argument for the
    /// settled data-first order: the data edit replaces the legacy flags line with what the game actually says, and
    /// only then does the formatter have a page it understands well enough to lay out. Running the formatter first
    /// would have met a page it refuses to touch.
    /// </summary>
    [Fact]
    public async Task ALegacyPageBecomesFormattableOnceTheDataEditHasModernizedIt()
    {
        string legacy = UntidyPage().Replace("Lore Equpped, No Trade<br>", "MAGIC ITEM  LORE ITEM<br>");

        (ItemCheckPipeline pipeline, FakeWiki wiki, _) = Build(Window(EarringLines), legacy);

        // Before the data edit, the formatter lays out the parameters it understands but will not touch the
        // statsblock — the legacy flags line comes back byte for byte, with a note saying why.
        (FormattingProposal? before, IReadOnlyList<string> whyNot) =
            await pipeline.PrepareFormattingAsync("Earring of Bashing");
        Assert.NotNull(before);
        Assert.Contains("MAGIC ITEM  LORE ITEM<br>", before!.Formatted);
        Assert.Contains(whyNot, n => n.Contains("not a current EQL flag"));

        ItemCheckResult result = (await pipeline.CheckAsync(BlankFrame()))[0];
        CommitResult commit = await pipeline.CommitAsync(result, result.Edit!.NewWikitext, result.Edit.Summary);

        Assert.Equal(CommitStatus.Committed, commit.Status);
        Assert.Equal(1, wiki.Edits);
        Assert.NotNull(commit.Formatting);
        Assert.DoesNotContain("MAGIC ITEM", commit.Formatting!.Formatted);
    }

    /// <summary>A page whose statsblock is laid out the wrong way round, so the formatter has something to do.</summary>
    private static string UntidyPage() =>
        "{{Classic Era}}\n<onlyinclude>{{Itempage\n|notes = \n|itemname = Earring of Bashing\n" +
        "|lucy_img_ID = 1\n|statsblock = \nRace: ALL<br>\nClass: WAR SHD SHM BST BER<br>\n" +
        "Lore Equpped, No Trade<br>\nSlot: EAR<br>\nAC: 5<br>\nSTR: +8  WIS: +8<br>\n" +
        "Size: TINY  WT: 0.1<br>\n}}</onlyinclude>";

    // --- the two-capture lore flow ---------------------------------------------------------------------

    /// <summary>A window that offers a Lore tab says so, so the UI can ask for the second capture instead of
    /// quietly checking an item whose lore it never saw.</summary>
    [Fact]
    public async Task ADescriptionCaptureOfALoreBearingItemAsksForTheLoreTab()
    {
        (ItemCheckPipeline pipeline, _, _) =
            Build(Window(EarringLines, hasLoreTab: true), EarringPage());

        ItemCheckResult result = (await pipeline.CheckAsync(BlankFrame()))[0];

        Assert.True(result.NeedsLoreCapture);
        Assert.True(result.NeedsAttention);
        Assert.Contains(result.Warnings, w => w.Contains("Lore tab"));
    }

    /// <summary>A Lore capture contributes its prose and nothing else — it has no stats to compare — and the
    /// Description capture that follows picks it up.</summary>
    [Fact]
    public async Task ALoreCaptureIsRecordedAndSatisfiesTheNextDescriptionCapture()
    {
        var wiki = new FakeWiki();
        wiki.Pages["Earring of Bashing"] =
            new WikiPage("Earring of Bashing", EarringPage(), 100, DateTimeOffset.UnixEpoch);

        var locator = new FakeLocator(Window(LoreTabLines, hasLoreTab: true, tab: ItemWindowTab.Lore));
        var pipeline = new ItemCheckPipeline(wiki, locator, new CheckedItemsLedger());

        ItemCheckResult lore = (await pipeline.CheckAsync(BlankFrame()))[0];
        Assert.Equal(ItemCheckStatus.LoreRecorded, lore.Status);
        Assert.Equal("A trophy of the first bashing.", lore.Lore);
        Assert.Equal(0, wiki.Fetches);

        locator.Window = Window(EarringLines, hasLoreTab: true);
        ItemCheckResult description = (await pipeline.CheckAsync(BlankFrame()))[0];

        Assert.False(description.NeedsLoreCapture);
        Assert.Equal("A trophy of the first bashing.", description.Lore);
    }

    // --- fakes ------------------------------------------------------------------------------------------

    private sealed class FakeLocator(LocatedWindow window) : IItemWindowLocator
    {
        public LocatedWindow Window { get; set; } = window;

        public Task<IReadOnlyList<LocatedWindow>> LocateAsync(
            CapturedImage image, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LocatedWindow>>([Window]);
    }


    // --- logging in before a write ----------------------------------------------------------------------

    /// <summary>
    /// **The bug the user hit on 2026-09-30, reproduced.** Logging in used to be each caller's job: the review
    /// screen's data commit did it, and "Save the formatting" did not — so a formatting edit on a page with no data
    /// change (exactly what the 2026-09-29 "offer formatting where nothing is written" work created) reached
    /// `EditAsync` with no session and threw past the handler's `catch` to the dispatcher. The user saw an
    /// unhandled-error dialog claiming they were logged out seconds after starting the tool, when the session had
    /// never been created at all.
    ///
    /// Asserted on **request counts**, the way the rest of this file asserts: a refused write must cost nothing, not
    /// merely fail late. The gate runs before the conflict re-fetch for that reason.
    /// </summary>
    [Fact]
    public async Task TheFormattingCommitWillNotWriteWhenTheGateRefuses()
    {
        (ItemCheckPipeline pipeline, FakeWiki wiki, _) = Build(Window(EarringLines), UntidyPage());
        (FormattingProposal? proposal, _) = await pipeline.PrepareFormattingAsync("Earring of Bashing");
        Assert.NotNull(proposal);

        int fetchesBefore = wiki.Fetches;
        pipeline.BeforeWriting = _ => Task.FromResult<string?>("No bot password is stored.");

        CommitResult commit = await pipeline.CommitFormattingAsync(proposal);

        Assert.Equal(CommitStatus.Failed, commit.Status);
        Assert.Equal("No bot password is stored.", commit.Error);
        Assert.Equal(0, wiki.Edits);
        Assert.Equal(fetchesBefore, wiki.Fetches);
    }

    /// <summary>The data commit honours the same gate, since the rule now lives in one place rather than in the
    /// handler that happened to remember it.</summary>
    [Fact]
    public async Task TheDataCommitWillNotWriteWhenTheGateRefuses()
    {
        (ItemCheckPipeline pipeline, FakeWiki wiki, CheckedItemsLedger ledger) =
            Build(Window(EarringLines), UntidyPage());
        ItemCheckResult result = (await pipeline.CheckAsync(BlankFrame()))[0];

        int fetchesBefore = wiki.Fetches;
        pipeline.BeforeWriting = _ => Task.FromResult<string?>("The wiki rejected the stored bot password.");

        CommitResult commit = await pipeline.CommitAsync(result, result.Edit!.NewWikitext, result.Edit.Summary);

        Assert.Equal(CommitStatus.Failed, commit.Status);
        Assert.Equal("The wiki rejected the stored bot password.", commit.Error);
        Assert.Equal(0, wiki.Edits);
        Assert.Equal(fetchesBefore, wiki.Fetches);

        // A refused write is not a check: the item must come back rather than look handled. Either no row at all or
        // a row that does not mean done satisfies that; what must not happen is a settled one.
        LedgerEntry? row = ledger.Find(result.ItemName);
        Assert.True(row is null || !CheckedItemsLedger.MeansDone(row.Outcome));
    }

    /// <summary>
    /// The negative control. Both tests above would pass against a pipeline that simply never writes, so this
    /// proves the gate is what stopped them — same setup, a gate that allows the write, and the edit lands.
    /// </summary>
    [Fact]
    public async Task AGateThatAllowsTheWriteChangesNothing()
    {
        (ItemCheckPipeline pipeline, FakeWiki wiki, _) = Build(Window(EarringLines), UntidyPage());
        (FormattingProposal? proposal, _) = await pipeline.PrepareFormattingAsync("Earring of Bashing");

        var asked = 0;
        pipeline.BeforeWriting = _ => { asked++; return Task.FromResult<string?>(null); };

        CommitResult commit = await pipeline.CommitFormattingAsync(proposal!);

        Assert.Equal(CommitStatus.Committed, commit.Status);
        Assert.Equal(1, wiki.Edits);
        Assert.Equal(1, asked);
    }

    private sealed class FakeWiki : IMediaWikiClient
    {
        public Dictionary<string, WikiPage> Pages { get; } = new(StringComparer.Ordinal);
        public int Fetches { get; private set; }
        public int Edits { get; private set; }
        public string? LastSummary { get; private set; }
        public bool FailFetches { get; init; }

        /// <summary>An error about the page rather than about the wiki, for the per-window path.</summary>
        public Exception? FetchError { get; init; }

        public Task<WikiPage?> FetchPageAsync(string title, CancellationToken cancellationToken = default)
        {
            Fetches++;
            // What the real client throws when it cannot reach the wiki — the fake has to match, or the tests would
            // be asserting against a contract nothing implements.
            if (FailFetches) throw new WikiUnavailableException("eqlwiki.com could not be reached.");
            if (FetchError is not null) throw FetchError;
            return Task.FromResult(Pages.GetValueOrDefault(title));
        }

        public Task LoginAsync(BotCredentials credentials, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<EditResult> EditAsync(
            string title, string newWikitext, string summary, DateTimeOffset baseTimestamp,
            CancellationToken cancellationToken = default)
        {
            Edits++;
            LastSummary = summary;
            bool unchanged = string.Equals(Pages[title].Wikitext, newWikitext, StringComparison.Ordinal);
            Pages[title] = Pages[title] with { Wikitext = newWikitext, RevisionId = Pages[title].RevisionId + 1 };
            return Task.FromResult(new EditResult(title, Pages[title].RevisionId, unchanged));
        }
    }
}
