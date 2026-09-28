using EQLWikiAssistant.Pipeline;

namespace EQLWikiAssistant.Tests.Pipeline;

/// <summary>
/// The review screen's diff. Worth its own tests because the whole tool rests on the user being able to trust what
/// they are looking at before it reaches a public wiki — and because the two properties here are exactly the ones
/// the spike tool's set-subtraction diff got wrong.
/// </summary>
public class WikitextDiffTests
{
    private static string Lines(params string[] lines) => string.Join("\n", lines);

    [Fact]
    public void AChangedLineShowsAsOneRemovalAndOneAddition()
    {
        IReadOnlyList<DiffLine> diff = WikitextDiff.Compute(
            Lines("AC: 5<br>"), Lines("AC: 6<br>"));

        Assert.Equal(
            [(DiffLineKind.Removed, "AC: 5<br>"), (DiffLineKind.Added, "AC: 6<br>")],
            diff.Select(d => (d.Kind, d.Text)));
    }

    /// <summary>The spike diff subtracted line *sets*, so a page with two identical lines lost one of them. Real
    /// statsblocks repeat lines (a bare <c>&lt;br&gt;</c>, a blank line mid-block), and a diff that silently drops one
    /// is a diff that hides an edit.</summary>
    [Fact]
    public void DuplicateUnchangedLinesAreBothKept()
    {
        IReadOnlyList<DiffLine> diff = WikitextDiff.Compute(
            Lines("<br>", "<br>", "AC: 5<br>"),
            Lines("<br>", "<br>", "AC: 6<br>"));

        Assert.Equal(2, diff.Count(d => d is { Kind: DiffLineKind.Unchanged, Text: "<br>" }));
    }

    /// <summary>A line that merely moved must not read as a rewrite of whatever now sits in its place.</summary>
    [Fact]
    public void AnInsertionDoesNotMakeEveryFollowingLineLookChanged()
    {
        IReadOnlyList<DiffLine> diff = WikitextDiff.Compute(
            Lines("a", "b", "c"),
            Lines("a", "new", "b", "c"));

        Assert.Single(diff, d => d.Kind == DiffLineKind.Added);
        Assert.DoesNotContain(diff, d => d.Kind == DiffLineKind.Removed);
    }

    [Fact]
    public void LineNumbersAreCarriedForBothSides()
    {
        IReadOnlyList<DiffLine> diff = WikitextDiff.Compute(Lines("a", "b"), Lines("a", "x", "b"));

        DiffLine added = diff.Single(d => d.Kind == DiffLineKind.Added);
        Assert.Equal(2, added.AfterLine);
        Assert.Null(added.BeforeLine);
        Assert.Equal(2, diff.Single(d => d.Text == "b").BeforeLine);
        Assert.Equal(3, diff.Single(d => d.Text == "b").AfterLine);
    }

    /// <summary>An item page can carry a long <c>dropsfrom</c> table with nothing to do with the edit, and a reviewer
    /// scrolling past it is a reviewer who stops reading.</summary>
    [Fact]
    public void LongUnchangedRunsAreFoldedAway()
    {
        string before = Lines([.. Enumerable.Range(0, 40).Select(i => $"line {i}")]);
        string after = before.Replace("line 20", "line twenty");

        IReadOnlyList<DiffLine> diff = WikitextDiff.Compute(before, after, context: 2);

        Assert.Equal(2, diff.Count(d => d.Kind == DiffLineKind.Gap));
        Assert.Contains(diff, d => d.Kind == DiffLineKind.Gap && d.Text.Contains("17 unchanged lines"));
        Assert.Equal(4, diff.Count(d => d.Kind == DiffLineKind.Unchanged));
    }

    /// <summary>"Nothing changed" is a real and common answer, and the user still wants to read the page — so it is
    /// shown whole rather than as one enormous gap.</summary>
    [Fact]
    public void AnUnchangedPageIsShownWholeRatherThanFoldedToNothing()
    {
        string page = Lines([.. Enumerable.Range(0, 40).Select(i => $"line {i}")]);

        IReadOnlyList<DiffLine> diff = WikitextDiff.Compute(page, page);

        Assert.Equal(40, diff.Count);
        Assert.All(diff, d => Assert.Equal(DiffLineKind.Unchanged, d.Kind));
    }

    /// <summary>Line endings are normalized for *display* only. A page that happens to use CRLF must not read as
    /// wholly rewritten — while the edit itself still preserves the original bytes.</summary>
    [Fact]
    public void LineEndingsDoNotMakeAPageLookRewritten()
    {
        IReadOnlyList<DiffLine> diff = WikitextDiff.Compute("a\r\nb\r\nc", "a\nb\nc");

        Assert.All(diff, d => Assert.Equal(DiffLineKind.Unchanged, d.Kind));
    }
}
