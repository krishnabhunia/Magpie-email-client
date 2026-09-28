using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Ganss.Xss;
using Magpie.Core.Models;

namespace Magpie.Core.Mail;

public sealed class RenderMessage
{
    public MessageRow Row { get; init; } = new();
    public MessageBody? Body { get; init; }
    public Dictionary<string, string> InlineImages { get; init; } = new();
    public bool Expanded { get; init; }
    public bool IsMine { get; init; }
    /// <summary>Shown while the body is still being downloaded (design R1), e.g. "Downloading from Gmail…".</summary>
    public string LoadingText { get; init; } = "Downloading this message…";
    /// <summary>Why the body couldn't be downloaded; the page then offers "Try again".</summary>
    public string? LoadError { get; init; }
}

public sealed class RenderResult
{
    public string Html { get; init; } = "";
    public int BlockedImages { get; init; }
}

/// <summary>
/// Builds the conversation page shown in the reading pane (WebView2).
/// Each email body is sanitised (no scripts, forms, frames, event handlers) and placed in its own
/// sandboxed iframe (no allow-scripts) so its CSS cannot leak into the page or other messages.
/// Remote images are blocked unless allowed; the count is reported so the UI can offer "Load images".
/// </summary>
public static class HtmlRenderer
{
    private const string Pixel = "data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7";

    private static HtmlSanitizer CreateSanitizer()
    {
        var s = new HtmlSanitizer();
        s.AllowedSchemes.Clear();
        foreach (var scheme in new[] { "http", "https", "mailto", "tel", "cid", "data" }) s.AllowedSchemes.Add(scheme);
        s.AllowedTags.Remove("form");
        s.AllowedTags.Remove("input");
        s.AllowedTags.Remove("button");
        s.AllowedTags.Remove("textarea");
        s.AllowedTags.Remove("select");
        s.AllowedTags.Add("center");
        s.AllowedTags.Add("style");
        s.AllowedTags.Add("font");
        s.AllowedAttributes.Add("bgcolor");
        s.AllowedAttributes.Add("color");
        s.AllowedAttributes.Add("face");
        s.AllowedAttributes.Add("size");
        s.AllowedAttributes.Add("background");
        s.AllowedAttributes.Add("valign");
        s.AllowedAttributes.Add("align");
        s.AllowedAttributes.Add("class");
        s.AllowedAttributes.Add("id");
        s.AllowedAttributes.Add("data-magpie-src");
        s.AllowedCssProperties.Add("display");
        s.AllowedCssProperties.Add("mso-line-height-rule");
        s.AllowedAtRules.Add(AngleSharp.Css.Dom.CssRuleType.Media);
        s.AllowedAtRules.Add(AngleSharp.Css.Dom.CssRuleType.FontFace);
        s.KeepChildNodes = true;
        return s;
    }

    [ThreadStatic] private static HtmlSanitizer? _sanitizer;
    private static HtmlSanitizer Sanitizer => _sanitizer ??= CreateSanitizer();

