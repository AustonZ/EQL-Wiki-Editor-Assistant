namespace EQLWikiEditorAssistant.Core.Glyphs;

/// <summary>
/// The game's UI scales the Assistant can read, and which UI fonts it knows at each (user, 2026-10-13, after a spike
/// that captured every scale the game offers).
///
/// **Whole-number scales only: 1.00, 2.00, 3.00 and 4.00, with Text Filtering set to None above 1.00.** At those the
/// game draws its text fresh at the target size, on the same seven-step anti-aliasing ramp, and every glyph is
/// identical wherever the window sits — so exact matching works exactly as it does at 1.00. And the window's chrome is
/// the 1.00 chrome with every distance times the scale, colours unchanged (the frame 2N, the outline N, the title bar
/// 16N), so a scale is today's measured distances times N plus its own glyph atlas.
///
/// **Refused, with a message, and why**:
/// <list type="bullet">
/// <item>0.75 is the 1.00 drawing shrunk: filtering None drops rows and columns, Linear blends them.</item>
/// <item>The fractional scales (1.25 to 2.75) place a glyph's pixels by where it sits on screen, so the same letter
/// changes shape as the window moves — the "dancing" a player sees dragging a window at 1.75.</item>
/// <item>Linear filtering blends text at every scale but 1.00, where it changes nothing.</item>
/// </list>
/// A scale is a plain <c>int</c> everywhere it is passed, and every pixel distance that came from measuring the
/// 1.00 window is multiplied by it.
/// </summary>
public static class UiScales
{
    public static IReadOnlyList<int> Supported { get; } = [1, 2, 3, 4];

    /// <summary>
    /// The UI fonts the Assistant can read at a scale. The EQL Wiki Editor Assistant font has a glyph sheet only at
    /// 1.00, so above it only Arial — the game's default, and the font every tester uses — is known.
    /// </summary>
    public static IReadOnlyList<UiFont> FontsAt(int scale) =>
        scale == 1 ? Enum.GetValues<UiFont>() : [UiFont.Arial];
}
