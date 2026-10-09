namespace EQLWikiEditorAssistant.Pipeline;

public enum DiffLineKind
{
    Unchanged,
    Removed,
    Added,

    /// <summary>A run of unchanged lines that was folded away. <see cref="DiffLine.Text"/> says how many.</summary>
    Gap,
}

/// <summary>One line of a rendered diff. The line numbers are for display, and are null on the side the line is not
/// on.</summary>
public sealed record DiffLine(DiffLineKind Kind, string Text, int? BeforeLine, int? AfterLine);

/// <summary>
/// A line diff of two versions of a page, for the review screen.
///
/// **The review screen is the whole point of this tool, so the diff has to be honest about position.** The spike
/// tool's version subtracted the two line *sets*, which is fine for eyeballing whether an edit is surgical but says
/// nothing about where a line moved to, pairs a changed line with an unrelated one, and collapses duplicates — a
/// page with two identical <c>&lt;br&gt;</c> lines would lose one. A real longest-common-subsequence diff costs a
/// few milliseconds on a page this size and removes that whole class of confusion.
///
/// Unchanged runs longer than twice the context are folded to a <see cref="DiffLineKind.Gap"/>, because an item page
/// can carry a long <c>dropsfrom</c> table that has nothing to do with the edit, and a reviewer scrolling past it is
/// a reviewer who stops reading.
/// </summary>
public static class WikitextDiff
{
    /// <summary>How many unchanged lines to keep either side of a change.</summary>
    public const int DefaultContext = 3;

    public static IReadOnlyList<DiffLine> Compute(string before, string after, int context = DefaultContext)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentOutOfRangeException.ThrowIfNegative(context);

        string[] a = SplitLines(before);
        string[] b = SplitLines(after);

        return Fold(Align(a, b), context);
    }

    /// <summary>Normalizes line endings so a page that happens to use CRLF does not read as wholly rewritten. The
    /// *edit* preserves the original bytes; this only affects how it is displayed.</summary>
    private static string[] SplitLines(string text) => text.Replace("\r\n", "\n").Split('\n');

    /// <summary>
    /// Standard LCS backtrack. Deliberately the plain O(n*m) table rather than anything cleverer: an item page is
    /// tens of lines, the longest on the wiki is a few hundred, and a subtle diff algorithm is a bad place to save
    /// microseconds when its output is what a human trusts before writing to a public wiki.
    /// </summary>
    private static List<DiffLine> Align(string[] a, string[] b)
    {
        var lengths = new int[a.Length + 1, b.Length + 1];
        for (int i = a.Length - 1; i >= 0; i--)
            for (int j = b.Length - 1; j >= 0; j--)
                lengths[i, j] = string.Equals(a[i], b[j], StringComparison.Ordinal)
                    ? lengths[i + 1, j + 1] + 1
                    : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);

        var lines = new List<DiffLine>();
        int x = 0, y = 0;
        while (x < a.Length && y < b.Length)
        {
            if (string.Equals(a[x], b[y], StringComparison.Ordinal))
            {
                lines.Add(new DiffLine(DiffLineKind.Unchanged, a[x], x + 1, y + 1));
                x++;
                y++;
            }
            else if (lengths[x + 1, y] >= lengths[x, y + 1])
            {
                lines.Add(new DiffLine(DiffLineKind.Removed, a[x], x + 1, null));
                x++;
            }
            else
            {
                lines.Add(new DiffLine(DiffLineKind.Added, b[y], null, y + 1));
                y++;
            }
        }

        for (; x < a.Length; x++) lines.Add(new DiffLine(DiffLineKind.Removed, a[x], x + 1, null));
        for (; y < b.Length; y++) lines.Add(new DiffLine(DiffLineKind.Added, b[y], null, y + 1));
        return lines;
    }

    private static List<DiffLine> Fold(List<DiffLine> lines, int context)
    {
        bool[] keep = new bool[lines.Count];
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].Kind == DiffLineKind.Unchanged) continue;
            for (int j = Math.Max(0, i - context); j <= Math.Min(lines.Count - 1, i + context); j++)
                keep[j] = true;
        }

        // Nothing changed at all: show the page as it stands rather than one enormous gap, since "no edit" is a real
        // and common answer the user still wants to see the page for.
        if (Array.TrueForAll(keep, k => !k)) return lines;

        var folded = new List<DiffLine>();
        int run = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            if (keep[i])
            {
                if (run > 0)
                {
                    folded.Add(new DiffLine(
                        DiffLineKind.Gap, $"... {run} unchanged line{(run == 1 ? "" : "s")} ...", null, null));
                    run = 0;
                }
                folded.Add(lines[i]);
            }
            else
            {
                run++;
            }
        }

        if (run > 0)
            folded.Add(new DiffLine(
                DiffLineKind.Gap, $"... {run} unchanged line{(run == 1 ? "" : "s")} ...", null, null));

        return folded;
    }
}
