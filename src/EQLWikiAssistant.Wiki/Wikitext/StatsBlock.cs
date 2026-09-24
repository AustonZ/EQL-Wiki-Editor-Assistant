using System.Text;

namespace EQLWikiAssistant.Wiki.Wikitext;

/// <summary>One <c>Label: Value</c> pair read off a statsblock line. A single line routinely carries two of them
/// (<c>WT: 0.1  Size: TINY</c>), which is why a line owns a list rather than one pair.</summary>
public sealed record StatsField(string Label, string Value);

/// <summary>What <see cref="StatsBlockLine"/> made of a line. <see cref="Unparsed"/> is a first-class outcome, not
/// an error: per the repo's untrusted-wiki-data constraint, a line this grammar does not recognize must be
/// reported and preserved verbatim, never dropped and never guessed at.</summary>
public enum StatsLineKind { Blank, Flags, Fields, Unparsed }

/// <summary>
/// One line of a <c>statsblock</c>, holding the <em>exact</em> source text it came from alongside whatever could
/// be made of it.
///
/// **<see cref="Raw"/> is the source of truth; the parse is only ever used for comparison.** That inversion is
/// what makes a byte-for-byte round trip trivially true rather than something to be tested into existence, and it
/// is the only honest way to satisfy "untouched parts of a page must survive byte-for-byte" against real pages.
/// Measured on 414 real item pages: alignment padding, single vs double spaces between stats, a stray space before
/// a <c>&lt;br&gt;</c> (<c>AC: 15 &lt;br&gt;</c>) and blank lines mid-block are all common and all meaningless to
/// MediaWiki — but re-rendering them would turn a one-value correction into a whole-page diff no reviewer can
/// skim, which defeats the review step the whole tool is built around.
/// </summary>
public sealed record StatsBlockLine(string Raw)
{
    /// <summary>The line's content: <see cref="Raw"/> with its line-break tag and surrounding whitespace removed.
    /// This is what gets parsed and compared.</summary>
    public string Text { get; } = StripBreak(Raw).Trim();

    /// <summary>The line-break tag this line ended with, verbatim (<c>&lt;br&gt;</c> on 2513 of 2514 real
    /// statsblock lines, <c>&lt;br/&gt;</c> on one), or null if it had none.</summary>
    public string? Break { get; } = FindBreak(Raw);

    /// <summary>Label/value pairs found on the line, in source order. Empty for a flags-only or unparsed line.</summary>
    public IReadOnlyList<StatsField> Fields { get; private init; } = [];

    /// <summary>Unlabelled tokens: the flags row (<c>MAGIC ITEM  LORE ITEM  NO DROP</c>), or the leading fragment
    /// of a mixed line (<c>EXPENDABLE  Charges: 10</c> yields the flag <c>EXPENDABLE</c> and the field
    /// <c>Charges</c>).</summary>
    public IReadOnlyList<string> Flags { get; private init; } = [];

    public StatsLineKind Kind { get; private init; } = StatsLineKind.Blank;

    internal static StatsBlockLine Create(string raw)
    {
        var line = new StatsBlockLine(raw);
        string text = line.Text;
        if (text.Length == 0) return line;

        IReadOnlyList<StatsField> fields = StatsBlockGrammar.ReadFields(text, out string leading);
        IReadOnlyList<string> flags = StatsBlockGrammar.ReadFlags(leading);

        // A line that yielded neither a field nor a flag still had *some* text on it, so something in it is
        // unrecognized. Say so rather than silently reporting an empty line.
        StatsLineKind kind = fields.Count > 0 ? StatsLineKind.Fields
            : flags.Count > 0 ? StatsLineKind.Flags
            : StatsLineKind.Unparsed;

        return line with { Fields = fields, Flags = flags, Kind = kind };
    }

    private static string StripBreak(string raw)
    {
        string? tag = FindBreak(raw);
        return tag is null ? raw : raw.Replace(tag, string.Empty, StringComparison.Ordinal);
    }

