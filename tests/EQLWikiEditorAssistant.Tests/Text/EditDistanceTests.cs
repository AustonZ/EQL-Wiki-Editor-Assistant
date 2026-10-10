using EQLWikiEditorAssistant.Core.Text;

namespace EQLWikiEditorAssistant.Tests.Text;

public class EditDistanceTests
{
    [Theory]
    [InlineData("", "", 0)]
    [InlineData("abc", "abc", 0)]
    [InlineData("abc", "", 3)]
    [InlineData("", "abc", 3)]
    [InlineData("kitten", "sitting", 3)]
    [InlineData("Description", "Descripbon", 2)]
    [InlineData("Ornamentation", "Omamentation", 2)] // rn -> m
    public void Levenshtein_MatchesExpectedDistance(string a, string b, int expected) =>
        Assert.Equal(expected, EditDistance.Levenshtein(a, b));

    [Theory]
    [InlineData("Description", "Description", 0, true)]
    [InlineData("Descripbon", "Description", 3, true)]
    [InlineData("Chat", "Description", 3, false)]
    [InlineData("description", "Description", 0, true)] // case-insensitive
    public void IsCloseMatch_RespectsThreshold(string candidate, string target, int maxDistance, bool expected) =>
        Assert.Equal(expected, EditDistance.IsCloseMatch(candidate, target, maxDistance));
}
