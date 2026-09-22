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
}
