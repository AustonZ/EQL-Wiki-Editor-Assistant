using EQLWikiAssistant.Core.Ocr;

namespace EQLWikiAssistant.Core.Icons;

/// <summary>
/// Decodes an image the tool downloaded — in practice a wiki icon PNG — into a <see cref="CapturedImage"/>.
///
/// **A port, because the only decoder available is Windows-only and this assembly is not.** Everything that
/// compares icons lives in portable code (<see cref="IconHasher"/>, <see cref="ItemIconReader"/>, the pipeline that
/// calls them), while the actual PNG decode needs WinRT imaging. Before this existed the icon comparison could only
/// be run from dev tooling, through a file-based loader, which is why CLAUDE.md listed the production path as
/// "still to wire up".
///
/// Implementations must composite transparency over the requested background rather than discarding it — see
/// <see cref="AlphaComposite"/> for why that is not a detail.
/// </summary>
public interface IImageDecoder
{
    /// <summary>
    /// Decodes <paramref name="bytes"/>, flattening any transparency onto <paramref name="background"/>.
    /// </summary>
    /// <param name="background">The grey to composite over — <see cref="AlphaComposite.GameBackground"/> for a wiki
    /// icon that will be compared against one the game drew.</param>
    Task<CapturedImage> DecodeAsync(
        byte[] bytes,
        byte background = AlphaComposite.GameBackground,
        CancellationToken cancellationToken = default);
}
