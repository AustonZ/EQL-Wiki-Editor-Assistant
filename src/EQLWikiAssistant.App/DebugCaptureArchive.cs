using System.IO;
using System.Windows.Media.Imaging;
using EQLWikiAssistant.Core.Ocr;
using EQLWikiAssistant.Pipeline;

namespace EQLWikiAssistant.App;

/// <summary>
/// Keeps the last few captured frames on disk, in debug builds only, so a bug can be reported by naming an item
/// rather than by keeping it in the game (user, 2026-09-29).
///
/// **Debug builds only, and never inside the repository.** A captured frame is a full screenshot: it can hold
/// character names, other players' names and chat, which is exactly why `samples/` is gitignored and why the pipeline
/// otherwise keeps every frame in memory. These land in the user's own app-data folder, outside any working copy, so
/// there is nothing for a commit to pick up by accident. A release build writes nothing at all.
///
/// **Named after the items in the frame**, because that is how a bug gets reported — "the icon for Lake Pebble is
/// wrong" should lead straight to the file, without the user having to remember when they captured it.
/// </summary>
public static class DebugCaptureArchive
{
    /// <summary>How many frames to keep. Enough to cover a testing session and still bounded, since each is a
    /// full-resolution screenshot.</summary>
    public const int Keep = 50;

    public static string Directory => Path.Combine(AppPaths.Root, "debug-captures");

    /// <summary>Whether this build archives captures at all. False in release.</summary>
    public static bool Enabled =>
#if DEBUG
        true;
#else
        false;
#endif

    /// <summary>
    /// Writes the frame and returns its path, or null when this build does not archive or the write failed.
    ///
    /// A failure is swallowed: not being able to keep a debugging aid is never a reason to fail the capture the user
    /// actually asked for.
    /// </summary>
    public static string? Save(CapturedImage frame, IEnumerable<string> itemNames)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(itemNames);

        if (!Enabled) return null;

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
