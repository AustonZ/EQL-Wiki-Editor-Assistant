using EQLWikiEditorAssistant.TestSupport;

namespace EQLWikiEditorAssistant.Tests.Wiki;

/// <summary>Loads the real wiki pages in <c>Wiki/Fixtures/</c>. Unlike the screenshot corpus these are tracked in
/// git (see that folder's README for why that is safe), so these tests run on a fresh clone with no network and no
/// local setup.</summary>
internal static class WikiFixtures
{
    public static string Directory =>
        Path.Combine(RepoPaths.Root, "tests", "EQLWikiEditorAssistant.Tests", "Wiki", "Fixtures");

    public static string Load(string pageTitle) =>
        File.ReadAllText(Path.Combine(Directory, pageTitle + ".txt"));

    /// <summary>Every fixture page, as (title, wikitext). Used by the tests that must hold for all of them.</summary>
    public static TheoryData<string> AllTitles()
    {
        var data = new TheoryData<string>();
        foreach (string file in System.IO.Directory.EnumerateFiles(Directory, "*.txt"))
            data.Add(Path.GetFileNameWithoutExtension(file));
        return data;
    }
}
