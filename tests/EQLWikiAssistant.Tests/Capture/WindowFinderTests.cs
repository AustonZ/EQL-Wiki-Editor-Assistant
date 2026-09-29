using EQLWikiAssistant.Capture;

namespace EQLWikiAssistant.Tests.Capture;

public class WindowFinderTests
{
    [Fact]
    public void EnumerateVisibleWindows_ReturnsWellFormedResults()
    {
        var windows = WindowFinder.EnumerateVisibleWindows();

        // Every entry must have a real handle and non-blank title (enforced by WindowFinder itself); check it
        // holds rather than just trusting the implementation.
        Assert.All(windows, w =>
        {
            Assert.NotEqual(IntPtr.Zero, w.Handle);
            Assert.False(string.IsNullOrWhiteSpace(w.Title));
        });

        // Handles should be unique (each is a distinct top-level window).
        Assert.Equal(windows.Select(w => w.Handle).Distinct().Count(), windows.Count);
    }

    [Fact]
    public void FindByTitleSubstring_IsCaseInsensitiveAndFiltersCorrectly()
    {
        var all = WindowFinder.EnumerateVisibleWindows();
        if (all.Count == 0)
        {
            return; // No visible windows (e.g. a non-interactive session) — nothing to assert against.
        }

        // Pick a real window's title and search for a fragment of it in a different case.
        string sampleTitle = all[0].Title;
        string fragment = sampleTitle.Length >= 3 ? sampleTitle[..3] : sampleTitle;

        var matches = WindowFinder.FindByTitleSubstring(fragment.ToUpperInvariant());
        Assert.Contains(matches, w => w.Handle == all[0].Handle);

        var noMatches = WindowFinder.FindByTitleSubstring(Guid.NewGuid().ToString("N"));
        Assert.Empty(noMatches);
    }

    /// <summary>
    /// Matching by process is what identifies the game, because its title does not: "EverQuest" also matches a
    /// browser on the wiki and a Discord server of that name (user, 2026-09-29). Since enumeration is in Z-order,
    /// taking the first match captured whichever of those was touched last.
    /// </summary>
    [Fact]
    public void FindByProcessName_ReturnsEveryWindowOfThatProcessAndNothingElse()
    {
        var all = WindowFinder.EnumerateVisibleWindows();
        string? sample = all.Select(w => w.ProcessName).FirstOrDefault(n => !string.IsNullOrEmpty(n));
        if (sample is null) return; // No window whose process could be read — nothing to assert against.

        var matches = WindowFinder.FindByProcessName(sample.ToUpperInvariant());

        Assert.All(matches, w => Assert.Equal(sample, w.ProcessName, ignoreCase: true));
        Assert.Equal(
            all.Count(w => string.Equals(w.ProcessName, sample, StringComparison.OrdinalIgnoreCase)),
            matches.Count);
    }

    [Fact]
    public void FindByProcessName_FindsNothingForAProcessThatDoesNotExist() =>
        Assert.Empty(WindowFinder.FindByProcessName(Guid.NewGuid().ToString("N")));
}
