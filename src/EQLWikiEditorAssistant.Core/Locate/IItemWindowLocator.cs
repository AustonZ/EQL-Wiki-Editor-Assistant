using EQLWikiEditorAssistant.Core.Glyphs;
using EQLWikiEditorAssistant.Core.Imaging;

namespace EQLWikiEditorAssistant.Core.Locate;

/// <summary>
/// Finds the item windows in a captured frame.
///
/// **A port because the real implementation needs real pixels.** <see cref="ItemWindowLocator"/> finds each window's
/// tab, traces the window's own grey outline and reads what is inside, so exercising anything downstream of it means
/// either a real screenshot or a frame synthesized to satisfy a dozen measured pixel thresholds. Neither is a
/// reasonable way to test the pipeline's *sequencing* — whether the ledger is consulted before the wiki, whether an
/// ineligible item gets a row — which is what this interface exists to make testable. The tracer keeps its own golden
/// tests against real samples, where those thresholds belong.
/// </summary>
public interface IItemWindowLocator
{
    Task<IReadOnlyList<LocatedWindow>> LocateAsync(CapturedImage image, CancellationToken cancellationToken = default);
}

/// <summary>The real locator: each window's Description tab found by its pixels, then outline tracing, then the glyph
/// reader. See <see cref="ItemWindowLocator"/> and CLAUDE.md before changing anything it depends on.</summary>
public sealed class BorderTracingWindowLocator(GlyphTextReader reader) : IItemWindowLocator
{
    private readonly GlyphTextReader _reader = reader ?? throw new ArgumentNullException(nameof(reader));

    public Task<IReadOnlyList<LocatedWindow>> LocateAsync(
        CapturedImage image, CancellationToken cancellationToken = default) =>
        Task.FromResult(ItemWindowLocator.Locate(image, _reader, cancellationToken));
}
