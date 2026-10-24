using System.Text.Json;
using Magpie.Core.Settings;

namespace Magpie.Core.Mail;

/// <summary>The rich-text editor used by the compose window (our own page — the only place scripts run).
/// Shared by the Windows app (WebView2) and Magpie for Mac (WKWebView, see <see cref="ForMac"/>).</summary>
public static class EditorPage
{
    /// <summary>Apply Windows writing defaults after setHtml; saved drafts keep their existing visible formatting.</summary>
    public static string TypographyScript(TypographySettings settings, bool preserveExisting)
    {
        object Style(string id) { var s = settings.Resolve(id); return new { family = s.Family, size = s.Size, overrideExisting = settings.HasOverride(id) }; }
        return "setTypography(" + JsonSerializer.Serialize(Style("composeBody")) + ","
            + JsonSerializer.Serialize(Style("signature")) + "," + JsonSerializer.Serialize(Style("composeQuote")) + ","
            + (preserveExisting ? "true" : "false") + ")";
    }
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
let typography = null;
function fontCss(style){ return 'font-family:' + JSON.stringify(style.family) + ';font-size:' + style.size + 'px;font-weight:400'; }
function applyFont(el, style, normal){ el.style.fontFamily=style.family; el.style.fontSize=style.size+'px'; if(normal!==false) el.style.fontWeight='400'; }
function applyProtectedFont(el, style){
  // A saved rich signature and a received quote keep their own formatting and emphasis.
  if(style.overrideExisting){
    applyFont(el,style,false);el.querySelectorAll('*').forEach(function(n){applyFont(n,style,false);});
  }else{
    if(!el.style.fontFamily) el.style.fontFamily=style.family;
    if(!el.style.fontSize) el.style.fontSize=style.size+'px';
  }
}
function preserveFont(el){
  const style=window.getComputedStyle(el);
  el.style.fontFamily=style.fontFamily; el.style.fontSize=style.fontSize; el.style.fontWeight=style.fontWeight;
}
function setTypography(body, signature, quote, preserveExisting){
  // Freeze old text's current appearance before changing inherited defaults; never reformat an existing styled draft.
  if(preserveExisting){
    Array.from(ed.querySelectorAll('*')).forEach(preserveFont);
    Array.from(ed.childNodes).forEach(function(n){
      if(n.nodeType===Node.TEXT_NODE && n.textContent){ const span=document.createElement('span'); preserveFont(ed); span.style.fontFamily=ed.style.fontFamily; span.style.fontSize=ed.style.fontSize; span.style.fontWeight=ed.style.fontWeight; n.replaceWith(span); span.appendChild(n); }
    });
  }
  typography={body:body,signature:signature,quote:quote};
  applyFont(ed,body);
  if(!preserveExisting){
    Array.from(ed.children).forEach(function(el){
      if(el.matches('.magpie-signature')) applyProtectedFont(el,signature);
      else if(el.matches('.magpie-quote')) applyProtectedFont(el,quote);
      else applyFont(el,body);
    });
  }
}
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
function getHtml(){
  if(!typography) return ed.innerHTML;
  // Stamp a detached copy, preserving body/signature/quote boundaries and existing rich formatting.
  const copy=ed.cloneNode(true), originals=Array.from(ed.querySelectorAll('*')), copies=Array.from(copy.querySelectorAll('*'));
  copies.forEach(function(el,i){const style=window.getComputedStyle(originals[i]);el.style.fontFamily=style.fontFamily;el.style.fontSize=style.fontSize;el.style.fontWeight=style.fontWeight;});
  Array.from(copy.childNodes).forEach(function(n){if(n.nodeType===Node.TEXT_NODE && n.textContent){const span=document.createElement('span');applyFont(span,typography.body);n.replaceWith(span);span.appendChild(n);}});
  return copy.innerHTML;
}
function getNewText(){
  const source=ed.cloneNode(true), stop=source.querySelector('.magpie-signature,.magpie-quote');
  const range=document.createRange();range.selectNodeContents(source);if(stop) range.setEndBefore(stop);
  const copy=document.createElement('div');copy.appendChild(range.cloneContents());
  copy.querySelectorAll('img,video,audio,object').forEach(function(n){n.replaceWith(document.createTextNode('[Embedded content]'));});
  copy.querySelectorAll('br').forEach(function(n){n.replaceWith(document.createTextNode('\n'));});
  copy.querySelectorAll('p,div,li').forEach(function(n){n.appendChild(document.createTextNode('\n'));});
  return (copy.textContent||'').replace(/\r\n/g,'\n').trim();
}
function setDark(on){ document.documentElement.classList.toggle('dark', !!on); }
function restore(){ if(saved){ const s = window.getSelection(); s.removeAllRanges(); s.addRange(saved); } }
function fmt(c, v){ ed.focus(); restore(); document.execCommand(c, false, v === undefined ? null : v); post({t:'dirty'}); }
document.addEventListener('selectionchange', function(){
  const s = window.getSelection();
  if (s.rangeCount && ed.contains(s.anchorNode)) { saved = s.getRangeAt(0).cloneRange(); post({t:'sel', text: s.toString()}); }
});
function esc(t){ return t.replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;'); }
function toHtml(t){ return t.replace(/\r\n/g,'\n').split(/\n{2,}/).map(function(p){ return '<p'+(typography?' style="'+esc(fontCss(typography.body)).replace(/"/g,'&quot;')+'"':'')+'>' + esc(p).replace(/\n/g,'<br>') + '</p>'; }).join(''); }
function inlineHtml(t){ return esc(t).replace(/\r?\n/g,'<br>'); }
function topChild(n){ while(n && n.parentNode !== ed) n = n.parentNode; return n; }
function insertBody(text, replace){
  const stop=ed.querySelector('.magpie-signature, .magpie-quote'),r=document.createRange();
  if(replace){
    r.setStart(ed,0);if(stop) r.setEndBefore(stop);else r.setEnd(ed,ed.childNodes.length);
    r.deleteContents();r.collapse(true);
  }else{
    if(stop) r.setStartBefore(stop);else r.setStart(ed,ed.childNodes.length);r.collapse(true);
  }
  const fragment=r.createContextualFragment(toHtml(text)),last=fragment.lastChild;
  // execCommand can merge a paragraph into the signature at this boundary. Insert the fragment exactly here.
  r.insertNode(fragment);
  if(last) r.setStartAfter(last);r.collapse(true);
  const selection=window.getSelection();selection.removeAllRanges();selection.addRange(r);saved=r.cloneRange();ed.focus();
}
function cmd(mode, text){
  if(mode==='replyDraft'){
    let request; try{request=JSON.parse(text);}catch(e){return false;}
    if(!request || typeof request.text!=='string' || typeof request.expectedNewText!=='string'
       || (request.mode!=='replaceBody' && request.mode!=='appendBody') || getNewText()!==request.expectedNewText) return false;
    return cmd(request.mode,request.text);
  }
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
    insertBody(text,true);
  }
  else if(mode === 'appendBody') {
    insertBody(text,false);
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
