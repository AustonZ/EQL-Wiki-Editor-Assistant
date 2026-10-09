using EQLWikiEditorAssistant.Wiki.Wikitext;

namespace EQLWikiEditorAssistant.Tests.Wiki;

/// <summary>The scanner's contract is "find the parameter, or admit you couldn't" — never an exception and never a
/// confidently wrong span, since a wrong span is an edit spliced into somebody else's prose.</summary>
public class WikitextScannerTests
{
    [Fact]
    public void FindTemplate_ReadsNamedParameters()
    {
        TemplateCall? call = WikitextScanner.FindTemplate("{{Itempage|itemname=Bladestopper|lucy_img_ID=606}}", "Itempage");

        Assert.NotNull(call);
        Assert.Equal("Itempage", call.Name);
        Assert.Equal("Bladestopper", call.Find("itemname")!.Value);
        Assert.Equal("606", call.Find("lucy_img_ID")!.Value);
    }

    /// <summary>Real pages put the name on its own line. Requiring the name segment to be newline-free rejected
    /// every item page on the wiki, which is how this test came to exist.</summary>
    [Fact]
    public void FindTemplate_TolerartesANewlineAfterTheName()
    {
        TemplateCall? call = WikitextScanner.FindTemplate("{{Itempage\n|itemname = Water Flask\n}}", "Itempage");

        Assert.NotNull(call);
        Assert.Equal("Itempage", call.Name);
        Assert.Equal("Water Flask", call.Find("itemname")!.Value);
    }

    [Theory]
    [InlineData("{{itempage|x=1}}")]          // MediaWiki upper-cases a template name's first letter
    [InlineData("{{Item_page|x=1}}", false)]  // ...but an underscore maps to a space, so this is "Item page"
    [InlineData("{{ Itempage |x=1}}")]
    [InlineData("{{Template:Itempage|x=1}}")]
    [InlineData("{{:Itempage|x=1}}")]
    public void FindTemplate_NormalizesTheNameLikeMediaWiki(string wikitext, bool shouldMatch = true) =>
        Assert.Equal(shouldMatch, WikitextScanner.FindTemplate(wikitext, "Itempage") is not null);

    /// <summary>A pipe inside a wikilink is part of the link, not a parameter separator. Water Flask's soldby table
    /// has hundreds of these, so getting it wrong truncates the parameter and every span after it.</summary>
    [Fact]
    public void FindTemplate_DoesNotSplitOnAPipeInsideAWikilink()
    {
        TemplateCall? call = WikitextScanner.FindTemplate(
            "{{Itempage|soldby=[[Cabilis|East Cabilis]] and [[Neriak|Neriak Commons]]|itemname=X}}", "Itempage");

        Assert.NotNull(call);
        Assert.Equal("[[Cabilis|East Cabilis]] and [[Neriak|Neriak Commons]]", call.Find("soldby")!.Value);
        Assert.Equal("X", call.Find("itemname")!.Value);
    }

    [Fact]
    public void FindTemplate_DoesNotSplitOnAPipeInsideANestedTemplate()
    {
        TemplateCall? call = WikitextScanner.FindTemplate(
            "{{Itempage|notes={{Item Lore|For those that like to bash}}|itemname=Earring of Bashing}}", "Itempage");

        Assert.NotNull(call);
        Assert.Equal("{{Item Lore|For those that like to bash}}", call.Find("notes")!.Value);
        Assert.Equal("Earring of Bashing", call.Find("itemname")!.Value);
    }

    /// <summary>Only the first top-level '=' names the parameter; a value containing one keeps it.</summary>
    [Fact]
    public void FindTemplate_KeepsAnEqualsSignInsideAValue()
    {
        TemplateCall? call = WikitextScanner.FindTemplate("{{Itempage|notes=width=200px|itemname=X}}", "Itempage");

        Assert.Equal("width=200px", call!.Find("notes")!.Value);
    }

    [Fact]
    public void FindTemplate_ReadsPositionalParameters()
    {
        TemplateCall? call = WikitextScanner.FindTemplate("{{Item Lore|Some lore text}}", "Item Lore");

        Assert.NotNull(call);
        Assert.Single(call.Parameters);
        Assert.Null(call.Parameters[0].Name);
        Assert.Equal(1, call.Parameters[0].Index);
        Assert.Equal("Some lore text", call.Parameters[0].Value);
    }

    /// <summary>MediaWiki renders the last of a set of duplicates; two real item pages have duplicate |notes=, the
    /// first empty. Returning the first would read a blank value off a page that visibly has content.</summary>
    [Fact]
    public void Find_ReturnsTheLastDuplicate()
    {
        TemplateCall? call = WikitextScanner.FindTemplate("{{Itempage|notes=|itemname=X|notes=Backstab DMG: 10}}", "Itempage");

        Assert.Equal("Backstab DMG: 10", call!.Find("notes")!.Value);
    }

    [Theory]
    [InlineData("{{Itempage|itemname=X", "unterminated braces")]
    [InlineData("no template here at all", "no braces")]
    [InlineData("{{Itempage|x=[[unclosed link}}", "unclosed wikilink still terminates the call")]
    [InlineData("{{", "nothing after the opener")]
    [InlineData("}}{{}}", "empty name")]
    public void FindTemplate_DegradesGracefullyOnMalformedInput(string wikitext, string _)
    {
        // The point is only that nothing throws. Whether a given malformed page yields a call or null is a
        // best-effort detail; corrupting one is not.
        Exception? thrown = Record.Exception(() => WikitextScanner.FindTemplate(wikitext, "Itempage"));
        Assert.Null(thrown);
    }

    /// <summary>An unclosed "[[" must not hide every later pipe. Before the blank-line rule it swallowed the rest
    /// of the page, and the parameters after it disappeared.</summary>
    [Fact]
    public void FindTemplate_RecoversFromAnUnclosedWikilink()
    {
        TemplateCall? call = WikitextScanner.FindTemplate(
            "{{Itempage|dropsfrom=[[Broken\n\nstill going|itemname=Recovered}}", "Itempage");

        Assert.Equal("Recovered", call!.Find("itemname")!.Value);
    }

    [Fact]
    public void ReplaceValue_ChangesOnlyThatSpan()
    {
        const string page = "intro\n{{Itempage|itemname=Old Name|lucy_img_ID=606}}\n[[Category:Ear]]";
        TemplateCall call = WikitextScanner.FindTemplate(page, "Itempage")!;

        string edited = WikitextScanner.ReplaceValue(page, call.Find("itemname")!, "New Name");

        Assert.Equal("intro\n{{Itempage|itemname=New Name|lucy_img_ID=606}}\n[[Category:Ear]]", edited);
    }

    [Fact]
    public void FindTemplates_ReturnsEveryMatchInOrder()
    {
        IReadOnlyList<TemplateCall> calls = WikitextScanner.FindTemplates(
            "{{Item Lore|first}} and {{Item Lore|second}}", "Item Lore");

        Assert.Equal(2, calls.Count);
        Assert.Equal("first", calls[0].Parameters[0].Value);
        Assert.Equal("second", calls[1].Parameters[0].Value);
    }
}