    private static string? FindBreak(string raw)
    {
        int i = raw.IndexOf("<br", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        int close = raw.IndexOf('>', i);
        return close < 0 ? null : raw[i..(close + 1)];
    }
}

/// <summary>
/// The <c>statsblock</c> parameter, split into lines that concatenate back to the original string exactly.
///
/// The plan describes diffing as "parse this block line-by-line into fields and re-render it". Building it against
/// real pages showed the re-render half is the wrong move: 414 sampled pages agree on the line <em>grammar</em>
/// and disagree on essentially every whitespace decision within it, so a wholesale re-render rewrites lines whose
/// data never changed. This type therefore renders by concatenating raw lines and offers targeted edits
/// (<see cref="ReplaceLine"/>) for the lines that genuinely differ — same data model, minimal diff.
/// </summary>
public sealed class StatsBlock
{
    private readonly List<StatsBlockLine> _lines;

    private StatsBlock(List<StatsBlockLine> lines) => _lines = lines;

    public IReadOnlyList<StatsBlockLine> Lines => _lines;

    /// <summary>Splits a raw <c>statsblock</c> value into lines. Never throws: any input at all is a valid
    /// statsblock as far as this type is concerned, because preserving it is always possible even when
    /// understanding it is not.</summary>
    public static StatsBlock Parse(string rawValue)
    {
        ArgumentNullException.ThrowIfNull(rawValue);

        var lines = new List<StatsBlockLine>();
        int start = 0;
        for (int i = 0; i < rawValue.Length; i++)
        {
            // A line ends at its <br> (consuming a newline that immediately follows it, since that newline is
            // part of the same visual line break), or at a bare newline for the last line of a block.
            if (StartsWithBreak(rawValue, i))
            {
                int close = rawValue.IndexOf('>', i);
                if (close < 0) continue;
                int end = close + 1;
                if (end < rawValue.Length && rawValue[end] == '\r') end++;
                if (end < rawValue.Length && rawValue[end] == '\n') end++;
                lines.Add(StatsBlockLine.Create(rawValue[start..end]));
                start = end;
                i = end - 1;
            }
            else if (rawValue[i] == '\n')
            {
                lines.Add(StatsBlockLine.Create(rawValue[start..(i + 1)]));
                start = i + 1;
            }
        }

        if (start < rawValue.Length) lines.Add(StatsBlockLine.Create(rawValue[start..]));
        return new StatsBlock(lines);
    }

    /// <summary>Whether a <c>&lt;br...&gt;</c> tag starts here. Case-insensitive because the tag is HTML; 2513 of
    /// 2514 real statsblock lines write <c>&lt;br&gt;</c> and one writes <c>&lt;br/&gt;</c>, but nothing stops an
    /// editor typing <c>&lt;BR&gt;</c>.</summary>
    private static bool StartsWithBreak(string text, int i) =>
        text.AsSpan(i).StartsWith("<br", StringComparison.OrdinalIgnoreCase);

    /// <summary>The original raw value, byte for byte, unless a line has been replaced.</summary>
    public string Render()
    {
        var sb = new StringBuilder();
        foreach (StatsBlockLine line in _lines) sb.Append(line.Raw);
        return sb.ToString();
    }

    /// <summary>Swaps one line's raw text, leaving every other line untouched — the surgical-patch primitive the
    /// diff step will build on.</summary>
    public StatsBlock ReplaceLine(int index, string newRaw)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _lines.Count);
        ArgumentNullException.ThrowIfNull(newRaw);

        var copy = new List<StatsBlockLine>(_lines) { [index] = StatsBlockLine.Create(newRaw) };
        return new StatsBlock(copy);
    }

    /// <summary>Every label/value pair in the block, in reading order. Convenience for the diff step; the block's
    /// line structure is still what an edit has to work through.</summary>
    public IEnumerable<StatsField> AllFields() => _lines.SelectMany(l => l.Fields);

    /// <summary>The first field with this label (ordinal, case-insensitive — real pages write both <c>Size</c> and
    /// <c>SIZE</c>), or null.</summary>
    public StatsField? Find(string label) =>
        AllFields().FirstOrDefault(f => string.Equals(f.Label, label, StringComparison.OrdinalIgnoreCase));
}
