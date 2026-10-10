using EQLWikiEditorAssistant.Core.Icons;
using EQLWikiEditorAssistant.Core.Imaging;

namespace EQLWikiEditorAssistant.Pipeline;

/// <summary>
/// The icon the library recognized in a captured item window.
///
/// **The tool proposes an id; it never writes one on its own.** On a page being created the id is filled into the
/// generated text, which the user reads before saving. On a page that already exists it reaches the wikitext only
/// when the user presses the button (user, 2026-10-02) — because there the tool cannot tell a wrong page from an odd
/// capture, and that judgement was always meant to be a human's rather than a threshold's. What changed is only that
/// the human is now shown the answer instead of being left to look it up: the search has already run, since it is
/// what noticed the mismatch.
///
/// **The id and the artwork stay consistent by construction**, which is what makes this safe even where the wiki's
/// own numbering disagrees. The tool writes <c>lucy_img_ID = N</c> and, if the wiki lacks the file, uploads
/// <c>game_assets/item_icons/N.png</c> as <c>File:Item_N.png</c> — the same N on both sides. Measured, this matters:
/// 2 of 90 corpus items have a wiki page whose id names a file holding *different* artwork from the library's file of
/// that id (`File:Item_2896.png` carries what the library calls 10275, and `Item_10275.png` does not exist on the
/// wiki at all). Writing the pair together means a new page renders the artwork the capture actually showed,
/// whichever numbering the wiki used historically.
/// </summary>
/// <param name="IconId">The library's id for the matched artwork — the value for <c>lucy_img_ID</c>.</param>
/// <param name="Distance">How close the match was. Reported rather than gated on: a correct match reaches 0.2977 on
/// this corpus, so an absolute cutoff loses right answers — see <see cref="IconLibrary.ConfidentMargin"/>.</param>
/// <param name="Margin">How far behind the nearest *different* artwork is. This is what confidence rests on.</param>
/// <param name="IsConfident">Whether the id was clear enough to write into the wikitext. When false the id is left
/// blank and the shortlist is offered instead — a flagged gap rather than a silent guess.</param>
/// <param name="Candidates">The ranked shortlist, so a user who has to choose can see what else was close.</param>
/// <param name="Image">The matched icon's own artwork, for showing beside the captured one. The confirmation this
/// feature rests on is the user's eye, not the number.</param>
/// <param name="AlreadyOnWiki">Whether the wiki already has <c>File:Item_&lt;id&gt;.png</c>; null when it could not be
/// determined. **Mostly it does not** — the wiki holds 796 item icons against the library's 11,592, so uploading is
/// the common case rather than the exception.</param>
public sealed record IconSuggestion(
    string IconId,
    double Distance,
    double Margin,
    bool IsConfident,
    IReadOnlyList<IconMatch> Candidates,
    CapturedImage? Image,
    bool? AlreadyOnWiki)
{
    /// <summary>Whether the tool should offer to upload this icon: it is confident which icon it is, and the wiki is
    /// known not to have it. An unknown answer is not an invitation to upload — publishing over a file that may
    /// exist is the one thing nobody here could undo.</summary>
    public bool CanUpload => IsConfident && AlreadyOnWiki == false;

    /// <summary>
    /// Whether the tool can offer to point an existing page at this icon.
    ///
    /// **It needs a known answer about the file, not merely a confident match**, and that is the whole difference
    /// from <see cref="CanUpload"/>. Writing an id whose file may not exist would leave the item's box rendering
    /// nothing — trading a wrong picture for no picture. Known-absent is fine, because the offer then includes the
    /// upload: 93% of the library is missing from the wiki, so that is the ordinary case rather than the exception.
    /// </summary>
    public bool CanApplyToPage => IsConfident && AlreadyOnWiki is not null;

    /// <summary>Whether applying this icon has to upload the file first.</summary>
    public bool NeedsUpload => AlreadyOnWiki == false;

    /// <summary>The name the file would take on the wiki.</summary>
    public string WikiFileName => IconLibraryFolder.WikiFileNameFor(IconId);
}

/// <summary>The outcome of uploading an icon. <paramref name="Url"/> is where it landed, so the user can go and look
/// at the one thing a tool cannot check for them.</summary>
public sealed record IconUploadResult(bool Uploaded, string? Url, string? Error);
