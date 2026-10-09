using EQLWikiEditorAssistant.Core.Ocr;

namespace EQLWikiEditorAssistant.Core.Locate;

/// <summary>
/// Finds the item windows in a captured frame.
///
/// **A port because the real implementation needs real pixels.** <see cref="ItemWindowLocator"/> traces the window's
/// own grey outline and runs OCR over the frame, so exercising anything downstream of it means either a real
/// screenshot or a frame synthesized to satisfy a dozen measured pixel thresholds. Neither is a reasonable way to
/// test the pipeline's *sequencing* — whether the ledger is consulted before the wiki, whether an ineligible item
/// gets a row — which is what this interface exists to make testable. The tracer keeps its own golden tests against
/// real samples, where those thresholds belong.
/// </summary>
public interface IItemWindowLocator
{
    Task<IReadOnlyList<LocatedWindow>> LocateAsync(CapturedImage image, CancellationToken cancellationToken = default);
}

/// <summary>The real locator: whole-frame OCR to find each window's Description tab, then outline tracing. See
/// <see cref="ItemWindowLocator"/> and CLAUDE.md before changing anything it depends on.</summary>
public sealed class BorderTracingWindowLocator(IOcrEngine ocrEngine) : IItemWindowLocator
{
    private readonly IOcrEngine _ocrEngine = ocrEngine ?? throw new ArgumentNullException(nameof(ocrEngine));

    public Task<IReadOnlyList<LocatedWindow>> LocateAsync(
        CapturedImage image, CancellationToken cancellationToken = default) =>
        ItemWindowLocator.LocateAsync(image, _ocrEngine, cancellationToken);
}
