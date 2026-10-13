using System.Text;
using EQLWikiEditorAssistant.Wiki.MediaWiki;

namespace EQLWikiEditorAssistant.Tests.Wiki;

/// <summary>
/// The review browser runs the wiki's scripts (user, 2026-10-12), so what it may send is the only thing standing between
/// a script and an anonymous edit. These pin the rule from both sides: everything that reads passes, and the writes the
/// wiki's own scripts really make — the verification toast's edit, the skin's purge — are refused.
/// </summary>
public class ReviewBrowserTests
{
    private static readonly Uri Wiki = new("https://eqlwiki.com/");
    private static readonly Uri Api = new("https://eqlwiki.com/api.php");
    private const string Form = "application/x-www-form-urlencoded; charset=UTF-8";

    private static bool Post(string body, string contentType = Form, Uri? to = null) =>
        ReviewBrowserRequests.MayPass("POST", to ?? Api, contentType, Encoding.UTF8.GetBytes(body), Wiki);

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    public void ReadingPassesAnywhere(string method) =>
        Assert.True(ReviewBrowserRequests.MayPass(method, new Uri("https://cdn.example/x.js"), null, null, Wiki));

    [Fact]
    public void TheWikisReadsPassEvenWhenPosted()
    {
        Assert.True(Post("action=parse&format=json&title=X&text=%7B%7BItempage%7D%7D"));
        Assert.True(Post("action=eqlmetadata&format=json&titles=X"));
        Assert.True(Post("action=query&meta=tokens&format=json"));
    }

    /// <summary>The verification toast's own request, as its script builds it: refused.</summary>
    [Fact]
    public void TheVerificationToastsEditIsRefused() =>
        Assert.False(Post("action=edit&title=VerifiedPages&appendtext=%0AX%0A&summary=Adding+page+to+verified+list" +
                          "&watchlist=nochange&format=json&token=%2B%5C"));

    [Fact]
    public void EveryOtherActionIsRefused()
    {
        Assert.False(Post("action=purge&titles=X&format=json"));
        Assert.False(Post("action=options&change=x%3D1&token=%2B%5C"));
        Assert.False(Post("format=json"));   // no action at all says nothing about what it does
    }

    /// <summary>A second action anywhere in the request — the address, or a repeated field — is judged too, so a read
    /// cannot carry a write past the check.</summary>
    [Fact]
    public void EveryActionTheRequestNamesMustRead()
    {
        Assert.False(Post("action=query&action=edit"));
        Assert.False(Post("action=query", to: new Uri("https://eqlwiki.com/api.php?action=edit")));
        Assert.True(Post("format=json", to: new Uri("https://eqlwiki.com/api.php?action=query")));
    }

    [Fact]
    public void AMultipartBodyIsReadToo()
    {
        const string boundary = "----b";
        string Body(string action) =>
            $"--{boundary}\r\nContent-Disposition: form-data; name=\"action\"\r\n\r\n{action}\r\n" +
            $"--{boundary}\r\nContent-Disposition: form-data; name=\"title\"\r\n\r\nX\r\n--{boundary}--\r\n";

        Assert.True(Post(Body("parse"), $"multipart/form-data; boundary={boundary}"));
        Assert.False(Post(Body("upload"), $"multipart/form-data; boundary={boundary}"));
    }

    [Fact]
    public void APostAnywhereElseIsRefused()
    {
        Assert.False(Post("action=parse", to: new Uri("https://eqlwiki.com/index.php")));
        Assert.False(Post("action=parse", to: new Uri("https://elsewhere.example/api.php")));
        Assert.False(Post("action=parse", to: new Uri("http://eqlwiki.com/api.php")));
        Assert.False(Post("{\"action\":\"parse\"}", "application/json"));   // a body it cannot read
    }

    [Theory]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("PATCH")]
    public void OtherMethodsAreRefused(string method) =>
        Assert.False(ReviewBrowserRequests.MayPass(method, Api, Form, Encoding.UTF8.GetBytes("action=query"), Wiki));

    // ---- the page view's scripts, loaded by the preview ----

    private const string PageView = """
        <script>document.documentElement.className="client-js";RLCONF={"wgPageName":"Special:BlankPage",
        "wgEQLClientDataEnabled":true,"wgEQLEraOutKeys":["kunark"],"wgEQLTalkPageName":"Special talk:BlankPage",
        "wgEQLPageVerified":true,"wgItemLevelDefault":2,"wgUserName":null};RLSTATE={"site.styles":"ready"};
        RLPAGEMODULES=["site","skins.EQLImmersive.base","ext.itemLevelSlider","ext.visualEditor.targetLoader","site"];</script>
        """;

    [Fact]
    public void APageViewsScriptsAreReadWithoutTheEditorsOwn()
    {
        PageScripts scripts = PageScripts.Read(PageView)!;

        Assert.Equal(["site", "skins.EQLImmersive.base", "ext.itemLevelSlider"], scripts.Modules);
    }

    /// <summary>The wiki's own settings come across; the ones describing the page that was viewed do not, or the
    /// preview would claim to be verified because Special:BlankPage is.</summary>
    [Fact]
    public void OnlyTheWikisOwnSettingsComeAcross()
    {
        string config = PageScripts.Read(PageView)!.ConfigJson;

        Assert.Contains("\"wgEQLClientDataEnabled\":true", config);
        Assert.Contains("\"wgEQLEraOutKeys\":[\"kunark\"]", config);
        Assert.Contains("\"wgItemLevelDefault\":2", config);
        Assert.DoesNotContain("wgEQLPageVerified", config);
        Assert.DoesNotContain("wgEQLTalkPageName", config);
        Assert.DoesNotContain("wgPageName", config);
        Assert.DoesNotContain("wgUserName", config);
    }

    [Fact]
    public void APageWithNoModuleListGivesNoScripts() =>
        Assert.Null(PageScripts.Read("<html><head><script>RLCONF={};</script></head></html>"));

    /// <summary>The loader goes last in the body, queued behind the page's own loader, and nothing in it can close the
    /// script element early.</summary>
    [Fact]
    public void ThePreviewLoadsThemLast()
    {
        var page = new RenderedPage("X", "<html><head></head><body>", "<p>body</p>", "",
            Scripts: new PageScripts(["site"], "{\"wgEQLX\":\"</script><b>\"}"));

        string html = page.ToDocument(Wiki);

        Assert.EndsWith(
            "<script>(RLQ=window.RLQ||[]).push(function(){mw.config.set({\"wgEQLX\":\"<\\/script><b>\"});" +
            "mw.loader.load([\"site\"]);});</script></body></html>",
            html);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "</script>"));
    }
}
