using EQLWikiAssistant.Core.Icons;
using EQLWikiAssistant.Core.Items;
using EQLWikiAssistant.Core.Ocr;
using EQLWikiAssistant.Wiki.Analysis;
using EQLWikiAssistant.Wiki.Ledger;
using EQLWikiAssistant.Wiki.MediaWiki;

namespace EQLWikiAssistant.Pipeline;

/// <summary>What happened to one captured window. Ordered roughly by how far down the pipeline it got.</summary>
public enum ItemCheckStatus
{
    /// <summary>The window is partly covered, so nothing was read from it. **No ledger row** — it was never
    /// checked, and a row would make it look handled forever.</summary>
    Occluded,

    /// <summary>A foreign exaltation or a levelled item. **No ledger row**, for the same reason.</summary>
    Ineligible,

    /// <summary>The ledger already has this exact capture settled, so the wiki was never asked.</summary>
    AlreadyChecked,

    /// <summary>A Lore-tab capture: its prose was recorded against the item and nothing else was done.</summary>
    LoreRecorded,

    /// <summary>The item has no page at its own name. <see cref="ItemCheckResult.Lookup"/> says whether a
    /// near-miss candidate or a title-illegal name is the reason, which changes what the user should do.</summary>
    NotOnWiki,

    /// <summary>A page exists but is not an item page — no <c>{{Itempage}}</c> call to read or edit.</summary>
    NotAnItemPage,

    /// <summary>The page already says what the capture says, and nothing needs fixing.</summary>
    AlreadyCorrect,

    /// <summary>There is an edit to review.</summary>
    EditProposed,

    /// <summary>The wiki refused or the network failed. Reported rather than thrown, so one bad item does not
    /// abandon the rest of the frame.</summary>
    Failed,
}

/// <summary>How the captured icon compared against the one the page points at. Flag-only: the tool never proposes a
/// new <c>lucy_img_ID</c>, because it cannot know whether the page is wrong or the capture caught something odd.</summary>
public sealed record IconComparison(
    string IconId,
    IconFingerprint Captured,
    IconFingerprint OnWiki,
    double Distance)
{
    public bool Matches => Captured.LooksLike(OnWiki);
}

/// <summary>
/// Everything the review UI needs about one window, and nothing it has to recompute.
///
/// **Deliberately a report, not a handle.** Checking is read-only — it changes no page and (except for the
/// automatic outcomes noted on <see cref="ItemCheckStatus"/>) writes no ledger row — so the user can look at every
/// window in a frame before deciding anything. Committing is a separate call taking one of these back.
/// </summary>
public sealed record ItemCheckResult
{
    public required ItemCheckStatus Status { get; init; }

    /// <summary>The item's base name, with any <c>+X</c> level suffix already stripped. Empty only for an occluded
    /// window, where nothing was read.</summary>
    public required string ItemName { get; init; }

    public ParsedItem? Item { get; init; }

    /// <summary>The window's crop of the frame, for showing the user what was captured beside the diff.</summary>
    public CapturedImage? WindowImage { get; init; }

    public ItemEligibility? Eligibility { get; init; }

    public LedgerVerdict LedgerVerdict { get; init; }

    /// <summary>
    /// The ledger row this capture matched, when there was one.
    ///
    /// **Carried so an already-checked item can still be opened on the wiki** (user, 2026-09-29). Skipping the fetch
    /// is the whole point of the ledger, so there is no <see cref="Page"/> to link to — but the row knows the page's
    /// title, and "let me go look at what this says" is exactly the question a stale-looking row raises. Before this
    /// the one result with nothing to click was the one most likely to need it.
    /// </summary>
    public LedgerEntry? LedgerRow { get; init; }

    public ItemPageLookupResult? Lookup { get; init; }

    /// <summary>The captured icon's fingerprint, kept so the item can be re-analyzed when its lore arrives from a
    /// second capture without re-reading pixels that have not changed.</summary>
    public Core.Icons.IconFingerprint? CapturedIcon { get; init; }

    /// <summary>
    /// The two icons themselves, for the user to compare by eye.
    ///
    /// **Carried whether or not the check reached a verdict** (user, 2026-09-28). The comparison is perceptual and
    /// has limits — it declines near-black sprites and occasionally flags a good one — and every one of those
    /// outcomes is cheap to resolve if the user can simply look at the two images, and expensive if they cannot.
    /// It also makes a wrong "match" visible, which no amount of threshold tuning can.
    /// </summary>
    public CapturedImage? CapturedIconImage { get; init; }

