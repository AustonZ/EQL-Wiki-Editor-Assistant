using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using EQLWikiEditorAssistant.Core.Icons;
using EQLWikiEditorAssistant.Core.Items;
using EQLWikiEditorAssistant.Core.Imaging;
using EQLWikiEditorAssistant.Pipeline;
using EQLWikiEditorAssistant.Wiki.Analysis;
using EQLWikiEditorAssistant.Wiki.Formatting;
using EQLWikiEditorAssistant.Wiki.Mapping;
using EQLWikiEditorAssistant.Wiki.Ledger;

namespace EQLWikiEditorAssistant.App;

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
        // Window text is not removed but moved, which is what the reader needs to know about it.
        finding.Field == ItemPageAnalyzer.WindowTextField
            ? "Move to notes"
            : DisplayVerdict(finding.Verdict, finding.Captured, finding.OnWiki),
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
        ItemPageAnalyzer.WindowTextField => "Flags (old window text)",
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

/// <summary>The pages of the step-by-step review, in order (user, 2026-10-07).</summary>
public enum ReviewStep
{
    Edit,
    Preview,
    Submit,
    Done,
}

public enum StepState
{
    Finished,
    Current,
    Upcoming,
    Skipped,
}

/// <summary>One stop on the review's timeline.</summary>
public sealed record StepViewModel(ReviewStep Step, int Number, string Label, StepState State, bool CanJump)
{
    public Brush Foreground => State switch
    {
        StepState.Current => Palette.Text,
        StepState.Finished => Palette.Done,
        _ => Palette.Muted,
    };

    /// <summary>The circle's fill: the accent for where the user is, the done colour for what is behind them. A skipped
    /// step is hollow — passed over, with nothing in it.</summary>
    public Brush Marker => State switch
    {
        StepState.Current => Palette.Accent,
        StepState.Finished => Palette.Done,
        StepState.Skipped => Brushes.Transparent,
        _ => Palette.Raised,
    };

    /// <summary>Dark on the two filled markers, muted on the empty ones.</summary>
    public Brush MarkerForeground => State is StepState.Current or StepState.Finished ? Palette.Surface : Palette.Muted;

    /// <summary>
    /// A skipped step is a hollow, dashed ring around a skip-forward glyph, with its label struck through (user,
    /// 2026-10-07, who asked for something other than the checkmark it had shared with a finished step). The dashes and
    /// the strike say "not done" and the glyph says "on purpose", so it reads as neither finished nor forgotten.
    /// </summary>
    public bool IsSkipped => State == StepState.Skipped;

