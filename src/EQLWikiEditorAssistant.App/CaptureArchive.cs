using System.IO;
using System.Windows.Media.Imaging;
using EQLWikiEditorAssistant.Core.Ocr;
using EQLWikiEditorAssistant.Pipeline;

namespace EQLWikiEditorAssistant.App;

/// <summary>
/// Keeps the last few captured frames on disk, when Settings > Saved captures says to, so a bug can be reported by
/// naming an item rather than by keeping it in the game (user, 2026-09-29), and so a frame worth adding to the sample
/// corpus is already on disk.
///
/// **Off unless chosen, and never inside the repository.** A captured frame is a full screenshot: it can hold
/// character names, other players' names and chat, which is exactly why `samples/` is gitignored and why the pipeline
/// otherwise keeps every frame in memory. These land in the user's own app-data folder, outside any working copy, so
/// there is nothing for a commit to pick up by accident. It was once tied to Debug builds; it became a setting when
/// the tool started running as a Release build (user, 2026-10-07), so the one person who wants it keeps it.
///
/// **Named after the items in the frame**, because that is how a bug gets reported — "the icon for Lake Pebble is
/// wrong" should lead straight to the file, without the user having to remember when they captured it.
/// </summary>
public static class CaptureArchive
{
    /// <summary>How many frames to keep. Enough to cover a testing session and still bounded, since each is a
    /// full-resolution screenshot.</summary>
    public const int Keep = 50;

    /// <summary>Named from when this was a Debug-build feature, and kept so the frames already there stay found.</summary>
    public static string Directory => Path.Combine(AppPaths.Root, "debug-captures");

    /// <summary>
    /// Writes the frame and returns its path, or null when the write failed. Whether to call it at all is the
    /// caller's, from <c>AppSettings.KeepCaptures</c>.
    ///
    /// A failure is swallowed: not being able to keep a debugging aid is never a reason to fail the capture the user
    /// actually asked for.
    /// </summary>
    public static string? Save(CapturedImage frame, IEnumerable<string> itemNames)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(itemNames);

        try
        {
            System.IO.Directory.CreateDirectory(Directory);

            string named = string.Join(
                ", ",
                itemNames.Where(n => !string.IsNullOrWhiteSpace(n)).Select(Sanitize).Distinct().Take(4));

            string path = Path.Combine(
                Directory,
                $"{DateTime.Now:yyyy-MM-dd HH-mm-ss}{(named.Length == 0 ? "" : " - " + named)}.png");

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(
                frame.Width, frame.Height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null,
                frame.Pixels, frame.Width * 4)));

            using (FileStream file = File.Create(path)) encoder.Save(file);

            Prune();
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Drops all but the newest <see cref="Keep"/>. Deleting is best-effort for the same reason writing is.</summary>
    private static void Prune()
    {
        try
        {
            foreach (FileInfo file in new DirectoryInfo(Directory)
                         .GetFiles("*.png")
                         .OrderByDescending(f => f.LastWriteTimeUtc)
                         .Skip(Keep))
                file.Delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>An item name can legitimately contain characters a path cannot — `Cell Key #5` is a real one.</summary>
    private static string Sanitize(string name) =>
        string.Concat(name.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
