using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using EQLWikiAssistant.Core.Icons;
using EQLWikiAssistant.Core.Items;
using EQLWikiAssistant.Core.Ocr;
using EQLWikiAssistant.Pipeline;
using EQLWikiAssistant.Wiki.Analysis;
using EQLWikiAssistant.Wiki.Ledger;

namespace EQLWikiAssistant.App;

/// <summary>One line of the rendered diff, with its colour already decided — simpler than a value converter, and it
/// keeps the palette in one place.</summary>
public sealed record DiffLineViewModel(DiffLine Line)
{
    public string Text => Line.Kind switch
    {
        DiffLineKind.Removed => "- " + Line.Text,
        DiffLineKind.Added => "+ " + Line.Text,
        DiffLineKind.Gap => "  " + Line.Text,
        _ => "  " + Line.Text,
    };

    public string LineNumbers => Line.Kind == DiffLineKind.Gap
        ? ""
        : $"{Line.BeforeLine?.ToString() ?? "",4} {Line.AfterLine?.ToString() ?? "",4}";

    public Brush Background => Line.Kind switch
    {
        DiffLineKind.Removed => Palette.DiffRemovedBack,
        DiffLineKind.Added => Palette.DiffAddedBack,
        _ => Brushes.Transparent,
    };

    public Brush Foreground => Line.Kind switch
    {
        DiffLineKind.Removed => Palette.DiffRemovedText,
        DiffLineKind.Added => Palette.DiffAddedText,
        DiffLineKind.Gap => Palette.Dim,
        _ => Palette.Text,
    };
}

/// <summary>
/// A row in the differences table, flattened for the grid.
///
/// **It takes a compliance finding as well as a field finding** (user, 2026-09-29). The era banner and the lore
/// placeholder are ordinary "the wiki says this, the game says that" comparisons, and a yellow warning bar each —
/// on top of the bars for everything else — made the strip long enough that nobody would read any of it. Which
/// findings the strip is *for* is the real rule: things nobody has judged yet. Both of these the tool fixes
/// itself, so they are differences, not open questions.
/// </summary>
public sealed record FindingViewModel(
    string Field,
    string Captured,
    string OnWiki,
    string Verdict,
    string Explanation,
    bool Blocks)
{
    /// <summary>Red for anything needing a human, because that is the one category the user must not skim past: the
    /// tool has declined to decide, so nobody has.</summary>
    public Brush Foreground => Blocks ? Palette.Attention : Palette.Text;

    public static FindingViewModel For(FieldFinding finding) => new(
        DisplayField(finding.Field),
        finding.Captured ?? "—",
        finding.OnWiki ?? "—",
        DisplayVerdict(finding.Verdict, finding.Captured, finding.OnWiki),
        finding.Explanation ?? "",
        finding.Blocks);

    /// <summary>
    /// The analyzer's field names, as a reader would write them (user, 2026-10-07). They are keys in the
    /// <c>Wiki</c> layer and the tests pin them, so they are translated here rather than renamed there. A stat keeps
    /// its wiki label (<c>AC</c>, <c>SV Fire</c>), which is already what the reader sees on the page.
    /// </summary>
    public static string DisplayField(string field) => field switch
    {
        ItemPageAnalyzer.ItemNameField => "Item name",
        ItemPageAnalyzer.PageTitleField => "Page title",
        ItemPageAnalyzer.MerchantValueField => "Merchant value",
        ItemPageAnalyzer.LoreField => "Lore",
        ItemPageAnalyzer.CategoryField => "Category",
        ItemPageAnalyzer.FlagsField => "Flags",
        ItemPageAnalyzer.FlagProseField => "Flags (text)",
        ItemPageAnalyzer.OrphanEffectField => "Effect",
        "focus_effect" => "Focus effect",
        _ => field,
    };

    /// <summary>
    /// What the edit does to the row, since the table lists proposed changes (user, 2026-10-07): the enum names
    /// (<c>MissingOnWiki</c>, <c>NeedsReview</c>) said what the comparison found, not what happens next.
    /// </summary>
    public static string DisplayVerdict(FieldVerdict verdict, string? captured, string? onWiki) => verdict switch
    {
        FieldVerdict.Matches => "Matches",
        FieldVerdict.MissingOnWiki => "Add",
        FieldVerdict.Differs when captured is null && onWiki is not null => "Remove",
        FieldVerdict.Differs => "Change",
        FieldVerdict.Unverifiable => "Kept",
        FieldVerdict.NeedsReview => "Your call",
        _ => verdict.ToString(),
    };

    /// <summary>A compliance finding the tool fixes, shown as the two-sided comparison it is. The verdict names
    /// which side is empty, matching the vocabulary the field rows already use, and the finding's own prose becomes
    /// the note — which is where it reads better than in a bar, because the table is where the reader is already
    /// looking at what this row compares.
    ///
    /// <paramref name="emptyCaptured"/> exists because for the lore placeholder the wanted state genuinely *is* the
    /// absence of something, and "no lore" says that where the table's usual "—" only says "nothing here". The
    /// domain keeps null meaning absent; the wording belongs to the view.</summary>
    public static FindingViewModel ForCompliance(
        string field, ComplianceFinding finding, string emptyCaptured = "—") => new(
        field,
        finding.Wanted ?? emptyCaptured,
        finding.OnPage ?? "—",
        DisplayVerdict(
            finding.OnPage is null ? FieldVerdict.MissingOnWiki : FieldVerdict.Differs, finding.Wanted, finding.OnPage),
        finding.Detail,
        Blocks: false);
}