    public string MarkerText => State switch
    {
        StepState.Finished => "✓",
        StepState.Skipped => "",
        _ => Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    /// <summary>The connector after this stop; the last has none.</summary>
    public bool HasConnector => Step != ReviewStep.Done;

    public string? Tip => CanJump ? $"Back to {Label}" : State == StepState.Skipped ? "Skipped" : null;
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
    private CapturedImage? _loreImage;
    private string? _captureFile;
    private bool _settled;
    private ReviewStep _step;
    private bool _skippedSubmit;
    private bool _layoutOffered;
    private bool _reformattingExpanded;
    private bool _sourceExpanded;
    private string _loadedWikitext = "";
    private string _loadedSummary = "";
    private bool _written;
    private string? _submitNotice;

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
        // A new item has no page to start from, so the box is seeded with the whole page the tool would create. An
        // existing one gets the proposed edit laid out, since the edit and its formatting are saved together.
        _wikitext = result.Creation?.Wikitext ?? result.Review?.Proposed ?? result.Edit?.NewWikitext
            ?? result.Page?.Wikitext ?? "";
        // Left blank when the tool proposes nothing, rather than inheriting its "No changes" — if the user is
        // hand-editing a page the tool judged correct, that summary would be a false description of the revision,
        // and there is nothing to inherit. CanAct requires one, so it has to be typed. A creation does have one to
        // inherit, because "created from the in-game item window" is the whole truth about that revision. A page
        // whose layout alone changes gets the layout summary (see ReviewText.SummaryFor).
        _summary = result.Creation?.Summary
            ?? (result.Review is { } texts && result.Edit is { } toolEdit ? texts.SummaryFor(toolEdit) : null)
            ?? (result.Edit is { HasChanges: true } proposed ? proposed.Summary : "");
        _loadedWikitext = _wikitext;
        _loadedSummary = _summary;
        _outcome = null;
        _settled = false;
        // Every reload starts the review over: a re-capture, a lore capture joining, or "Edit again" after a save all
        // mean there is a fresh comparison to read first.
        _step = ReviewStep.Edit;
        _skippedSubmit = false;
        _layoutOffered = false;
        _sourceExpanded = false;
        _written = false;
        _submitNotice = null;
        PreviewedText = null;
        PreviewDocument = null;
        Replace(FormattingNotes, result.Review?.FormattingNotes.Distinct() ?? []);
        Diff.Clear();
        // The data changes against the page laid out the same way, so the diff holds nothing but data, each line in
        // its blueprint place; the reformatting is its own section (user, 2026-10-12, see ReviewText).
        Replace(ProposedDiff, IsCreation || result.Edit is not { HasChanges: true } ? []
            : result.Review is { } review
                ? WikitextDiff.Compute(review.FormattedOriginal, review.Proposed).Select(l => new DiffLineViewModel(l))
                : WikitextDiff.Compute(result.Edit.OriginalWikitext, result.Edit.NewWikitext).Select(l => new DiffLineViewModel(l)));
        IReadOnlyList<DiffLine> reformatting = result.Review is { Reformats: true } layout && !IsCreation
            ? WikitextDiff.Compute(layout.Original, layout.FormattedOriginal)
            : [];
        Replace(ReformattingDiff, reformatting.Select(l => new DiffLineViewModel(l)));
        _reformattedLines = reformatting.Count(l => l.Kind is DiffLineKind.Added or DiffLineKind.Removed);
        Replace(Findings, (result.Analysis?.Findings
                .Where(f => f.IsChange || f.Blocks)
                .Select(FindingViewModel.For) ?? [])
            .Concat(ComplianceRows(result)));
        Replace(Warnings, BuildWarnings());

        // Open when the layout is all there is to look at.
        _reformattingExpanded = HasOnlyLayoutProposed;

        RebuildSteps();

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

    /// <summary>
    /// The edit the tool worked out, as a diff on the Edit page (user, 2026-10-07). Fixed when the item was checked,
    /// unlike <see cref="Diff"/>, which Submit builds from whatever the editor then holds — this one says what the tool
    /// proposes, and that one what will be written. Empty for a creation, which shows its whole text instead.
    /// </summary>
    public ObservableCollection<DiffLineViewModel> ProposedDiff { get; } = [];

    public bool HasProposedDiff => ProposedDiff.Count > 0;

    /// <summary>
    /// The page's own reformatting — as fetched against laid out to the blueprint — shown collapsed under the proposed
    /// edit (user, 2026-10-12). It needs no approval of its own: the formatter proves it changed nothing the page says.
    /// The same on the Submit page, since the text on screen is laid out before it gets there.
    /// </summary>
    public ObservableCollection<DiffLineViewModel> ReformattingDiff { get; } = [];

    private int _reformattedLines;

    public bool HasReformatting => ReformattingDiff.Count > 0;

    public string ReformattingHeader =>
        $"Automatic reformatting ({_reformattedLines} line{(_reformattedLines == 1 ? "" : "s")})";

    /// <summary>Nothing about the data changes, only the layout: said in words where the proposed edit would be.</summary>
    public bool HasOnlyLayoutProposed => (!HasProposedDiff || _settled) && HasReformatting && IsLayoutOnly;

    /// <summary>The same on the Submit page, from the text on screen.</summary>
    public bool HasOnlyLayoutToSubmit => !HasDiff && HasReformatting && IsLayoutOnly;

    /// <summary>
    /// Whether the text on screen changes only the page's layout: it is the page laid out and nothing else. Such a
    /// save leaves the ledger alone (<c>ItemCheckPipeline.CommitLayoutAsync</c>), since the data question was already
    /// settled — by the check, or by "the wiki is right".
    /// </summary>
    public bool IsLayoutOnly =>
        !IsCreation && Result.Review is { Reformats: true } review &&
        string.Equals(_wikitext, review.FormattedOriginal, StringComparison.Ordinal);

    /// <summary>
    /// After "the wiki is right" on a page whose layout is off (user, 2026-10-12): the data stays as the page has it,
    /// and the review offers the page's layout instead, to save or leave. In two halves so the screen can animate
    /// between them: <see cref="BeginLayoutOffer"/> puts up the notice with the comparison still showing, and
    /// <see cref="FinishLayoutOffer"/> takes the comparison away and opens the reformatting.
    /// </summary>
    public void OfferLayoutOnly()
    {
        BeginLayoutOffer();
        FinishLayoutOffer();
    }

    public void BeginLayoutOffer()
    {
        if (Result.Review is not { Reformats: true } review) return;

        _wikitext = review.FormattedOriginal;
        _loadedWikitext = _wikitext;
        _summary = ReviewText.LayoutOnlySummary;
        _loadedSummary = _summary;
        _settled = true;
        RebuildSteps();
        OnPropertyChanged(null);
    }

    public void FinishLayoutOffer()
    {
        if (Result.Review is not { Reformats: true }) return;

        // The comparison is answered, so it goes, and what is left is shown open (user, 2026-10-12).
        ProposedDiff.Clear();
        _layoutOffered = true;
        _reformattingExpanded = true;
        OnPropertyChanged(null);
    }

    /// <summary>Whether the first page shows the comparison — warnings, icon, lore, findings and proposed edit. Not
    /// once "the wiki is right" has answered it and only the layout is left to offer (user, 2026-10-12).</summary>
    public bool ShowsComparison => !_layoutOffered;

    /// <summary>Whether the reformatting section is open. Collapsed by default; open when the layout is all the review
    /// offers. The user can open or close it either way.</summary>
    public bool ReformattingExpanded
    {
        get => _reformattingExpanded;
        set => Set(ref _reformattingExpanded, value);
    }

    /// <summary>
    /// "Done" in place of Next (user, 2026-10-12): a page that already agrees, needs no layout and has nothing typed
    /// into it has nothing to step through, so the item closes from here. Next comes back once the text is changed.
    /// </summary>
    public bool CanFinishHere =>
        HasReview && !IsCreation && IsDone && !HasChangesToSubmit && !HasOutcome && _step == ReviewStep.Edit;

    /// <summary>Next only where it leads somewhere (user, 2026-10-12): a page that agrees but still wants a human shows
    /// "the wiki is right" and "Skip for now" instead, until the text is changed.</summary>
    public bool ShowsNext => HasReview && !CanFinishHere && NextGoesSomewhere;

    /// <summary>
    /// Lays out a hand edit as the first page is left, so Preview, Submit and the save all show exactly what is written
    /// (user, 2026-10-12). Text the tool proposed is laid out already. Returns why the formatter would not, when it
    /// would not: the text is then saved as typed.
    /// </summary>
    public string? LayOutHandEdits()
    {
        if (Result.Review is { } review &&
            (string.Equals(_wikitext, review.Proposed, StringComparison.Ordinal) ||
             string.Equals(_wikitext, review.FormattedOriginal, StringComparison.Ordinal)))
            return null;
        if (Result.Review is null && !IsCreation) return null;
        if (IsCreation && string.Equals(_wikitext, Result.Creation?.Wikitext, StringComparison.Ordinal)) return null;

        PrettifyResult formatted = ItemPagePrettifier.Format(_wikitext, WikiMapping.Default);
        if (!formatted.IsSafe) return string.Join(" ", formatted.Refusals);
        if (formatted.Changed) Wikitext = formatted.Formatted;
        return null;
    }

    /// <summary>
    /// Whether the user has done anything to this item since it was loaded: moved on from the first page, settled it,
    /// or changed the text or the summary.
    /// </summary>
    public bool HasWorkInProgress =>
        _step != ReviewStep.Edit || _settled ||
        !string.Equals(_wikitext, _loadedWikitext, StringComparison.Ordinal) ||
        !string.Equals(_summary, _loadedSummary, StringComparison.Ordinal);

    /// <summary>
    /// Whether a new capture of this item should be left out rather than replace what is on screen (user, 2026-10-08):
    /// the user is partway through it and the capture says nothing new. Capturing a Lore tab with several item windows
    /// open captures all the others again too, and reloading them threw away whatever was half done.
    ///
    /// "Nothing new" is the same fingerprint the ledger uses — the item's data, its icon and its lore — so a capture
    /// that really did change the item still replaces it. A capture that could not be read at all (covered, the wrong
    /// font) cannot say the item changed, so it does not get to throw the work away either.
    /// </summary>
    public bool KeepsWorkAgainst(ItemCheckResult capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        if (!HasWorkInProgress) return false;
        if (capture.Item is null ||
            capture.Status is ItemCheckStatus.Occluded or ItemCheckStatus.WrongFont or ItemCheckStatus.UnsupportedSkin)
            return true;
        if (Result.Item is null) return false;

        return string.Equals(
            ItemFingerprint.Compute(Result.Item, Result.CapturedIcon, Result.Lore),
            ItemFingerprint.Compute(capture.Item, capture.CapturedIcon, capture.Lore),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether "Edit wikitext source" is open, per item (user, 2026-10-08). The one Expander serves every item, so it
    /// used to carry its state from one to the next. It is closed whenever the item loads afresh — a capture, "Edit
    /// again" — and kept otherwise, so Back from Preview finds it as it was left.
    /// </summary>
    public bool SourceExpanded
    {
        get => _sourceExpanded;
        set => Set(ref _sourceExpanded, value);
    }
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
    /// <remarks>Not for an item the ledger let skip the wiki: nothing was compared, so the section could only say
    /// "not compared" beside an empty wiki tile. It carries the icon for its list entry and a re-check.</remarks>
    // Not for an item nothing was compared for: one the ledger let skip the wiki, or a levelled one, whose captured icon
    // is there only for its entry in the list.
    public bool HasIcons =>
        Result.Status is not (ItemCheckStatus.AlreadyChecked or ItemCheckStatus.Ineligible) &&
        (Result.CapturedIconImage is not null || Result.WikiIconImage is not null || MatchedIconImage is not null);

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
    /// <remarks>"Suggested" on an existing page, where the tile is an offer the user acts on rather than an id already
    /// written (tester feedback, 2026-10-11).</remarks>
    public string MatchedIconCaption => Result.IconSuggestion switch
    {
        { IsConfident: true } s when !IsCreation => $"Suggested (ID: {s.IconId})",
        { IsConfident: true } s => $"Matched (ID: {s.IconId})",
        { } s => $"Best Guess (ID: {s.IconId})",
        _ => "",
    };

    /// <summary>Bright green for a match the user can act on, amber for one they have to settle. The green is the
    /// suggestion button's own, not the panel's "done" green, so the tile and the button read as one call to action
    /// (tester feedback, 2026-10-11).</summary>
    public Brush MatchedIconBorderBrush =>
        Result.IconSuggestion is { IsConfident: true } ? Palette.Suggestion : Palette.Warning;

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

    /// <summary>
    /// What pressing it will do, said before it is pressed — the upload is the part that cannot be taken back, so it
    /// is named rather than implied.
    ///
    /// **On an existing page it also says the button is the fix and when it is saved** (tester feedback, 2026-10-11:
    /// "it wasn't immediately clear what I needed to do"). The verdict above only said the page's icon may be wrong.
    /// </summary>
    public string IconActionPrompt => (IsCreation, Result.IconSuggestion) switch
    {
        (true, { NeedsUpload: true } s) => $"Icon #{s.IconId} is missing on the wiki.",
        (false, { NeedsUpload: true } s) =>
            $"The wiki doesn't have icon #{s.IconId} yet. Press the button below to upload it and put it in this " +
            "edit, which is saved with the rest of the edit.",
        (false, { } s) =>
            $"Press the button below to put icon #{s.IconId} in this edit. It is saved with the rest of the edit.",
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
        {
            // Laid out like the rest of the proposal, so a new lucy_img_ID line lines up with its neighbours.
            if (Result.Review is not null && ItemPagePrettifier.Format(wikitext, WikiMapping.Default) is { IsSafe: true } laidOut)
                _wikitext = laidOut.Formatted;

            string before = Result.Review?.FormattedOriginal ?? edit.OriginalWikitext;
            Replace(Diff, WikitextDiff.Compute(before, _wikitext).Select(l => new DiffLineViewModel(l)));
            // Setting the icon is the tool's proposal too, so the proposed edit shows it.
            Replace(ProposedDiff, WikitextDiff.Compute(before, _wikitext).Select(l => new DiffLineViewModel(l)));
        }

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
        string next = IsCreation ? "" : $" in this edit. To save it, press Next, then \"{SaveButtonText}\"";
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
        { WikiFileDiffersFromGame: true } c =>
            $"The ID is right, but the wiki's file for icon #{c.IconId} is different artwork from the game's.",
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
            OnPropertyChanged(nameof(CanGoNext));
            OnPropertyChanged(nameof(ShowsNext));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(StatusBrush));
            // A page just created becomes clickable at this moment and not before — see KnownPageTitle.
            OnPropertyChanged(nameof(PageTitle));
            OnPropertyChanged(nameof(HasPage));
            OnPropertyChanged(nameof(PageTitleIfDifferent));
            OnPropertyChanged(nameof(PageUrl));
        }
    }

    public string StatusText => Result switch
    {
        // Green either way (see StatusBrush), but worded for what actually happened (user, 2026-10-07): "Done" is
        // kept for an item the user settled by acting on it. A page that already matched had nothing done to it, and
        // an item the ledger let skip the wiki was not compared at all this time.
        // The data is settled and the layout is all that is left (user, 2026-10-07, worded 2026-10-12): "Done" and
        // "No differences" both left out that the review still had something to offer.
        _ when NeedsReformattingOnly => "Needs reformatting",
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
        ItemCheckStatus.UnsupportedSkin => "Unsupported UI skin",
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

    /// <summary>
    /// The data question is answered — by the check, or by "the wiki is right" — and the page's layout is still to save
    /// or leave. Before Done: once there, it was saved.
    /// </summary>
    private bool NeedsReformattingOnly =>
        _step < ReviewStep.Done && !IsCreation &&
        (_settled ||
         (Result is { Status: ItemCheckStatus.AlreadyCorrect, NeedsAttention: false, Review.Reformats: true }));

    /// <summary>"The item matches the wiki.", where a proposed edit would otherwise be: the check found nothing to
    /// change and nothing for a human to judge.</summary>
    public bool HasMatchNotice =>
        !IsCreation && Result is { Status: ItemCheckStatus.AlreadyCorrect, NeedsAttention: false };

    /// <summary>
    /// "The page's formatting is correct.", the second green notice (user, 2026-10-12: with nothing to do, saying so
    /// twice is what reassures). Only where the formatter understood the page and had nothing to change: a page it left
    /// alone also comes back unchanged, but with a note saying why, and that page's layout is not known to be right.
    /// </summary>
    public bool HasLayoutCorrectNotice =>
        HasMatchNotice && Result.Review is { Reformats: false, FormattingNotes.Count: 0 };

    /// <summary>The first page's yellow notice. "(no data changes)" only where nothing above says so already: after
    /// "the wiki is right" the comparison is gone, while a page that matched has the green notice (user, 2026-10-12).</summary>
    public string LayoutNoticeText => HasMatchNotice
        ? "The page still needs to be reformatted."
        : "The page still needs to be reformatted (no data changes).";

    public Brush StatusBrush => Result switch
    {
        _ when NeedsReformattingOnly => Palette.Warning,
        _ when IsDone => Palette.Done,
        { Status: ItemCheckStatus.AlreadyCorrect, NeedsAttention: true } => Palette.Attention,
        { NeedsLoreCapture: true } or { Status: ItemCheckStatus.LoreRecorded } => Palette.Attention,
        { Status: ItemCheckStatus.EditProposed } => Palette.Warning,
        { Status: ItemCheckStatus.Failed or ItemCheckStatus.Occluded or ItemCheckStatus.WrongFont
            or ItemCheckStatus.UnsupportedSkin } => Palette.Attention,
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

    /// <summary>
    /// Whether the item's name was read, which is all "Open wiki page" and "Refresh wiki data" need (user, 2026-10-12:
    /// "no special rules, just dumb links whenever we can make them"). Every result past the point where a window is
    /// read has one — a levelled item and a Lore-tab capture included; a covered window, one in the other font and one
    /// in another skin do not, since nothing was read from them.
    /// </summary>
    public bool HasItemName => Result.Item is not null && Result.ItemName.Length > 0;

    /// <summary>The page "Open wiki page" opens: the one known, else the item's own name, which on a wiki without
    /// that page is where the wiki offers to create it.</summary>
    private string? LinkTitle => KnownPageTitle ?? (HasItemName ? Result.ItemName : null);

    /// <summary>
    /// The page's title beside the "Open wiki page" link, only when it is not the item's own name — which happens when
    /// the wiki redirected the name to another page, or the ledger recorded a hand-chosen title (<c>Cell Key #5</c> at
    /// <c>Cell Key No. 5</c>). Otherwise the name above already says it.
    /// </summary>
    public string PageTitleIfDifferent => KnownPageTitle is { } title && !string.Equals(title, Result.ItemName, StringComparison.Ordinal)
        ? $"  ({title})"
        : "";

    public string? PageUrl => LinkTitle is { } title
        ? $"https://eqlwiki.com/{Uri.EscapeDataString(title.Replace(' ', '_'))}"
        : null;

    /// <summary>When an already-checked item was last settled, and how. Shown beside the link, because the reason to
    /// open the page is usually to judge whether the row is still right.</summary>
    public string LedgerNote => Result.LedgerRow is not { } row
        ? ""
        : $"{row.Outcome.ToString().ToLowerInvariant()} {row.CheckedAt.ToLocalTime():yyyy-MM-dd HH:mm}";

    public bool HasLedgerNote => LedgerNote.Length > 0;

    /// <summary>The final diff on the Submit page, against the page as fetched and laid out — so, like the first page's,
    /// it holds the data changes and the reformatting is its own section — or, for a new page, the whole text. Built on
    /// entering that page, from whatever the editor then holds.</summary>
    public bool HasDiff => Diff.Count > 0;

    /// <summary>Whether the first page shows the raw wikitext editor: anything with a page to edit, including one
    /// the tool found correct, since a hand-written correction is a real edit.</summary>
    public bool HasEditor => Result.Edit is not null && !IsCreation;

    /// <summary>Rebuilds <see cref="Diff"/> from the text on screen. What Save writes is what is in the editor, so
    /// the diff shown beside it has to be of that, not of the tool's proposal.</summary>
    public void RefreshSubmitDiff()
    {
        string before = IsCreation ? "" : Result.Review?.FormattedOriginal ?? Result.Edit?.OriginalWikitext ?? "";
        Replace(Diff, WikitextDiff.Compute(before, _wikitext).Select(l => new DiffLineViewModel(l)));
        OnPropertyChanged(nameof(HasDiff));
        OnPropertyChanged(nameof(HasOnlyLayoutToSubmit));
    }
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
        !HasOutcome && !IsBusy && !_settled &&
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
            OnPropertyChanged(nameof(CanSave));
            OnPropertyChanged(nameof(HasChangesToSubmit));
            OnPropertyChanged(nameof(CanGoNext));
            OnPropertyChanged(nameof(IsLayoutOnly));
            OnPropertyChanged(nameof(HasOnlyLayoutProposed));
            OnPropertyChanged(nameof(CanSkipLayout));
            OnPropertyChanged(nameof(CanFinishHere));
            OnPropertyChanged(nameof(ShowsNext));
            OnPropertyChanged(nameof(CanReset));
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
            OnPropertyChanged(nameof(CanSave));
            OnPropertyChanged(nameof(CanReset));
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
            OnPropertyChanged(nameof(CanSave));
            OnPropertyChanged(nameof(CanAct));
            OnPropertyChanged(nameof(CanCreatePage));
            OnPropertyChanged(nameof(CanSkip));
            OnPropertyChanged(nameof(CanMarkChecked));
            OnPropertyChanged(nameof(CanActOnIcon));
            OnPropertyChanged(nameof(IsNotBusy));
            OnPropertyChanged(nameof(HasOutcome));
            OnPropertyChanged(nameof(CanReset));
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
            OnPropertyChanged(nameof(CanSave));
            OnPropertyChanged(nameof(CanGoNext));
            OnPropertyChanged(nameof(CanAct));
            OnPropertyChanged(nameof(CanCreatePage));
            OnPropertyChanged(nameof(CanSkip));
            OnPropertyChanged(nameof(CanMarkChecked));
            OnPropertyChanged(nameof(CanSkipLayout));
            OnPropertyChanged(nameof(CanActOnIcon));
            OnPropertyChanged(nameof(CanReCheck));
            OnPropertyChanged(nameof(IsNotBusy));
            OnPropertyChanged(nameof(CanReset));
        }
    }

    /// <summary>
    /// Whether "Refresh wiki data" can run now. It is shown on every item whose name was read (<see cref="HasItemName"/>,
    /// user, 2026-10-12), replacing a list of the statuses where the wiki was what stopped the comparison; this only
    /// greys it out while the item is busy. A levelled item or a Lore-tab capture comes back unchanged, since nothing
    /// on the wiki changes what was decided about it (<c>ItemCheckPipeline.ReanalyzeAsync</c>).
    /// </summary>
    public bool CanReCheck => !IsBusy && HasItemName && Result.WindowImage is not null;

    /// <summary>
    /// "Reset" on the first page (user, 2026-10-12): "Refresh wiki data" under a name that says what it is for here,
    /// shown only when there is something to undo — the text or summary changed (by hand, or by the icon button), or
    /// "the wiki is right" turned the review into the layout offer. Resetting from there takes that answer back, in
    /// the ledger too: a check that finds an edit to propose removes a row saying the item is done.
    /// </summary>
    public bool CanReset =>
        CanReCheck && HasReview && !HasOutcome && _step == ReviewStep.Edit &&
        (_layoutOffered ||
         !string.Equals(_wikitext, _loadedWikitext, StringComparison.Ordinal) ||
         !string.Equals(_summary, _loadedSummary, StringComparison.Ordinal));

    /// <summary>
    /// Whether refreshing would throw away something typed and not saved: the wikitext or the summary, changed since
    /// the item was loaded, on an item not yet settled. A refresh reloads the item from the wiki, so it asks first.
    /// Moving between pages is not work a refresh loses, since the item comes back on its first page anyway.
    /// </summary>
    public bool HasUnsavedEdits =>
        !_settled &&
        (!string.Equals(_wikitext, _loadedWikitext, StringComparison.Ordinal) ||
         !string.Equals(_summary, _loadedSummary, StringComparison.Ordinal));

    /// <summary>The captured icon, small, for the item list.</summary>
    public System.Windows.Media.ImageSource? TabIcon =>
        Result.CapturedIconImage is { } icon ? Bitmaps.From(icon) : null;

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

    /// <summary>
    /// "Don't reformat": leave a page's layout as it is, when the layout is all the review offers — a page whose data
    /// already matched, or one the user has said the wiki is right about. Nothing is written and the ledger stays as
    /// the check or the user left it.
    /// </summary>
    public bool CanSkipLayout => !IsBusy && !HasOutcome && _step == ReviewStep.Edit && IsDone && IsLayoutOnly;

    /// <summary>Skipping is available for anything that reached the wiki but was not settled — including a page the
    /// tool would not touch, and one that agrees but still wants a human (user, 2026-10-12) — so the user can record "I
    /// looked, not now".</summary>
    public bool CanSkip =>
        !HasOutcome && !IsBusy && !_settled &&
        (Result.Status is ItemCheckStatus.EditProposed or ItemCheckStatus.NotOnWiki or ItemCheckStatus.NotAnItemPage ||
         (Result.Status == ItemCheckStatus.AlreadyCorrect && Result.NeedsAttention));

    // ============================ the step-by-step review ============================

    /// <summary>Whether this item gets the step-by-step review at all: something to edit or create, or a page that
    /// already agrees and may still want its formatting. Everything else is a report with nothing to step through.
    /// </summary>
    public bool HasReview =>
        IsCreation || Result.Status is ItemCheckStatus.EditProposed or ItemCheckStatus.AlreadyCorrect;

    /// <summary>The page of the review this item is on. Each item keeps its own, so switching between them in the list
    /// returns to where the user left each one.</summary>
    public ReviewStep Step
    {
        get => _step;
        set
        {
            if (_step == value) return;
            _step = value;
            OnPropertyChanged(nameof(Step));
            OnPropertyChanged(nameof(CanSkipLayout));
            OnPropertyChanged(nameof(CanFinishHere));
            OnPropertyChanged(nameof(ShowsNext));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(StatusBrush));
            OnPropertyChanged(nameof(CanReset));
            RebuildSteps();
        }
    }

