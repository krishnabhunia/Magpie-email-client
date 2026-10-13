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
    /// <summary>Design DD1 (R3): "deletes in 3 days" on this email's header, when it has an auto-delete timer.</summary>
    public string? DeleteNote { get; init; }
    /// <summary>How close that is (red / amber / grey, as the list's tag).</summary>
    public DeleteUrgency DeleteUrgency { get; init; }
    /// <summary>Shown when pointing at the note: the full date and the rule.</summary>
    public string DeleteTip { get; init; } = "";
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

    private static readonly Regex OwnColours = new(@"\b(?:bg)?color\s*=|(?<![-\w])(?:background(?:-color)?|color)\s*:|\bbackground\s*=", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// True when an email sets its own colours (a background, or text colours). In the dark theme such a message
    /// is shown on a white "paper" card so it stays readable; plain messages are drawn light-on-dark (design B1).
    /// </summary>
    public static bool HasOwnColours(string html) => !string.IsNullOrEmpty(html) && OwnColours.IsMatch(html);

    /// <summary>Page colours for the light and dark theme (design B1).</summary>
    private sealed record Palette(string Page, string Ink, string Body, string Muted, string Line, string Soft, string Border, string Button,
        string Accent, string Link, string AvBg, string AvFg, string MeBg, string MeFg, string Danger, string Scroll);

    private static readonly Palette LightPalette = new("#FFFFFF", "#14181C", "#23282E", "#5A6068", "#ECE9E2", "#F7F6F2", "#DCD8CF", "#FFFFFF",
        "#14606E", "#14606E", "#DFE9E9", "#14606E", "#EDE8F7", "#4B3F86", "#B3261E", "#C9C4B9");
    private static readonly Palette DarkPalette = new("#181C20", "#E8E6E1", "#D9D6D0", "#9AA1A8", "#22272C", "#1E2328", "#2C3238", "#1E2328",
        "#1F7F8C", "#6CC3CF", "#1B2A2E", "#6CC3CF", "#2A2640", "#C4BCF2", "#E0645A", "#3A4148");

    private static string FrameStyle(Palette p) =>
        "html,body{margin:0;padding:0;overflow:hidden}body{font:13.5px/1.6 'IBM Plex Sans','Segoe UI',sans-serif;color:" + p.Body + ";word-wrap:break-word;overflow-wrap:anywhere}" +
        (p == DarkPalette ? "a{color:" + p.Link + "}" : "") + "img{max-width:100%;height:auto}blockquote{margin:0 0 0 .6em;padding-left:.8em;border-left:3px solid " + p.Border + ";color:" + p.Muted + "}" +
        "pre{white-space:pre-wrap}table{max-width:100%}";

    /// <summary>The whole conversation page. <paramref name="dark"/> = the dark theme (design B1).</summary>
    public static RenderResult BuildConversation(string subject, IReadOnlyList<RenderMessage> messages, bool allowRemote, DateTimeOffset now, bool dark = false,
        int hoverDelayMs = 600)
    {
        var p = dark ? DarkPalette : LightPalette;
        var sb = new StringBuilder();
        int blockedTotal = 0;
        sb.Append("""
            <!doctype html><html><head><meta charset="utf-8">
            <meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src data: https: http:; style-src 'unsafe-inline'; script-src 'unsafe-inline'; frame-src 'self' about: data:; font-src data:">
            <style>
            """);
        sb.Append($":root{{--teal:{p.Accent};--ink:{p.Ink};--muted:{p.Muted};--line:{p.Line};--bg:{p.Soft};--page:{p.Page};--border:{p.Border};--btn:{p.Button};--link:{p.Link};--avbg:{p.AvBg};--avfg:{p.AvFg};--mebg:{p.MeBg};--mefg:{p.MeFg}}}\n");
        sb.Append(dark ? ":root{--ddsoon:#F28B82;--ddsoonbg:rgba(224,100,90,.16);--ddweeks:#F0B35A;--ddweeksbg:rgba(240,179,90,.14)}\n"
                       : ":root{--ddsoon:#B3261E;--ddsoonbg:#FBE9E7;--ddweeks:#9A5B00;--ddweeksbg:#FDF1DC}\n");
        sb.Append($"html{{scrollbar-color:{p.Scroll} {p.Page}}}\n");
        sb.Append("""
            html,body{margin:0;padding:0;background:var(--page);color:var(--ink);font:13.5px/1.55 'IBM Plex Sans','Segoe UI',system-ui,sans-serif}
            .wrap{padding:6px 22px 28px}
            .msg{border-bottom:1px solid var(--line)}
            .hdr{display:flex;gap:12px;align-items:flex-start;padding:14px 0 10px;cursor:pointer;user-select:none}
            .av{width:32px;height:32px;flex:0 0 32px;border-radius:50%;background:var(--avbg);color:var(--avfg);font-weight:600;font-size:12px;display:flex;align-items:center;justify-content:center}
            .av.me{background:var(--mebg);color:var(--mefg)}
            .who{flex:1;min-width:0}
            .name{font-weight:600}
            .addr{color:var(--muted);font-weight:400;font-size:12px;margin-left:6px}
            .to{color:var(--muted);font-size:12px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
            .snip{color:var(--muted);font-size:12.5px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
            .date{color:var(--muted);font-size:12px;white-space:nowrap}
            .acts{display:none;gap:4px;margin-left:8px}
            .msg.open .acts{display:flex}
            .acts button{border:1px solid var(--border);background:var(--btn);border-radius:5px;font:inherit;font-size:11.5px;padding:3px 8px;color:var(--ink);cursor:pointer}
            .acts button:hover{background:var(--bg)}
            .body{display:none;padding:0 0 16px 44px}
            .msg.open .body{display:block}
            .msg.open .snip{display:none}
            .msg:not(.open) .to{display:none}
            iframe{width:100%;border:0;display:block;min-height:40px}
            .atts{display:flex;flex-wrap:wrap;gap:8px;margin-top:12px}
            .retry{margin:6px 0 4px}.retry button{border:1px solid var(--teal);background:var(--teal);color:#fff;border-radius:6px;font:inherit;font-size:12.5px;padding:5px 12px;cursor:pointer}
            .att{border:1px solid var(--border);border-radius:6px;padding:6px 10px;font-size:12px;cursor:pointer;background:var(--btn);color:var(--ink);display:flex;gap:8px;align-items:center}
            .att:hover{background:var(--bg)}
            .att .sz{color:var(--muted)}
            .paper{background:#fff;border-radius:10px;padding:12px 14px;margin-top:2px}
            .hm-a,.hm-f{outline-offset:2px}
            .hm-a{cursor:default;border-radius:3px}
            .hm-a:hover,.hm-a.hm-on{text-decoration:underline;text-decoration-color:var(--border);text-underline-offset:3px}
            .hm{position:absolute;z-index:50;width:300px;max-width:calc(100vw - 24px);background:var(--page);color:var(--ink);border:1px solid var(--border);border-radius:10px;box-shadow:0 8px 24px rgba(0,0,0,.18);padding:6px 0;font-size:13px;cursor:default}
            .hm[hidden]{display:none}
            .hm .hm-top{display:flex;gap:10px;align-items:center;padding:8px 14px 10px;border-bottom:1px solid var(--line);margin-bottom:4px}
            .hm .hm-ic{width:30px;height:30px;flex:0 0 30px;border-radius:50%;background:var(--avbg);color:var(--avfg);font-weight:600;font-size:11.5px;display:flex;align-items:center;justify-content:center}
            .hm .hm-ic.file{border-radius:6px;font-size:10px}
            .hm .hm-t{font-weight:600;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
            .hm .hm-s{color:var(--muted);font-size:12px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
            .hm .hm-tx{min-width:0;flex:1}
            .hm button{display:flex;justify-content:space-between;width:100%;border:0;background:none;color:var(--ink);font:inherit;text-align:left;padding:6px 14px;cursor:pointer}
            .hm button:hover,.hm button:focus{background:var(--bg);outline:none}
            .hm button .hm-id{color:var(--muted);font-size:11px;margin-left:12px}
            .hm .hm-sep{height:1px;background:var(--line);margin:4px 0}
            .hm .hm-note{border-top:1px solid var(--line);margin-top:4px;padding:7px 14px 3px;color:var(--teal);font-size:12px}
            .hm .hm-note[hidden]{display:none}
            .dd{display:inline-block;margin-left:8px;padding:1px 8px;border-radius:9px;font-size:11.5px;font-weight:600;white-space:nowrap;vertical-align:1px}
            .dd.soon{color:var(--ddsoon);background:var(--ddsoonbg)}.dd.weeks{color:var(--ddweeks);background:var(--ddweeksbg)}.dd.later{color:var(--muted);background:var(--bg)}
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
            window.addEventListener('resize',function(){ document.querySelectorAll('iframe').forEach(size); hmHide(); });
            // Design HM1: resting the mouse on an address or a file (or right-click / Menu key) opens a small card of actions.
            var HM={timer:0,hide:0,target:null,ctx:null,kbd:false};
            function hmEl(n){ return n&&n.closest?n.closest('.hm-a,.hm-f'):null; }
            function hmIn(n){ return n&&n.closest?n.closest('.hm-a,.hm-f,#hm'):null; }
            function hmCtx(el){ var d=el.dataset; return el.classList.contains('hm-a')?{k:'a',a:d.a,n:d.n,m:+d.m}:{k:'f',m:+d.m,i:+d.i,f:d.f}; }
            function hmAsk(el,kbd){ clearTimeout(HM.hide); if(HM.target) HM.target.classList.remove('hm-on'); HM.target=el; HM.kbd=!!kbd; HM.ctx=hmCtx(el); el.classList.add('hm-on'); post({t:'hmopen',c:HM.ctx}); }
            function hmHide(){ clearTimeout(HM.timer); var c=document.getElementById('hm'); if(c) c.hidden=true; if(HM.target) HM.target.classList.remove('hm-on'); HM.target=null; }
            function hmText(el,cls,t){ var d=document.createElement('div'); d.className=cls; d.textContent=t; el.appendChild(d); return d; }
            function hmShow(o){
              var c=document.getElementById('hm'); if(!HM.target||!c) return;
              c.innerHTML='';
              var top=document.createElement('div'); top.className='hm-top';
              var ic=hmText(top,'hm-ic'+(o.file?' file':''),o.badge||'');
              var tx=document.createElement('div'); tx.className='hm-tx'; hmText(tx,'hm-t',o.title||''); hmText(tx,'hm-s',o.sub||''); top.appendChild(tx);
              c.appendChild(top);
              (o.items||[]).forEach(function(it){
                if(it.sep){ hmText(c,'hm-sep',''); return; }
                var b=document.createElement('button'); b.type='button'; b.setAttribute('role','menuitem');
                var l=document.createElement('span'); l.textContent=it.label; b.appendChild(l);
                b.onclick=function(ev){ ev.stopPropagation(); post({t:'hmdo',id:it.id,c:HM.ctx}); };
                c.appendChild(b);
              });
              var note=hmText(c,'hm-note',''); note.id='hm-note'; note.hidden=true;
              c.hidden=false;
              var r=HM.target.getBoundingClientRect(), w=c.offsetWidth, h=c.offsetHeight;
              var x=Math.max(8,Math.min(r.left,window.innerWidth-w-12)), y=r.bottom+4;
              if(y+h>window.innerHeight-8&&r.top-h-4>8) y=r.top-h-4;
              c.style.left=(x+window.scrollX)+'px'; c.style.top=(y+window.scrollY)+'px';
              if(HM.kbd){ var f=c.querySelector('button'); if(f) f.focus(); }
            }
            function hmNote(t){ var n=document.getElementById('hm-note'); if(!n) return; n.textContent=t; n.hidden=!t; }
            document.addEventListener('mouseover',function(ev){
              var t=hmEl(ev.target);
              if(t){ if(t===HM.target){ clearTimeout(HM.hide); return; } clearTimeout(HM.timer); HM.timer=setTimeout(function(){ hmAsk(t,false); },HM_DELAY); }
              else if(hmIn(ev.target)) clearTimeout(HM.hide);
            });
            document.addEventListener('mouseout',function(ev){
              if(!hmIn(ev.target)||hmIn(ev.relatedTarget)) return;
              clearTimeout(HM.timer); HM.hide=setTimeout(hmHide,300);
            });
            document.addEventListener('contextmenu',function(ev){ var t=hmEl(ev.target); if(!t) return; ev.preventDefault(); hmAsk(t,ev.button!==2); });
            document.addEventListener('keydown',function(ev){
              if(ev.key==='Escape'){ hmHide(); return; }
              var c=document.getElementById('hm'); if(!c||c.hidden) return;
              if(ev.key==='ArrowDown'||ev.key==='ArrowUp'){
                var bs=Array.prototype.slice.call(c.querySelectorAll('button')); if(!bs.length) return;
                var i=bs.indexOf(document.activeElement); i=ev.key==='ArrowDown'?(i+1)%bs.length:(i<=0?bs.length-1:i-1); bs[i].focus(); ev.preventDefault();
              }
            });
            document.addEventListener('mousedown',function(ev){ if(!hmIn(ev.target)) hmHide(); });
            window.addEventListener('scroll',hmHide);
            </script></head><body><div class="wrap">
            """);
        sb.Insert(sb.ToString().IndexOf("function post(", StringComparison.Ordinal), $"var HM_DELAY={Math.Clamp(hoverDelayMs, 50, 1000)};\n");

        foreach (var m in messages)
        {
            var row = m.Row;
            var (bodyHtml, blocked) = m.LoadError != null
                ? ($"<p style=\"color:{p.Danger}\">Couldn't download this message: {Esc(m.LoadError)}</p>", 0)
                : m.Body == null
                    ? ($"<p style=\"color:{p.Muted}\">{Esc(m.LoadingText)}</p>", 0)
                    : SanitizeBody(m.Body.Html, m.InlineImages, allowRemote);
            blockedTotal += blocked;
            // Dark theme: a message with its own colours keeps them, on a white card; plain ones follow the theme.
            var paper = dark && m.Body != null && m.LoadError == null && HasOwnColours(bodyHtml);
            var frameDoc = "<!doctype html><html><head><meta charset=\"utf-8\"><base target=\"_blank\"><style>"
                + FrameStyle(paper ? LightPalette : p) + "</style></head><body>" + bodyHtml + "</body></html>";

            var cls = "msg" + (m.Expanded ? " open" : "") + (row.IsSeen ? "" : " unread");
            sb.Append($"<div class=\"{cls}\" id=\"m{row.Id}\">");
            sb.Append($"<div class=\"hdr\" onclick=\"toggle({row.Id})\">");
            sb.Append($"<div class=\"av{(m.IsMine ? " me" : "")}\">{Esc(Initials(row.Sender))}</div>");
            sb.Append("<div class=\"who\">");
            var fromAttrs = $"class=\"hm-a\" tabindex=\"0\" data-a=\"{Esc(row.FromAddress)}\" data-n=\"{Esc(row.FromName)}\" data-m=\"{row.Id}\"";
            sb.Append($"<div><span {fromAttrs}><span class=\"name\">{Esc(m.IsMine ? "Me" : row.Sender)}</span><span class=\"addr\">{Esc(row.FromAddress)}</span></span></div>");
            sb.Append("<div class=\"to\">to ").Append(People(row.To, row.Id));
            if (!string.IsNullOrEmpty(row.Cc)) sb.Append(" · cc ").Append(People(row.Cc, row.Id));
            sb.Append("</div>");
            sb.Append($"<div class=\"snip\">{Esc(row.Preview)}</div>");
            sb.Append("</div>");
            sb.Append($"<div class=\"date\">{Esc(FriendlyDate(row.Date, now))}");
            if (!string.IsNullOrEmpty(m.DeleteNote))
            {
                var level = m.DeleteUrgency switch { DeleteUrgency.Soon => "soon", DeleteUrgency.Weeks => "weeks", _ => "later" };
                sb.Append($"<span class=\"dd {level}\" title=\"{Esc(m.DeleteTip)}\">🗑 {Esc(m.DeleteNote)}</span>");
            }
            sb.Append("</div>");
            sb.Append("<div class=\"acts\" onclick=\"event.stopPropagation()\">");
            sb.Append($"<button onclick=\"post({{t:'reply',id:{row.Id}}})\">Reply</button>");
            sb.Append($"<button onclick=\"post({{t:'replyall',id:{row.Id}}})\">Reply all</button>");
            sb.Append($"<button onclick=\"post({{t:'forward',id:{row.Id}}})\">Forward</button>");
            sb.Append("</div></div>");
            sb.Append("<div class=\"body\">");
            if (m.LoadError != null)
                sb.Append("<div class=\"retry\"><button onclick=\"post({t:'retry'})\">Try again</button></div>");
            if (paper) sb.Append("<div class=\"paper\">");
            sb.Append($"<iframe id=\"f{row.Id}\" sandbox=\"allow-same-origin allow-popups allow-popups-to-escape-sandbox\" onload=\"hook(this)\" srcdoc=\"{WebUtility.HtmlEncode(frameDoc)}\"></iframe>");
            if (paper) sb.Append("</div>");
            if (m.Body?.Attachments.Count(a => !a.Inline) > 0)
            {
                sb.Append("<div class=\"atts\">");
                foreach (var a in m.Body.Attachments.Where(a => !a.Inline))
                    sb.Append($"<div class=\"att hm-f\" tabindex=\"0\" data-m=\"{row.Id}\" data-i=\"{a.Index}\" data-f=\"{Esc(a.FileName)}\" onclick=\"post({{t:'att',id:{row.Id},i:{a.Index}}})\" onkeydown=\"if(event.key==='Enter')post({{t:'att',id:{row.Id},i:{a.Index}}})\">📎 {Esc(a.FileName)} <span class=\"sz\">{Esc(FormatSize(a.Size))}</span></div>");
                sb.Append("</div>");
            }
            sb.Append("</div></div>");
        }
        sb.Append("</div><div id=\"hm\" class=\"hm\" role=\"menu\" hidden onclick=\"event.stopPropagation()\"></div></body></html>");
        return new RenderResult { Html = sb.ToString(), BlockedImages = blockedTotal };
    }

    /// <summary>Design HM1: each person of a To / Cc line as its own hover target ("Anita Rao, sandeep@x.in").</summary>
    internal static string People(string list, long rowId)
    {
        var parts = new List<string>();
        foreach (var mb in Composer.ParseAddresses(list).Mailboxes)
        {
            var label = string.IsNullOrWhiteSpace(mb.Name) ? mb.Address : mb.Name;
            parts.Add($"<span class=\"hm-a\" tabindex=\"0\" data-a=\"{Esc(mb.Address)}\" data-n=\"{Esc(mb.Name)}\" data-m=\"{rowId}\" title=\"{Esc(mb.Address)}\">{Esc(label)}</span>");
        }
        return parts.Count > 0 ? string.Join(", ", parts) : Esc(list);
    }

    /// <summary>Plain page for empty/error states.</summary>
    /// <summary>
    /// What the reading pane shows the moment another conversation is picked, before its page is built:
    /// the subject and sender we already know from the list, and "Loading…". Injected into the current page
    /// (no navigation), so it appears at once instead of the previous message staying on screen.
    /// </summary>
    /// <summary>What the reading pane shows the moment another conversation is picked (Q38): its subject and sender,
    /// with "Loading…" and placeholder lines only when it has to be downloaded.</summary>
    public static string LoadingBody(string subject, string sender, string when, bool dark = false, bool downloading = true) =>
        "<style>" +
        (dark ? "html,body{background:#181C20}" : "") +
        "@keyframes mg-pulse{0%,100%{opacity:.45}50%{opacity:1}}" +
        "@keyframes mg-spin{to{transform:rotate(360deg)}}" +
        ".mg-l{font:13px 'IBM Plex Sans','Segoe UI',sans-serif;color:" + (dark ? "#E8E6E1" : "#14181C") + ";padding:22px 30px;max-width:760px}" +
        ".mg-l h1{font:600 22px 'IBM Plex Serif',Georgia,serif;margin:0 0 14px}" +
        ".mg-l .who{display:flex;align-items:center;gap:10px;color:" + (dark ? "#9AA1A8" : "#5A6068") + ";margin-bottom:22px}" +
        ".mg-l .spin{width:16px;height:16px;border:2px solid " + (dark ? "#2F5A61" : "#CFE3E7") + ";border-top-color:" + (dark ? "#6CC3CF" : "#14606E") + ";border-radius:50%;animation:mg-spin .8s linear infinite}" +
        ".mg-l .bar{height:11px;border-radius:6px;background:" + (dark ? "#252B31" : "#ECEEF2") + ";margin:10px 0;animation:mg-pulse 1.2s ease-in-out infinite}" +
        "</style>" +
        "<div class=\"mg-l\" role=\"status\" aria-live=\"polite\">" +
        "<h1>" + Esc(subject) + "</h1>" +
        "<div class=\"who\">" + (downloading ? "<span class=\"spin\"></span>" : "") + "<span><b style=\"color:" + (dark ? "#E8E6E1" : "#14181C") + "\">" + Esc(sender) + "</b> · " + Esc(when) + (downloading ? " · Loading…" : "") + "</span></div>" +
        (downloading ? "<div class=\"bar\" style=\"width:92%\"></div><div class=\"bar\" style=\"width:84%\"></div><div class=\"bar\" style=\"width:88%\"></div><div class=\"bar\" style=\"width:52%\"></div>" : "") +
        "</div>";

    public static string Placeholder(string title, string detail, bool dark = false) => $$"""
        <!doctype html><html><head><meta charset="utf-8"><style>
        body{margin:0;height:100vh;display:flex;align-items:center;justify-content:center;background:{{(dark ? "#181C20" : "#fff")}};font:13px 'IBM Plex Sans','Segoe UI',sans-serif;color:{{(dark ? "#9AA1A8" : "#5A6068")}};text-align:center}
        h2{font:600 17px 'IBM Plex Serif',Georgia,serif;color:{{(dark ? "#E8E6E1" : "#14181C")}};margin:0 0 6px}
        </style></head><body><div><h2>{{Esc(title)}}</h2><div>{{Esc(detail)}}</div></div></body></html>
        """;
}