/// <summary>
/// One captured window, as the review screen sees it.
///
/// **The proposed wikitext is held here and is editable**, and it is what gets committed — not
/// <c>Edit.NewWikitext</c>. A review screen whose approve button saved something other than what was on screen would
/// make the review meaningless, which is the one thing this tool cannot afford.
/// </summary>
public sealed class ResultViewModel : INotifyPropertyChanged
{
    private string _wikitext;
    private string _summary;
    private string? _outcome;
    private bool _isBusy;
    private FormattingProposal? _formatting;
    private string? _formattingOutcome;
    private CapturedImage? _loreImage;
    private string _formattedWikitext = "";
    private string? _captureFile;
    private bool _settled;

    public ResultViewModel(ItemCheckResult result) => Load(result);

    /// <summary>
    /// Replaces everything this entry shows, in place.
    ///
    /// **In place rather than by swapping the object**, because the list now accumulates across captures (user,
    /// 2026-09-28) — re-capturing an item, or feeding it lore from the second capture, updates the entry the user is
    /// already looking at instead of adding a duplicate or losing their selection.
    /// </summary>
    [MemberNotNull(nameof(Result), nameof(_wikitext), nameof(_summary))]
    public void Load(ItemCheckResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        Result = result;
        // A new item has no page to start from, so the box is seeded with the whole page the tool would create.
        _wikitext = result.Creation?.Wikitext ?? result.Edit?.NewWikitext ?? result.Page?.Wikitext ?? "";
        // Left blank when the tool proposes nothing, rather than inheriting its "No changes" — if the user is
        // hand-editing a page the tool judged correct, that summary would be a false description of the revision,
        // and there is nothing to inherit. CanAct requires one, so it has to be typed. A creation does have one to
        // inherit, because "created from the in-game item window" is the whole truth about that revision.
        _summary = result.Creation?.Summary
            ?? (result.Edit is { HasChanges: true } proposed ? proposed.Summary : "");
        _outcome = null;
        _settled = false;
        _formatting = null;
        _formattingOutcome = null;

        Replace(Diff, result.Edit is null
            ? []
            : WikitextDiff.Compute(result.Edit.OriginalWikitext, result.Edit.NewWikitext)
                .Select(l => new DiffLineViewModel(l)));
        Replace(Findings, (result.Analysis?.Findings
                .Where(f => f.IsChange || f.Blocks)
                .Select(FindingViewModel.For) ?? [])
            .Concat(ComplianceRows(result)));
        Replace(Warnings, BuildWarnings());
        FormattingDiff.Clear();

        // Through the property, not the field, so its diff gets built. A page that already matches arrives with its
        // formatting proposal already worked out — the check had the page in hand, so it cost nothing. For one the
        // tool wants to edit this stays null until the user settles the data question.
        Formatting = result.Formatting;

        // Everything on this object is derived from Result, so the simplest correct notification is "all of it".
        OnPropertyChanged(null);
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (T item in items) target.Add(item);
    }

    public ItemCheckResult Result { get; private set; }

    public ObservableCollection<DiffLineViewModel> Diff { get; } = [];
    public ObservableCollection<FindingViewModel> Findings { get; } = [];
    public ObservableCollection<string> Warnings { get; } = [];

    /// <summary>The Lore-tab capture of this item, when the user has taken one — shown beside the Description
    /// capture rather than replacing it.</summary>
    public CapturedImage? LoreImage
    {
        get => _loreImage;
        set
        {
            _loreImage = value;
            OnPropertyChanged(nameof(LoreImage));
            OnPropertyChanged(nameof(HasLoreImage));
        }
    }

    public bool HasLoreImage => _loreImage is not null;

    /// <summary>Whether there is anything to show in the icon section — at least one of the images.</summary>
    public bool HasIcons =>
        Result.CapturedIconImage is not null || Result.WikiIconImage is not null || MatchedIconImage is not null;

    /// <summary>The artwork the icon library matched, for a new item. This is what the user confirms against the
    /// captured icon — see <c>IconLibrary.ConfidentMargin</c> for why the eye is the backstop and not the number.
    /// </summary>
    public CapturedImage? MatchedIconImage => Result.IconSuggestion?.Image;

    public bool HasMatchedIcon => MatchedIconImage is not null;

