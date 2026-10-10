using EQLWikiEditorAssistant.Core.Imaging;

namespace EQLWikiEditorAssistant.Core.Glyphs;

/// <summary>One word read from an image, with its location in it.</summary>
public sealed record TextWord(string Text, Rect BoundingBox);

/// <summary>
/// One line read from an image, with its location and the words that make it up. A row of the item window can come
/// back as several lines — the stat block's two columns are split at their gap — so the parser groups lines into rows
/// itself rather than trusting one line to be one row.
///
/// <see cref="DrawnIn"/> is which <see cref="UiFont"/> the line was drawn in, when its characters can tell: only some
/// shapes belong to one font, so a line without any leaves it null. It is evidence, not a setting: the reader still
/// resolves the line by the font it was configured with.
/// </summary>
public sealed record TextLine(string Text, Rect BoundingBox, IReadOnlyList<TextWord> Words, UiFont? DrawnIn = null);
