using EQLWikiAssistant.Core.Icons;
using EQLWikiAssistant.Core.Items;
using EQLWikiAssistant.Core.Locate;
using EQLWikiAssistant.Core.Ocr;
using EQLWikiAssistant.Wiki.Analysis;
using EQLWikiAssistant.Wiki.Formatting;
using EQLWikiAssistant.Wiki.Ledger;
using EQLWikiAssistant.Wiki.Mapping;
using EQLWikiAssistant.Wiki.MediaWiki;
using EQLWikiAssistant.Wiki.Wikitext;

namespace EQLWikiAssistant.Pipeline;

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

    /// <summary>Lore captured from a Lore-tab window, by item name, waiting for the Description capture that needs
    /// it. Held here rather than by the caller because the two captures are separate frames, and the pipeline is
    /// already the stateful part of this flow.</summary>
    private readonly Dictionary<string, string> _pendingLore = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="icons">The icon cache, or null to skip the icon check. Optional because the check is flag-only:
    /// the tool never writes an icon id, so running without one is degraded rather than wrong.</param>
    /// <param name="decoder">Decodes a downloaded icon. Needed alongside <paramref name="icons"/>.</param>
    public ItemCheckPipeline(
        IMediaWikiClient wiki,
        IItemWindowLocator locator,
        CheckedItemsLedger ledger,
        WikiMapping? mapping = null,
        IconCache? icons = null,
        IImageDecoder? decoder = null)
    {
        _wiki = wiki ?? throw new ArgumentNullException(nameof(wiki));
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _mapping = mapping ?? WikiMapping.Default;
        _icons = icons;
        _decoder = decoder;
    }

    /// <summary>Set for the user's "re-check anyway" action: the ledger is still consulted, but never allowed to
    /// skip the wiki.</summary>
    public bool ReCheckAnyway { get; set; }

    /// <summary>
    /// Runs the whole read-only pipeline over one captured frame. Every window found is reported, including the ones
    /// nothing could be done with — a frame may hold several item windows, and a window the tool refused is exactly
    /// what the user needs to be told about.
    /// </summary>
    public async Task<IReadOnlyList<ItemCheckResult>> CheckAsync(
        CapturedImage frame, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);

        IReadOnlyList<LocatedWindow> windows = await _locator
            .LocateAsync(frame, cancellationToken).ConfigureAwait(false);

        var results = new List<ItemCheckResult>();
        foreach (LocatedWindow window in windows)
            results.Add(await CheckWindowAsync(frame, window, cancellationToken).ConfigureAwait(false));

        return results;
    }

    private async Task<ItemCheckResult> CheckWindowAsync(
        CapturedImage frame, LocatedWindow window, CancellationToken cancellationToken)
    {
        CapturedImage crop = frame.Crop(window.Bounds);

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

        ParsedItem item = ItemParser.Parse(window.Lines, window.ActiveTab);
        var warnings = new List<string>(item.Warnings);

        // A Lore capture is a different view of the same item — prose, no stats. It contributes its text and nothing
        // else; the Description capture is what gets analyzed.
        if (window.ActiveTab == ItemWindowTab.Lore)
        {
            if (!string.IsNullOrWhiteSpace(item.Lore)) _pendingLore[item.Name] = item.Lore!;
            else warnings.Add("The Lore tab was captured but no lore text could be read from it.");

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

        if (item.TitleContentNameMismatch)
            warnings.Add(
                "The name in the title bar does not match the one in the window body, which usually means the " +
                "capture was obscured. Treat this item's data as unreliable.");

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

        // Read from the frame, not the crop: ItemIconReader works in frame coordinates. It has to happen before the
        // ledger is consulted, because the icon is part of the fingerprint the ledger is keyed on.
        IconFingerprint? capturedIcon =
            ItemIconReader.TryRead(frame, window, out IconFingerprint read) ? read : null;

        _pendingLore.TryGetValue(item.Name, out string? lore);
        bool needsLore = window.HasLoreTab && lore is null;

        string fingerprint = ItemFingerprint.Compute(item, capturedIcon, lore);

        // Before any network call — that is the entire point of the ledger.
        LedgerVerdict verdict = _ledger.Consult(
            item.Name, fingerprint, _mapping.Version, CheckedItemsLedger.ItemKind, ReCheckAnyway);

        if (verdict == LedgerVerdict.AlreadyDone)
            return new ItemCheckResult
            {
                Status = ItemCheckStatus.AlreadyChecked,
                ItemName = item.Name,
                Item = item,
                WindowImage = crop,
                LedgerVerdict = verdict,
                Lore = lore,
                Warnings = warnings,
            };

        try
        {
            return await AnalyzeAgainstWikiAsync(
                    item, crop, verdict, capturedIcon, lore, needsLore, fingerprint, warnings, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is MediaWikiException or HttpRequestException or TaskCanceledException)
        {
            // One unreachable page must not abandon the other windows in the frame — and must not write a ledger row
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

    private async Task<ItemCheckResult> AnalyzeAgainstWikiAsync(
        ParsedItem item,
        CapturedImage crop,
        LedgerVerdict verdict,
        IconFingerprint? capturedIcon,
        string? lore,
        bool needsLore,
        string fingerprint,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        ItemPageLookupResult lookup = await ItemPageLookup
            .FindAsync(_wiki, item.Name, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (lookup.Warning is { } lookupWarning) warnings.Add(lookupWarning);

        if (lookup.TreatAsNew)
        {
            // Recorded, because "not on the wiki" is a real finding — but as NotOnWiki, which never lets a later
            // capture skip the fetch: somebody may have created the page since.
            RecordLedgerEntry(item.Name, null, CheckOutcome.NotOnWiki, null, fingerprint);
            return new ItemCheckResult
            {
                Status = ItemCheckStatus.NotOnWiki,
                ItemName = item.Name,
                Item = item,
                WindowImage = crop,
                LedgerVerdict = verdict,
                Lookup = lookup,
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
                Page = wikiPage,
                Warnings =
                [
                    .. warnings,
                    $"'{wikiPage.Title}' exists but carries no Itempage template call, so there is nothing to compare.",
                ],
            };
        }

        // The lore came from a separate capture of the Lore tab, so it is attached here rather than being on the
        // Description capture the parser produced. The analyzer then sees one complete item.
        ItemPageAnalysis analysis = ItemPageAnalyzer.Analyze(
            lore is null ? item : item with { Lore = lore }, page, wikiPage.Title, _mapping);
        ProposedEdit edit = ItemPageEditor.BuildEdit(page, analysis, _mapping);

        (IconComparison? icon, string? iconNote) =
            await CompareIconAsync(capturedIcon, page.IconId, cancellationToken).ConfigureAwait(false);

        if (needsLore)
            warnings.Add(
                "This item has a Lore tab that has not been captured. Switch to it and capture again so the lore " +
                "can be checked too.");

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
            Icon = icon,
            IconNote = iconNote,
            Page = wikiPage,
            Lore = lore,
            NeedsLoreCapture = needsLore,
            Warnings = warnings,
        };

        // A page that already agrees is settled here and now — there is nothing for the user to approve. Anything
        // wanting a human is Flagged instead, which never counts as done however unchanged the next capture is.
        if (result.Status == ItemCheckStatus.AlreadyCorrect)
            RecordLedgerEntry(
                item.Name,
                wikiPage.Title,
                result.NeedsAttention ? CheckOutcome.Flagged : CheckOutcome.Matched,
                wikiPage.RevisionId,
                fingerprint);

        return result;
    }

    /// <summary>
    /// Compares the captured icon against the file the page points at. **Flag-only** — a mismatch is reported and
    /// never acted on, because the tool cannot know whether the page is wrong or the capture caught something odd.
    ///
    /// Every "cannot judge" path returns a note rather than a verdict. An icon check that cannot see the icon has to
    /// stay silent: a false "wrong icon" sends the user hunting for a problem that is not there.
    /// </summary>
    private async Task<(IconComparison?, string?)> CompareIconAsync(
        IconFingerprint? captured, string? iconId, CancellationToken cancellationToken)
    {
        if (_icons is null || _decoder is null) return (null, null);
        if (captured is null) return (null, "The captured icon could not be read, so it was not compared.");
        if (string.IsNullOrWhiteSpace(iconId))
            return (null, "The page has no lucy_img_ID, so there is no icon to compare against.");

        byte[]? bytes;
        try
        {
            bytes = await _icons.GetAsync(iconId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return (null, $"The icon could not be fetched: {ex.Message}");
        }

        if (bytes is null) return (null, $"The wiki has no File:item_{iconId}.png yet.");

        CapturedImage wikiIcon;
        try
        {
            // The decoder composites over the game's own background grey, so both sides are the same sprite on the
            // same backdrop — see AlphaComposite for why that is not a detail.
            wikiIcon = await _decoder.DecodeAsync(bytes, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (null, $"The wiki's icon {iconId} could not be decoded: {ex.Message}");
        }

        if (!IconHasher.TryFingerprint(
                wikiIcon, new Rect(0, 0, wikiIcon.Width, wikiIcon.Height), out IconFingerprint onWiki))
            return (null, $"The wiki's icon {iconId} has too little ink to compare.");

        if (!onWiki.IsComparable)
            return (null, $"The wiki's icon {iconId} is too low-contrast to judge — see IconFingerprint.MinimumContrast.");

        return (new IconComparison(iconId!, captured, onWiki, captured.CorrelationDistanceTo(onWiki)), null);
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
    /// Records that the user looked at an item and chose not to act. `Skipped` never counts as done, so the item
    /// comes back on the next capture — which is the point: skipping is "not now", not "this is fine".
    /// </summary>
    public void RecordSkipped(ItemCheckResult result, string? note = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        // These two never get a row at all, whatever the user does with them.
        if (result.Status is ItemCheckStatus.Occluded or ItemCheckStatus.Ineligible) return;
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
    /// </summary>
    public async Task<ItemCheckResult> ReanalyzeAsync(
        ItemCheckResult previous, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(previous);
        if (previous.Item is not { } item)
            throw new InvalidOperationException("This result has no parsed item, so there is nothing to re-analyze.");

        _pendingLore.TryGetValue(item.Name, out string? lore);
        string fingerprint = ItemFingerprint.Compute(item, previous.CapturedIcon, lore);

        try
        {
            return await AnalyzeAgainstWikiAsync(
                    item,
                    previous.WindowImage!,
                    previous.LedgerVerdict,
                    previous.CapturedIcon,
                    lore,
                    needsLore: lore is null && previous.NeedsLoreCapture,
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
    public async Task<(FormattingProposal? Proposal, IReadOnlyList<string> Notes)> PrepareFormattingAsync(
        string title, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        try
        {
            WikiPage? current = await _wiki.FetchPageAsync(title, cancellationToken).ConfigureAwait(false);
            if (current is null) return (null, []);

            PrettifyResult formatting = ItemPagePrettifier.Format(current.Wikitext, _mapping);
            if (!formatting.IsSafe) return (null, formatting.Refusals);
            if (!formatting.Changed) return (null, formatting.Notes);

            return (
                new FormattingProposal(
                    current.Title, current.Wikitext, formatting.Formatted, formatting.Notes, current.Timestamp),
                formatting.Notes);
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
        // These never get a row at all, whatever the user says about them: nothing was read from them.
        if (result.Status is ItemCheckStatus.Occluded or ItemCheckStatus.Ineligible) return;
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
    private static string FingerprintOf(ItemCheckResult result) =>
        result.Item is null ? "" : ItemFingerprint.Compute(result.Item, null, result.Lore);

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
