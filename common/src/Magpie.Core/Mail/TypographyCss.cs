using System.Globalization;
using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Magpie.Core.Settings;

namespace Magpie.Core.Mail;

/// <summary>CSS for Magpie-owned reader text. Sender content remains sandboxed and opt-in font overrides affect display only.</summary>
public static class TypographyCss
{
    public static string Family(string family)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in family)
        {
            if (c is '"' or '\\' or '<' or '>' or '&' || char.IsControl(c))
                sb.Append('\\').Append(((int)c).ToString("x", CultureInfo.InvariantCulture)).Append(' ');
            else sb.Append(c);
        }
        return sb.Append("\",\"Segoe UI\",Arial,sans-serif").ToString();
    }

    private static string Rule(string selector, TextStyle style, bool important = false)
    {
        var end = important ? "!important" : "";
        return selector + "{font-family:" + Family(style.Family) + end + ";font-size:"
            + style.Size.ToString("0.#", CultureInfo.InvariantCulture) + "px" + end + ";}";
    }

    public static string ReaderChrome(TypographySettings? settings)
    {
        if (settings == null) return "";
        var sb = new StringBuilder();
        foreach (var (id, selector) in new[]
        {
            ("readSender", ".name"), ("readAddress", ".addr,.to"), ("readDate", ".date,.dd"),
            ("fileName", ".att .filename"), ("fileInfo", ".att .sz"),
            ("toolbar", ".acts button,.retry button"), ("menu", ".hm,.hm button"),
            ("helper", ".snip,.hm .hm-s"),
        })
            if (settings.HasOverride(id)) sb.Append(Rule(selector, settings.Resolve(id)));
        return sb.ToString();
    }

    public static string ReaderBody(TypographySettings? settings)
    {
        if (settings?.OverrideReceivedFonts != true) return "";
        // Explicit selectors override sender-chosen fonts without allowing any sender script or changing the stored HTML.
        return Rule("html,body,body *", settings.Resolve("readBody"), true)
            + Rule("h1,h1 *", settings.Resolve("readH1"), true)
            + Rule("h2,h2 *", settings.Resolve("readH2"), true)
            + Rule("h3,h3 *", settings.Resolve("readH3"), true)
            + Rule("blockquote,blockquote *", settings.Resolve("readQuote"), true);
    }

    /// <summary>A disposable display copy. Inline important fonts beat even sender inline/shorthand important rules.</summary>
    public static string ReaderContent(string html, TypographySettings? settings)
    {
        if (settings?.OverrideReceivedFonts != true || string.IsNullOrWhiteSpace(html)) return html;
        var parser = new HtmlParser();
        var document = parser.ParseDocument("");
        var holder = document.CreateElement("div");
        foreach (var node in parser.ParseFragment(html, holder).ToArray()) holder.AppendChild(node);
        foreach (var element in holder.QuerySelectorAll("*"))
        {
            if (element.LocalName is "style" or "meta" or "link" or "head") continue;
            var role = "readBody";
            for (IElement? ancestor = element; ancestor != null && ancestor != holder; ancestor = ancestor.ParentElement)
            {
                var specific = ancestor.LocalName switch { "h1" => "readH1", "h2" => "readH2", "h3" => "readH3", "blockquote" => "readQuote", _ => null };
                if (specific == null) continue;
                role = specific;
                break;
            }
            var style = settings.Resolve(role);
            element.SetAttribute("style", (element.GetAttribute("style") ?? "") + ";font-family:" + Family(style.Family)
                + "!important;font-size:" + style.Size.ToString("0.#", CultureInfo.InvariantCulture) + "px!important;");
        }
        return holder.InnerHtml;
    }
}
