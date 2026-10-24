namespace Magpie.App.Services;

/// <summary>
/// The signature editor in Settings → Signatures &amp; replies (design B6): a small rich-text page, our own (the only
/// kind of page where scripts run). Pictures are pasted or inserted as data: URIs; they are sent embedded (cid).
/// </summary>
public static class SignatureEditorPage
{
    public const string Html = """
<!doctype html>
<html><head><meta charset="utf-8">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src data:; style-src 'unsafe-inline'; script-src 'unsafe-inline'; font-src data:">
<style>
html,body{margin:0;padding:0;background:#fff;height:100%}
body{font:14px/1.5 'IBM Plex Sans','Segoe UI',system-ui,sans-serif;color:#23282E}
#ed{min-height:100%;padding:10px 14px;outline:none;box-sizing:border-box;word-wrap:break-word;overflow-wrap:anywhere}
#ed p{margin:0 0 .4em}
#ed:empty:before{content:attr(data-ph);color:#9AA0A6}
img{max-width:100%}
a{color:#14606E}
html.dark,html.dark body{background:#1E2328}
html.dark body{color:#D9D6D0}
html.dark #ed:empty:before{color:#6F7780}
html.dark a{color:#6CC3CF}
</style></head>
<body><div id="ed" contenteditable="true" spellcheck="true" data-ph="Write your signature… (name, role, phone, a logo)"></div>
<script>
const ed = document.getElementById('ed');
let saved = null, timer = null;
function setTypography(family, size, preserveExisting){
  if(preserveExisting){
    Array.from(ed.querySelectorAll('*')).forEach(function(el){const s=getComputedStyle(el);el.style.fontFamily=s.fontFamily;el.style.fontSize=s.fontSize;el.style.fontWeight=s.fontWeight;});
    Array.from(ed.childNodes).forEach(function(n){if(n.nodeType===Node.TEXT_NODE && n.textContent){const span=document.createElement('span'),s=getComputedStyle(ed);span.style.fontFamily=s.fontFamily;span.style.fontSize=s.fontSize;span.style.fontWeight=s.fontWeight;n.replaceWith(span);span.appendChild(n);}});
  }
  ed.style.fontFamily=family;ed.style.fontSize=size+'px';ed.style.fontWeight='400';
}
function post(o){ try{ window.chrome.webview.postMessage(JSON.stringify(o)); }catch(e){} }
function changed(){ clearTimeout(timer); timer = setTimeout(function(){ post({t:'change', html: getHtml()}); }, 200); }
function setHtml(h){ ed.innerHTML = h || ''; saved = null; }
function getHtml(){
  const copy=ed.cloneNode(true),originals=Array.from(ed.querySelectorAll('*')),copies=Array.from(copy.querySelectorAll('*'));
  copies.forEach(function(el,i){const s=getComputedStyle(originals[i]);el.style.fontFamily=s.fontFamily;el.style.fontSize=s.fontSize;el.style.fontWeight=s.fontWeight;});
  Array.from(copy.childNodes).forEach(function(n){if(n.nodeType===Node.TEXT_NODE && n.textContent){const span=document.createElement('span'),s=getComputedStyle(ed);span.style.fontFamily=s.fontFamily;span.style.fontSize=s.fontSize;span.style.fontWeight=s.fontWeight;n.replaceWith(span);span.appendChild(n);}});
  return copy.innerHTML;
}
function setDark(on){ document.documentElement.classList.toggle('dark', !!on); }
function restore(){ if(saved){ const s = window.getSelection(); s.removeAllRanges(); s.addRange(saved); } }
function fmt(c, v){ ed.focus(); restore(); document.execCommand('styleWithCSS', false, true); document.execCommand(c, false, v === undefined ? null : v); changed(); }
function insertImage(src){ ed.focus(); restore(); document.execCommand('insertImage', false, src); changed(); }
document.addEventListener('selectionchange', function(){
  const s = window.getSelection();
  if (s.rangeCount && ed.contains(s.anchorNode)) saved = s.getRangeAt(0).cloneRange();
});
ed.addEventListener('input', changed);
document.addEventListener('keydown', function(e){
  if (e.ctrlKey && (e.key === 'k' || e.key === 'K')) { e.preventDefault(); post({t:'link'}); }
});
document.addEventListener('dragover', function(e){ e.preventDefault(); });
document.addEventListener('drop', function(e){ e.preventDefault(); });
post({t:'ready'});
</script>
</body></html>
""";
}
