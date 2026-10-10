using EQLWikiEditorAssistant.Core.Imaging;

namespace EQLWikiEditorAssistant.Core.Glyphs;

/// <summary>
/// Reads the text in an item window crop by exact atlas matching, in the UI font it is set to. See
/// <see cref="GlyphReader"/> for the method and <see cref="GlyphAtlas"/> for why it is exact.
///
/// The one thing that reads text in the Assistant: windows are found by their "Description" tab's pixels
/// (<c>Locate.DescriptionTabFinder</c>) and read by this. It needs nothing Windows-specific, so it is exercised by
/// plain <c>net10.0</c> tests with no capture involved.
/// </summary>
public sealed class GlyphTextReader
{
    private readonly GlyphAtlas _atlas;

    /// <summary>The UI font this reader reads as. Required rather than defaulted: it decides what the bare bar
    /// means (see <see cref="GlyphReader.ResolveBar"/>), so a caller that never thought about it would be making
    /// that call by accident.
    ///
    /// Settable because the user can change it in the settings window without restarting. The app changes it only
    /// while no capture is running, and together with the pipeline's wrong-font guard, in one setter.</summary>
    public UiFont Font { get; set; }

    public GlyphTextReader(UiFont font, GlyphAtlas? atlas = null)
    {
        Font = font;
        _atlas = atlas ?? GlyphAtlas.Bundled;
    }

    /// <summary>Every line of text in <paramref name="image"/>, in reading order.</summary>
    public IReadOnlyList<TextLine> Read(CapturedImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return GlyphReader.Read(image, new Rect(0, 0, image.Width, image.Height), _atlas, Font);
    }
}