    /// <summary>True when Preview and Submit were passed over — the user said the wiki is right, or there was nothing
    /// to save — so the timeline shows them as not needed rather than as done.</summary>
    public bool SkippedSubmit
    {
        get => _skippedSubmit;
        set
        {
            _skippedSubmit = value;
            RebuildSteps();
        }
    }

    /// <summary>True once the data edit or the creation has been saved. The pages before it can no longer be gone back
    /// to, since they describe a page that is not there any more; "Make another edit" starts afresh instead.</summary>
    public bool Written
    {
        get => _written;
        set
        {
            _written = value;
            RebuildSteps();
        }
    }

    public ObservableCollection<StepViewModel> Steps { get; } = [];

    private void RebuildSteps()
    {
        // Back is allowed only until something is settled: after a save, or "the wiki is right", the earlier pages
        // describe a decision already made.
        bool settledAlready = _skippedSubmit || _written;
        var steps = new List<StepViewModel>();
        foreach (ReviewStep step in Enum.GetValues<ReviewStep>())
        {
            // Done is reached only once everything is finished, so it reads as finished itself (user, 2026-10-07).
            StepState state =
                step == _step && step == ReviewStep.Done ? StepState.Finished
                : step == _step ? StepState.Current
                : _skippedSubmit && step is ReviewStep.Preview or ReviewStep.Submit ? StepState.Skipped
                : step < _step ? StepState.Finished
                : StepState.Upcoming;

            bool canJump = step < _step && !settledAlready && state == StepState.Finished;
            steps.Add(new StepViewModel(step, (int)step + 1, LabelFor(step), state, canJump));
        }

        Replace(Steps, steps);
    }

