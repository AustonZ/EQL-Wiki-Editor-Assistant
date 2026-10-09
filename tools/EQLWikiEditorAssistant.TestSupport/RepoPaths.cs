namespace EQLWikiEditorAssistant.TestSupport;

/// <summary>Locates repo-relative paths from a test/tool's output directory, wherever `dotnet test`/`dotnet run`
/// happens to place it.</summary>
public static class RepoPaths
{
    private static readonly Lazy<string> RootLazy = new(FindRoot);

    /// <summary>The repo root (the directory containing EQLWikiEditorAssistant.slnx).</summary>
    public static string Root => RootLazy.Value;

    /// <summary>The gitignored samples/ folder of real screenshots (may not exist on a fresh clone).</summary>
    public static string SamplesDirectory => Path.Combine(Root, "samples");

    /// <summary>Ground truth for the samples corpus. **Tracked in git** — it lives under tests/ rather than
    /// beside the screenshots because samples/ is gitignored, so a sidecar file there would silently vanish on a
    /// fresh clone and take the regression guard with it. See ExpectedCorpus for what may go in it.</summary>
    public static string ExpectedItemsFile =>
        Path.Combine(Root, "tests", "EQLWikiEditorAssistant.Tests", "Accuracy", "expected-items.json");

    /// <summary>Gitignored scratch space for harness output (candidate ground-truth files, cached crops, JSON
    /// summaries) — anything derived from real screenshots and therefore not committable.</summary>
    public static string LocalDataDirectory => Path.Combine(Root, ".local-data");

    /// <summary>Every item icon extracted from the game's own asset files, one PNG per icon, named by its id —
    /// **tracked in git** (the user added it), unlike samples/. These are the game's artwork rather than a capture of
    /// anyone's screen, so they carry none of the private-information risk that keeps screenshots out of the repo.
    /// </summary>
    public static string IconLibraryDirectory => Path.Combine(Root, "game_assets", "item_icons");

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (dir.EnumerateFiles("EQLWikiEditorAssistant.slnx").Any())
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            $"Could not find EQLWikiEditorAssistant.slnx above {AppContext.BaseDirectory}.");
    }
}