    public CapturedImage? WikiIconImage { get; init; }

    public ItemPageAnalysis? Analysis { get; init; }

    public ProposedEdit? Edit { get; init; }

    public IconComparison? Icon { get; init; }

    /// <summary>Why the icon could not be compared, when it could not — a Lore capture, an unuploaded file, a page
    /// with no <c>lucy_img_ID</c>, or a sprite too dark to judge. Informational: an icon check that cannot see the
    /// icon must stay silent rather than report a mismatch.</summary>
    public string? IconNote { get; init; }

    /// <summary>The revision the analysis was built from, and the base timestamp any edit must carry.</summary>
    public WikiPage? Page { get; init; }

    /// <summary>True when the window offers a Lore tab whose text has not been captured yet — the prompt for the
    /// two-capture flow.</summary>
    public bool NeedsLoreCapture { get; init; }

    /// <summary>Lore recorded from an earlier Lore-tab capture of this item, if any.</summary>
    public string? Lore { get; init; }

    /// <summary>Parser warnings plus anything the pipeline itself wants the user to see. Never a reason to hide a
    /// result — a flagged gap is the point.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    public string? Error { get; init; }

    /// <summary>Whether a commit is possible: there is an edit, and nothing is asking for a human first.</summary>
    public bool CanCommit =>
        Status == ItemCheckStatus.EditProposed &&
        Edit is { HasChanges: true } &&
        Page is not null;

    /// <summary>
    /// Whether something here wants the user's judgement before the page is written — a field the analyzer could not
    /// decide, something the editor declined to write, a title defect, a suspect icon, or a name the capture itself
    /// could not agree on.
    ///
    /// **<see cref="ProposedEdit.Deferred"/> belongs here and was missing** (found by the user, 2026-09-29). Deferred
    /// is by definition "the tool knew about this and did not do it": the review screen already listed each one as
    /// "Not done", while a page that otherwise agreed was recorded `Matched` and never raised again — the screen and
    /// the ledger saying opposite things about the same item. Anything the tool declines is exactly what must keep
    /// coming back.
    /// </summary>
    public bool NeedsAttention =>
        (Analysis?.Blockers.Any() ?? false) ||
        Edit is { Deferred.Count: > 0 } ||
        Icon is { Matches: false } ||
        NeedsLoreCapture ||
        (Item?.TitleContentNameMismatch ?? false);
}

/// <summary>What committing an edit did.</summary>
public enum CommitStatus
{
    /// <summary>Written. <see cref="CommitResult.RevisionId"/> is the new revision.</summary>
    Committed,

    /// <summary>MediaWiki accepted the request and found the text identical to what was already there. Recorded as
    /// a match rather than an edit.</summary>
    NoChange,

    /// <summary>**Somebody else edited the page between the check and the commit.** Not written: our text was built
    /// from the older revision, so saving it would revert them. The user needs to re-check the item.</summary>
    PageChangedSinceCheck,

    /// <summary>The wiki refused, or the network failed. <see cref="CommitResult.Error"/> carries MediaWiki's own
    /// code where there is one.</summary>
    Failed,
}

/// <summary>
/// Formatting the page still wants after a data edit — the second, separate commit.
///
/// **Separate because the user asked for it to be** (2026-09-25): *"A single 'automatically reformatted' edit with
/// no actual data changes is much easier to work with when reviewing diff history."* Mixing the two would bury the
/// data change under a whole-page reflow, which is the review problem this tool exists to solve.
/// </summary>
public sealed record FormattingProposal(
    string PageTitle,
    string Original,
    string Formatted,
    IReadOnlyList<string> Notes,
    DateTimeOffset BaseTimestamp)
{
    /// <summary>Says plainly that no data changed, because that is the fact a reviewer scanning page history most
    /// needs from this edit.</summary>
    public string Summary => "Reformatted to the Item Page Blueprint (no data changes)";
}

public sealed record CommitResult(
    CommitStatus Status,
    long? RevisionId = null,
    string? Error = null,
    FormattingProposal? Formatting = null,
    IReadOnlyList<string>? FormattingNotes = null)
{
    /// <summary>What the formatting pass declined to lay out, when there is nothing to commit but something worth
    /// saying — most often that the page still carries legacy flags.</summary>
    public IReadOnlyList<string> FormattingNotes { get; init; } = FormattingNotes ?? [];
}
