using EQLWikiEditorAssistant.Core.Icons;
using EQLWikiEditorAssistant.Core.Items;
using EQLWikiEditorAssistant.Core.Locate;
using EQLWikiEditorAssistant.Core.Imaging;
using EQLWikiEditorAssistant.Core.Glyphs;
using EQLWikiEditorAssistant.Wiki.Analysis;
using EQLWikiEditorAssistant.Wiki.Formatting;
using EQLWikiEditorAssistant.Wiki.Ledger;
using EQLWikiEditorAssistant.Wiki.Mapping;
using EQLWikiEditorAssistant.Wiki.MediaWiki;
using EQLWikiEditorAssistant.Wiki.Wikitext;

namespace EQLWikiEditorAssistant.Pipeline;

/// <summary>
/// The pipeline, in one place: a captured frame in, a reviewable report per item window out.
///
/// This is the orchestration the plan's architecture section describes as steps 2-6 — locate, parse, check
/// eligibility, consult the ledger, fetch, analyze, build the edit — which every previous milestone left "still to
/// wire up" because nothing yet needed it end to end. <c>WikiSpike preview</c> was a hand-rolled version of the same
/// sequence; the differences are that this one is injectable and therefore testable, and that it includes the two
/// steps preview skipped: the ledger and the icon check.
///
/// **It lives in its own assembly on purpose.** The pipeline is the only thing that depends on both the game side
/// (<c>Core</c>: locate, parse, eligibility, icons) and the wiki side (<c>Wiki</c>: lookup, analysis, editor,
/// ledger), and neither of those should ever depend on it. Keeping it out of the WPF project is what lets the whole
/// sequence be tested with fakes, on a portable target, with no window and no screenshot.
///
/// **Checking never writes to the wiki.** <see cref="CheckAsync"/> can only fetch. Committing is
/// <see cref="CommitAsync"/>, one item at a time, after the user has looked at it.
///
/// **The ledger rule that is easy to get backwards**: an occluded or ineligible window gets *no row at all*, not a
/// `Skipped` one. It was never actually checked, so the next capture must treat it as new; a row would make it look
/// handled and quietly exclude it from ever being checked properly.
/// </summary>
public sealed class ItemCheckPipeline
{
    private readonly IMediaWikiClient _wiki;
    private readonly IItemWindowLocator _locator;
    private readonly CheckedItemsLedger _ledger;
    private readonly WikiMapping _mapping;
    private readonly IconCache? _icons;
    private readonly IImageDecoder? _decoder;
    private readonly VerifiedPages? _verified;
    private readonly IconLibrary? _iconLibrary;
    private readonly IconLibraryFolder? _iconFiles;

    /// <summary>Lore captured from a Lore-tab window, by item name, waiting for the Description capture that needs
    /// it. Held here rather than by the caller because the two captures are separate frames, and the pipeline is
    /// already the stateful part of this flow.</summary>
    private readonly Dictionary<string, string> _pendingLore = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Locked because a capture runs on a background thread while the review screen stays live.</summary>
    private string? PendingLoreFor(string itemName)
    {
        lock (_pendingLore) return _pendingLore.GetValueOrDefault(itemName);
    }

    /// <param name="icons">The icon cache, or null to skip the icon check. Optional because the check is flag-only:
    /// the tool never writes an icon id, so running without one is degraded rather than wrong.</param>
    /// <param name="decoder">Decodes a downloaded icon. Needed alongside <paramref name="icons"/>.</param>
    /// <param name="verified">The wiki's verified-pages list, or null to say nothing about verification. Optional
    /// for the same reason the icon cache is: the tool only ever reports this and never acts on it.</param>
    /// <param name="iconLibrary">Fingerprints of every icon the game ships, used to identify a new item's icon id.
    /// Null simply leaves <c>lucy_img_ID</c> blank on a generated page, which is the behaviour that existed before
    /// the library did — degraded, not wrong.</param>
    /// <param name="iconFiles">The icon PNGs themselves, for showing a match and for uploading it. Needed alongside
    /// <paramref name="iconLibrary"/>; without it an id can still be identified and written, just not illustrated or
    /// published.</param>
    public ItemCheckPipeline(
        IMediaWikiClient wiki,
        IItemWindowLocator locator,
        CheckedItemsLedger ledger,
        WikiMapping? mapping = null,
        IconCache? icons = null,
        IImageDecoder? decoder = null,
        VerifiedPages? verified = null,
        IconLibrary? iconLibrary = null,
        IconLibraryFolder? iconFiles = null)
    {
        _wiki = wiki ?? throw new ArgumentNullException(nameof(wiki));
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _mapping = mapping ?? WikiMapping.Default;
        _icons = icons;
        _decoder = decoder;
        _verified = verified;
        _iconLibrary = iconLibrary;
        _iconFiles = iconFiles;
    }

    /// <summary>Set for the user's "re-check anyway" action: the ledger is still consulted, but never allowed to
    /// skip the wiki.</summary>
    public bool ReCheckAnyway { get; set; }

    /// <summary>
    /// The UI font the window reader is set to read, so a window drawn in a different one can be refused rather
    /// than misread. Null skips the check (the tests' fakes produce no font evidence at all).
    ///
    /// <b>This must be the same value the reader was given</b>, which is why the composition root sets both from one
    /// variable. The reader cannot refuse a window itself — it only ever sees one line at a time — so the window
    /// verdict (<c>LocatedWindow.DrawnIn</c>) is compared here, where the whole window is known.
    /// </summary>
    public UiFont? ConfiguredFont { get; set; }

    /// <summary>The statuses that never get a ledger row, whatever the user does with them: nothing was read from
    /// the window, or what was read is ineligible. One rule shared by every path that records, so a new status
    /// cannot be excluded by one and recorded by another.</summary>
    private static bool NeverGetsALedgerRow(ItemCheckStatus status) =>
        status is ItemCheckStatus.Occluded or ItemCheckStatus.WrongFont or ItemCheckStatus.UnsupportedSkin
            or ItemCheckStatus.Ineligible;

