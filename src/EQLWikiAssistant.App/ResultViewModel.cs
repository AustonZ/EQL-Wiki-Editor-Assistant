using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using EQLWikiAssistant.Core.Icons;
using EQLWikiAssistant.Core.Items;
using EQLWikiAssistant.Pipeline;
using EQLWikiAssistant.Wiki.Analysis;

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

/// <summary>A field the analyzer had something to say about, flattened for the grid.</summary>
public sealed record FindingViewModel(FieldFinding Finding)
{
    public string Field => Finding.Field;
    public string Verdict => Finding.Verdict.ToString();
    public string Captured => Finding.Captured ?? "—";
    public string OnWiki => Finding.OnWiki ?? "—";
    public string Explanation => Finding.Explanation ?? "";

    /// <summary>Red for anything needing a human, because that is the one category the user must not skim past: the
    /// tool has declined to decide, so nobody has.</summary>
    public Brush Foreground => Finding.Blocks ? Brushes.Firebrick : Brushes.Black;
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

    public ResultViewModel(ItemCheckResult result)
    {
        Result = result;
        _wikitext = result.Edit?.NewWikitext ?? result.Page?.Wikitext ?? "";
        _summary = result.Edit?.Summary ?? "";

        Diff = new ObservableCollection<DiffLineViewModel>(
            result.Edit is null
                ? []
                : WikitextDiff.Compute(result.Edit.OriginalWikitext, result.Edit.NewWikitext)
                    .Select(l => new DiffLineViewModel(l)));

        Findings = new ObservableCollection<FindingViewModel>(
            result.Analysis?.Findings
                .Where(f => f.IsChange || f.Blocks)
                .Select(f => new FindingViewModel(f)) ?? []);

        Warnings = new ObservableCollection<string>(BuildWarnings(result));
    }

    public ItemCheckResult Result { get; }

    public ObservableCollection<DiffLineViewModel> Diff { get; }
    public ObservableCollection<FindingViewModel> Findings { get; }
    public ObservableCollection<string> Warnings { get; }

    public string ItemName => string.IsNullOrEmpty(Result.ItemName) ? "(unreadable window)" : Result.ItemName;

    public string StatusText => Result.Status switch
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
        _ => Result.Status.ToString(),
    };

    public Brush StatusBrush => Result.Status switch
    {
        ItemCheckStatus.AlreadyCorrect or ItemCheckStatus.AlreadyChecked => Brushes.SeaGreen,
        ItemCheckStatus.EditProposed => Brushes.DarkOrange,
        ItemCheckStatus.Failed or ItemCheckStatus.Occluded => Brushes.Firebrick,
        _ => Brushes.DimGray,
    };

    public string PageTitle => Result.Page?.Title ?? Result.Lookup?.RequestedTitle ?? "";

    public bool HasPage => Result.Page is not null;

    public string? PageUrl => Result.Page is null
        ? null
        : $"https://eqlwiki.com/{Uri.EscapeDataString(Result.Page.Title.Replace(' ', '_'))}";

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
    /// Whether the user can settle this item by hand. Offered whenever something wants a human, because that is the
    /// only thing standing between the item and "done" — the tool has already concluded everything it is willing to.
    /// Not offered once an edit is pending, since saving it is the action that matters then.
    /// </summary>
    public bool CanMarkChecked =>
        !HasOutcome && !IsBusy && Result.NeedsAttention &&
        Result.Status is ItemCheckStatus.AlreadyCorrect or ItemCheckStatus.EditProposed;

    /// <summary>What the user is about to save. Editable, and what <c>Commit</c> writes.</summary>
    public string Wikitext
    {
        get => _wikitext;
        set => Set(ref _wikitext, value);
    }

    public string Summary
    {
        get => _summary;
        set => Set(ref _summary, value);
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
            OnPropertyChanged(nameof(HasOutcome));
        }
    }

    public bool HasOutcome => !string.IsNullOrEmpty(_outcome);

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            Set(ref _isBusy, value);
            OnPropertyChanged(nameof(CanAct));
            OnPropertyChanged(nameof(CanSkip));
            OnPropertyChanged(nameof(CanMarkChecked));
        }
    }

    public bool CanAct => Result.CanCommit && !HasOutcome && !IsBusy;

    /// <summary>Skipping is available for anything that reached the wiki but was not settled — including a page the
    /// tool would not touch — so the user can record "I looked, not now".</summary>
    public bool CanSkip =>
        !HasOutcome && !IsBusy &&
        Result.Status is ItemCheckStatus.EditProposed or ItemCheckStatus.NotOnWiki or ItemCheckStatus.NotAnItemPage;

    private static IEnumerable<string> BuildWarnings(ItemCheckResult result)
    {
        foreach (string warning in result.Warnings) yield return warning;

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
            yield return compliance.ToolWillFix
                ? $"Template compliance (fixed by this edit): {compliance.Detail}"
                : $"Template compliance — needs a human: {compliance.Detail}";

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
