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
mark.mf{background:#fff3a0;color:#23282E;padding:0}
mark.mf.cur{background:#f2a63b}
.mq-pill{display:inline-flex;align-items:center;gap:8px;border:1px solid #DCD8CF;border-radius:999px;padding:2px 10px;color:#5A6068;font-size:12px;margin:8px 0;cursor:pointer;user-select:none;font-family:'IBM Plex Sans','Segoe UI',system-ui,sans-serif}
.mq-pill b{letter-spacing:2px}
html.dark .mq-pill{border-color:#2C3238;color:#9AA1A8}
.magpie-quote[data-folded]{display:none}
#ed table.mg-table{border-collapse:collapse}
#ed table.mg-table td{border:1px solid #BFBFBF;padding:4px 8px;min-width:40px}
img.mg-sel{outline:2px solid #14606E;outline-offset:2px}
</style></head>
<body><div id="ed" contenteditable="true" spellcheck="true" data-ph="Write your message…"></div>
<script>
const ed = document.getElementById('ed');
let saved = null;
let rewrite = null;
let typography = null;
const protectedFonts = new WeakMap();
function fontCss(style){ return 'font-family:' + JSON.stringify(style.family) + ';font-size:' + style.size + 'px;font-weight:400'; }
function applyFont(el, style, normal){ el.style.fontFamily=style.family; el.style.fontSize=style.size+'px'; if(normal!==false) el.style.fontWeight='400'; }
function applyProtectedFont(el, style){
  // A saved rich signature and a received quote keep their own formatting and emphasis.
  const nodes=[el].concat(Array.from(el.querySelectorAll('*')));
  if(style.overrideExisting){
    nodes.forEach(function(n){
      if(!protectedFonts.has(n)) protectedFonts.set(n,{
        family:n.style.getPropertyValue('font-family'),familyPriority:n.style.getPropertyPriority('font-family'),
        size:n.style.getPropertyValue('font-size'),sizePriority:n.style.getPropertyPriority('font-size')
      });
      n.style.setProperty('font-family',style.family,'important');n.style.setProperty('font-size',style.size+'px','important');
    });
  }else{
    nodes.forEach(function(n){
      const original=protectedFonts.get(n);if(!original) return;
      if(original.family) n.style.setProperty('font-family',original.family,original.familyPriority);else n.style.removeProperty('font-family');
      if(original.size) n.style.setProperty('font-size',original.size,original.sizePriority);else n.style.removeProperty('font-size');
      protectedFonts.delete(n);
    });
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
  const subsequent=typography!==null;
  if(preserveExisting || subsequent){
    Array.from(ed.querySelectorAll('*')).forEach(function(el){
      if(preserveExisting || !el.closest('.magpie-signature,.magpie-quote')) preserveFont(el);
    });
    Array.from(ed.childNodes).forEach(function(n){
      if(n.nodeType===Node.TEXT_NODE && n.textContent){ const span=document.createElement('span'); preserveFont(ed); span.style.fontFamily=ed.style.fontFamily; span.style.fontSize=ed.style.fontSize; span.style.fontWeight=ed.style.fontWeight; n.replaceWith(span); span.appendChild(n); }
    });
  }
  typography={body:body,signature:signature,quote:quote};
  applyFont(ed,body);
  if(!preserveExisting){
    Array.from(ed.children).forEach(function(el){
      if(!subsequent && !el.matches('.magpie-signature,.magpie-quote')) applyFont(el,body);
    });
    ed.querySelectorAll('.magpie-signature').forEach(function(el){applyProtectedFont(el,signature);});
    ed.querySelectorAll('.magpie-quote').forEach(function(el){applyProtectedFont(el,quote);});
  }
}

// ───────────────────────── design CW1: toolbar, tables, find, fold, mentions ─────────────────────────
try{ document.execCommand('styleWithCSS', false, true); }catch(e){}
var tableCell=null, imgSel=null, linkSel=null, mentionAt=null, mentionOpen=false, plainPaste=false, askPaste=false, findMarks=[], findCur=-1, lastState='', statTimer=null;
function setSpell(on){ ed.spellcheck=!!on; }
function setMentionOpen(on){ mentionOpen=!!on; }   // the app says whether its suggestion list is showing
function setAskPaste(on){ askPaste=!!on; }
function nodeIn(n){ while(n && n!==ed){ if(n.nodeType===1) return n; n=n.parentNode; } return null; }
function closestIn(n, sel){ var el=n && n.nodeType===1 ? n : (n ? n.parentNode : null); var c=el && el.closest ? el.closest(sel) : null; return c && ed.contains(c) ? c : null; }
function selNode(){ var s=window.getSelection(); return s.rangeCount ? s.getRangeAt(0).startContainer : null; }
function setFontSize(px){
  ed.focus(); restore();
  var s0=window.getSelection();
  if(s0.rangeCount && s0.isCollapsed && ed.contains(s0.anchorNode)){
    // Nothing selected: what is typed next gets the size — a sized span with a zero-width space to type into (removed by cleanCopy).
    var sz=document.createElement('span'); sz.style.fontSize=px+'px'; sz.appendChild(document.createTextNode('\u200b'));
    var r0=s0.getRangeAt(0); r0.insertNode(sz); r0.setStart(sz.firstChild,1); r0.collapse(true); s0.removeAllRanges(); s0.addRange(r0);
    post({t:'dirty'}); postState(); return;
  }
  // Only what this command creates is converted: size-7 / xxx-large elements already in the message (a reopened draft, the quote) stay as they are.
  var sel='font[size="7"],span[style*="xxx-large"]', before=new Set(ed.querySelectorAll(sel));
  document.execCommand('fontSize', false, '7');
  ed.querySelectorAll(sel).forEach(function(el){
    if(before.has(el)) return;
    if(el.tagName==='FONT'){ var sp=document.createElement('span'); sp.style.fontSize=px+'px'; while(el.firstChild) sp.appendChild(el.firstChild); el.replaceWith(sp); }
    else el.style.fontSize=px+'px';
  });
  post({t:'dirty'}); postState();
}
function setBlock(tag){
  ed.focus(); restore();
  document.execCommand('formatBlock', false, tag==='code' ? 'pre' : tag);
  if(tag==='code'){
    var n=closestIn(selNode(),'pre');
    if(n){ n.style.fontFamily='Consolas,"Courier New",monospace'; n.style.background='#F3F3F0'; n.style.color='#23282E'; n.style.padding='8px 10px'; n.style.borderRadius='4px'; n.style.whiteSpace='pre-wrap'; }
  }
  post({t:'dirty'}); postState();
}
function insertTable(rows, cols){
  var h='<table class="mg-table" style="border-collapse:collapse"><tbody>';
  for(var r=0;r<rows;r++){ h+='<tr>'; for(var c=0;c<cols;c++) h+='<td style="border:1px solid #BFBFBF;padding:4px 8px;min-width:40px">'+(r===0?'<b><br></b>':'<br>')+'</td>'; h+='</tr>'; }
  h+='</tbody></table><p><br></p>';
  ed.focus(); restore(); document.execCommand('insertHTML', false, h); post({t:'dirty'});
}
function tableCmd(what){
  var cell=tableCell && ed.contains(tableCell) ? tableCell : closestIn(selNode(),'td,th');
  if(!cell) return false;
  var row=cell.parentNode, table=cell.closest('table'), idx=Array.prototype.indexOf.call(row.children, cell);
  function newCell(){ var td=document.createElement('td'); td.setAttribute('style','border:1px solid #BFBFBF;padding:4px 8px;min-width:40px'); td.innerHTML='<br>'; return td; }
  if(what==='rowAbove'||what==='rowBelow'){ var tr=document.createElement('tr'); for(var i=0;i<row.children.length;i++) tr.appendChild(newCell()); row.parentNode.insertBefore(tr, what==='rowAbove'?row:row.nextSibling); }
  else if(what==='colLeft'||what==='colRight'){ Array.prototype.forEach.call(table.rows, function(r){ var ref=r.children[idx]; if(!ref){ r.appendChild(newCell()); return; } r.insertBefore(newCell(), what==='colLeft'?ref:ref.nextSibling); }); }
  else if(what==='delRow'){ if(table.rows.length<=1) table.remove(); else row.remove(); }
  else if(what==='delCol'){ Array.prototype.forEach.call(table.rows, function(r){ if(r.children[idx]) r.children[idx].remove(); }); if(!table.rows[0] || table.rows[0].children.length===0) table.remove(); }
  else if(what==='delTable'){ table.remove(); }
  tableCell=null; post({t:'dirty'}); return true;
}
function insertPicture(html){ ed.focus(); restore(); document.execCommand('insertHTML', false, html); post({t:'dirty'}); }
function insertEmoji(text){ ed.focus(); restore(); document.execCommand('insertText', false, text); post({t:'dirty'}); }
function imgSize(how){ if(!imgSel) return; imgSel.style.width = how==='small' ? '320px' : how==='medium' ? '560px' : ''; imgSel.style.maxWidth='100%'; post({t:'dirty'}); }
function imgRemove(){ if(!imgSel) return; imgSel.remove(); imgSel=null; post({t:'dirty'}); post({t:'img', src:''}); }
function imgSrc(){ return imgSel ? imgSel.src : ''; }
function clearImgSel(){ if(imgSel){ imgSel.classList.remove('mg-sel'); imgSel=null; } }
ed.addEventListener('click', function(e){
  var t=e.target;
  if(t && t.tagName==='IMG' && !t.closest('.magpie-quote')){ clearImgSel(); imgSel=t; t.classList.add('mg-sel'); var r=t.getBoundingClientRect(); post({t:'img', src:'set', x:r.left, y:r.bottom, w:r.width}); }
  else if(imgSel){ clearImgSel(); post({t:'img', src:''}); }
  var pill=t && t.closest ? t.closest('.mq-pill') : null;
  if(pill){ e.preventDefault(); unfoldQuote(); }
});
ed.addEventListener('contextmenu', function(e){
  var cell=closestIn(e.target,'td,th');
  if(cell && !cell.closest('.magpie-quote')){ tableCell=cell; e.preventDefault(); post({t:'table', x:e.clientX, y:e.clientY}); }
});
function editLink(url){ if(!linkSel) return; linkSel.href=url; post({t:'dirty'}); }
function unlinkAt(){ if(!linkSel) return; var a=linkSel; var frag=document.createDocumentFragment(); while(a.firstChild) frag.appendChild(a.firstChild); a.replaceWith(frag); linkSel=null; post({t:'dirty'}); post({t:'linkat', href:''}); }
function postLinkAt(){
  var a=closestIn(selNode(),'a');
  if(a && !a.closest('.magpie-quote,.magpie-signature')){ if(a!==linkSel){ linkSel=a; var r=a.getBoundingClientRect(); post({t:'linkat', href:a.href, x:r.left, y:r.bottom}); } }
  else if(linkSel){ linkSel=null; post({t:'linkat', href:''}); }
}
function postState(){
  var st={t:'state'};
  try{
    st.bold=document.queryCommandState('bold'); st.italic=document.queryCommandState('italic'); st.underline=document.queryCommandState('underline'); st.strike=document.queryCommandState('strikeThrough');
    st.ul=document.queryCommandState('insertUnorderedList'); st.ol=document.queryCommandState('insertOrderedList');
    var b=(document.queryCommandValue('formatBlock')||'').toLowerCase(); st.block = b==='pre' ? 'code' : (b==='h1'||b==='h2'||b==='h3') ? b : 'p';
    st.font=(document.queryCommandValue('fontName')||'').replace(/["']/g,'').split(',')[0];
    var n=selNode(); var el=n ? (n.nodeType===1?n:n.parentNode) : null; st.size = el ? Math.round(parseFloat(window.getComputedStyle(el).fontSize)) : 0;
    st.inQuote = !!closestIn(n,'.magpie-quote,.magpie-signature');
  }catch(e){}
  var key=JSON.stringify(st); if(key!==lastState){ lastState=key; post(st); }
}
// ── find & replace (E1) ──
function findClear(){
  findMarks.forEach(function(m){ if(m.parentNode){ var t=document.createTextNode(m.textContent); m.replaceWith(t); t.parentNode && t.parentNode.normalize(); } });
  findMarks=[]; findCur=-1;
}
function findAll(q, matchCase){
  findClear(); if(!q) return 0;
  var walker=document.createTreeWalker(ed, NodeFilter.SHOW_TEXT, null), hits=[], n;
  var needle=matchCase?q:q.toLowerCase();
  while((n=walker.nextNode())){ if(n.parentNode && n.parentNode.closest && n.parentNode.closest('.mq-pill')) continue; var text=matchCase?n.textContent:n.textContent.toLowerCase(); var i=text.indexOf(needle); while(i>=0){ hits.push({node:n,start:i,end:i+q.length}); i=text.indexOf(needle,i+q.length); } }
  for(var k=hits.length-1;k>=0;k--){ var h=hits[k], r=document.createRange(); r.setStart(h.node,h.start); r.setEnd(h.node,h.end); var m=document.createElement('mark'); m.className='mf'; try{ r.surroundContents(m); findMarks.unshift(m); }catch(e){} }
  if(findMarks.length){ findCur=0; findMarks[0].classList.add('cur'); findMarks[0].scrollIntoView({block:'center'}); }
  return findMarks.length;
}
function findNext(dir){
  if(!findMarks.length) return 0;
  findMarks[findCur] && findMarks[findCur].classList.remove('cur');
  findCur=(findCur+dir+findMarks.length)%findMarks.length;
  findMarks[findCur].classList.add('cur'); findMarks[findCur].scrollIntoView({block:'center'});
  return findCur+1;
}
function replaceOne(text){
  if(findCur<0 || !findMarks[findCur]) return 0;
  var m=findMarks[findCur]; var t=document.createTextNode(text); m.replaceWith(t); findMarks.splice(findCur,1);
  if(findMarks.length){ findCur=findCur%findMarks.length; findMarks[findCur].classList.add('cur'); findMarks[findCur].scrollIntoView({block:'center'}); } else findCur=-1;
  post({t:'dirty'}); return findMarks.length;
}
function replaceAll(text){
  var n=findMarks.length; findMarks.forEach(function(m){ m.replaceWith(document.createTextNode(text)); }); findMarks=[]; findCur=-1; ed.normalize(); if(n) post({t:'dirty'}); return n;
}
// ── folded quote (E2) ──
function foldQuote(){
  var q=ed.querySelector('.magpie-quote'); if(!q || q.hasAttribute('data-folded')) return false;
  var who=''; var first=q.querySelector('p'); if(first){ var m=/On (.+?), (.+?) wrote:/.exec(first.textContent||''); if(m) who=m[2]+', '+m[1]; else who=(first.textContent||'').slice(0,60); }
  var pill=document.createElement('div'); pill.className='mq-pill'; pill.setAttribute('contenteditable','false'); pill.innerHTML='<b>···</b> Show quoted text'+(who?' · '+esc(who):'');
  q.setAttribute('data-folded','1'); q.parentNode.insertBefore(pill, q);
  return true;
}
function unfoldQuote(){ var q=ed.querySelector('.magpie-quote[data-folded]'); if(!q) return; q.removeAttribute('data-folded'); ed.querySelectorAll('.mq-pill').forEach(function(p){ p.remove(); }); post({t:'dirty'}); }
function cleanCopy(copy){
  copy.querySelectorAll('mark.mf').forEach(function(m){ m.replaceWith(document.createTextNode(m.textContent)); });
  var w=document.createTreeWalker(copy, NodeFilter.SHOW_TEXT), zw=[]; while(w.nextNode()){ if(w.currentNode.nodeValue.indexOf('\u200b')>=0) zw.push(w.currentNode); }
  zw.forEach(function(t){ t.nodeValue=t.nodeValue.replace(/\u200b/g,''); if(!t.nodeValue && t.parentNode && t.parentNode.tagName==='SPAN' && t.parentNode.childNodes.length===1 && t.parentNode!==copy) t.parentNode.remove(); });
  copy.querySelectorAll('.mq-pill').forEach(function(p){ p.remove(); });
  copy.querySelectorAll('[data-folded]').forEach(function(q){ q.removeAttribute('data-folded'); });
  copy.querySelectorAll('img.mg-sel').forEach(function(i){ i.classList.remove('mg-sel'); if(!i.getAttribute('class')) i.removeAttribute('class'); });
  return copy;
}
// ── @mention (E6) ──
function checkMention(){
  var s=window.getSelection(); if(!s.rangeCount || !s.isCollapsed){ mentionAt=null; return; }
  var r=s.getRangeAt(0), n=r.startContainer; if(n.nodeType!==3 || closestIn(n,'.magpie-quote,.magpie-signature')){ if(mentionAt){ mentionAt=null; post({t:'mention', q:''}); } return; }
  var before=n.textContent.slice(0, r.startOffset), m=/(^|\s)@([^\s@]{2,})$/.exec(before);
  if(m){ mentionAt={node:n, start:r.startOffset-m[2].length-1, end:r.startOffset}; var rc=r.getBoundingClientRect(); post({t:'mention', q:m[2], x:rc.left, y:rc.bottom}); }
  else if(mentionAt){ mentionAt=null; mentionOpen=false; post({t:'mention', q:''}); }
}
function mention(name, address){
  if(!mentionAt || !mentionAt.node.parentNode) return false;
  var n=mentionAt.node, r=document.createRange(); r.setStart(n, mentionAt.start); r.setEnd(n, mentionAt.end);
  var s=window.getSelection(); s.removeAllRanges(); s.addRange(r); ed.focus();
  var a=document.createElement('a'); a.href='mailto:'+address; a.textContent=name; var holder=document.createElement('div'); holder.appendChild(a); holder.appendChild(document.createTextNode('\u00a0'));
  document.execCommand('insertHTML', false, holder.innerHTML);
  mentionAt=null; mentionOpen=false; post({t:'dirty'}); return true;
}
// ── signature swap (D3) ──
function ownSignature(){ var all=ed.querySelectorAll('.magpie-signature'); for(var i=0;i<all.length;i++){ if(!all[i].closest('.magpie-quote')) return all[i]; } return null; }
function setSignature(html){
  var sig=ownSignature();   // never the other person's signature inside the quoted email
  var block = html ? '<div class="magpie-signature"><br>'+html+'</div>' : '';
  if(sig){ if(block){ var d=document.createElement('div'); d.innerHTML=block; sig.replaceWith(d.firstChild); } else sig.remove(); }
  else if(block){ var q=ed.querySelector('.magpie-quote,.mq-pill'); var d2=document.createElement('div'); d2.innerHTML=block; if(q) ed.insertBefore(d2.firstChild, q); else ed.appendChild(d2.firstChild); }
  if(typography){ var own=ownSignature(); if(own) applyProtectedFont(own, typography.signature); }
  post({t:'dirty'});
}
function postStats(){ if(statTimer) clearTimeout(statTimer); statTimer=setTimeout(function(){ post({t:'stats', text:getNewText()}); }, 400); }
document.addEventListener('paste', function(e){
  var cd=e.clipboardData; if(!cd) return;
  if(plainPaste){ plainPaste=false; e.preventDefault(); var txt=cd.getData('text/plain'); if(txt) document.execCommand('insertText', false, txt); return; }
  var files=cd.files ? Array.prototype.slice.call(cd.files) : [];
  var hasText=cd.types && (cd.types.indexOf('text/html')>=0 || cd.types.indexOf('text/plain')>=0);
  if(files.length && !hasText){
    var pics=files.filter(function(f){ return /^image\//.test(f.type); });
    if(pics.length===files.length && pics.length){
      if(askPaste){
        // Every pasted picture is read, then sent together so the app asks once and keeps them all.
        e.preventDefault(); var urls=new Array(pics.length), left=pics.length;
        pics.forEach(function(f,i){ var fr=new FileReader(); fr.onload=function(){ urls[i]=fr.result; if(--left===0) post({t:'pastepics', data:urls.filter(function(u){ return !!u; })}); }; fr.onerror=function(){ if(--left===0) post({t:'pastepics', data:urls.filter(function(u){ return !!u; })}); }; fr.readAsDataURL(f); });
      }
      return;
    }
    e.preventDefault(); post({t:'pastefiles'});
  }
});

function post(o){ try{ window.chrome.webview.postMessage(JSON.stringify(o)); }catch(e){} }
function setHtml(h){
  findMarks=[]; findCur=-1; imgSel=null; linkSel=null; mentionAt=null; tableCell=null; lastState='';
  ed.innerHTML = h && h.length ? h : '<p><br></p>';
  typography=null;
  const r = document.createRange();
  const first = ed.firstChild || ed;
  r.setStart(first, 0); r.collapse(true);
  const s = window.getSelection(); s.removeAllRanges(); s.addRange(r);
  saved = r.cloneRange();
  ed.focus();
}
function getHtml(){
  if(!typography) return cleanCopy(ed.cloneNode(true)).innerHTML;
  // Stamp a detached copy, preserving body/signature/quote boundaries and existing rich formatting.
  const copy=ed.cloneNode(true), originals=Array.from(ed.querySelectorAll('*')), copies=Array.from(copy.querySelectorAll('*'));
  copies.forEach(function(el,i){const style=window.getComputedStyle(originals[i]);el.style.fontFamily=style.fontFamily;el.style.fontSize=style.fontSize;el.style.fontWeight=style.fontWeight;});
  Array.from(copy.childNodes).forEach(function(n){if(n.nodeType===Node.TEXT_NODE && n.textContent){const span=document.createElement('span');applyFont(span,typography.body);n.replaceWith(span);span.appendChild(n);}});
  return cleanCopy(copy).innerHTML;
}
function getNewText(){
  const source=cleanCopy(ed.cloneNode(true)), stop=source.querySelector('.magpie-signature,.magpie-quote');
  const range=document.createRange();range.selectNodeContents(source);if(stop) range.setEndBefore(stop);
  const copy=document.createElement('div');copy.appendChild(range.cloneContents());
  copy.querySelectorAll('img,video,audio,object').forEach(function(n){n.replaceWith(document.createTextNode('[Embedded content]'));});
  copy.querySelectorAll('br').forEach(function(n){n.replaceWith(document.createTextNode('\n'));});
  copy.querySelectorAll('p,div,li').forEach(function(n){n.appendChild(document.createTextNode('\n'));});
  return (copy.textContent||'').replace(/\r\n/g,'\n').trim();
}
function setDark(on){ document.documentElement.classList.toggle('dark', !!on); }
function restore(){ if(saved){ const s = window.getSelection(); s.removeAllRanges(); s.addRange(saved); } }
function fmt(c, v){ ed.focus(); restore(); document.execCommand(c, false, v === undefined ? null : v); post({t:'dirty'}); postState(); }
document.addEventListener('selectionchange', function(){
  const s = window.getSelection();
  if (s.rangeCount && ed.contains(s.anchorNode)) { saved = s.getRangeAt(0).cloneRange(); post({t:'sel', text: s.toString()}); postLinkAt(); postState(); checkMention(); }
});
function esc(t){ return String(t).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;').replace(/'/g,'&#39;'); }
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
ed.addEventListener('input', function(){ post({t:'dirty'}); postStats(); });
document.addEventListener('keydown', function(e){
  if(e.defaultPrevented) return;   // a key the app already took (on the Mac the menu bar's ⌘ keys, e.g. ⌘, = Settings) never formats text too
  var k=e.key, K=k.length===1?k.toUpperCase():k;
  if(mentionAt && mentionOpen && !e.ctrlKey && !e.altKey && (k==='Enter' || k==='Tab')){ e.preventDefault(); post({t:'key', k:'mentionpick'}); return; }
  if(mentionAt && mentionOpen && (k==='ArrowDown' || k==='ArrowUp')){ e.preventDefault(); post({t:'key', k:k==='ArrowDown'?'mentiondown':'mentionup'}); return; }
  if (e.ctrlKey && e.key === 'Enter' && e.altKey) { e.preventDefault(); post({t:'key', k:'sendarchive'}); }
  else if (e.ctrlKey && e.key === 'Enter') { e.preventDefault(); post({t:'send'}); }
  else if (e.ctrlKey && e.shiftKey && K === 'K') { e.preventDefault(); unlinkAt(); }
  else if (e.ctrlKey && (e.key === 'k' || e.key === 'K') && !e.shiftKey) { e.preventDefault(); post({t:'link'}); }
  else if (e.ctrlKey && e.shiftKey && K === 'X') { e.preventDefault(); fmt('strikeThrough'); }
  else if (e.ctrlKey && e.shiftKey && (K === '8' || K === '*')) { e.preventDefault(); fmt('insertUnorderedList'); }
  else if (e.ctrlKey && e.shiftKey && (K === '7' || K === '&')) { e.preventDefault(); fmt('insertOrderedList'); }
  else if (e.ctrlKey && !e.shiftKey && k === ']') { e.preventDefault(); fmt('indent'); }
  else if (e.ctrlKey && !e.shiftKey && k === '[') { e.preventDefault(); fmt('outdent'); }
  else if (e.ctrlKey && e.shiftKey && K === 'L') { e.preventDefault(); fmt('justifyLeft'); }
  else if (e.ctrlKey && e.shiftKey && K === 'E') { e.preventDefault(); fmt('justifyCenter'); }
  else if (e.ctrlKey && e.shiftKey && K === 'R') { e.preventDefault(); fmt('justifyRight'); }
  else if (e.ctrlKey && e.shiftKey && K === 'J') { e.preventDefault(); fmt('justifyFull'); }
  else if (e.ctrlKey && e.altKey && K === '0') { e.preventDefault(); setBlock('p'); }
  else if (e.ctrlKey && e.altKey && K === '1') { e.preventDefault(); setBlock('h1'); }
  else if (e.ctrlKey && e.altKey && K === '2') { e.preventDefault(); setBlock('h2'); }
  else if (e.ctrlKey && e.altKey && K === '3') { e.preventDefault(); setBlock('h3'); }
  else if (e.ctrlKey && e.altKey && K === 'C') { e.preventDefault(); setBlock('code'); }
  else if (e.ctrlKey && k === '\\') { e.preventDefault(); fmt('removeFormat'); }
  else if (e.ctrlKey && k === '.') { e.preventDefault(); fmt('superscript'); }
  else if (e.ctrlKey && k === ',') { e.preventDefault(); fmt('subscript'); }
  else if (e.ctrlKey && !e.shiftKey && K === 'F') { e.preventDefault(); post({t:'key', k:'find'}); }
  else if (e.ctrlKey && !e.shiftKey && K === 'H') { e.preventDefault(); post({t:'key', k:'replace'}); }
  else if (e.ctrlKey && !e.shiftKey && K === 'S') { e.preventDefault(); post({t:'key', k:'save'}); }
  else if (e.ctrlKey && k === '/') { e.preventDefault(); post({t:'key', k:'shortcuts'}); }
  else if (e.ctrlKey && e.shiftKey && K === 'O') { e.preventDefault(); post({t:'key', k:'attach'}); }
  else if (e.ctrlKey && e.shiftKey && K === 'C') { e.preventDefault(); post({t:'key', k:'cc'}); }
  else if (e.ctrlKey && e.shiftKey && K === 'B') { e.preventDefault(); post({t:'key', k:'bcc'}); }
  else if (e.ctrlKey && e.shiftKey && K === 'A') { e.preventDefault(); post({t:'key', k:'assistant'}); }
  else if (e.ctrlKey && e.shiftKey && K === 'V') { plainPaste=true; }
  else if (k === 'Escape') { if(findMarks.length){ findClear(); post({t:'key', k:'findclose'}); } else if(mentionAt){ mentionAt=null; post({t:'mention', q:''}); } else post({t:'key', k:'escape'}); }
});
document.addEventListener('dragover', function(e){ e.preventDefault(); });
document.addEventListener('drop', function(e){ e.preventDefault(); });
post({t:'ready'});
</script>
</body></html>
""";
}