    /// <summary>
    /// Run before anything is written to the wiki. Returns null when the write may proceed, or a message for the
    /// user explaining why it may not — which both commit methods turn into a <see cref="CommitStatus.Failed"/>
    /// result rather than an exception.
    ///
    /// **This exists because the login was a per-caller responsibility and one caller forgot** (bug found by the
    /// user, 2026-09-30). The review screen's data commit logged in; "Save the formatting" did not, so a formatting
    /// edit on a page with no data change — which is precisely the case the 2026-09-29 "offer formatting where
    /// nothing is written" work created — threw
    /// <c>InvalidOperationException("Log in before editing")</c> straight past the handler's `catch` and out to the
    /// dispatcher. The user saw an unhandled-error dialog saying they were logged out seconds after starting the
    /// tool, when in fact the session had never been created.
    ///
    /// **Same shape as the ledger-fingerprint bug**, and the same fix: the rule belongs in one place the write path
    /// must go through, not copied into each caller. A new write path must call this; it is checked before the
    /// conflict re-fetch so a refused write costs no requests at all.
    ///
    /// Null means no gate, which is what the tools and tests use — <c>WikiSpike</c> logs in explicitly and the
    /// fakes never need a session.
    /// </summary>
    public Func<CancellationToken, Task<string?>>? BeforeWriting { get; set; }

    private async Task<string?> WhyWritingIsNotAllowedAsync(CancellationToken cancellationToken) =>
        BeforeWriting is null ? null : await BeforeWriting(cancellationToken).ConfigureAwait(false);


