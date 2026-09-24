using EQLWikiAssistant.Core.Items;

namespace EQLWikiAssistant.TestSupport.Accuracy;

public enum FieldVerdict
{
    Correct,

    /// <summary>Ground truth has a value; we produced none.</summary>
    Missing,

    /// <summary>We produced a value that differs from ground truth.</summary>
    Wrong,

    /// <summary>We produced a field or entry that ground truth doesn't have.</summary>
    Extra,
}

/// <summary><see cref="Flagged"/> records whether the item carried any parser warning. That's the axis this
/// project actually cares about: an unflagged wrong value is a wiki-corruption risk, while a flagged one is a
/// manual-review cost. Deliberately item-level rather than per-field — warnings quote OCR fragments and matching
/// them back to a specific field would be guesswork.</summary>
public sealed record FieldResult(string Field, FieldVerdict Verdict, string? Expected, string? Actual, bool Flagged);

/// <summary>
/// Field-level diff of a parsed window against its ground truth.
///
/// **Comparison is exact** (after <c>Trim()</c>), never fuzzy. Fuzzy matching is right at *runtime* — see
/// <c>EditDistance</c>, used for exaltation names and wiki page-title lookup, where OCR noise must be tolerated —
/// and wrong for *measurement*: an edit-distance-tolerant comparer would score "Tarnished" -> "Tamished" as a
/// pass and make an entire error class invisible, which is the opposite of what a harness is for.
/// </summary>
public static class ItemComparer
{
    /// <summary>Compares one window. <paramref name="actual"/> is null when the window was reported occluded and
    /// therefore not parsed.</summary>
    public static IReadOnlyList<FieldResult> CompareWindow(ExpectedWindow expected, ParsedItem? actual)
    {
        var results = new List<FieldResult>();

        if (expected.Occluded || actual is null)
        {
            // Occlusion is a boolean about the window, not a field of the item. Agreeing that a window is
            // unreadable is a pass; disagreeing either way is a single, loud failure rather than a cascade of
            // field errors against a window nobody parsed.
            bool actuallyOccluded = actual is null;
            results.Add(new FieldResult(
                "occluded",
                expected.Occluded == actuallyOccluded ? FieldVerdict.Correct
                    : expected.Occluded ? FieldVerdict.Missing : FieldVerdict.Extra,
                expected.Occluded.ToString(), actuallyOccluded.ToString(), Flagged: true));
            return results;
        }

        bool flagged = actual.Warnings.Count > 0;

        results.Add(Scalar("name", expected.Name, actual.Name, flagged));
        results.Add(Scalar("level", expected.Level.ToString(), actual.Level.ToString(), flagged));
        results.Add(Scalar("merchantValue", expected.MerchantValue, actual.MerchantValue, flagged));
        results.Add(Scalar("lore", expected.Lore, actual.Lore, flagged));
        results.Add(Scalar("titleContentNameMismatch",
            expected.TitleContentNameMismatch.ToString(), actual.TitleContentNameMismatch.ToString(), flagged));

        results.AddRange(CompareList("flags", expected.Flags, actual.Flags, flagged));
        results.AddRange(CompareList("classes", expected.Classes, actual.Classes, flagged));
        results.AddRange(CompareList("races", expected.Races, actual.Races, flagged));
        results.AddRange(CompareList("slots", expected.Slots, actual.Slots, flagged));

        results.AddRange(CompareList(
            "stats",
            expected.Stats.Select(s => $"{s.Label}={s.Value}").ToList(),
            actual.Stats.Select(s => $"{s.Key}={s.Value}").ToList(),
            flagged));

        results.AddRange(CompareList(
            "exaltations",
            expected.Exaltations.Select(e => $"{e.Kind}={e.Name ?? "empty"}").ToList(),
            actual.ExaltationSlots.Select(e => $"{e.Kind}={e.Name ?? "empty"}").ToList(),
            flagged));

        results.AddRange(CompareList(
            "effects",
            expected.Effects.Select(DescribeEffect).ToList(),
            actual.Effects.Select(DescribeEffect).ToList(),
            flagged));

        return results;
    }

    private static string DescribeEffect(ExpectedEffect e) =>
        Describe(e.Kind, e.Name, e.Conditions, e.Modifiers.Select(m => $"{m.Label}={m.Value}"));

    private static string DescribeEffect(EffectEntry e) =>
        Describe(e.Kind, e.Name, e.Conditions, e.Modifiers.Select(m => $"{m.Key}={m.Value}"));

    private static string Describe(string kind, string name, IEnumerable<string> conditions, IEnumerable<string> modifiers)
    {
        string text = $"{kind}={name}";
        string conditionText = string.Join("; ", conditions);
        if (conditionText.Length > 0) text += $" ({conditionText})";
        string modifierText = string.Join("; ", modifiers);
        if (modifierText.Length > 0) text += $" [{modifierText}]";
        return text;
    }

    private static FieldResult Scalar(string field, string? expected, string? actual, bool flagged)
    {
        string? e = Normalize(expected);
        string? a = Normalize(actual);

        FieldVerdict verdict = (e, a) switch
        {
            (null, null) => FieldVerdict.Correct,
            (not null, null) => FieldVerdict.Missing,
            (null, not null) => FieldVerdict.Extra,
            _ when e == a => FieldVerdict.Correct,
            _ => FieldVerdict.Wrong,
        };
        return new FieldResult(field, verdict, e, a, flagged);
    }

    /// <summary>Positional comparison — order is part of the contract, because the stat block's reading order is
    /// how the renderer will lay the wikitext back out.</summary>
    private static IEnumerable<FieldResult> CompareList(string field, IReadOnlyList<string> expected, IReadOnlyList<string> actual, bool flagged)
    {
        for (int i = 0; i < Math.Max(expected.Count, actual.Count); i++)
        {
            string? e = i < expected.Count ? Normalize(expected[i]) : null;
            string? a = i < actual.Count ? Normalize(actual[i]) : null;
            yield return Scalar($"{field}[{i}]", e, a, flagged);
        }
    }

    private static string? Normalize(string? value)
    {
        if (value is null) return null;
        string trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }
}