    /// <summary>
    /// Shown whenever the library has something to say — on a creation, and on an existing page whose icon the
    /// comparison flagged (user, 2026-10-02).
    ///
    /// It used to be creation-only, on the rule that an existing page's icon is compared and never proposed. That
    /// rule still holds for what the tool *writes*; what changed is that it no longer throws away the answer it
    /// already computed, leaving the user to look the id up by hand after being told their page is wrong.
    /// </summary>
    public bool ShowMatchedIcon => Result.IconSuggestion is not null;

    /// <summary>
    /// What the user is being asked to do with this column, which changed when the icon became identifiable.
    ///
    /// It used to read "read the lucy_img_ID off this artwork", which was the only thing available: no capture can
    /// read an icon id off the game, so the user looked the icon up by hand. With a confident match that instruction
    /// is stale — the id is already in the box, and the job is now to confirm it rather than to find it.
    /// </summary>
    /// <remarks>Only a creation gets one (user, 2026-10-07): on an existing page the verdict line and the button
    /// already say everything a hint would.</remarks>
    public string IconColumnHint => (IsCreation, Result.IconSuggestion) switch
    {
        (true, { IsConfident: true }) => "lucy_img_ID automatically set below",
        (true, _) => "lucy_img_ID not automatically set",
        _ => "",
    };

    /// <summary>Red when the id was left blank: with the warning-strip bar gone, this line is the one place the gap is
    /// said, so it has to read as one.</summary>
    public Brush IconColumnHintBrush =>
        Result.IconSuggestion is { IsConfident: true } ? Palette.Muted : Palette.Attention;

    /// <summary>Whether the hint says anything. It is blank on an ordinary page whose icon agrees.</summary>
    public bool HasIconColumnHint => !string.IsNullOrEmpty(IconColumnHint);

    /// <summary>
    /// Names the id, because the id is the thing being written into the page — and says plainly when the tool is not
    /// sure, since an unsure match leaves <c>lucy_img_ID</c> blank and becomes the user's job.
    /// </summary>
    public string MatchedIconCaption => Result.IconSuggestion switch
    {
        { IsConfident: true } s => $"Matched (ID: {s.IconId})",
        { } s => $"Best Guess (ID: {s.IconId})",
        _ => "",
    };

    /// <summary>Green for a match written into the page, amber for one the user has to settle — the same two-state
    /// reading the rest of the panel uses.</summary>
    public Brush MatchedIconBorderBrush =>
        Result.IconSuggestion is { IsConfident: true } ? Palette.Done : Palette.Warning;

    /// <summary>
    /// Whether to offer the icon action, which is a different question on each path.
    ///
    /// On a creation the id is already in the generated text, so the only thing left to do is publish the file —
    /// offered when the tool is sure which icon it is and the wiki is known not to have it. On an existing page the
    /// id itself is the fix, so the offer also needs the file question *answered*: see
    /// <c>IconSuggestion.CanApplyToPage</c> for why an unknown answer is not good enough there either.
    /// </summary>
    public bool CanActOnIcon =>
        !_iconActioned &&
        Result.IconSuggestion is { } s &&
        (IsCreation ? s.CanUpload : s.CanApplyToPage && Result.Edit is not null);

    /// <summary>What pressing it will do, said before it is pressed — the upload is the part that cannot be taken
    /// back, so it is named rather than implied.</summary>
    public string IconActionPrompt => (IsCreation, Result.IconSuggestion) switch
    {
        (_, { NeedsUpload: true } s) => $"Icon #{s.IconId} is missing on the wiki.",
        _ => "",
    };

    public bool HasIconActionPrompt => !string.IsNullOrEmpty(IconActionPrompt);

    public string IconActionButtonText => Result.IconSuggestion switch
    {
        null => "",
        { NeedsUpload: true } s => $"Upload and use icon #{s.IconId}",
        { } s => $"Use icon #{s.IconId}",
    };

    private bool _iconActioned;
    private string? _iconUploadOutcome;
    private bool _iconUploadFailed;

    public string? IconUploadOutcome => _iconUploadOutcome;

    public bool HasIconUploadOutcome => !string.IsNullOrWhiteSpace(_iconUploadOutcome);

    public Brush IconUploadOutcomeBrush => _iconUploadFailed ? Palette.Attention : Palette.Done;

    /// <summary>Records what the upload did. Kept on the view model rather than re-running the check, because the
    /// capture is gone and re-asking the wiki would only confirm what it just told us.</summary>
    public void RecordIconUpload(bool uploaded, string? url, string? error)
    {
        _iconActioned = uploaded;
        _iconUploadFailed = !uploaded;
        _iconUploadOutcome = uploaded
            ? $"Uploaded{(url is null ? "." : $" — {url}")}"
            : $"The icon was not uploaded: {error}";

        OnPropertyChanged(nameof(CanActOnIcon));
        OnPropertyChanged(nameof(IconUploadOutcome));
        OnPropertyChanged(nameof(HasIconUploadOutcome));
        OnPropertyChanged(nameof(IconUploadOutcomeBrush));
    }