    private string LabelFor(ReviewStep step) => step switch
    {
        ReviewStep.Edit => "Edit",
        ReviewStep.Preview => "Preview",
        ReviewStep.Submit => "Submit",
        _ => "Done",
    };

    /// <summary>Whether the text on screen would change anything: it differs from the page, or it is a new page with
    /// anything in it. What decides whether there is anything to preview and save.</summary>
    public bool HasChangesToSubmit => IsCreation
        ? !string.IsNullOrWhiteSpace(_wikitext)
        : Result.Page is not null && Result.Edit is not null &&
          !string.Equals(_wikitext, Result.Edit.OriginalWikitext, StringComparison.Ordinal);

    /// <summary>
    /// Whether the first page's Next goes anywhere: on to Preview when there is something to save, or straight to
    /// Done for a page that already agrees, is laid out, and was settled by the check itself.
    ///
    /// A page that agrees but still wants a human is not settled, so it has no Next — "the wiki is right" is how it
    /// moves on, and that is a decision the user has to make rather than one Next makes for them.
    /// </summary>
    public bool CanGoNext => !IsBusy && NextGoesSomewhere;

    private bool NextGoesSomewhere => HasChangesToSubmit || (IsDone && Result.Page is not null);

    /// <summary>Whether the Submit page's button writes anything: the edit or the creation, with a summary.</summary>
    public bool CanSave => IsCreation ? CanCreatePage : CanAct;

