using EQLWikiEditorAssistant.Core.Items;
using EQLWikiEditorAssistant.TestSupport;
using EQLWikiEditorAssistant.TestSupport.Accuracy;

namespace EQLWikiEditorAssistant.Tests.Items;

/// <summary>
/// Every input here is a real captured value or a real wiki value. The pairs come from both sides of the same
/// items, so they pin the actual transform rather than a plausible one — `Peridot`'s window says
/// "9 platinum 5 gold 2 silver 4 copper" and its page already says "9p 5g 2s 4c".
/// </summary>
public class MerchantValueTests
{
    [Theory]
    // Captured window text -> the compact form the wiki wants. All twelve are verbatim from the verified corpus.
    [InlineData("22 platinum 8 gold 5 silver 7 copper", "22p 8g 5s 7c")]
    [InlineData("9 platinum 5 gold 2 silver 4 copper", "9p 5g 2s 4c")]   // Peridot: matches its page exactly
    [InlineData("33 platinum 3 gold 3 silver 3 copper", "33p 3g 3s 3c")]
    [InlineData("142 platinum 8 gold 5 silver 7 copper", "142p 8g 5s 7c")]
    [InlineData("1 platinum 5 silver 8 copper", "1p 5s 8c")]             // gold omitted by the game itself
    [InlineData("19 platinum 4 silver 8 copper", "19p 4s 8c")]
    [InlineData("2 platinum 5 silver 9 copper", "2p 5s 9c")]
    [InlineData("350 platinum", "350p")]
    [InlineData("2 gold 4 silver 8 copper", "2g 4s 8c")]
    [InlineData("1 gold 9 silver", "1g 9s")]
    [InlineData("1 silver 6 copper", "1s 6c")]
    [InlineData("8 copper", "8c")]
    [InlineData("1 silver", "1s")]
    public void RealCapturedValuesRenderTheWikiForm(string captured, string expected)
    {
        Assert.True(MerchantValue.TryParseGameText(captured, out MerchantValue value));
        Assert.Equal(expected, value.ToWikiText());
    }

    /// <summary>The worthless case is a string both sides write literally, so it must survive as itself rather than
    /// collapsing to an empty value.</summary>
    [Fact]
    public void AbsolutelyNothingRoundTripsAsItself()
    {
        Assert.True(MerchantValue.TryParseGameText("absolutely nothing", out MerchantValue value));
        Assert.True(value.IsNothing);
        Assert.Equal("absolutely nothing", value.ToWikiText());
        Assert.Equal("absolutely nothing", MerchantValue.Nothing.ToWikiText());
    }

    /// <summary>Zero denominations are dropped. The game omits them already, so this only matters for a value the
    /// tool constructs itself — but a real page carries "0p 0g 1s 0c with 111 Charisma", so the form exists.</summary>
    [Fact]
    public void ZeroDenominationsAreDropped() =>
        Assert.Equal("1s", new MerchantValue(0, 0, 1, 0).ToWikiText());

    /// <summary>An unreadable value must report failure, not zero. Reporting zero would publish "absolutely
    /// nothing" for an item whose value simply could not be read — a silently wrong figure, which is the one
    /// outcome this project treats as unacceptable.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("22p 8g 5s 7c")]                        // the wiki form is not the game's; not this method's job
    [InlineData("2.6pp")]                               // a real legacy page value
    [InlineData("1gp to vendor.")]
    [InlineData("187p1g9s1cp (68 CHA @ Kindly)")]
    [InlineData("22 platinum 8 gold 5 silver 7")]       // trailing amount with no denomination
    [InlineData("platinum 8")]
    [InlineData("-1 platinum")]
    [InlineData("1 platinum 2 platinum")]               // a repeat is not the window's phrasing
    [InlineData("3 dollars")]
    public void UnreadableValuesFailRatherThanReturningZero(string? text)
    {
        Assert.False(MerchantValue.TryParseGameText(text, out MerchantValue value));
        Assert.Equal(default, value);
    }

    /// <summary>
    /// The whole point of the type, checked against every merchant value in the verified corpus rather than the
    /// dozen hand-picked above: if the game ever phrases one differently, this fails instead of the tool quietly
    /// writing a wrong figure to a public wiki.
    /// </summary>
    [Fact]
    public void EveryMerchantValueInTheVerifiedCorpusParses()
    {
        if (!File.Exists(RepoPaths.ExpectedItemsFile)) return; // ground truth is tracked, but be robust anyway

        ExpectedCorpus corpus = ExpectedCorpus.Load(RepoPaths.ExpectedItemsFile);
        string[] values = [.. corpus.Samples
            .SelectMany(s => s.Windows)
            .Select(w => w.MerchantValue)
            .Where(v => !string.IsNullOrWhiteSpace(v) && v != ExpectedCorpus.TodoMarker)
            .Select(v => v!)
            .Distinct(StringComparer.Ordinal)];

        Assert.NotEmpty(values);

        var unreadable = new List<string>();
        foreach (string value in values)
            if (!MerchantValue.TryParseGameText(value, out _)) unreadable.Add(value);

        Assert.True(unreadable.Count == 0,
            $"These captured merchant values could not be read: {string.Join(" | ", unreadable)}");
    }
}