    private static readonly Regex ImgSrc = new(@"(<img\b[^>]*?\s)src\s*=\s*(""([^""]*)""|'([^']*)'|([^\s>]+))", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CssUrl = new(@"url\(\s*(?:&quot;|['""])?\s*(https?:[^'"")&]+?)\s*(?:&quot;|['""])?\s*\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Unwanted = new(@"<(script|title|template|object|embed|applet)\b[\s\S]*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BgAttr = new(@"\sbackground\s*=\s*(""https?:[^""]*""|'https?:[^']*')", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Sanitises one email body and resolves/blocks images. Returns safe HTML for an iframe srcdoc.</summary>
    public static (string html, int blocked) SanitizeBody(string html, IReadOnlyDictionary<string, string> inlineImages, bool allowRemote)
    {
        if (string.IsNullOrWhiteSpace(html)) return ("", 0);
        string clean;
        try { clean = Sanitizer.Sanitize(Unwanted.Replace(html, "")); }
        catch (Exception ex)
        {
            Log.Warn("sanitizer failed, falling back to text: " + ex.Message);
            clean = MimeText.TextToHtml(MimeText.HtmlToText(html));
        }

        int blocked = 0;
        clean = ImgSrc.Replace(clean, m =>
        {
            var src = WebUtility.HtmlDecode(m.Groups[3].Success ? m.Groups[3].Value : m.Groups[4].Success ? m.Groups[4].Value : m.Groups[5].Value).Trim();
            if (src.StartsWith("cid:", StringComparison.OrdinalIgnoreCase))
            {
                var cid = src[4..].Trim('<', '>');
                return inlineImages.TryGetValue(cid, out var data) ? $"{m.Groups[1].Value}src=\"{data}\"" : $"{m.Groups[1].Value}src=\"{Pixel}\"";
            }
            if (src.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)) return m.Value;
            if (src.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                if (allowRemote) return m.Value;
                blocked++;
                return $"{m.Groups[1].Value}src=\"{Pixel}\" data-magpie-src=\"{WebUtility.HtmlEncode(src)}\"";
            }
            return $"{m.Groups[1].Value}src=\"{Pixel}\"";
        });
        if (!allowRemote)
        {
            clean = CssUrl.Replace(clean, _ => { blocked++; return "none"; });
            clean = BgAttr.Replace(clean, _ => { blocked++; return ""; });
        }
        return (clean, blocked);
    }

    private static string Esc(string? s) => WebUtility.HtmlEncode(s ?? "");

    public static string Initials(string name)
    {
        var parts = Regex.Split(name.Trim().Trim('"'), @"[\s._@-]+").Where(p => p.Length > 0 && char.IsLetter(p[0])).ToList();
        if (parts.Count == 0) return "?";
        if (parts.Count == 1) return parts[0][..1].ToUpperInvariant();
        return (parts[0][..1] + parts[1][..1]).ToUpperInvariant();
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        <= 0 => "",
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes / 1024.0 / 1024.0:0.0} MB",
    };

    public static string FriendlyDate(DateTimeOffset d, DateTimeOffset now)
    {
        var local = d.ToLocalTime();
        var n = now.ToLocalTime();
        if (local.Date == n.Date) return local.ToString("HH:mm");
        if (local.Date == n.Date.AddDays(-1)) return "Yesterday " + local.ToString("HH:mm");
        if (local.Year == n.Year) return local.ToString("d MMM, HH:mm");
        return local.ToString("d MMM yyyy");
    }

    /// <summary>The whole conversation page.</summary>
    public static RenderResult BuildConversation(string subject, IReadOnlyList<RenderMessage> messages, bool allowRemote, DateTimeOffset now)
    {
        var sb = new StringBuilder();
        int blockedTotal = 0;
        sb.Append("""
            <!doctype html><html><head><meta charset="utf-8">
            <meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src data: https: http:; style-src 'unsafe-inline'; script-src 'unsafe-inline'; frame-src 'self' about: data:; font-src data:">
            <style>
            :root{--teal:#14606E;--ink:#14181C;--muted:#5A6068;--line:#ECE9E2;--bg:#F7F6F2}
            html,body{margin:0;padding:0;background:#fff;color:var(--ink);font:13.5px/1.55 'IBM Plex Sans','Segoe UI',system-ui,sans-serif}
            .wrap{padding:6px 22px 28px}
            .msg{border-bottom:1px solid var(--line)}
            .hdr{display:flex;gap:12px;align-items:flex-start;padding:14px 0 10px;cursor:pointer;user-select:none}
            .av{width:32px;height:32px;flex:0 0 32px;border-radius:50%;background:#DFE9E9;color:var(--teal);font-weight:600;font-size:12px;display:flex;align-items:center;justify-content:center}
            .av.me{background:#EDE8F7;color:#4B3F86}
            .who{flex:1;min-width:0}
            .name{font-weight:600}
            .addr{color:var(--muted);font-weight:400;font-size:12px;margin-left:6px}
            .to{color:var(--muted);font-size:12px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
            .snip{color:var(--muted);font-size:12.5px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
            .date{color:var(--muted);font-size:12px;white-space:nowrap}
            .acts{display:none;gap:4px;margin-left:8px}
            .msg.open .acts{display:flex}
            .acts button{border:1px solid #DCD8CF;background:#fff;border-radius:5px;font:inherit;font-size:11.5px;padding:3px 8px;color:var(--ink);cursor:pointer}
            .acts button:hover{background:var(--bg)}
            .body{display:none;padding:0 0 16px 44px}
            .msg.open .body{display:block}
            .msg.open .snip{display:none}
            .msg:not(.open) .to{display:none}
            iframe{width:100%;border:0;display:block;min-height:40px}
            .atts{display:flex;flex-wrap:wrap;gap:8px;margin-top:12px}
            .retry{margin:6px 0 4px}.retry button{border:1px solid #14606E;background:#14606E;color:#fff;border-radius:6px;font:inherit;font-size:12.5px;padding:5px 12px;cursor:pointer}
            .att{border:1px solid #DCD8CF;border-radius:6px;padding:6px 10px;font-size:12px;cursor:pointer;background:#fff;display:flex;gap:8px;align-items:center}
            .att:hover{background:var(--bg)}
            .att .sz{color:var(--muted)}
            .unread .name::before{content:'';display:inline-block;width:7px;height:7px;border-radius:50%;background:var(--teal);margin-right:6px;vertical-align:1px}
            </style>
            <script>
            function post(o){ try{ window.chrome.webview.postMessage(JSON.stringify(o)); }catch(e){} }
            function toggle(id){ var el=document.getElementById('m'+id); el.classList.toggle('open'); if(el.classList.contains('open')) size(document.getElementById('f'+id)); }
            function size(f){ if(!f) return; try{ var d=f.contentDocument; if(!d||!d.body) return; f.style.height=(Math.max(d.documentElement.scrollHeight,d.body.scrollHeight)+4)+'px'; }catch(e){} }
            function hook(f){
              size(f);
              try{
                var d=f.contentDocument;
                d.addEventListener('click',function(ev){ var a=ev.target.closest('a'); if(a&&a.href){ ev.preventDefault(); post({t:'link',href:a.href}); } },true);
                new ResizeObserver(function(){ size(f); }).observe(d.body);
                Array.prototype.forEach.call(d.images,function(i){ i.addEventListener('load',function(){ size(f); }); });
              }catch(e){}
            }
            window.addEventListener('resize',function(){ document.querySelectorAll('iframe').forEach(size); });
            </script></head><body><div class="wrap">
            """);

        foreach (var m in messages)
        {
            var row = m.Row;
            var (bodyHtml, blocked) = m.LoadError != null
                ? ($"<p style=\"color:#B3261E\">Couldn't download this message: {Esc(m.LoadError)}</p>", 0)
                : m.Body == null
                    ? ($"<p style=\"color:#5A6068\">{Esc(m.LoadingText)}</p>", 0)
                    : SanitizeBody(m.Body.Html, m.InlineImages, allowRemote);
            blockedTotal += blocked;

            var frameDoc = """
                <!doctype html><html><head><meta charset="utf-8"><base target="_blank">
                <style>html,body{margin:0;padding:0;overflow:hidden}body{font:13.5px/1.6 'IBM Plex Sans','Segoe UI',sans-serif;color:#23282E;word-wrap:break-word;overflow-wrap:anywhere}img{max-width:100%;height:auto}blockquote{margin:0 0 0 .6em;padding-left:.8em;border-left:3px solid #DCD8CF;color:#5A6068}pre{white-space:pre-wrap}table{max-width:100%}</style>
                </head><body>
                """ + bodyHtml + "</body></html>";

            var cls = "msg" + (m.Expanded ? " open" : "") + (row.IsSeen ? "" : " unread");
            sb.Append($"<div class=\"{cls}\" id=\"m{row.Id}\">");
            sb.Append($"<div class=\"hdr\" onclick=\"toggle({row.Id})\">");
            sb.Append($"<div class=\"av{(m.IsMine ? " me" : "")}\">{Esc(Initials(row.Sender))}</div>");
            sb.Append("<div class=\"who\">");
            sb.Append($"<div><span class=\"name\">{Esc(m.IsMine ? "Me" : row.Sender)}</span><span class=\"addr\">{Esc(row.FromAddress)}</span></div>");
            var to = row.To + (string.IsNullOrEmpty(row.Cc) ? "" : " · cc " + row.Cc);
            sb.Append($"<div class=\"to\">to {Esc(to)}</div>");
            sb.Append($"<div class=\"snip\">{Esc(row.Preview)}</div>");
            sb.Append("</div>");
            sb.Append($"<div class=\"date\">{Esc(FriendlyDate(row.Date, now))}</div>");
            sb.Append("<div class=\"acts\" onclick=\"event.stopPropagation()\">");
            sb.Append($"<button onclick=\"post({{t:'reply',id:{row.Id}}})\">Reply</button>");
            sb.Append($"<button onclick=\"post({{t:'replyall',id:{row.Id}}})\">Reply all</button>");
            sb.Append($"<button onclick=\"post({{t:'forward',id:{row.Id}}})\">Forward</button>");
            sb.Append("</div></div>");
            sb.Append("<div class=\"body\">");
            if (m.LoadError != null)
                sb.Append("<div class=\"retry\"><button onclick=\"post({t:'retry'})\">Try again</button></div>");
            sb.Append($"<iframe id=\"f{row.Id}\" sandbox=\"allow-same-origin allow-popups allow-popups-to-escape-sandbox\" onload=\"hook(this)\" srcdoc=\"{WebUtility.HtmlEncode(frameDoc)}\"></iframe>");
            if (m.Body?.Attachments.Count(a => !a.Inline) > 0)
            {
                sb.Append("<div class=\"atts\">");
                foreach (var a in m.Body.Attachments.Where(a => !a.Inline))
                    sb.Append($"<div class=\"att\" title=\"Open\" onclick=\"post({{t:'att',id:{row.Id},i:{a.Index}}})\">📎 {Esc(a.FileName)} <span class=\"sz\">{Esc(FormatSize(a.Size))}</span></div>");
                sb.Append("</div>");
            }
            sb.Append("</div></div>");
        }
        sb.Append("</div></body></html>");
        return new RenderResult { Html = sb.ToString(), BlockedImages = blockedTotal };
    }

    /// <summary>Plain page for empty/error states.</summary>
    /// <summary>
    /// What the reading pane shows the moment another conversation is picked, before its page is built:
    /// the subject and sender we already know from the list, and "Loading…". Injected into the current page
    /// (no navigation), so it appears at once instead of the previous message staying on screen.
    /// </summary>
    public static string LoadingBody(string subject, string sender, string when) =>
        "<style>" +
        "@keyframes mg-pulse{0%,100%{opacity:.45}50%{opacity:1}}" +
        "@keyframes mg-spin{to{transform:rotate(360deg)}}" +
        ".mg-l{font:13px 'IBM Plex Sans','Segoe UI',sans-serif;color:#14181C;padding:22px 30px;max-width:760px}" +
        ".mg-l h1{font:600 22px 'IBM Plex Serif',Georgia,serif;margin:0 0 14px}" +
        ".mg-l .who{display:flex;align-items:center;gap:10px;color:#5A6068;margin-bottom:22px}" +
        ".mg-l .spin{width:16px;height:16px;border:2px solid #CFE3E7;border-top-color:#14606E;border-radius:50%;animation:mg-spin .8s linear infinite}" +
        ".mg-l .bar{height:11px;border-radius:6px;background:#ECEEF2;margin:10px 0;animation:mg-pulse 1.2s ease-in-out infinite}" +
        "</style>" +
        "<div class=\"mg-l\" role=\"status\" aria-live=\"polite\">" +
        "<h1>" + Esc(subject) + "</h1>" +
        "<div class=\"who\"><span class=\"spin\"></span><span><b style=\"color:#14181C\">" + Esc(sender) + "</b> · " + Esc(when) + " · Loading…</span></div>" +
        "<div class=\"bar\" style=\"width:92%\"></div><div class=\"bar\" style=\"width:84%\"></div><div class=\"bar\" style=\"width:88%\"></div><div class=\"bar\" style=\"width:52%\"></div>" +
        "</div>";

    public static string Placeholder(string title, string detail) => $$"""
        <!doctype html><html><head><meta charset="utf-8"><style>
        body{margin:0;height:100vh;display:flex;align-items:center;justify-content:center;background:#fff;font:13px 'IBM Plex Sans','Segoe UI',sans-serif;color:#5A6068;text-align:center}
        h2{font:600 17px 'IBM Plex Serif',Georgia,serif;color:#14181C;margin:0 0 6px}
        </style></head><body><div><h2>{{Esc(title)}}</h2><div>{{Esc(detail)}}</div></div></body></html>
        """;
}
