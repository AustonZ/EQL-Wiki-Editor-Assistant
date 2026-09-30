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
        DiffLineKind.Removed => new SolidColorBrush(Color.FromRgb(0xFF, 0xE5, 0xE5)),
        DiffLineKind.Added => new SolidColorBrush(Color.FromRgb(0xE2, 0xF6, 0xE2)),
        _ => Brushes.Transparent,
    };

    public Brush Foreground => Line.Kind switch
    {
        DiffLineKind.Removed => new SolidColorBrush(Color.FromRgb(0x8B, 0x1A, 0x1A)),
        DiffLineKind.Added => new SolidColorBrush(Color.FromRgb(0x14, 0x5A, 0x14)),
        DiffLineKind.Gap => Brushes.Gray,
        _ => Brushes.Black,
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
    public Brush Foreground => Blocks ? Brushes.Firebrick : Brushes.Black;

    public static FindingViewModel For(FieldFinding finding) => new(
        finding.Field,
        finding.Captured ?? "—",
        finding.OnWiki ?? "—",
        finding.Verdict.ToString(),
        finding.Explanation ?? "",
        finding.Blocks);

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
        finding.OnPage is null ? nameof(FieldVerdict.MissingOnWiki) : nameof(FieldVerdict.Differs),
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
        _wikitext = result.Edit?.NewWikitext ?? result.Page?.Wikitext ?? "";
        // Left blank when the tool proposes nothing, rather than inheriting its "No changes" — if the user is
        // hand-editing a page the tool judged correct, that summary would be a false description of the revision,
        // and there is nothing to inherit. CanAct requires one, so it has to be typed.
        _summary = result.Edit is { HasChanges: true } proposed ? proposed.Summary : "";
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
        Replace(Warnings, BuildWarnings(result));
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

    /// <summary>Whether there is anything to show in the icon section — at least one of the two images.</summary>
    public bool HasIcons => Result.CapturedIconImage is not null || Result.WikiIconImage is not null;

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
    public string IconVerdict => Result.Icon switch
    {
        { Matches: true } icon => $"These look like the same artwork (difference {icon.Distance:F3}, " +
                                  $"threshold {IconFingerprint.SameIconThreshold:F2}). Check by eye if you like.",
        { Matches: false } icon => $"These do not look like the same artwork (difference {icon.Distance:F3}, " +
                                   $"threshold {IconFingerprint.SameIconThreshold:F2}). The tool never changes an " +
                                   "icon id — decide which side is wrong.",
        _ => Result.IconNote ?? "The icons were not compared.",
    };

    public Brush IconVerdictBrush => Result.Icon switch
    {
        { Matches: true } => Brushes.SeaGreen,
        { Matches: false } => Brushes.Firebrick,
        _ => Brushes.DimGray,
    };

    /// <summary>The lore read from the game, once a Lore-tab capture has been merged in.</summary>
    public string? CapturedLore => Result.Lore;

    public bool HasCapturedLore => !string.IsNullOrWhiteSpace(Result.Lore);

    /// <summary>True while this item is waiting for its Lore tab to be captured — what the lore section's prompt
    /// hangs off.</summary>
    public bool WantsLoreCapture => Result.NeedsLoreCapture;

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
        }
    }

    public string StatusText => Result switch
    {
        _ when IsDone => "Done",

        // "Already correct" is a false statement about a page the tool declined to finish, and it sat directly above
        // a warning strip saying "Not done" (user, 2026-09-29). The ledger records Flagged in this case; the label
        // has to agree with it.
        { Status: ItemCheckStatus.AlreadyCorrect, NeedsAttention: true } => "Needs attention",
        _ => StatusTextFor(Result.Status),
    };

    private static string StatusTextFor(ItemCheckStatus status) => status switch
    {
        ItemCheckStatus.Occluded => "Occluded",
        ItemCheckStatus.Ineligible => "Not eligible",
        ItemCheckStatus.AlreadyChecked => "Already checked",
        ItemCheckStatus.LoreRecorded => "Lore recorded",
        ItemCheckStatus.NotOnWiki => "Not on the wiki",
        ItemCheckStatus.NotAnItemPage => "Not an item page",
        ItemCheckStatus.AlreadyCorrect => "Already correct",
        ItemCheckStatus.EditProposed => "Edit proposed",
        ItemCheckStatus.Failed => "Failed",
        _ => status.ToString(),
    };

    public Brush StatusBrush => Result switch
    {
        _ when IsDone => Brushes.SeaGreen,
        { Status: ItemCheckStatus.AlreadyCorrect, NeedsAttention: true } => Brushes.Firebrick,
        { Status: ItemCheckStatus.EditProposed } => Brushes.DarkOrange,
        { Status: ItemCheckStatus.Failed or ItemCheckStatus.Occluded } => Brushes.Firebrick,
        _ => Brushes.DimGray,
    };

    /// <summary>
    /// The page this item lives on, as far as anything knows.
    ///
    /// **The ledger row is the last fallback, and it is the one that matters** (user, 2026-09-29): an already-checked
    /// item never reaches the wiki — that is the point of the ledger — so it has no <see cref="ItemCheckResult.Page"/>
    /// and used to be the one result with nothing to click. It is also the one most likely to need it, since "is this
    /// row stale, or has the page really been fixed?" is answered by going and looking.
    /// </summary>
    private string? KnownPageTitle =>
        Result.Page?.Title ??
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
        }
    }

    public string Summary
    {
        get => _summary;
        set
        {
            Set(ref _summary, value);
            OnPropertyChanged(nameof(CanAct));
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
            OnPropertyChanged(nameof(CanSkip));
            OnPropertyChanged(nameof(CanMarkChecked));
            OnPropertyChanged(nameof(CanCommitFormatting));
            OnPropertyChanged(nameof(HasOutcome));
        }
    }

    public bool HasOutcome => !string.IsNullOrEmpty(_outcome);

    /// <summary>Where the frame this item came from was archived, in debug builds. Shown as the list entry's tooltip
    /// so a bug can be reported by pointing at the item — see <see cref="DebugCaptureArchive"/>.</summary>
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

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            Set(ref _isBusy, value);
            OnPropertyChanged(nameof(CanAct));
            OnPropertyChanged(nameof(CanSkip));
            OnPropertyChanged(nameof(CanMarkChecked));
            OnPropertyChanged(nameof(CanCommitFormatting));
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

    private static IEnumerable<string> BuildWarnings(ItemCheckResult result)
    {
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
            yield return $"{blocker.Field}: {blocker.Explanation ?? "needs a human before anything is written."}";

        foreach (IneligibilityDetail blocker in result.Eligibility?.Blockers ?? [])
            yield return blocker.Explanation;

        if (result.Error is { } error) yield return error;

        if (result.Icon is { Matches: false } icon)
            yield return
                $"The icon on the page (lucy_img_ID {icon.IconId}) does not look like the one in the window " +
                $"(difference {icon.Distance:F2} against a threshold of {IconFingerprint.SameIconThreshold:F2}). " +
                "The tool never changes an icon id — check by eye which side is wrong.";

        if (result.IconNote is { } note) yield return note;

        foreach (ComplianceFinding compliance in result.Analysis?.Compliance ?? [])
        {
            // The era banner and the lore placeholder are rows in the differences table instead — see ComplianceRows.
            if (BelongsInTheDifferencesTable(compliance, result)) continue;

            yield return compliance.ToolWillFix
                ? $"Template compliance (fixed by this edit): {compliance.Detail}"
                : $"Template compliance — needs a human: {compliance.Detail}";
        }

        foreach (string deferred in result.Edit?.Deferred ?? []) yield return "Not done: " + deferred;

        if (result.Edit is { NeedsReformatting: true })
            yield return
                "This edit adds a line it did not position, so the page will want the formatting pass afterwards.";
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