    /// <summary>
    /// Runs the whole read-only pipeline over one captured frame. Every window found is reported, including the ones
    /// nothing could be done with — a frame may hold several item windows, and a window the tool refused is exactly
    /// what the user needs to be told about.
    ///
    /// <paramref name="progress"/> hears each stage as it starts, so a caller can say what is actually happening
    /// rather than blaming one step's time on another (user, 2026-10-07).
    /// </summary>
    public async Task<IReadOnlyList<ItemCheckResult>> CheckAsync(
        CapturedImage frame, CancellationToken cancellationToken = default, IProgress<CheckProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(frame);

        progress?.Report(new CheckProgress(CheckStage.FindingWindows));
        IReadOnlyList<LocatedWindow> windows = await _locator
            .LocateAsync(frame, cancellationToken).ConfigureAwait(false);

        // Once per frame rather than once per window: every window in a frame is judged against the same list, and
        // it is one small request that its own TTL usually skips anyway.
        if (_verified is not null)
            await _verified.RefreshAsync(cancellationToken).ConfigureAwait(false);

        var results = new List<ItemCheckResult>();
        for (int i = 0; i < windows.Count; i++)
        {
            progress?.Report(new CheckProgress(CheckStage.CheckingWindow, i + 1, windows.Count));
            results.Add(await CheckWindowAsync(frame, windows[i], cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    /// <summary>
    /// Says so when the wiki has not marked this page "Verified for EQLegends".
    ///
    /// **Only when it is unverified, and it never blocks** (user, 2026-09-29). Verification attests that a *whole
    /// page* is accurate — drops, quests and recipes this tool never reads — so the tool has no standing to demand
    /// it, and with only 48 of 744 sampled item pages verified, making it a blocker would leave nearly every item
    /// permanently unsettled. A message on the other branch was considered and rejected by the user for the reason
    /// that decides it: an alert on *every* page, either "not yet verified" or "already verified", is an alert
    /// everyone learns to ignore.
    ///
    /// **Silence when the list could not be read.** A false "this is unverified" would send the user to re-verify a
    /// page that is already done — the same rule the icon check follows when it cannot see an icon.
    /// </summary>
    private void AddVerificationNotice(string pageTitle, List<string> warnings)
    {
        if (_verified?.IsVerified(pageTitle) is not false) return;

        // **Short, because the strip's length is what decides whether any of it gets read** (user, 2026-09-29). The
        // long form explained what verification covers and how to do it; the user knows both, and the sentence was
        // competing with the warnings that actually need judging. The page title is dropped too — the panel is headed
        // by the item and links to its page, so there is no ambiguity about which page this is about.
        warnings.Add(NotVerifiedNotice);
    }

    /// <summary>
    /// The user has gone to the wiki, perhaps to verify a page, so the next capture re-reads the verified list rather
    /// than trusting one read minutes ago (user, 2026-10-07).
    /// </summary>
    public void ExpectVerificationChange() => _verified?.MarkStale();

    /// <summary>The verification notice. Public so the review screen can show it as a badge beside the item's name
    /// (user, 2026-10-07) rather than as one more bar in the strip — see <see cref="ItemCheckResult.NotVerifiedForEql"/>.</summary>
    public const string NotVerifiedNotice = "Wiki page not verified for EQL";

    private async Task<ItemCheckResult> CheckWindowAsync(
        CapturedImage frame, LocatedWindow window, CancellationToken cancellationToken)
    {
        CapturedImage crop = frame.Crop(window.Bounds);

        // Found, but in a skin whose windows cannot be read. Said outright rather than reported as covered, which it
        // would otherwise look like, and which moving the window would not fix.
        if (window.InOtherSkin)
            return new ItemCheckResult
            {
                Status = ItemCheckStatus.UnsupportedSkin,
                ItemName = "",
                WindowImage = crop,
                Warnings =
                [
                    "This window is drawn in another of the game's UI skins. Only default_modern can be read: switch " +
                    "the game's UI skin to it, then capture again.",
                ],
            };

        // Nothing read from a partly-covered window can be trusted. The parser's own title-vs-content check is a
        // second line of defence, not a reason to proceed past this one.
        if (window.PossiblyOccluded)
            return new ItemCheckResult
            {
                Status = ItemCheckStatus.Occluded,
                ItemName = "",
                WindowImage = crop,
                Warnings = ["This window is partly covered, so nothing was read from it. Move it clear and capture again."],
            };

        // A window drawn in another UI font than the reader was set to is refused the same way, and for the same
        // reason: what was read from it cannot be trusted. The font decides what the bare bar means, so an Arial
        // capture read as EQL Wiki Editor Assistant turns every capital I into an l — "Iron" becomes "lron", a page that
        // does not exist, which the tool would then offer to create.
        //
        // Both remedies are offered, the tool's first (user, 2026-10-07): it is one click in Settings, while the game's
        // font is a per-character, per-loadout setting that the user chose deliberately.
        if (ConfiguredFont is { } configured && window.DrawnIn is { } drawn && drawn != configured)
            return new ItemCheckResult
            {
                Status = ItemCheckStatus.WrongFont,
                ItemName = "",
                WindowImage = crop,
                Warnings =
                [
                    $"Drawn in {UiFonts.DisplayName(drawn)}, but Settings > UI font is set to " +
                    $"{UiFonts.DisplayName(configured)}. Switch that setting to {UiFonts.DisplayName(drawn)}, or switch the game's " +
                    $"font to {UiFonts.DisplayName(configured)}, then capture again.",
                ],
            };

        ParsedItem item = ItemParser.Parse(window.Lines, window.ActiveTab);
        var warnings = new List<string>(item.Warnings);

        if (item.TitleContentNameMismatch)
            warnings.Add(
                "The name in the title bar does not match the one in the window body, which usually means the " +
                "capture was obscured. Treat this item's data as unreliable.");

        // **Before the Lore branch, not after it** (bug found by the user, 2026-10-07, on `Arydryidriyorn +5`). The
        // Lore tab shows the level in its title like the Description tab does, and a levelled item's Lore capture
        // used to be accepted — asking the user for a Description capture the pipeline would then refuse. A Lore
        // window has no exaltation rows, so for it only the level can block.
        ItemEligibility eligibility = ItemEligibility.Check(item);
        if (!eligibility.IsEligible)
            return new ItemCheckResult
            {
                // No ledger row — see ItemEligibility.ShouldWriteLedgerEntry and this type's comment.
                Status = ItemCheckStatus.Ineligible,
                ItemName = item.Name,
                Item = item,
                WindowImage = crop,
                Eligibility = eligibility,
                Warnings = warnings,
            };

        // A Lore capture is a different view of the same item — prose, no stats. It contributes its text and nothing
        // else; the Description capture is what gets analyzed.
        if (window.ActiveTab == ItemWindowTab.Lore)
        {
            if (!string.IsNullOrWhiteSpace(item.Lore))
                lock (_pendingLore) _pendingLore[item.Name] = item.Lore!;
            else warnings.Add("Failed to parse lore tab.");

            return new ItemCheckResult
            {
                Status = ItemCheckStatus.LoreRecorded,
                ItemName = item.Name,
                Item = item,
                WindowImage = crop,
                Lore = item.Lore,
                Warnings = warnings,
            };
        }

        // Read from the frame, not the crop: ItemIconReader works in frame coordinates. It has to happen before the
        // ledger is consulted, because the icon is part of the fingerprint the ledger is keyed on.
        IconFingerprint? capturedIcon =
            ItemIconReader.TryRead(frame, window, out IconFingerprint read, out string? noIconBecause) ? read : null;
        string? iconUnreadableNote =
            noIconBecause is null ? null : $"The icon was not compared: {noIconBecause}.";

        // Cropped whatever the verdict: the review screen shows it beside the wiki's copy so the user can settle by
        // eye anything the comparison declines or gets wrong. The icon's own cell rather than the fingerprinted strip,
        // so it shows at the same scale as the wiki's file — see ItemIconReader.IconCell.
        CapturedImage? iconCrop = window.ActiveTab == ItemWindowTab.Description
            ? frame.Crop(new Rect(
                window.Bounds.X + ItemIconReader.IconCell.X, window.Bounds.Y + ItemIconReader.IconCell.Y,
                ItemIconReader.IconCell.Width, ItemIconReader.IconCell.Height))
            : null;

        string? lore = PendingLoreFor(item.Name);
        bool needsLore = window.HasLoreTab && lore is null;

        string fingerprint = ItemFingerprint.Compute(item, capturedIcon, lore);

        // Before any network call — that is the entire point of the ledger.
        LedgerVerdict verdict = _ledger.Consult(
            item.Name, fingerprint, _mapping.Version, CheckedItemsLedger.ItemKind, ReCheckAnyway);

        if (verdict == LedgerVerdict.AlreadyDone)
        {
            // Worth saying even here, where no wiki request happens: the list is local, so the answer is free, and an
            // item settled weeks ago is exactly the one whose verification is most likely still outstanding.
            AddVerificationNotice(_ledger.Find(item.Name)?.WikiPageTitle ?? item.Name, warnings);

            // The icon travels with it although nothing here compares it. Re-checking this result from the review
            // screen goes through ReanalyzeAsync, which fingerprints whatever icon the result carries — and a row
            // fingerprinted without the icon never matches the next capture, so the item would come back forever
            // (the 2026-09-29 bug, reached by a new route).
            return new ItemCheckResult
            {
                Status = ItemCheckStatus.AlreadyChecked,
                ItemName = item.Name,
                Item = item,
                WindowImage = crop,
                CapturedIcon = capturedIcon,
                CapturedIconImage = iconCrop,
                IconNote = iconUnreadableNote,
                LedgerVerdict = verdict,
                LedgerRow = _ledger.Find(item.Name),
                Lore = lore,
                LoreNotCaptured = needsLore,
                Warnings = warnings,
            };
        }

        try
        {
            return await AnalyzeAgainstWikiAsync(
                    item, crop, verdict, capturedIcon, iconCrop, iconUnreadableNote, lore, needsLore, fingerprint, warnings,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is MediaWikiException or HttpRequestException or TaskCanceledException)
        {
            // A WikiUnavailableException is deliberately NOT caught here: if the wiki is gone there is nothing for
            // the remaining windows to be checked against, so it aborts the frame instead of producing one identical
            // failure per item (user, 2026-09-29).
            // One page that fails on its own merits must not abandon the other windows in the frame — and must not write a ledger row
            // either, because nothing was checked.
            return new ItemCheckResult
            {
                Status = ItemCheckStatus.Failed,
                ItemName = item.Name,
                Item = item,
                WindowImage = crop,
                LedgerVerdict = verdict,
                Warnings = warnings,
                Error = ex.Message,
            };
        }
    }

    /// <summary>
    /// Where the effect links on a written line should point, for the effects this page has no line for.
    ///
    /// **Only the lines the tool writes itself are asked about.** An existing line's target is the page's own
    /// editorial choice and is preserved rather than looked up — see <c>EffectLine.Render</c>. So an item with no
    /// effects, or whose page already carries them, costs no request at all, and the one request this can make is
    /// batched across however many effects need it.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>> ResolveEffectLinksAsync(
        ParsedItem item, ItemPageDocument? page, CancellationToken cancellationToken) =>
        await EffectPageLookup.ResolveLinkTargetsAsync(
                _wiki, ItemPageAnalyzer.EffectsNeedingALinkTarget(item, page, _mapping), cancellationToken)
            .ConfigureAwait(false);

    private async Task<ItemCheckResult> AnalyzeAgainstWikiAsync(
        ParsedItem item,
        CapturedImage crop,
        LedgerVerdict verdict,
        IconFingerprint? capturedIcon,
        CapturedImage? iconCrop,
        string? iconUnreadableNote,
        string? lore,
        bool needsLore,
        string fingerprint,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        // **The lore joins the item here, once, before anything branches on what the wiki said.** It came from a
        // separate capture of the Lore tab, so the parsed Description item does not carry it, and *every* path
        // below that reasons about item data needs the complete item — the analyzer and the page generator alike.
        //
        // Attaching it at the analyzer's own call site instead is exactly what hid the bug the user found
        // (2026-10-02): the creation branch a few lines down was handed the bare `item`, so a brand-new page was
        // generated with nothing in `notes` even though the lore had been captured — and creation is the case where
        // that costs most, because there is no existing page whose lore the tool was deliberately leaving alone.
        // One rule, one home: the same lesson as the ledger fingerprint and the login gate.
        if (lore is not null) item = item with { Lore = lore };

        ItemPageLookupResult lookup = await ItemPageLookup
            .FindAsync(_wiki, item.Name, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (lookup.Warning is { } lookupWarning) warnings.Add(lookupWarning);

        if (lookup.TreatAsNew)
        {
            // Identified here rather than up with the icon *comparison*, because this question only exists on this
            // branch: there is no page, so there is nothing to compare against and an id to propose instead.
            // **No warning is raised here**, deliberately. An unidentified icon and a blank `lucy_img_ID` are the same
            // fact, and the review screen already has a bar for the second — adding one here produced two bars saying
            // overlapping things, which is exactly how the warning strip got too long to read before (user,
            // 2026-09-29). The screen's own message names the closest candidate when there is one.
            IconSuggestion? suggestion = await SuggestIconAsync(capturedIcon, cancellationToken).ConfigureAwait(false);

            // A generated page needs a link target for every effect it writes, since it has no existing line whose
            // target could be preserved. Resolved only where a page will actually be proposed, so the request is not
            // spent on an item the tool has already decided not to create.
            ProposedPage? creation = null;
            if (lookup.MayCreate)
            {
                IReadOnlyDictionary<string, string> effectLinks =
                    await ResolveEffectLinksAsync(item, null, cancellationToken).ConfigureAwait(false);
                creation = ItemPageCreator.Build(
                    item, item.Name, _mapping,
                    suggestion is { IsConfident: true } ? suggestion.IconId : null, effectLinks);
            }

            // Recorded, because "not on the wiki" is a real finding — but as NotOnWiki, which never lets a later
            // capture skip the fetch: somebody may have created the page since.
            RecordLedgerEntry(item.Name, null, CheckOutcome.NotOnWiki, null, fingerprint);
            return new ItemCheckResult
            {
                Status = ItemCheckStatus.NotOnWiki,
                // The page the tool would create, built only where creating is safe. `MayCreate` is narrower than
                // "this item is new": a quote-variant candidate almost certainly *is* this item's page under a
                // misspelt name, and a title-illegal name has no title to create at. Both would produce a page
                // nobody on this wiki can delete, so both get the warning and no proposal.
                // Only a *confident* match reaches the wikitext. An unsure one still travels on the result, so the
                // review screen can offer its shortlist — the id is left blank and reported as a gap, which keeps
                // the item coming back until a human settles it.
                Creation = creation,
                IconSuggestion = suggestion,
                ItemName = item.Name,
                Item = item,
                WindowImage = crop,
                LedgerVerdict = verdict,
                Lookup = lookup,
                // The captured icon belongs here too: there is no wiki icon to compare against, but an item with no
                // page is precisely the one whose artwork the user has to look at — it is how they choose its
                // lucy_img_ID when they create the page. Dropping it left the one case with nothing to show.
                CapturedIcon = capturedIcon,
                CapturedIconImage = iconCrop,
                IconNote = iconUnreadableNote,
                Lore = lore,
                NeedsLoreCapture = needsLore,
                Warnings = warnings,
            };
        }

        WikiPage wikiPage = lookup.Page!;
        ItemPageDocument? page = ItemPageDocument.Parse(wikiPage.Wikitext);
        if (page is null)
        {
            RecordLedgerEntry(item.Name, wikiPage.Title, CheckOutcome.Flagged, wikiPage.RevisionId, fingerprint);
            return new ItemCheckResult
            {
                Status = ItemCheckStatus.NotAnItemPage,
                ItemName = item.Name,
                Item = item,
                WindowImage = crop,
                LedgerVerdict = verdict,
                Lookup = lookup,
                CapturedIcon = capturedIcon,
                CapturedIconImage = iconCrop,
                IconNote = iconUnreadableNote,
                Page = wikiPage,
                Warnings =
                [
                    .. warnings,
                    $"'{wikiPage.Title}' exists but carries no Itempage template call, so there is nothing to compare.",
                ],
            };
        }

        // `item` already carries the lore — see the top of this method for why that happens once, up there.
        ItemPageAnalysis analysis = ItemPageAnalyzer.Analyze(
            item, page, wikiPage.Title, _mapping,
            effectLinkTargets: await ResolveEffectLinksAsync(item, page, cancellationToken).ConfigureAwait(false),
            loreMayConfuseLAndI: ConfiguredFont == UiFont.Arial);
        ProposedEdit edit = ItemPageEditor.BuildEdit(page, analysis, _mapping);

        (IconComparison? icon, string? iconNote, CapturedImage? wikiIcon) =
            await CompareIconAsync(capturedIcon, iconUnreadableNote, page.IconId, cancellationToken).ConfigureAwait(false);

        // **And when the comparison says the page is pointing at the wrong artwork, say which one is right** (user,
        // 2026-10-02). Flagging alone left the user to find the id by hand — the tool had just searched 11,562
        // icons to check this one and threw the answer away. It still *proposes* rather than acts: nothing reaches
        // the wikitext until the user has looked at the two images and pressed the button, which is the same human
        // confirmation that made the creation path's matcher acceptable.
        //
        // Only these two cases, deliberately. A page with no id at all has the same gap and no risk. A comparison
        // that could not judge — no file on the wiki, too little ink, too low contrast — is *not* evidence the id is
        // wrong, so offering a different one there would be guessing.
        IconSuggestion? iconFix =
            icon is { Matches: false } || string.IsNullOrWhiteSpace(page.IconId)
                ? await SuggestIconAsync(capturedIcon, cancellationToken).ConfigureAwait(false)
                : null;

        // The library agreeing with the page is a different finding: the id is right and the *file* holds the wrong
        // artwork, which is the 31-file numbering divergence the icon audit found. Re-uploading over it is the one
        // act this tool refuses, so there is nothing to offer and the mismatch stays flagged.
        if (iconFix is not null && string.Equals(iconFix.IconId, page.IconId, StringComparison.Ordinal))
            iconFix = null;

        if (needsLore)
            warnings.Add(
                "This item has a Lore tab that has not been captured. Switch to it and capture again.");

        AddVerificationNotice(wikiPage.Title, warnings);

        var result = new ItemCheckResult
        {
            Status = edit.HasChanges ? ItemCheckStatus.EditProposed : ItemCheckStatus.AlreadyCorrect,
            ItemName = item.Name,
            Item = item,
            WindowImage = crop,
            LedgerVerdict = verdict,
            Lookup = lookup,
            Analysis = analysis,
            Edit = edit,
            CapturedIcon = capturedIcon,
            CapturedIconImage = iconCrop,
            WikiIconImage = wikiIcon,
            Icon = icon,
            IconNote = iconNote,
            IconSuggestion = iconFix,
            Page = wikiPage,
            Lore = lore,
            NeedsLoreCapture = needsLore,
            Warnings = warnings,

            // Only when the data already agrees. A page the tool wants to change is one whose formatting would be
            // rebuilt by that change anyway, and offering both at once would bury the data diff under a reflow.
            Formatting = edit.HasChanges ? null : PrepareFormatting(wikiPage).Proposal,
        };

        // A page that already agrees is settled here and now — there is nothing for the user to approve. Anything
        // wanting a human is Flagged instead, which never counts as done however unchanged the next capture is.
        //
        // **A page this tool edited or created keeps saying so** when a later check finds it agreeing, which is the
        // expected sequel to a save: the review's "Edit again" re-checks the page it has just written. Recording that
        // as Matched would erase a Created row — the one most worth finding again, since its drops and vendors are
        // what nobody has filled in yet.
        if (result.Status == ItemCheckStatus.AlreadyCorrect)
        {
            CheckOutcome? earlier = _ledger.Find(item.Name)?.Outcome;
            RecordLedgerEntry(
                item.Name,
                wikiPage.Title,
                result.NeedsAttention ? CheckOutcome.Flagged
                    : earlier is CheckOutcome.Edited or CheckOutcome.Created ? earlier.Value
                    : CheckOutcome.Matched,
                wikiPage.RevisionId,
                fingerprint);
        }

        return result;
    }

    /// <summary>
    /// Compares the captured icon against the file the page points at. **Flag-only** — a mismatch is reported and
    /// never acted on, because the tool cannot know whether the page is wrong or the capture caught something odd.
    ///
    /// Every "cannot judge" path returns a note rather than a verdict. An icon check that cannot see the icon has to
    /// stay silent: a false "wrong icon" sends the user hunting for a problem that is not there.
    /// </summary>
    /// <summary>
    /// Identifies a captured icon against the whole library — for an item with no page, and for one whose page
    /// points at the wrong artwork.
    ///
    /// **A search rather than a comparison, which is a harder question than it looks.** The icon check elsewhere only
    /// has to separate one right answer from one wrong one; this has to beat 11,561 wrong ones. It gates on the gap
    /// to the runner-up rather than on the absolute score — see <see cref="IconLibrary.ConfidentMargin"/>, where the
    /// measurement says an absolute cutoff loses correct answers without catching anything extra.
    /// </summary>
    private async Task<IconSuggestion?> SuggestIconAsync(
        IconFingerprint? captured, CancellationToken cancellationToken)
    {
        if (_iconLibrary is null || captured is null) return null;
        if (_iconLibrary.Identify(captured) is not { } found) return null;

        // Loaded only for the winner, not for the shortlist: this is a file read per capture, and the thing the user
        // confirms by eye is the match itself.
        CapturedImage? image = null;
        if (_iconFiles is not null && _decoder is not null &&
            await _iconFiles.ReadAsync(found.IconId, cancellationToken).ConfigureAwait(false) is { } bytes)
        {
            image = await _decoder
                .DecodeAsync(bytes, AlphaComposite.GameBackground, cancellationToken).ConfigureAwait(false);
        }

        // "Does the wiki have this icon?" is already the icon cache's question — a null answer from it means the file
        // is absent, and it negative-caches that for six hours so a creation session does not re-ask on every
        // capture. Asking a second way here would be a second home for the same question.
        bool? onWiki = null;
        if (_icons is not null)
        {
            try
            {
                onWiki = await _icons.GetAsync(found.IconId, cancellationToken).ConfigureAwait(false) is not null;
            }
            catch (MediaWikiException)
            {
                // The wiki answered about this one file and would not say. Unknown is the honest value, and
                // CanUpload treats it as "do not offer" — publishing over a file that may exist is the one act
                // nobody here could undo.
            }
        }

        return new IconSuggestion(
            found.IconId, found.Distance, found.Margin, found.IsConfident, found.Candidates, image, onWiki);
    }

    /// <summary>
    /// Renders the text on screen as the wiki would, for the review's Preview page — the page being edited, or the one
    /// about to be created, under its own title so the item box's links resolve as they will once saved.
    ///
    /// **Only on the user's request.** It sends the text to the wiki, so it is never part of a check: nothing goes to
    /// the wiki unless the user did something they would expect to send it (user, 2026-10-07). Writes nothing, and
    /// touches neither the ledger nor the page.
    /// </summary>
    public Task<RenderedPage> PreviewAsync(
        ItemCheckResult result, string wikitext, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        string title = result.Page?.Title ?? result.Creation?.Title
            ?? throw new InvalidOperationException("Only a page being edited or created can be previewed.");
        return _wiki.RenderAsync(title, wikitext, cancellationToken);
    }

    /// <summary>
    /// Uploads a new item icon to the wiki, from the library's own file.
    ///
    /// **It uploads the file whose name matches the id the page carries**, which is the invariant the whole feature
    /// rests on: the artwork and the <c>lucy_img_ID</c> are written as a pair, so the page renders what the capture
    /// showed even where the wiki's historical numbering differs from the game's.
    ///
    /// Refuses rather than overwrites — the client omits <c>ignorewarnings</c>, so an existing file comes back as a
    /// failure. That is the right way round here: only an admin on this wiki can delete a file, so a wrong
    /// overwrite replaces the original with nothing this tool's user can put back themselves.
    /// </summary>
    public async Task<IconUploadResult> UploadIconAsync(string iconId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(iconId);

        if (_iconFiles is null)
            return new IconUploadResult(false, null, "There is no icon library configured, so there is no file to upload.");

        if (await WhyWritingIsNotAllowedAsync(cancellationToken).ConfigureAwait(false) is { } blocked)
            return new IconUploadResult(false, null, blocked);

        byte[]? bytes = await _iconFiles.ReadAsync(iconId, cancellationToken).ConfigureAwait(false);
        if (bytes is null)
            return new IconUploadResult(false, null, $"The icon library has no file for icon {iconId}.");

        string fileName = IconLibraryFolder.WikiFileNameFor(iconId);

        try
        {
            UploadResult uploaded = await _wiki.UploadFileAsync(
                fileName,
                bytes,
                // Matches the convention the wiki's own 796 imported icon files use, which all read
                // "== Summary ==\nImporting file", while saying the one thing that is actually true of this one.
                $"== Summary ==\nItem icon {iconId}, from the game's own asset files.",
                $"Item icon {iconId}",
                cancellationToken).ConfigureAwait(false);

            // The cache said this file was missing; it is not missing any more, and a stale negative entry would
            // keep offering the upload for six hours.
            _icons?.Forget(iconId);

            return new IconUploadResult(true, uploaded.Url, null);
        }
        catch (MediaWikiException ex)
        {
            return new IconUploadResult(false, null, ex.Message);
        }
    }

    private async Task<(IconComparison?, string?, CapturedImage?)> CompareIconAsync(
        IconFingerprint? captured, string? unreadableNote, string? iconId, CancellationToken cancellationToken)
    {
        if (_icons is null || _decoder is null) return (null, null, null);
        if (string.IsNullOrWhiteSpace(iconId))
            return (null, "The page has no lucy_img_ID, so there is no icon to compare against.", null);

        byte[]? bytes;
        try
        {
            bytes = await _icons.GetAsync(iconId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return (null, $"The icon could not be fetched: {ex.Message}", null);
        }

        if (bytes is null) return (null, $"The wiki has no File:item_{iconId}.png yet.", null);

        CapturedImage wikiIcon;
        try
        {
            // The decoder composites over the game's own background grey, so both sides are the same sprite on the
            // same backdrop — see AlphaComposite for why that is not a detail.
            wikiIcon = await _decoder.DecodeAsync(bytes, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (null, $"The wiki's icon {iconId} could not be decoded: {ex.Message}", null);
        }

        if (!IconHasher.TryFingerprint(
                wikiIcon, new Rect(0, 0, wikiIcon.Width, wikiIcon.Height), out IconFingerprint onWiki))
            return (null, $"The wiki's icon {iconId} has too little ink to compare.", wikiIcon);

        if (!onWiki.IsComparable)
            return (null, $"The wiki's icon {iconId} is too low-contrast to judge — compare them by eye below.", wikiIcon);

        // The wiki image comes back even when the capture could not be fingerprinted, so the user can still see both.
        return captured is null
            ? (null, unreadableNote, wikiIcon)
            : (new IconComparison(iconId!, captured, onWiki, captured.CorrelationDistanceTo(onWiki)), null, wikiIcon);
    }

    /// <summary>
    /// Writes the user's approved edit, then records the outcome.
    ///
    /// **It re-fetches first and refuses if the page has moved on.** Our text was spliced into the revision the check
    /// read, so if somebody else has edited since, saving it would revert them. `basetimestamp` is sent as well and
    /// is the wiki's own guard, but MediaWiki merges what it can — and this tool's edits are wholesale parameter
    /// replacements, exactly the shape that merges cleanly while still discarding somebody's work.
    /// </summary>
    /// <param name="wikitext">The text to save — normally <c>result.Edit.NewWikitext</c>, but the review UI lets the
    /// user amend it, and what they see is what must be written.</param>
    public async Task<CommitResult> CommitAsync(
        ItemCheckResult result,
        string wikitext,
        string summary,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(wikitext);
        ArgumentException.ThrowIfNullOrWhiteSpace(summary);
        if (result.Page is null || result.Edit is null)
            throw new InvalidOperationException("This result has no page or no proposed edit, so there is nothing to commit.");

        string title = result.Page.Title;

        if (await WhyWritingIsNotAllowedAsync(cancellationToken).ConfigureAwait(false) is { } blocked)
            return new CommitResult(CommitStatus.Failed, Error: blocked);

        try
        {
            WikiPage? current = await _wiki.FetchPageAsync(title, cancellationToken).ConfigureAwait(false);
            if (current is null)
                return new CommitResult(CommitStatus.Failed, Error: $"'{title}' no longer exists.");

            if (!string.Equals(current.Wikitext, result.Edit.OriginalWikitext, StringComparison.Ordinal))
                return new CommitResult(
                    CommitStatus.PageChangedSinceCheck,
                    Error: $"'{title}' has been edited since this item was checked (it is now revision " +
                           $"{current.RevisionId}). Nothing was written — capture the item again so the edit is " +
                           "rebuilt against the new text.");

            EditResult edit = await _wiki
                .EditAsync(title, wikitext, summary, current.Timestamp, cancellationToken).ConfigureAwait(false);

            // **A commit does not settle a judgement the tool declined to make.** Writing a corrected AC says
            // nothing about a lore difference the user was warned of on the same screen, and recording `Edited`
            // would count the whole item as done and never raise it again. `Flagged` keeps it coming back; the user
            // settles it deliberately with RecordCheckedByHand. This is the same rule a page that already agrees
            // follows, and it has to be, or the outcome would depend on whether some unrelated stat also changed.
            RecordLedgerEntry(
                result.ItemName,
                title,
                result.NeedsAttention ? CheckOutcome.Flagged
                    : edit.NoChange ? CheckOutcome.Matched
                    : CheckOutcome.Edited,
                edit.NewRevisionId ?? current.RevisionId,
                FingerprintOf(result));

            // The formatting pass runs automatically after any change, as the user specified — but as its own
            // proposal, for its own commit. Never folded into this one.
            (FormattingProposal? formatting, IReadOnlyList<string> notes) =
                await PrepareFormattingAsync(title, cancellationToken).ConfigureAwait(false);

            return new CommitResult(
                edit.NoChange ? CommitStatus.NoChange : CommitStatus.Committed,
                edit.NewRevisionId,
                Formatting: formatting,
                FormattingNotes: notes);
        }
        catch (MediaWikiException ex)
        {
            return new CommitResult(CommitStatus.Failed, Error: $"The wiki refused the edit ({ex.Code}): {ex.Message}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new CommitResult(CommitStatus.Failed, Error: ex.Message);
        }
    }

    /// <summary>
    /// Creates the page for an item the wiki has never heard of, then records the outcome.
    ///
    /// **It does not re-fetch first, unlike <see cref="CommitAsync"/>, because the wiki enforces this one itself.**
    /// An edit has to re-read the page to be sure our splice still applies to it; a creation only has to be sure
    /// the page is still absent, and `createonly=1` makes the wiki refuse otherwise. That is a stronger guarantee
    /// than a check here could give, since nothing can happen between the wiki's own check and its own write.
    ///
    /// **The outcome is read back off the text that was actually written.** The review screen is editable, so the
    /// icon ID may have been typed in — or left blank, which is allowed (user, 2026-10-01: warn, do not block).
    /// A page created with a gap still in it is recorded `Flagged`, not `Created`, so it keeps coming back until
    /// somebody fills it. Reading the proposal instead would settle a page on the strength of what the tool
    /// suggested rather than what went to the wiki.
    /// </summary>
    /// <param name="wikitext">The text to save — normally <c>result.Creation.Wikitext</c>, but what the user sees
    /// is what must be written.</param>
    public async Task<CommitResult> CreateAsync(
        ItemCheckResult result,
        string wikitext,
        string summary,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(wikitext);
        ArgumentException.ThrowIfNullOrWhiteSpace(summary);
        if (result.Creation is null)
            throw new InvalidOperationException("This result has no proposed page, so there is nothing to create.");

        string title = result.Creation.Title;

        if (await WhyWritingIsNotAllowedAsync(cancellationToken).ConfigureAwait(false) is { } blocked)
            return new CommitResult(CommitStatus.Failed, Error: blocked);

        try
        {
            EditResult created = await _wiki
                .CreatePageAsync(title, wikitext, summary, cancellationToken).ConfigureAwait(false);

            IReadOnlyList<string> gaps = ItemPageCreator.GapsIn(wikitext, _mapping);

            RecordLedgerEntry(
                result.ItemName,
                title,
                gaps.Count > 0 || result.NeedsAttention ? CheckOutcome.Flagged : CheckOutcome.Created,
                created.NewRevisionId,
                FingerprintOf(result));

            // The formatting follow-up runs here too, for the reason the user gave when asking for creation
            // (2026-10-01): the generated text is already laid out, so this normally finds nothing — but the box is
            // editable and a hand-edit can mangle it, which is exactly when a formatting pass is worth offering.
            (FormattingProposal? formatting, IReadOnlyList<string> notes) =
                await PrepareFormattingAsync(title, cancellationToken).ConfigureAwait(false);

            return new CommitResult(
                CommitStatus.Committed,
                created.NewRevisionId,
                Formatting: formatting,
                FormattingNotes: notes);
        }
        catch (MediaWikiException ex)
        {
            // articleexists is the one worth wording plainly: it means somebody created the page between the check
            // and this write, which is the case createonly exists to catch.
            return new CommitResult(
                CommitStatus.Failed,
                Error: string.Equals(ex.Code, "articleexists", StringComparison.Ordinal)
                    ? $"'{title}' now exists — somebody created it since this item was checked. Nothing was " +
                      "written. Capture the item again to check it against the new page."
                    : $"The wiki refused to create the page ({ex.Code}): {ex.Message}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new CommitResult(CommitStatus.Failed, Error: ex.Message);
        }
    }

    /// <summary>
    /// Records that the user looked at an item and chose not to act. `Skipped` never counts as done, so the item
    /// comes back on the next capture — which is the point: skipping is "not now", not "this is fine".
    /// </summary>
    public void RecordSkipped(ItemCheckResult result, string? note = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (NeverGetsALedgerRow(result.Status)) return;
        if (string.IsNullOrWhiteSpace(result.ItemName)) return;

        RecordLedgerEntry(
            result.ItemName, result.Page?.Title, CheckOutcome.Skipped, result.Page?.RevisionId,
            FingerprintOf(result), note);
    }

    /// <summary>
    /// Re-runs the wiki half of the pipeline for an item already checked, picking up anything learned since —
    /// in practice, lore from the second capture of the two-capture flow.
    ///
    /// **It exists so the lore capture joins the item already on screen instead of arriving as its own result.**
    /// The parsed item and its icon have not changed, so nothing is re-read from pixels; only the comparison against
    /// the page is redone, now with a complete capture. Without this the user would have to capture the Description
    /// tab a second time for the lore to reach the edit.
    ///
    /// <paramref name="refreshVerification"/> is for a re-analysis the user asked for ("Refresh wiki data", "Edit
    /// again"): it re-reads the verified-pages list too, whatever its age, since marking the page verified on the wiki
    /// is one of the things the user may have just done (bug found by the user, 2026-10-07).
    /// </summary>
    public async Task<ItemCheckResult> ReanalyzeAsync(
        ItemCheckResult previous, CancellationToken cancellationToken = default, bool refreshVerification = false)
    {
        ArgumentNullException.ThrowIfNull(previous);
        if (previous.Item is not { } item)
            throw new InvalidOperationException("This result has no parsed item, so there is nothing to re-analyze.");

        if (refreshVerification && _verified is not null)
            await _verified.RefreshAsync(evenIfRecent: true, cancellationToken).ConfigureAwait(false);

        string? lore = PendingLoreFor(item.Name);
        string fingerprint = ItemFingerprint.Compute(item, previous.CapturedIcon, lore);

        try
        {
            return await AnalyzeAgainstWikiAsync(
                    item,
                    previous.WindowImage!,
                    previous.LedgerVerdict,
                    previous.CapturedIcon,
                    previous.CapturedIconImage,
                    // The icon has not been re-read, so whatever was said about it still holds.
                    previous.IconNote,
                    lore,
                    needsLore: lore is null && (previous.NeedsLoreCapture || previous.LoreNotCaptured),
                    fingerprint,
                    [.. item.Warnings],
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is MediaWikiException or HttpRequestException or TaskCanceledException)
        {
            return previous with { Status = ItemCheckStatus.Failed, Error = ex.Message };
        }
    }

    /// <summary>
    /// Fetches a page as it now stands and asks the formatting pass what it would do to it.
    ///
    /// **It formats what is actually on the wiki, not what we think we wrote.** MediaWiki normalizes a saved page
    /// (trailing whitespace, for one), so formatting our own submitted text would propose an edit against a revision
    /// that does not exist. It also means this is usable on its own, for a page nobody has just edited.
    ///
    /// Returns no proposal when the page is already laid out, when the formatter declined, or when there is nothing
    /// to format — with the formatter's own notes either way, because "why is this page still untidy" is a question
    /// the user will have and the answer is usually "it still carries legacy flags".
    /// </summary>
    /// <summary>
    /// Runs the formatter over a page already in hand, with no wiki request.
    ///
    /// **For the cases where nothing is being written** (user, 2026-09-29): a page that already matches the capture,
    /// and one the user has settled by hand. Both leave the page untouched, so the copy just fetched *is* the current
    /// one and re-reading it would be a request for nothing — unlike after a commit, where MediaWiki normalizes what
    /// was saved and the formatter has to see the result.
    ///
    /// **Deliberately not offered after "Skip for now".** Skipping means the data question is unanswered, and the
    /// whole reason formatting runs second is that the formatter should never have to understand a page the data pass
    /// has not modernized yet.
    /// </summary>
    public (FormattingProposal? Proposal, IReadOnlyList<string> Notes) PrepareFormatting(WikiPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        PrettifyResult formatting = ItemPagePrettifier.Format(page.Wikitext, _mapping);
        if (!formatting.IsSafe) return (null, formatting.Refusals);
        if (!formatting.Changed) return (null, formatting.Notes);

        return (
            new FormattingProposal(page.Title, page.Wikitext, formatting.Formatted, formatting.Notes, page.Timestamp),
            formatting.Notes);
    }

    public async Task<(FormattingProposal? Proposal, IReadOnlyList<string> Notes)> PrepareFormattingAsync(
        string title, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        try
        {
            WikiPage? current = await _wiki.FetchPageAsync(title, cancellationToken).ConfigureAwait(false);
            return current is null ? (null, []) : PrepareFormatting(current);
        }
        catch (Exception ex) when (ex is MediaWikiException or HttpRequestException or TaskCanceledException)
        {
            // A data edit that succeeded must not be reported as failed because the follow-up could not be
            // prepared. The formatting is an extra, and the user can run it again.
            return (null, [$"The page could not be re-read to check its formatting: {ex.Message}"]);
        }
    }

    /// <summary>
    /// Writes the formatting edit. Same conflict rule as the data commit — re-fetch, and refuse a page that has
    /// moved on — because a reflow saved over somebody else's change would be the worst kind to review.
    /// </summary>
    public async Task<CommitResult> CommitFormattingAsync(
        FormattingProposal proposal, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposal);

        if (await WhyWritingIsNotAllowedAsync(cancellationToken).ConfigureAwait(false) is { } blocked)
            return new CommitResult(CommitStatus.Failed, Error: blocked);

        try
        {
            WikiPage? current = await _wiki.FetchPageAsync(proposal.PageTitle, cancellationToken).ConfigureAwait(false);
            if (current is null)
                return new CommitResult(CommitStatus.Failed, Error: $"'{proposal.PageTitle}' no longer exists.");

            if (!string.Equals(current.Wikitext, proposal.Original, StringComparison.Ordinal))
                return new CommitResult(
                    CommitStatus.PageChangedSinceCheck,
                    Error: $"'{proposal.PageTitle}' changed since the formatting was worked out. Nothing was " +
                           "written — capture the item again.");

            EditResult edit = await _wiki.EditAsync(
                    proposal.PageTitle, proposal.Formatted, proposal.Summary, current.Timestamp, cancellationToken)
                .ConfigureAwait(false);

            return new CommitResult(
                edit.NoChange ? CommitStatus.NoChange : CommitStatus.Committed, edit.NewRevisionId);
        }
        catch (MediaWikiException ex)
        {
            return new CommitResult(CommitStatus.Failed, Error: $"The wiki refused the edit ({ex.Code}): {ex.Message}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new CommitResult(CommitStatus.Failed, Error: ex.Message);
        }
    }

    /// <summary>
    /// Records that the user looked at everything this item had flagged and is happy with it — their answer to a
    /// judgement the tool deliberately declined to make.
    ///
    /// **This is the only way a flagged item becomes done**, and it has to exist, because both alternatives are
    /// worse: without it, an item whose page the user has decided is *correct* — most often lore the wiki states
    /// better than the game does — stays flagged and re-fetches on every capture forever, and the only other escape
    /// would be making the tool overwrite the very thing the user just approved.
    ///
    /// It records `Matched` against this capture's fingerprint, so the item settles until something about it
    /// actually changes.
    /// </summary>
    public void RecordCheckedByHand(ItemCheckResult result, string? note = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (NeverGetsALedgerRow(result.Status)) return;
        if (string.IsNullOrWhiteSpace(result.ItemName)) return;

        RecordLedgerEntry(
            result.ItemName, result.Page?.Title, CheckOutcome.Matched, result.Page?.RevisionId,
            FingerprintOf(result), note);
    }

    /// <summary>Persists the ledger. Called after a batch rather than per row — it is written whole.</summary>
    public Task SaveLedgerAsync(string path, CancellationToken cancellationToken = default) =>
        _ledger.SaveAsync(path, cancellationToken);

    /// <summary>The captured icon is deliberately left out here: this recomputes the fingerprint for a row being
    /// written after the fact, and the icon reading is not carried on the result. A row whose fingerprint omits the
    /// icon simply invalidates itself on the next capture, which errs toward re-checking.</summary>
    /// <summary>
    /// The fingerprint to record for a result the user acted on.
    ///
    /// **It must include the captured icon, and passing null here was a real bug** (found by the user, 2026-09-29).
    /// <see cref="CheckWindowAsync"/> computes the fingerprint *with* the icon, so a row written without one could
    /// never match the next capture of the same unchanged item: every item settled by a commit, by hand, or by
    /// skipping came back on every capture forever, exactly as if it had never been seen. That silently defeated the
    /// ledger for the two outcomes the user reaches most.
    /// </summary>
    private static string FingerprintOf(ItemCheckResult result) =>
        result.Item is null ? "" : ItemFingerprint.Compute(result.Item, result.CapturedIcon, result.Lore);

    private void RecordLedgerEntry(
        string itemName,
        string? pageTitle,
        CheckOutcome outcome,
        long? revisionId,
        string fingerprint,
        string? note = null)
    {
        if (string.IsNullOrWhiteSpace(itemName) || string.IsNullOrWhiteSpace(fingerprint)) return;

        _ledger.Record(new LedgerEntry
        {
            ItemName = itemName,
            EntityKind = CheckedItemsLedger.ItemKind,
            // Recorded separately from the item name because the two legitimately differ: an item whose in-game name
            // cannot be a MediaWiki title lives at a name a human chose.
            WikiPageTitle = pageTitle,
            Outcome = outcome,
            CheckedAt = DateTimeOffset.UtcNow,
            WikiRevisionId = revisionId,
            Fingerprint = fingerprint,
            MappingVersion = _mapping.Version,
            Note = note,
        });
    }
}
