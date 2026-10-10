using EQLWikiEditorAssistant.Core.Icons;
using EQLWikiEditorAssistant.Core.Items;
using EQLWikiEditorAssistant.Core.Imaging;
using EQLWikiEditorAssistant.Wiki.Analysis;
using EQLWikiEditorAssistant.Wiki.Ledger;
using EQLWikiEditorAssistant.Wiki.MediaWiki;

namespace EQLWikiEditorAssistant.Pipeline;

/// <summary>What happened to one captured window. Ordered roughly by how far down the pipeline it got.</summary>
public enum ItemCheckStatus
{
    /// <summary>The window is partly covered, so nothing was read from it. **No ledger row** — it was never
    /// checked, and a row would make it look handled forever.</summary>
    Occluded,

    /// <summary>The window was drawn in a different UI font from the one the tool is set to read, so nothing read
    /// from it was used. **No ledger row**, for the same reason as <see cref="Occluded"/>. The font decides what the
    /// bare vertical bar means, so reading through the wrong one confuses capital I and lowercase l silently.</summary>
    WrongFont,

    /// <summary>The window is drawn in one of the game's skins other than `default_modern`, which the Assistant can
    /// find but not read (user, 2026-10-09). **No ledger row**, for the same reason as <see cref="Occluded"/>.</summary>
    UnsupportedSkin,

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

/// <summary>How the captured icon compared against the file the page points at.</summary>
/// <param name="Distance">The capture against the wiki's file.</param>
/// <param name="BestInLibrary">The capture against the closest icon in the game's library, when there is a library.
/// The match is judged against it — see <see cref="IconLibrary.SameArtworkMargin"/>.</param>
/// <param name="PageIdInLibrary">The capture against the library's own icon for the page's id, when it has one.</param>
public sealed record IconComparison(
    string IconId,
    IconFingerprint Captured,
    IconFingerprint OnWiki,
    double Distance,
    double? BestInLibrary = null,
    double? PageIdInLibrary = null)
{
    /// <summary>Whether the wiki's file shows the artwork the game drew: within the margin of the best icon in the
    /// library, or by the absolute threshold when there is no library.</summary>
    public bool Matches => BestInLibrary is { } best
        ? Distance - best <= IconLibrary.SameArtworkMargin
        : Captured.LooksLike(OnWiki);

    /// <summary>
    /// **The id is right and the wiki's file is not**: the game's own icon for this id matches the capture, while the
    /// file the wiki holds under that id is different artwork. Measured on 4 of 96 corpus items. The fix is a new
    /// version of the wiki's file, which this tool does not upload over, so it is reported rather than offered.
    /// </summary>
    public bool WikiFileDiffersFromGame =>
        !Matches && BestInLibrary is { } best && PageIdInLibrary is { } own &&
        own - best <= IconLibrary.SameArtworkMargin;
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

    /// <summary>
    /// The whole page the tool would create, for an item the wiki has never heard of. Null for everything else, and
    /// null even for a new item whose name makes creating unsafe — see <c>ItemPageLookupResult.MayCreate</c>.
    ///
    /// Separate from <see cref="Edit"/> because there is no original to diff against: the review is of the text
    /// itself rather than of a change to somebody else's text, which is why the screen hides its diff and findings
    /// for this case.
    /// </summary>
    public ProposedPage? Creation { get; init; }

    /// <summary>
    /// The formatting the page would want, when the page already matches the capture (user, 2026-09-29).
    ///
    /// **Offered without anything being written**, because a page whose data is right can still be laid out wrongly,
    /// and there is otherwise no moment at which the user would be shown that. It costs no request: the page was just
    /// fetched, and nothing has changed it since. For a page the tool is proposing to edit, this stays null until the
    /// user settles the data question one way or the other — by committing, or by saying the wiki is right.
    /// </summary>
    public FormattingProposal? Formatting { get; init; }

    public IconComparison? Icon { get; init; }

    /// <summary>
    /// Which icon the library recognized in the captured artwork, for an item being created. Null when there is no
    /// library, no readable icon, or nothing close enough to say.
    ///
    /// **Only ever set on the creation path.** On a page that already exists the icon is *compared and flagged*,
    /// never proposed, because the tool cannot know whether the page's id or the capture is the odd one — see
    /// <see cref="Icon"/>. A page that does not exist yet has no such doubt and nothing to overwrite, which is the
    /// whole reason this can fill a field that is elsewhere forbidden to touch.
    /// </summary>
    public IconSuggestion? IconSuggestion { get; init; }

    /// <summary>Why the icon could not be compared, when it could not — a Lore capture, an unuploaded file, a page
    /// with no <c>lucy_img_ID</c>, or a sprite too dark to judge. Informational: an icon check that cannot see the
    /// icon must stay silent rather than report a mismatch.</summary>
    public string? IconNote { get; init; }

    /// <summary>The revision the analysis was built from, and the base timestamp any edit must carry.</summary>
    public WikiPage? Page { get; init; }

    /// <summary>True when the window offers a Lore tab whose text has not been captured yet — the prompt for the
    /// two-capture flow.</summary>
    public bool NeedsLoreCapture { get; init; }

    /// <summary>
    /// The window offers a Lore tab whose text has not been captured, on a result that does not ask for it — one the
    /// ledger let skip the wiki, where there is no comparison for the lore to join. Kept so that comparing it after
    /// all ("Refresh wiki data") asks for the Lore tab as a fresh check would (bug found by the user, 2026-10-08, on
    /// `Tarnished Ancient Tiara`).
    /// </summary>
    public bool LoreNotCaptured { get; init; }

    /// <summary>Lore recorded from an earlier Lore-tab capture of this item, if any.</summary>
    public string? Lore { get; init; }

    /// <summary>Parser warnings plus anything the pipeline itself wants the user to see. Never a reason to hide a
    /// result — a flagged gap is the point.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    public string? Error { get; init; }

    /// <summary>The wiki's Verified for EQLegends list is known not to include this page. Read off the warning rather
    /// than kept as a second flag, so the two can never disagree.</summary>
    public bool NotVerifiedForEql => Warnings.Contains(ItemCheckPipeline.NotVerifiedNotice);

    /// <summary>Whether a commit is possible: there is an edit, and nothing is asking for a human first.</summary>
    public bool CanCommit =>
        Status == ItemCheckStatus.EditProposed &&
        Edit is { HasChanges: true } &&
        Page is not null;

    /// <summary>Whether this item can be created: it is not on the wiki, and a page was generated for it. A new
    /// item whose name cannot safely become a title gets no proposal, so this is false and the user sees only the
    /// warning explaining why.</summary>
    public bool CanCreate => Status == ItemCheckStatus.NotOnWiki && Creation is not null;

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
