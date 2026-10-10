namespace EQLWikiEditorAssistant.Core.Imaging;

/// <summary>
/// A portable, integer pixel rectangle. Deliberately not System.Drawing.Rectangle or a WinRT type,
/// so this type (and everything that uses it) stays usable from the Windows-API-free Core project.
/// </summary>
public readonly record struct Rect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;

    public bool IntersectsWith(Rect other) =>
        X < other.Right && other.X < Right && Y < other.Bottom && other.Y < Bottom;
}
