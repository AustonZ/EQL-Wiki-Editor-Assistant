namespace EQLWikiEditorAssistant.Core.Ocr;

/// <summary>
/// The font the game draws its UI text in. The game lets a player choose it; these are the two the glyph reader
/// knows.
///
/// They render <b>pixel-identically except for two characters</b> — measured from the in-game glyph sheet, 87 of
/// the 89 shapes Arial produces are byte-identical in both — so one atlas serves both and each font contributes
/// only what is unique to it (see <c>AtlasEntry.Font</c>):
/// <list type="bullet">
/// <item><b>I</b>: in Arial a bare 2x9 bar, the very same pixels as lowercase <b>l</b>. EQL Wiki Editor Assistant gives it
/// serifs, so there the bar is only ever an l.</item>
/// <item><b>r</b>: EQL Wiki Editor Assistant's is one pixel wider with its arm a quarter-pixel longer, so that "rn" stops
/// reading as "m". Its tip pixel differs from Arial's, which also makes the two r's distinct shapes.</item>
/// </list>
/// </summary>
public enum UiFont
{
    /// <summary>Windows' Arial, the game's default. The bare bar is genuinely ambiguous between l and I, and the
    /// reader resolves it from the surrounding word, with a dictionary for a bar that starts a lowercase word.</summary>
    Arial,

    /// <summary>The user's personal modification of Arial (<c>fonts/</c>, gitignored): a serifed I and a wider r.
    /// Nothing is guessed: every character it can draw has a shape of its own.</summary>
    EqlWikiEditorAssistant,
}

public static class UiFonts
{
    /// <summary>
    /// The font the Assistant assumes the game is drawing in when the user has not chosen one in the settings window.
    ///
    /// **Arial, the game's own default** (user, 2026-10-08, for the first release). It was EQL Wiki Editor Assistant (user,
    /// 2026-10-05), which is the user's own font and is never distributed — so a tester on stock Arial would have had
    /// every capture refused as the wrong font until they found the setting. The user's own settings name their font
    /// explicitly, so this changes nothing for them.
    ///
    /// <b>An explicit choice rather than detection, by the user's decision.</b> A capture that contradicts it is
    /// still caught — the two r shapes differ, so the reader records which font a line was drawn in and the
    /// pipeline refuses a window that disagrees with this setting (see <c>ItemCheckStatus.WrongFont</c>). Without
    /// that, an Arial capture read as EQL Wiki Editor Assistant would turn every capital I into an l, silently.
    ///
    /// Reading the setting from the game is a future feature: the per-character UI ini
    /// (<c>UI_&lt;character&gt;_&lt;server&gt;_&lt;loadout&gt;.ini</c>) carries it as <c>[Fonts] Font.us.0=Arial</c>,
    /// but the player can switch loadout on the fly, so which file applies is itself a question.
    /// </summary>
    public const UiFont AppDefault = UiFont.Arial;

    /// <summary>The font's name as the game's font option shows it.</summary>
    public static string DisplayName(UiFont font) => font switch
    {
        UiFont.EqlWikiEditorAssistant => "EQL Wiki Editor Assistant",
        _ => font.ToString(),
    };

    /// <summary>Parses a font name as the tools and the corpus file write it (the enum name, any case).</summary>
    public static UiFont Parse(string text) =>
        Enum.TryParse(text, ignoreCase: true, out UiFont font) && Enum.IsDefined(font)
            ? font
            : throw new ArgumentException(
                $"'{text}' is not a UI font. Known: {string.Join(", ", Enum.GetNames<UiFont>())}.", nameof(text));
}
