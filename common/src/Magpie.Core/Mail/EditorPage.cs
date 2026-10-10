namespace Magpie.Core.Mail;

/// <summary>The rich-text editor used by the compose window (our own page — the only place scripts run).
/// Shared by the Windows app (WebView2) and Magpie for Mac (WKWebView, see <see cref="ForMac"/>).</summary>
public static class EditorPage
{
    /// <summary>The same page with the Mac keys: ⌘↩ sends and ⌘K adds a link (Ctrl still works too).</summary>
    public static string ForMac => Html.Replace("e.ctrlKey && ", "(e.ctrlKey || e.metaKey) && ", StringComparison.Ordinal);

    public const string Html = """
<!doctype html>
<html><head><meta charset="utf-8">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src data:; style-src 'unsafe-inline'; script-src 'unsafe-inline'; font-src data:">
<style>
html,body{margin:0;padding:0;background:#fff;height:100%}
body{font:14px/1.6 'IBM Plex Sans','Segoe UI',system-ui,sans-serif;color:#23282E}
#ed{min-height:100%;padding:14px 20px 40px;outline:none;box-sizing:border-box;word-wrap:break-word;overflow-wrap:anywhere}
#ed p{margin:0 0 .6em}
#ed:empty:before{content:attr(data-ph);color:#9AA0A6}
blockquote{margin:0 0 0 .8ex;border-left:2px solid #DCD8CF;padding-left:1ex;color:#5A6068}
.magpie-quote{color:#5A6068}
.magpie-signature{color:#5A6068}
img{max-width:100%}
a{color:#14606E}
html.dark,html.dark body{background:#181C20}
html.dark body{color:#D9D6D0}
html.dark #ed:empty:before{color:#6F7780}
html.dark blockquote{border-left-color:#2C3238;color:#9AA1A8}
html.dark .magpie-signature{color:#9AA1A8}
html.dark a{color:#6CC3CF}
html.dark .magpie-quote{background:#fff;color:#23282E;border-radius:8px;padding:8px 12px}
html.dark .magpie-quote blockquote{border-left-color:#DCD8CF;color:#5A6068}
html.dark .magpie-quote a{color:#14606E}
</style></head>
<body><div id="ed" contenteditable="true" spellcheck="true" data-ph="Write your message…"></div>
<script>
const ed = document.getElementById('ed');
let saved = null;
let rewrite = null;
function post(o){ try{ window.chrome.webview.postMessage(JSON.stringify(o)); }catch(e){} }
function setHtml(h){
  ed.innerHTML = h && h.length ? h : '<p><br></p>';
  const r = document.createRange();
  const first = ed.firstChild || ed;
  r.setStart(first, 0); r.collapse(true);
  const s = window.getSelection(); s.removeAllRanges(); s.addRange(r);
  saved = r.cloneRange();
  ed.focus();
}
function getHtml(){ return ed.innerHTML; }
function setDark(on){ document.documentElement.classList.toggle('dark', !!on); }
function restore(){ if(saved){ const s = window.getSelection(); s.removeAllRanges(); s.addRange(saved); } }
function fmt(c, v){ ed.focus(); restore(); document.execCommand(c, false, v === undefined ? null : v); post({t:'dirty'}); }
document.addEventListener('selectionchange', function(){
  const s = window.getSelection();
  if (s.rangeCount && ed.contains(s.anchorNode)) { saved = s.getRangeAt(0).cloneRange(); post({t:'sel', text: s.toString()}); }
});
function esc(t){ return t.replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;'); }
function toHtml(t){ return t.replace(/\r\n/g,'\n').split(/\n{2,}/).map(function(p){ return '<p>' + esc(p).replace(/\n/g,'<br>') + '</p>'; }).join(''); }
function inlineHtml(t){ return esc(t).replace(/\r?\n/g,'<br>'); }
function topChild(n){ while(n && n.parentNode !== ed) n = n.parentNode; return n; }
function cmd(mode, text){
  if (mode === 'captureRewrite') {
    if (!saved || saved.collapsed || saved.toString() !== text) return false;
    rewrite = { range: saved.cloneRange(), html: ed.innerHTML };
    return true;
  }
  if (mode === 'replaceSelection') {
    if (!rewrite || rewrite.html !== ed.innerHTML) return false;
    const s = window.getSelection(); s.removeAllRanges(); s.addRange(rewrite.range);
    ed.focus(); document.execCommand('insertHTML', false, inlineHtml(text));
    rewrite = null;
  }
  else if (mode === 'insert') { ed.focus(); restore(); document.execCommand('insertHTML', false, toHtml(text)); }
  else if (mode === 'replaceBody') {
    ed.focus();
    const stop = topChild(ed.querySelector('.magpie-signature, .magpie-quote'));
    const r = document.createRange();
    r.setStart(ed, 0);
    if (stop) r.setEndBefore(stop); else r.setEnd(ed, ed.childNodes.length);
    const s = window.getSelection(); s.removeAllRanges(); s.addRange(r);
    document.execCommand('insertHTML', false, toHtml(text));
  }
  post({t:'dirty'});
  return true;
}
ed.addEventListener('input', function(){ post({t:'dirty'}); });
document.addEventListener('keydown', function(e){
  if (e.ctrlKey && e.key === 'Enter') { e.preventDefault(); post({t:'send'}); }
  else if (e.ctrlKey && (e.key === 'k' || e.key === 'K')) { e.preventDefault(); post({t:'link'}); }
});
document.addEventListener('dragover', function(e){ e.preventDefault(); });
document.addEventListener('drop', function(e){ e.preventDefault(); });
post({t:'ready'});
</script>
</body></html>
""";
}
