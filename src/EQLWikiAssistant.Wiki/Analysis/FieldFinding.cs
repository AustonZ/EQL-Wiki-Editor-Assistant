namespace EQLWikiAssistant.Wiki.Analysis;

/// <summary>
/// What the comparison concluded about one field.
///
/// The ordering matters: these are deliberately not collapsed into "same / different", because the *reason* a field
/// differs decides what the tool may do about it. Writing a value the capture could not see is the failure mode this
/// whole project is built to avoid.
/// </summary>
public enum FieldVerdict
{
    /// <summary>The wiki already says what the capture says. Nothing to do.</summary>
    Matches,

    /// <summary>The wiki says something different and the capture is authoritative. Safe to write.</summary>
    Differs,

    /// <summary>The capture has a value the wiki has no entry for. Safe to add.</summary>
    MissingOnWiki,

    /// <summary>The wiki has a value the capture could not see, so the tool cannot judge it. **Preserve the wiki's
    /// value.** The canonical case is merchant value on a `No Trade` item: the window shows no price because the
    /// item is untradeable, not because it is worthless.</summary>
    Unverifiable,

    /// <summary>Something a human has to decide. The tool proposes nothing.</summary>
    NeedsReview,
}

/// <summary>
/// One field's outcome. <see cref="Captured"/> and <see cref="OnWiki"/> are the rendered forms being compared, so a
/// review UI can show them verbatim without re-deriving anything.
/// </summary>
public sealed record FieldFinding(
    string Field,
    FieldVerdict Verdict,
    string? Captured,
    string? OnWiki,
    string? Explanation = null)
{
    /// <summary>True when the tool would change the page for this field.</summary>
    public bool IsChange => Verdict is FieldVerdict.Differs or FieldVerdict.MissingOnWiki;

    /// <summary>True when this needs a human before anything is written.</summary>
    public bool Blocks => Verdict is FieldVerdict.NeedsReview;
}
