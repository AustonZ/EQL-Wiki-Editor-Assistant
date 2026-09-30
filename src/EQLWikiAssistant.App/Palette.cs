using System.Windows;
using System.Windows.Media;

namespace EQLWikiAssistant.App;

/// <summary>
/// The view models' side of the palette, **read out of Theme.xaml rather than restated here**.
///
/// The view models decide colour in C# (a settled item is green, a finding nobody has judged is red) because a
/// value converter per case would be more code for the same decision. That leaves two places a colour could be
/// written down, and a second copy of a palette is the kind that drifts — one half of the app would go on using
/// `SeaGreen` after the other stopped. So every brush here resolves by name against the application's merged
/// dictionaries, and Theme.xaml stays the only file with a hex value in it.
///
/// A missing key throws rather than falling back to a default, which is the right failure: a silently grey status
/// column is exactly the kind of wrong this tool tries not to be, and the key is a compile-time constant in
/// practice — one typo, caught the first time the window opens.
/// </summary>
internal static class Palette
{
    public static Brush Text => Named("TextBrush");
    public static Brush Muted => Named("TextMutedBrush");
    public static Brush Dim => Named("TextDimBrush");

    /// <summary>Matched or edited: the item is done and will not come back.</summary>
    public static Brush Done => Named("DoneBrush");

    /// <summary>Something wants a human. The one colour the user must not learn to skim past.</summary>
    public static Brush Attention => Named("AttentionBrush");

    /// <summary>An edit is proposed, or the page does not exist yet — real work, but nothing is wrong.</summary>
    public static Brush Warning => Named("WarningBrush");

    /// <summary>No verdict either way: skipped, or a comparison the tool declined to make.</summary>
    public static Brush Neutral => Named("NeutralBrush");

    /// <summary>A ledger row the next capture will check again.</summary>
    public static Brush Recheck => Named("RecheckBrush");

    public static Brush DiffRemovedBack => Named("DiffRemovedBackBrush");
    public static Brush DiffRemovedText => Named("DiffRemovedTextBrush");
    public static Brush DiffAddedBack => Named("DiffAddedBackBrush");
    public static Brush DiffAddedText => Named("DiffAddedTextBrush");

    private static Brush Named(string key) =>
        Application.Current?.TryFindResource(key) as Brush
        ?? throw new InvalidOperationException($"Theme.xaml has no brush named '{key}'.");
}