    public string SaveButtonText => IsCreation ? "Create the page" : "Save to the wiki";

    public string SubmitHeading => IsCreation ? "Create the page" : "Save the edit to";

    /// <summary>The title the save goes to — large on the Submit page, since for a creation it is the one thing the
    /// user is the only one able to judge.</summary>
    public string SubmitTitle => IsCreation ? Result.Creation?.Title ?? PageTitle : PageTitle;

    /// <summary>A warning shown on the Submit page, worked out from the text on screen when the page is entered — for a
    /// creation with no <c>lucy_img_ID</c>.</summary>
    public string? SubmitNotice
    {
        get => _submitNotice;
        set
        {
            Set(ref _submitNotice, value);
            OnPropertyChanged(nameof(HasSubmitNotice));
        }
    }

    public bool HasSubmitNotice => !string.IsNullOrEmpty(_submitNotice);

    /// <summary>What the formatting pass had to say besides a layout — fields it kept without a place for them, why it
    /// left the statsblock alone, or why a hand edit is saved as typed. Shown under the proposed edit and on Submit.
    /// </summary>
    public ObservableCollection<string> FormattingNotes { get; } = [];

    public bool HasFormattingNotes => FormattingNotes.Count > 0;

    public void AddFormattingNote(string note)
    {
        if (!FormattingNotes.Contains(note)) FormattingNotes.Add(note);
        OnPropertyChanged(nameof(HasFormattingNotes));
    }

    /// <summary>The text last rendered for Preview and the document it produced. Kept so going back and forward
    /// without changing anything does not send the text to the wiki again.</summary>
    public string? PreviewedText { get; set; }

    public string? PreviewDocument { get; set; }

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

            // Shown as a badge beside the item's name instead (user, 2026-10-07).
            if (warning == ItemCheckPipeline.NotVerifiedNotice) continue;
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
