namespace EQLWikiEditorAssistant.Wiki.Wikitext;

/// <summary>
/// One parameter of a template call, carrying the <em>exact source spans</em> it occupies rather than just its
/// text. That is the whole point of this type: the repo's hard constraint is that an edit must be a minimal,
/// surgical patch with every untouched part of the page surviving byte-for-byte, and the only way to guarantee
/// that is to splice a new value into the original string at a known offset instead of re-rendering the template.
///
/// <see cref="Name"/> is normalized (trimmed, since MediaWiki ignores whitespace around a named parameter's name);
/// <see cref="RawValue"/> is the source text verbatim, <em>including</em> its surrounding whitespace, because that
/// whitespace is significant to a diff even when it is not significant to MediaWiki. Real pages are wildly
/// inconsistent here — <c>|notes       = </c> aligned with spaces on one page, <c>|notes=</c> on another, a
/// trailing blank line before the next parameter on a third — and reformatting any of it would turn a one-value
/// correction into a whole-page diff a human reviewer cannot skim.
///
/// <see cref="Index"/> is the 1-based position for a positional (unnamed) parameter, or null for a named one.
/// Item pages use only named parameters today, but <c>{{ItemWhereRow | zone | npc | note | loc }}</c> in a
/// <c>soldby</c> value is positional, and this scanner has to walk those to find the enclosing template's real
/// extent.
/// </summary>
public sealed record TemplateParameter(
    string? Name,
    int? Index,
    string RawValue,
    int ValueStart,
    int ValueLength,
    int SegmentStart = -1,
    int SegmentEnd = -1)
{
    /// <summary>Offset of this parameter's own <c>|</c>, and the offset just past its last character — the whole
    /// <c>|name = value</c> run. Needed to *remove* a parameter, which the value span alone cannot express:
    /// splicing out only the value leaves a stray <c>|name =</c> behind.</summary>
    public int SegmentStart { get; init; } = SegmentStart;

    public int SegmentEnd { get; init; } = SegmentEnd;

    /// <summary>The value with surrounding whitespace removed — what a comparison should use. MediaWiki itself
    /// trims named parameter values, so this is the value the wiki actually sees.</summary>
    public string Value => RawValue.Trim();

    /// <summary>End offset (exclusive) of the value span in the source wikitext.</summary>
    public int ValueEnd => ValueStart + ValueLength;
}

/// <summary>
/// A <c>{{Template|...}}</c> call located in a page, with its exact source extent. Produced by
/// <see cref="WikitextScanner"/>; see that type for what "tolerant" means here and why it matters.
/// </summary>
public sealed record TemplateCall(
    string Name,
    int Start,
    int Length,
    IReadOnlyList<TemplateParameter> Parameters)
{
    /// <summary>End offset (exclusive) of the whole <c>{{...}}</c> call.</summary>
    public int End => Start + Length;

    /// <summary>
    /// The named parameter with this name, or null.
    ///
    /// The match is ordinal: MediaWiki's first-letter case-insensitivity applies to <em>page</em> titles, while
    /// parameter names are fully case-sensitive, so a page writing <c>|Statsblock=</c> genuinely has a different
    /// parameter and treating it as <c>statsblock</c> would edit the wrong thing.
    ///
    /// **The <em>last</em> match wins, because that is the one MediaWiki renders.** Duplicated parameters are real
    /// — two of 414 sampled item pages have two <c>|notes=</c>, one empty near the top and the real one further
    /// down — and returning the first would have made the tool read an empty value off a page that visibly has
    /// content, then "correct" it. <see cref="ItemPageDocument"/> warns when this happens, since a duplicate is
    /// still a page defect worth a human's attention.
    /// </summary>
    public TemplateParameter? Find(string name) =>
        Parameters.LastOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));
}
