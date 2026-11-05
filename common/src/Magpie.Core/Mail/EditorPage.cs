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
#ed.quote-folded .magpie-quote > :not(:first-child){display:none}
#ed.quote-folded .magpie-quote{border-bottom:1px dashed #B7BEC5;padding-bottom:6px}
#ed.quote-folded .magpie-quote::after{content:'Quoted message collapsed';display:block;color:#6D7881;font:12px/1.5 system-ui,sans-serif}
</style></head>
<body><div id="ed" contenteditable="true" spellcheck="true" data-ph="Write your message…"></div>
<script>
const ed = document.getElementById('ed');
let saved = null;
let selectedImage = null;
let selectedTableCell = null;
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
function post(o){ try{ window.chrome.webview.postMessage(JSON.stringify(o)); }catch(e){} }
function setHtml(h){
  ed.innerHTML = h && h.length ? h : '<p><br></p>';
  // Fold only in the editor's view. getHtml() still serializes the full quote.
  ed.classList.toggle('quote-folded',!!ed.querySelector('.magpie-quote'));
  typography=null;
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
function toggleQuoteFold(){
  if(!ed.querySelector('.magpie-quote')) return false;
  ed.classList.toggle('quote-folded');return ed.classList.contains('quote-folded');
}
function restore(){ if(saved){ const s = window.getSelection(); s.removeAllRanges(); s.addRange(saved); } }
function fmt(c, v){ ed.focus(); restore(); document.execCommand(c, false, v === undefined ? null : v); post({t:'dirty'}); }
function linkAtCaret(){
  const node=saved?.startContainer || window.getSelection()?.anchorNode;
  const element=node?.nodeType===Node.ELEMENT_NODE ? node : node?.parentElement;
  const link=element?.closest('a');
  return link && ed.contains(link) ? link : null;
}
function selectedLinkUrl(){ return linkAtCaret()?.getAttribute('href') || ''; }
function setSelectedLink(url){
  let parsed; try{parsed=new URL(url);}catch(e){return false;}
  if(!['https:', 'http:', 'mailto:'].includes(parsed.protocol)) return false;
  ed.focus();restore();
  const existing=linkAtCaret();
  if(existing) existing.setAttribute('href', url);
  else if(saved && saved.collapsed){
    const link=document.createElement('a');link.setAttribute('href',url);link.textContent=url;
    saved.insertNode(link);saved.setStartAfter(link);saved.collapse(true);
    const selection=window.getSelection();selection.removeAllRanges();selection.addRange(saved);
  }else document.execCommand('createLink', false, url);
  post({t:'dirty'});return true;
}
function removeSelectedLink(){
  ed.focus();restore();
  const existing=linkAtCaret();
  if(!existing) return false;
  const parent=existing.parentNode;
  while(existing.firstChild) parent.insertBefore(existing.firstChild,existing);
  parent.removeChild(existing);
  post({t:'dirty'});return true;
}
ed.addEventListener('click', function(e){
  if(e.target instanceof HTMLImageElement && ed.contains(e.target)) selectedImage=e.target;
});
function resizeSelectedImage(percent){
  if(!selectedImage || !ed.contains(selectedImage) || ![25,50,75,100].includes(percent)) return false;
  selectedImage.style.width=percent+'%';
  selectedImage.style.height='auto';
  selectedImage.style.maxWidth='100%';
  post({t:'dirty'});return true;
}
function insertTableGrid(rows,cols){
  if(!Number.isInteger(rows)||!Number.isInteger(cols)||rows<1||cols<1||rows>6||cols>6) return false;
  const cells='<td style="border:1px solid #B8C0C8;padding:5px"><br></td>'.repeat(cols);
  fmt('insertHTML','<table style="border-collapse:collapse"><tbody>'+('<tr>'+cells+'</tr>').repeat(rows)+'</tbody></table><p><br></p>');
  return true;
}
ed.addEventListener('contextmenu',function(e){
  const cell=e.target?.closest?.('td,th');
  if(!cell||!ed.contains(cell)) return;
  e.preventDefault();selectedTableCell=cell;post({t:'tableMenu'});
});
function editTable(action){
  const cell=selectedTableCell;
  if(!cell||!ed.contains(cell)) return false;
  const row=cell.parentElement,table=cell.closest('table');
  if(!row||!table||table.querySelector('[rowspan],[colspan]')) return false;
  const rows=Array.from(table.rows),rowIndex=rows.indexOf(row),column=Array.from(row.cells).indexOf(cell);
  if(rowIndex<0||column<0) return false;
  if(action==='deleteTable') table.remove();
  else if(action==='insertRowBefore'||action==='insertRowAfter'){
    const created=row.cloneNode(false);
    for(let i=0;i<row.cells.length;i++){const td=document.createElement('td');td.setAttribute('style','border:1px solid #B8C0C8;padding:5px');td.appendChild(document.createElement('br'));created.appendChild(td);}
    row.parentNode.insertBefore(created,action==='insertRowBefore'?row:row.nextSibling);
    selectedTableCell=created.cells[Math.min(column,created.cells.length-1)];
  }
  else if(action==='deleteRow'){
    if(rows.length===1)table.remove();else row.remove();
  }
  else if(action==='insertColumnBefore'||action==='insertColumnAfter'){
    for(const tr of rows){
      const td=document.createElement('td');td.setAttribute('style','border:1px solid #B8C0C8;padding:5px');td.appendChild(document.createElement('br'));
      const at=column+(action==='insertColumnAfter'?1:0);tr.insertBefore(td,tr.cells[at]||null);
    }
  }
  else if(action==='deleteColumn'){
    if(row.cells.length===1)table.remove();
    else for(const tr of rows){if(tr.cells[column])tr.cells[column].remove();}
  }
  else return false;
  post({t:'dirty'});return true;
}
function currentWritingBlock(){
  const node=saved?.startContainer || window.getSelection()?.anchorNode;
  const element=node?.nodeType===Node.ELEMENT_NODE ? node : node?.parentElement;
  const block=element?.closest('p,li,div,blockquote');
  return block && block !== ed && ed.contains(block) ? block : null;
}
function setWritingDirection(direction){
  if(!['ltr','rtl'].includes(direction)) return false;
  const block=currentWritingBlock();if(!block) return false;
  block.setAttribute('dir',direction);
  post({t:'dirty'});return true;
}
function setWritingLanguage(language){
  if(!['en','hi','bn','zh-CN','ar'].includes(language)) return false;
  const block=currentWritingBlock();if(!block) return false;
  block.setAttribute('lang',language);block.setAttribute('spellcheck','true');
  post({t:'dirty'});return true;
}
function editorMatches(query, matchCase){
  if(typeof query!=='string' || !query.length || query.length>200) return [];
  const needle=matchCase?query:query.toLocaleLowerCase(),matches=[];
  const walker=document.createTreeWalker(ed,NodeFilter.SHOW_TEXT);
  while(walker.nextNode()){
    const node=walker.currentNode;
    if(!node.nodeValue || node.parentElement?.closest('[contenteditable="false"]')
       || (ed.classList.contains('quote-folded') && node.parentElement?.closest('.magpie-quote'))) continue;
    const text=matchCase?node.nodeValue:node.nodeValue.toLocaleLowerCase();
    for(let at=0;(at=text.indexOf(needle,at))>=0;at+=needle.length){
      const range=document.createRange();range.setStart(node,at);range.setEnd(node,at+needle.length);
      matches.push(range);
      if(matches.length>2000) return null;
    }
  }
  return matches;
}
function selectEditorRange(range){
  ed.focus();const selection=window.getSelection();selection.removeAllRanges();selection.addRange(range);
  saved=range.cloneRange();
  const el=range.startContainer.parentElement;
  if(el) el.scrollIntoView({block:'nearest'});
}
function findInEditor(query,matchCase){
  const matches=editorMatches(query,matchCase);
  if(!matches) return {count:-1,index:0};
  if(!matches.length) return {count:0,index:0};
  // Search from the end of the current selection, wrapping back to the first hit.
  const index=matches.findIndex(function(range){
    return !saved || range.compareBoundaryPoints(Range.START_TO_END,saved)>=0;
  });
  const selected=index>=0?index:0;
  selectEditorRange(matches[selected]);
  return {count:matches.length,index:selected+1};
}
function replaceFoundText(query,replacement,matchCase){
  if(!saved || saved.collapsed || typeof replacement!=='string') return false;
  const selected=saved.toString();
  if(matchCase?selected!==query:selected.toLocaleLowerCase()!==query.toLocaleLowerCase()) return false;
  if(!ed.contains(saved.commonAncestorContainer)) return false;
  ed.focus();restore();
  if(!document.execCommand('insertText',false,replacement)) return false;
  post({t:'dirty'});return true;
}
function replaceAllInEditor(query,replacement,matchCase){
  if(typeof replacement!=='string') return 0;
  const matches=editorMatches(query,matchCase);
  if(!matches) return -1;
  // Work backwards so ranges before the edited position remain stable.
  let changed=0;
  for(let i=matches.length-1;i>=0;i--){
    selectEditorRange(matches[i]);
    if(document.execCommand('insertText',false,replacement)) changed++;
  }
  if(changed) post({t:'dirty'});
  return changed;
}
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
  else if (e.ctrlKey && (e.key === 'f' || e.key === 'F')) { e.preventDefault(); post({t:'find'}); }
  else if (e.ctrlKey && (e.key === 'h' || e.key === 'H')) { e.preventDefault(); post({t:'replace'}); }
  else if (e.ctrlKey && e.key === '/') { e.preventDefault(); post({t:'shortcuts'}); }
});
document.addEventListener('dragover', function(e){ e.preventDefault(); });
document.addEventListener('drop', function(e){ e.preventDefault(); });
post({t:'ready'});
</script>
</body></html>
""";
}
