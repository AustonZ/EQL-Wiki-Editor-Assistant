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
