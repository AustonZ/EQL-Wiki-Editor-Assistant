using System.Net;
using System.Text.RegularExpressions;

namespace EQLWikiEditorAssistant.Wiki.MediaWiki;

/// <summary>
/// Wikitext as the wiki itself renders it, from <c>action=parse</c> — what the review's Preview page shows.
///
/// **Rendered by the wiki rather than imitated here**, because the thing being previewed is the wiki's own templates:
/// <c>Itempage</c>'s item box, the effect links, the categories. Any local imitation would be a second renderer to
/// keep in step with templates other editors change, and would preview what this tool thinks the page looks like
/// rather than what it does.
/// </summary>
/// <param name="Title">The title the text was rendered as, which is what the item box's own links resolve against.</param>
/// <param name="HeadHtml">The wiki's page head up to and including the opening <c>&lt;body&gt;</c>, with site-relative
/// URLs.</param>
/// <param name="BodyHtml">The rendered content.</param>
/// <param name="CategoriesHtml">The category links the text produces, or empty — shown because categories are part of
/// what the data pass writes and would otherwise be invisible in a preview.</param>
/// <param name="Stylesheets">The skin's own stylesheets, as site-relative addresses. **<c>action=parse</c> leaves these
/// out of <paramref name="HeadHtml"/>** — it links only the site's styles, so the item box rendered as plain serif
/// text — and a real page view links them separately; see <see cref="SkinStylesheets"/>.</param>
public sealed record RenderedPage(
    string Title,
    string HeadHtml,
    string BodyHtml,
    string CategoriesHtml,
    IReadOnlyList<string>? Stylesheets = null)
{
    /// <summary>
    /// One self-contained HTML document, for a browser that is handed the text rather than navigated to a URL.
    ///
    /// **A <c>&lt;base&gt;</c> is added first thing in the head**, because every stylesheet, script and image the wiki
    /// returns is site-relative (<c>/load.php?...</c>, <c>/images/...</c>), and a document loaded from a string has no
    /// site to be relative to — without it the preview is unstyled text with broken images. The content sits in the
    /// same containers a real page puts it in, since the skin's styles are written against them.
    /// </summary>
    public string ToDocument(Uri site)
    {
        ArgumentNullException.ThrowIfNull(site);

        string head = HeadHtml;
        string @base = $"<base href=\"{WebUtility.HtmlEncode(site.GetLeftPart(UriPartial.Authority))}/\">";
        int open = head.IndexOf("<head>", StringComparison.OrdinalIgnoreCase);
        head = open >= 0 ? head.Insert(open + "<head>".Length, @base) : @base + head;

        string links = string.Concat((Stylesheets ?? [])
            .Select(WebUtility.HtmlEncode)
            .Where(href => !head.Contains($"href=\"{href}\"", StringComparison.Ordinal))
            .Select(href => $"<link rel=\"stylesheet\" href=\"{href}\">"));
        int close = head.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        head = close >= 0 ? head.Insert(close, links) : head + links;

        return head +
               "<div class=\"mw-page-container\"><div class=\"mw-page-container-inner\"><div class=\"mw-content-container\">" +
               "<main id=\"content\" class=\"mw-body\"><header class=\"mw-body-header vector-page-titlebar\">" +
               "<h1 id=\"firstHeading\" class=\"firstHeading mw-first-heading\"><span class=\"mw-page-title-main\">" +
               WebUtility.HtmlEncode(Title) + "</span></h1></header>" +
               "<div id=\"bodyContent\" class=\"vector-body\"><div id=\"mw-content-text\" class=\"mw-body-content\">" +
               BodyHtml + "</div>" + CategoriesHtml + "</div></main></div></div></div></body></html>";
    }

    /// <summary>
    /// The stylesheets a page view of the wiki links, read from that page's HTML: only <c>load.php</c> addresses on the
    /// wiki itself, so nothing else a page might link (or anything somebody managed to put in one) is carried over.
    /// </summary>
    public static IReadOnlyList<string> SkinStylesheets(string pageHtml)
    {
        ArgumentNullException.ThrowIfNull(pageHtml);

        var found = new List<string>();
        foreach (Match link in Regex.Matches(pageHtml, "<link\\b[^>]*>", RegexOptions.IgnoreCase))
        {
            if (!Regex.IsMatch(link.Value, "\\brel=\"stylesheet\"", RegexOptions.IgnoreCase)) continue;
            Match href = Regex.Match(link.Value, "\\bhref=\"([^\"]+)\"", RegexOptions.IgnoreCase);
            if (!href.Success) continue;

            string address = WebUtility.HtmlDecode(href.Groups[1].Value);
            if (address.StartsWith("/load.php?", StringComparison.Ordinal) && !found.Contains(address))
                found.Add(address);
        }

        return found;
    }
}
