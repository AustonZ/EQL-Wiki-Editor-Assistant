using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EQLWikiEditorAssistant.Wiki.MediaWiki;

/// <summary>
/// The scripts a page view of the wiki runs, and the settings they read — what a preview needs to show the page as a
/// reader sees it (user, 2026-10-12): the item level slider, the damage bonus and ratio the site's own script works
/// out, anything else the wiki computes in the reader's browser.
///
/// **<c>action=parse</c> leaves these out**, as it leaves out the skin's stylesheets: the slider and the skin's scripts
/// are added to every page *view* (they are on <c>Special:BlankPage</c> too), not by anything in the text. So they are
/// read from one page view, as the stylesheets are, and loaded by the preview itself.
/// </summary>
/// <param name="Modules">The script modules to load, by name.</param>
/// <param name="ConfigJson">Settings to give <c>mw.config</c> first, as a JSON object.</param>
public sealed record PageScripts(IReadOnlyList<string> Modules, string ConfigJson)
{
    /// <summary>
    /// **The editor's own modules are left out**: they add edit links and a loader for an editor, and say nothing about
    /// how the page looks. A preview is not a place to edit, and its links open outside the review anyway.
    /// </summary>
    private static readonly string[] SkippedModulePrefixes = ["ext.visualEditor."];

    /// <summary>
    /// The wiki's own settings that the skin's scripts read — era filtering, the client-data API, the slider's default
    /// level — copied over. **Not the ones about the page that was viewed**: whether it is verified, its talk page.
    /// Those would describe <c>Special:BlankPage</c>, not the page being previewed. MediaWiki's own settings about the
    /// page (its title, its namespace) come with the rendering already.
    /// </summary>
    private static readonly string[] CopiedConfigPrefixes = ["wgEQL", "wgItemLevel", "wgSpellLevel"];

    private static readonly string[] SkippedConfigPrefixes = ["wgEQLTalk", "wgEQLPageVerified"];

    /// <summary>
    /// Reads the scripts and settings from a page view's HTML: the <c>RLPAGEMODULES</c> list the page asks its loader
    /// for, and <c>RLCONF</c>, the settings. Null when the page carries no module list, so a preview goes without
    /// scripts rather than with a guess at them.
    /// </summary>
    public static PageScripts? Read(string pageHtml)
    {
        ArgumentNullException.ThrowIfNull(pageHtml);

        if (JsonAfter(pageHtml, "RLPAGEMODULES=") is not { ValueKind: JsonValueKind.Array } modules) return null;

        List<string> names = [.. modules.EnumerateArray()
            .Where(m => m.ValueKind == JsonValueKind.String)
            .Select(m => m.GetString()!)
            .Where(name => !SkippedModulePrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal)))
            .Distinct(StringComparer.Ordinal)];

        var config = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (JsonAfter(pageHtml, "RLCONF=") is { ValueKind: JsonValueKind.Object } all)
        {
            foreach (JsonProperty setting in all.EnumerateObject())
            {
                if (CopiedConfigPrefixes.Any(p => setting.Name.StartsWith(p, StringComparison.Ordinal)) &&
                    !SkippedConfigPrefixes.Any(p => setting.Name.StartsWith(p, StringComparison.Ordinal)))
                    config[setting.Name] = setting.Value.Clone();
            }
        }

        return new PageScripts(names, JsonSerializer.Serialize(config));
    }

    /// <summary>
    /// The script that loads them, for the end of the preview's body. It waits on the page's own loader queue
    /// (<c>RLQ</c>), which the rendering's head sets up, so it runs once the loader exists. <c>&lt;/</c> is escaped so no
    /// value can close the script element early.
    /// </summary>
    public string LoaderScript()
    {
        string modules = JsonSerializer.Serialize(Modules);
        string body = $"(RLQ=window.RLQ||[]).push(function(){{mw.config.set({ConfigJson});mw.loader.load({modules});}});";
        return "<script>" + body.Replace("</", "<\\/", StringComparison.Ordinal) + "</script>";
    }

    /// <summary>The first JSON value following <paramref name="marker"/> in the page, or null.</summary>
    private static JsonElement? JsonAfter(string html, string marker)
    {
        int at = html.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0) return null;

        byte[] rest = Encoding.UTF8.GetBytes(html[(at + marker.Length)..]);
        var reader = new Utf8JsonReader(rest, new JsonReaderOptions { AllowTrailingCommas = true });
        try
        {
            return JsonElement.ParseValue(ref reader);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