    /// <summary>
    /// Points the proposed edit at the matched icon: the new wikitext goes into the box the user is reviewing, and
    /// the diff is rebuilt so it shows the id changing (user, 2026-10-02 — "update the diff to use the new icon ID").
    ///
    /// **The diff has to be rebuilt here rather than left alone**, because it is computed once from the tool's own
    /// proposal and this change does not come from there. Leaving it would show the user a diff that no longer
    /// describes what Save would write, which is the one property this screen cannot give up.
    ///
    /// The summary gains a mention for the same reason: a revision whose summary omits a change it made is exactly
    /// the page history this tool is supposed to be improving. It is left alone if the user already wrote one that
    /// mentions the icon.
    /// </summary>
    public void ApplyIconId(string wikitext, string iconId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(wikitext);
        ArgumentException.ThrowIfNullOrWhiteSpace(iconId);

        _iconActioned = true;
        _wikitext = wikitext;

        if (Result.Edit is { } edit)
            Replace(Diff, WikitextDiff.Compute(edit.OriginalWikitext, wikitext).Select(l => new DiffLineViewModel(l)));

        if (IsCreation)
        {
            // Nothing to add: a creation's summary already covers every field on the page.
        }
        else if (_summary.Length == 0)
            _summary = $"Set lucy_img_ID to {iconId} from the in-game icon";
        else if (!_summary.Contains("lucy_img_ID", StringComparison.OrdinalIgnoreCase) &&
                 !_summary.Contains("icon", StringComparison.OrdinalIgnoreCase))
            _summary += "; updated lucy_img_ID";

        Replace(Warnings, BuildWarnings());

        _iconUploadFailed = false;
        string next = IsCreation ? "" : " — review the diff, then Save";
        _iconUploadOutcome = _iconUploadOutcome is { } uploaded
            ? $"{uploaded} lucy_img_ID set to {iconId}{next}."
            : $"lucy_img_ID set to {iconId}{next}.";

        // Everything the box, the diff, the summary and the button all read from has moved.
        OnPropertyChanged(null);
    }

    /// <summary>Whether each side actually has artwork to show. **An absent icon must look absent**: an empty
    /// <c>Border</c> keeps its near-black background and reads as a corrupt icon, which is how one of these got
    /// reported as a capture bug (user, 2026-09-29).</summary>
    public bool HasCapturedIcon => Result.CapturedIconImage is not null;

    public bool HasNoCapturedIcon => !HasCapturedIcon;

    public bool HasWikiIcon => Result.WikiIconImage is not null;

    public bool HasNoWikiIcon => !HasWikiIcon;

    /// <summary>What the comparison concluded, in words, including when it declined to conclude anything. The
    /// images are shown regardless, so this is a starting point for the user's own look rather than a verdict they
    /// have to take on trust.</summary>
    /// <summary>The comparison's verdict in plain words. **No distance figure** (user, 2026-10-07): a number on a
    /// scale only the algorithm knows tells the reader nothing, and the tiles below are the real evidence.</summary>
    public string IconVerdict => Result.Icon switch
    {
        { Matches: true } => "Appears to be correct.",
        { Matches: false } when Result.IconSuggestion is { CanApplyToPage: true } =>
            $"May be incorrect on the wiki. The in-game icon looks like icon #{Result.IconSuggestion.IconId}.",
        { Matches: false } => "May be incorrect on the wiki. Failed to automatically determine icon ID.",
        _ => Result.IconNote ?? "Not compared.",
    };

    /// <summary>
    /// Whether to show the icon *comparison* — the verdict line and the wiki-side tile.
    ///
    /// **False for a creation** (user, 2026-10-01: do not bother showing in-game against wiki). There is no page,
    /// so there is nothing to compare: the verdict would read "the icons were not compared" and the wiki tile would
    /// read "none on the page", which is true of a page that does not exist and says nothing useful. The captured
    /// icon itself stays, because it is how the user reads off the `lucy_img_ID` they are about to type.
    /// </summary>
    public bool ShowIconComparison => !IsCreation;

    public Brush IconVerdictBrush => Result.Icon switch
    {
        { Matches: true } => Palette.Done,
        { Matches: false } => Palette.Attention,
        _ => Palette.Neutral,
    };

    /// <summary>The lore read from the game, once a Lore-tab capture has been merged in.</summary>
    public string? CapturedLore => Result.Lore;

    public bool HasCapturedLore => !string.IsNullOrWhiteSpace(Result.Lore);

    /// <summary>True while this item is waiting for its Lore tab to be captured — what the lore section's prompt
    /// hangs off.</summary>
    public bool WantsLoreCapture => Result.NeedsLoreCapture;

    /// <summary>True when only the Lore tab has been captured, so the item's data is still to come — the reverse of
    /// <see cref="WantsLoreCapture"/> (user, 2026-10-07).</summary>
    public bool WantsDescriptionCapture => Result.Status == ItemCheckStatus.LoreRecorded;

    /// <summary>Whether the lore section has anything at all to show.</summary>
    public bool HasLoreSection => WantsLoreCapture || HasCapturedLore;

