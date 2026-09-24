namespace EQLWikiAssistant.TestSupport;

/// <summary>Locates repo-relative paths from a test/tool's output directory, wherever `dotnet test`/`dotnet run`
/// happens to place it.</summary>
public static class RepoPaths
{
    private static readonly Lazy<string> RootLazy = new(FindRoot);

    /// <summary>The repo root (the directory containing EQLWikiAssistant.slnx).</summary>
    public static string Root => RootLazy.Value;

    /// <summary>The gitignored samples/ folder of real screenshots (may not exist on a fresh clone).</summary>
    public static string SamplesDirectory => Path.Combine(Root, "samples");

    /// <summary>Ground truth for the samples corpus. **Tracked in git** — it lives under tests/ rather than
    /// beside the screenshots because samples/ is gitignored, so a sidecar file there would silently vanish on a
    /// fresh clone and take the regression guard with it. See ExpectedCorpus for what may go in it.</summary>
    public static string ExpectedItemsFile =>
        Path.Combine(Root, "tests", "EQLWikiAssistant.Tests", "Accuracy", "expected-items.json");

    /// <summary>Gitignored scratch space for harness output (candidate ground-truth files, cached crops, JSON
    /// summaries) — anything derived from real screenshots and therefore not committable.</summary>
    public static string LocalDataDirectory => Path.Combine(Root, ".local-data");

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (dir.EnumerateFiles("EQLWikiAssistant.slnx").Any())
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            $"Could not find EQLWikiAssistant.slnx above {AppContext.BaseDirectory}.");
    }
}
