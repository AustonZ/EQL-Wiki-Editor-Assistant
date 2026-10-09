using EQLWikiEditorAssistant.Core.Ocr;

namespace EQLWikiEditorAssistant.Tests.Core.Ocr;

public class RectTests
{
    [Fact]
    public void RightAndBottom_AreComputedFromOriginAndSize()
    {
        var rect = new Rect(10, 20, 30, 40);
        Assert.Equal(40, rect.Right);
        Assert.Equal(60, rect.Bottom);
    }

    [Theory]
    [InlineData(0, 0, 10, 10, 5, 5, 10, 10, true)]   // overlapping
    [InlineData(0, 0, 10, 10, 10, 10, 5, 5, false)]  // touching at a corner only -> no overlap
    [InlineData(0, 0, 10, 10, 20, 20, 5, 5, false)]  // disjoint
    [InlineData(0, 0, 10, 10, 2, 2, 3, 3, true)]     // fully contained
    public void IntersectsWith_MatchesExpected(
        int ax, int ay, int aw, int ah, int bx, int by, int bw, int bh, bool expected)
    {
        var a = new Rect(ax, ay, aw, ah);
        var b = new Rect(bx, by, bw, bh);

        Assert.Equal(expected, a.IntersectsWith(b));
        Assert.Equal(expected, b.IntersectsWith(a)); // symmetric
    }
}