    /// <summary>Lore captured with nothing on the page to weigh it against — so it is simply shown, and the edit
    /// will add it. The comparison panel handles the case where the page has its own.</summary>
    public bool HasCapturedLoreOnly => HasCapturedLore && !HasLoreToCompare;

    public string ItemName => string.IsNullOrEmpty(Result.ItemName) ? "(unreadable window)" : Result.ItemName;

    /// <summary>
    /// Whether this item is finished and wants nothing further.
    ///
    /// **The list is read to find what still needs attention** (user, 2026-09-29), so "done" is the state worth
    /// showing plainly and everything else is the exception. It covers the item settled by the tool itself — a page
    /// that already agreed, or one the ledger had settled before — and the one the user settled by committing or by
    /// saying the wiki is right.
    ///
    /// **Skipping is deliberately not done.** `Skipped` means "not now": it never settles the ledger and the item
    /// returns on the next capture, so showing it green would promise something the ledger does not honour.
    /// </summary>
    public bool IsDone =>
        _settled ||
        Result.Status == ItemCheckStatus.AlreadyChecked ||
        (Result.Status == ItemCheckStatus.AlreadyCorrect && !Result.NeedsAttention);

    /// <summary>Set by the review screen when the user's action settled the item — a successful commit, or "the wiki
    /// is right". Not set by skipping.</summary>
    public bool Settled
    {
        get => _settled;
        set
        {
            Set(ref _settled, value);
            OnPropertyChanged(nameof(IsDone));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(StatusBrush));
            // A page just created becomes clickable at this moment and not before — see KnownPageTitle.
            OnPropertyChanged(nameof(PageTitle));
            OnPropertyChanged(nameof(HasPage));
        }
    }

    public string StatusText => Result switch
    {
        // Green either way (see StatusBrush), but worded for what actually happened (user, 2026-10-07): "Done" is
        // kept for an item the user settled by acting on it. A page that already matched had nothing done to it, and
        // an item the ledger let skip the wiki was not compared at all this time.
        _ when _settled => "Done",
        { Status: ItemCheckStatus.AlreadyCorrect, NeedsAttention: false } => "No differences",
        { Status: ItemCheckStatus.AlreadyChecked } => "Already checked",

        // "Already correct" is a false statement about a page the tool declined to finish, and it sat directly above
        // a warning strip saying "Not done" (user, 2026-09-29). The ledger records Flagged in this case; the label
        // has to agree with it.
        { Status: ItemCheckStatus.AlreadyCorrect, NeedsAttention: true } => "Needs attention",

        // The next thing to do with an item missing one of its two tabs is to capture it, whatever else is true, so
        // the label says that and the list doubles as a to-do (user, 2026-10-07).
        { NeedsLoreCapture: true } => "Lore tab capture needed",
        _ => StatusTextFor(Result.Status),
    };

    private static string StatusTextFor(ItemCheckStatus status) => status switch
    {
        ItemCheckStatus.Occluded => "Occluded",
        ItemCheckStatus.WrongFont => "Wrong font",
        ItemCheckStatus.Ineligible => "Not eligible",
        ItemCheckStatus.AlreadyChecked => "Already checked",
        ItemCheckStatus.LoreRecorded => "Description tab capture needed",
        ItemCheckStatus.NotOnWiki => "Not on the wiki",
        ItemCheckStatus.NotAnItemPage => "Wiki page is not an item page",
        ItemCheckStatus.AlreadyCorrect => "Already correct",
        ItemCheckStatus.EditProposed => "Edit proposed",
        ItemCheckStatus.Failed => "Failed",
        _ => status.ToString(),
    };

    public Brush StatusBrush => Result switch
    {
        _ when IsDone => Palette.Done,
        { Status: ItemCheckStatus.AlreadyCorrect, NeedsAttention: true } => Palette.Attention,
        { NeedsLoreCapture: true } or { Status: ItemCheckStatus.LoreRecorded } => Palette.Attention,
        { Status: ItemCheckStatus.EditProposed } => Palette.Warning,
        { Status: ItemCheckStatus.Failed or ItemCheckStatus.Occluded or ItemCheckStatus.WrongFont } => Palette.Attention,
        _ => Palette.Neutral,
    };

    /// <summary>
    /// The page this item lives on, as far as anything knows.
    ///
    /// **The ledger row is the last fallback, and it is the one that matters** (user, 2026-09-29): an already-checked
    /// item never reaches the wiki — that is the point of the ledger — so it has no <see cref="ItemCheckResult.Page"/>
    /// and used to be the one result with nothing to click. It is also the one most likely to need it, since "is this
    /// row stale, or has the page really been fixed?" is answered by going and looking.
    /// </summary>
    /// <remarks>
    /// A page this tool has just created counts too (2026-10-01). It has no <see cref="ItemCheckResult.Page"/> —
    /// the check found nothing to fetch — but it exists now, and the moment after creating one is exactly when the
    /// user wants to open it and start filling in where the item drops. Gated on <see cref="Settled"/> so the link
    /// appears only once the write has actually landed; before that it would be a red link to nothing.
    /// </remarks>
    private string? KnownPageTitle =>
        Result.Page?.Title ??
        (Settled && Result.Creation is { } created ? created.Title : null) ??
        (Result.LedgerRow is { Outcome: not CheckOutcome.NotOnWiki } row ? row.WikiPageTitle ?? row.ItemName : null);

    /// <summary>Includes a title that was merely looked up and not found, so the user can see what was searched for
    /// — but that case gets no link, since it would only ever be a red one.</summary>
    public string PageTitle => KnownPageTitle ?? Result.Lookup?.RequestedTitle ?? "";

    public bool HasPage => KnownPageTitle is not null;

    public string? PageUrl => KnownPageTitle is { } title
        ? $"https://eqlwiki.com/{Uri.EscapeDataString(title.Replace(' ', '_'))}"
        : null;

    /// <summary>When an already-checked item was last settled, and how. Shown beside the link, because the reason to
    /// open the page is usually to judge whether the row is still right.</summary>
    public string LedgerNote => Result.LedgerRow is not { } row
        ? ""
        : $"{row.Outcome.ToString().ToLowerInvariant()} {row.CheckedAt.ToLocalTime():yyyy-MM-dd HH:mm}";

    public bool HasLedgerNote => LedgerNote.Length > 0;

    public bool HasDiff => Diff.Count > 0;
    public bool HasFindings => Findings.Count > 0;
    public bool HasWarnings => Warnings.Count > 0;

    /// <summary>
    /// The lore the tool refused to overwrite, when the page's copy differs from the captured one.
    ///
    /// **It gets its own panel because lore is the one field a table cannot show.** Every other finding is a short
    /// token that fits in a grid cell; lore is a paragraph, and the user's decision here is a judgement about prose
    /// they have to actually read — most of the time the page's wording is the better one, which is exactly why the
    /// tool does not touch it and exactly why the user has to be able to see both.
    /// </summary>
    public FieldFinding? LoreToCompare =>
        Result.Analysis?.Find(ItemPageAnalyzer.LoreField) is { Verdict: FieldVerdict.NeedsReview, OnWiki: not null } f
            ? f
            : null;

    public bool HasLoreToCompare => LoreToCompare is not null;
    public string LoreOnWiki => LoreToCompare?.OnWiki ?? "";
    public string LoreInGame => LoreToCompare?.Captured ?? "";

    /// <summary>
    /// The formatting edit offered after a data commit — its own edit, never folded into the first one.
    /// </summary>
    public FormattingProposal? Formatting
    {
        get => _formatting;
        set
        {
            _formatting = value;
            _formattedWikitext = value?.Formatted ?? "";
            FormattingDiff.Clear();
            if (value is not null)
                foreach (DiffLine line in WikitextDiff.Compute(value.Original, value.Formatted))
                    FormattingDiff.Add(new DiffLineViewModel(line));

            OnPropertyChanged(nameof(FormattedWikitext));
            OnPropertyChanged(nameof(Formatting));
            OnPropertyChanged(nameof(HasFormatting));
            OnPropertyChanged(nameof(CanCommitFormatting));
            OnPropertyChanged(nameof(CanActOnIcon));
            OnPropertyChanged(nameof(IsNotBusy));
        }
    }

    public ObservableCollection<DiffLineViewModel> FormattingDiff { get; } = [];

    /// <summary>
    /// The formatted text the user is about to save, editable.
    ///
    /// **Editable for the same reason the data edit is**: the formatter has to put a field it has no rule for
    /// *somewhere*, and the user may disagree — real case, a `Velocity` field somebody added to a bridle. What is
    /// in the box is what gets written.
    /// </summary>
    public string FormattedWikitext
    {
        get => _formattedWikitext;
        set => Set(ref _formattedWikitext, value);
    }

    public bool HasFormatting => _formatting is not null;

    public bool CanCommitFormatting => _formatting is not null && !IsBusy && FormattingOutcome is null;

    /// <summary>What the formatting commit did, shown in place of its button afterwards.</summary>
    public string? FormattingOutcome
    {
        get => _formattingOutcome;
        set
        {
            Set(ref _formattingOutcome, value);
            OnPropertyChanged(nameof(HasFormattingOutcome));
            OnPropertyChanged(nameof(CanCommitFormatting));
            OnPropertyChanged(nameof(CanActOnIcon));
            OnPropertyChanged(nameof(IsNotBusy));
        }
    }

    public bool HasFormattingOutcome => !string.IsNullOrEmpty(_formattingOutcome);

    /// <summary>
    /// Whether the user can settle this item by hand, recording it as matched against this capture so it stops
    /// coming back.
    ///
    /// **Offered alongside a proposed edit too** (user, 2026-09-29, on `Eyerazzia`). It used to require
    /// <see cref="ItemCheckResult.NeedsAttention"/>, on the reasoning that with an edit pending saving it is the
    /// action that matters — which quietly assumed the tool's proposal is always the one to take. It is not: that
    /// page's damage bonus was deliberately annotated with the character level it applies at, something the tool is
    /// oblivious to by design, so the user wants to *keep the wiki's value*. Without this the only options were to
    /// overwrite their annotation or to skip, and skipping never settles — the item would return on every capture
    /// forever.
    /// </summary>
    public bool CanMarkChecked =>
        !HasOutcome && !IsBusy &&
        (Result.Status == ItemCheckStatus.EditProposed ||
         (Result.Status == ItemCheckStatus.AlreadyCorrect && Result.NeedsAttention));

    /// <summary>What the user is about to save. Editable, and what <c>Commit</c> writes.</summary>
    public string Wikitext
    {
        get => _wikitext;
        set
        {
            Set(ref _wikitext, value);
            // Typing a correction into a page the tool judged correct is what makes saving possible at all.
            OnPropertyChanged(nameof(CanAct));
            OnPropertyChanged(nameof(CanCreatePage));
        }
    }

    public string Summary
    {
        get => _summary;
        set
        {
            Set(ref _summary, value);
            OnPropertyChanged(nameof(CanAct));
            OnPropertyChanged(nameof(CanCreatePage));
        }
    }

    /// <summary>What happened when the user committed or skipped — shown in place of the buttons afterwards, so a
    /// page is never silently committed twice.</summary>
    public string? Outcome
    {
        get => _outcome;
        set
        {
            Set(ref _outcome, value);
            OnPropertyChanged(nameof(CanAct));
            OnPropertyChanged(nameof(CanCreatePage));
            OnPropertyChanged(nameof(CanSkip));
            OnPropertyChanged(nameof(CanMarkChecked));
            OnPropertyChanged(nameof(CanCommitFormatting));
            OnPropertyChanged(nameof(CanActOnIcon));
            OnPropertyChanged(nameof(IsNotBusy));
            OnPropertyChanged(nameof(HasOutcome));
        }
    }

    public bool HasOutcome => !string.IsNullOrEmpty(_outcome);

    /// <summary>Where the frame this item came from was archived, in debug builds. Shown as the list entry's tooltip
    /// so a bug can be reported by pointing at the item — see <see cref="CaptureArchive"/>.</summary>
    public string? CaptureFile
    {
        get => _captureFile;
        set
        {
            Set(ref _captureFile, value);
            OnPropertyChanged(nameof(CaptureTip));
        }
    }

    public string? CaptureTip => _captureFile is null ? null : $"Captured frame: {_captureFile}";

    /// <summary>The inverse of <see cref="IsBusy"/>, for controls that are enabled while idle. A converter for one
    /// binding would be more machinery than a property.</summary>
    public bool IsNotBusy => !IsBusy;

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            Set(ref _isBusy, value);
            OnPropertyChanged(nameof(CanAct));
            OnPropertyChanged(nameof(CanCreatePage));
            OnPropertyChanged(nameof(CanSkip));
            OnPropertyChanged(nameof(CanMarkChecked));
            OnPropertyChanged(nameof(CanCommitFormatting));
            OnPropertyChanged(nameof(CanActOnIcon));
            OnPropertyChanged(nameof(IsNotBusy));
        }
    }

    /// <summary>
    /// Whether "Save to the wiki" does anything.
    ///
    /// **The condition is what is on screen, not what the tool proposed** (user, 2026-09-29). It used to be
    /// <c>Result.CanCommit</c>, which is true only when the *tool* found changes — so on a page it judged already
    /// correct the button stayed dead even after the user typed a correction of their own into the wikitext box. That
    /// contradicts the one rule this screen owes: what is on screen is what gets saved. If the text differs from the
    /// page, there is something to save, whoever wrote it.
    ///
    /// The summary must be filled in too. For a tool-proposed edit it always is; for a hand-written one there is
    /// nothing to inherit, and the wiki should not receive an unexplained revision.
    /// </summary>
    public bool CanAct =>
        !HasOutcome && !IsBusy &&
        Result.Page is not null && Result.Edit is not null &&
        !string.IsNullOrWhiteSpace(_summary) &&
        !string.Equals(_wikitext, Result.Edit.OriginalWikitext, StringComparison.Ordinal);

    /// <summary>
    /// Whether this entry is a page the tool would *create* rather than an edit to one that exists.
    ///
    /// The review screen reads differently for it (user, 2026-10-01): there is no original to diff against, so the
    /// findings table and the diff have nothing to say, and the generated wikitext is shown outright rather than
    /// behind an Expander — on a creation it is not a detail to drill into, it is the whole thing being reviewed.
    /// </summary>
    public bool IsCreation => Result.CanCreate;

    /// <summary>
    /// Whether "Create the page" does anything. Same rule as <see cref="CanAct"/> minus its comparison against the
    /// original, since there is no original: text and a summary are all that is required.
    ///
    /// **A blank icon ID deliberately does not block it** (user, 2026-10-01). It is warned about instead, and the
    /// ledger records such a page as still wanting a human, so it comes back rather than being lost.
    /// </summary>
    public bool CanCreatePage =>
        !HasOutcome && !IsBusy && Result.Creation is not null &&
        !string.IsNullOrWhiteSpace(_wikitext) &&
        !string.IsNullOrWhiteSpace(_summary);

    /// <summary>Skipping is available for anything that reached the wiki but was not settled — including a page the
    /// tool would not touch — so the user can record "I looked, not now".</summary>
    public bool CanSkip =>
        !HasOutcome && !IsBusy &&
        Result.Status is ItemCheckStatus.EditProposed or ItemCheckStatus.NotOnWiki or ItemCheckStatus.NotAnItemPage;

    /// <summary>
    /// The two compliance findings that read as differences rather than as open questions (user, 2026-09-29): the
    /// era banner and the lore placeholder. Both are two-sided comparisons the tool fixes itself, so a yellow bar
    /// each was overstating them — and the strip's length is what decides whether any of it gets read.
    ///
    /// **Only these two, and only when the tool will fix them.** A duplicate parameter has no in-game side to
    /// compare against, and a compliance problem the tool *cannot* fix is exactly what the strip is for: nobody has
    /// acted on it.
    /// </summary>
    private static IEnumerable<FindingViewModel> ComplianceRows(ItemCheckResult result)
    {
        foreach (ComplianceFinding compliance in result.Analysis?.Compliance ?? [])
        {
            if (!BelongsInTheDifferencesTable(compliance, result)) continue;

            yield return compliance.Rule == ComplianceChecker.LorePlaceholderRule
                ? FindingViewModel.ForCompliance("Lore", compliance, emptyCaptured: "no lore")
                : FindingViewModel.ForCompliance("Era", compliance);
        }
    }

    /// <summary>
    /// The era banner and the lore placeholder, and only when the tool will fix them — plus the overlap rule the
    /// analysis owns (<see cref="ItemPageAnalysis.IsAlreadyCoveredByAFieldFinding"/>), which keeps the placeholder
    /// from producing a second, wrong `Lore` row on an item that does have lore.
    /// </summary>
    private static bool BelongsInTheDifferencesTable(ComplianceFinding compliance, ItemCheckResult result) =>
        compliance.ToolWillFix &&
        compliance.Rule is ComplianceChecker.EraTemplateRule or ComplianceChecker.LorePlaceholderRule &&
        result.Analysis?.IsAlreadyCoveredByAFieldFinding(compliance) is false;

    /// <summary>An instance method rather than a static one over the result, because one of these bars depends on
    /// what the user has done since: an icon bar telling them to press a button they have already pressed is the
    /// kind of stale instruction that teaches people to stop reading the strip.</summary>
    private IEnumerable<string> BuildWarnings()
    {
        ItemCheckResult result = Result;

        foreach (string warning in result.Warnings)
        {
            // The lore section says this, with a button to act on it. Repeating it up here was noise once lore got
            // a region of its own (user, 2026-09-28).
            if (result.NeedsLoreCapture && warning.Contains("Lore tab", StringComparison.Ordinal)) continue;
            yield return warning;
        }

        // Every field the tool declined to decide, in the warning strip rather than only as a row in the findings
        // table. The table truncates, and these are precisely the items nobody has judged yet — the tool has said
        // so explicitly, so they are the last thing that should be skimmable.
        foreach (FieldFinding blocker in result.Analysis?.Blockers ?? [])
            yield return $"{FindingViewModel.DisplayField(blocker.Field)}: {blocker.Explanation ?? "needs a human before anything is written."}";

        foreach (IneligibilityDetail blocker in result.Eligibility?.Blockers ?? [])
            yield return blocker.Explanation;

        if (result.Error is { } error) yield return error;

        // Nothing about the icon: a mismatch, a comparison that could not run and a missing lucy_img_ID are all said
        // in red in the Icon panel, beside the images and the button that acts on them (user, 2026-10-07). A second
        // copy up here was one more bar to read past.

        foreach (ComplianceFinding compliance in result.Analysis?.Compliance ?? [])
        {
            // The era banner and the lore placeholder are rows in the differences table instead — see ComplianceRows.
            if (BelongsInTheDifferencesTable(compliance, result)) continue;

            yield return compliance.ToolWillFix
                ? $"Template compliance (fixed by this edit): {compliance.Detail}"
                : $"Template compliance — needs a human: {compliance.Detail}";
        }

        foreach (string deferred in result.Edit?.Deferred ?? []) yield return "Not done: " + deferred;

        if (result.Creation is { } creation)
        {
            foreach (string gap in creation.Gaps)
            {
                if (gap.Contains("lucy_img_ID", StringComparison.Ordinal)) continue; // the Icon panel says it
                yield return gap;
            }

            foreach (string refusal in creation.FormattingRefusals)
                yield return "The formatting pass declined to lay this page out: " + refusal;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(name);
    }

    private void OnPropertyChanged(string? name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
