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
#ed:focus{box-shadow:inset 0 0 0 2px #418DA0}
#ed p{margin:0 0 .6em}
#ed pre{white-space:pre-wrap;overflow-wrap:anywhere;background:#F1F3F5;border:1px solid #DCE2E7;border-radius:5px;padding:8px 10px;font:13px/1.5 Consolas,monospace}
#ed:empty:before{content:attr(data-ph);color:#9AA0A6}
#ed.magpie-plain-text{white-space:pre-wrap;font-family:Consolas,monospace}
blockquote{margin:0 0 0 .8ex;border-left:2px solid #DCD8CF;padding-left:1ex;color:#5A6068}
.magpie-quote{color:#5A6068}
.magpie-signature{color:#5A6068}
img{max-width:100%}
a{color:#14606E}
html.dark,html.dark body{background:#181C20}
html.dark body{color:#D9D6D0}
html.dark #ed:focus{box-shadow:inset 0 0 0 2px #6CC3CF}
html.dark #ed:empty:before{color:#6F7780}
html.dark blockquote{border-left-color:#2C3238;color:#9AA1A8}
html.dark .magpie-signature{color:#9AA1A8}
html.dark #ed pre{background:#232B32;border-color:#39434B;color:#E0E4E8}
html.dark a{color:#6CC3CF}
html.dark .magpie-quote{background:#fff;color:#23282E;border-radius:8px;padding:8px 12px}
html.dark .magpie-quote blockquote{border-left-color:#DCD8CF;color:#5A6068}
html.dark .magpie-quote a{color:#14606E}
#ed.quote-folded .magpie-quote > :not(:first-child){display:none}
#ed.quote-folded .magpie-quote{border-bottom:1px dashed #B7BEC5;padding-bottom:6px}
#ed.quote-folded .magpie-quote::after{content:'Quoted message collapsed';display:block;color:#6D7881;font:12px/1.5 system-ui,sans-serif}
#linkBubble{position:fixed;z-index:10;max-width:min(420px,calc(100vw - 24px));padding:10px 12px;border:1px solid #B8C0C8;border-radius:8px;background:#fff;color:#23282E;box-shadow:0 6px 24px #0002;font:13px/1.5 'Segoe UI',system-ui,sans-serif}
#linkBubble[hidden]{display:none}#linkBubble .link-address{display:block;max-width:380px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;margin-bottom:8px;user-select:text}
#linkBubble .link-actions{display:flex;gap:8px}#linkBubble button{border:1px solid #B8C0C8;border-radius:4px;background:#F1F5F6;color:#14606E;padding:4px 10px;cursor:pointer;font:inherit}
#linkBubble button:focus-visible{outline:2px solid #418DA0;outline-offset:2px}html.dark #linkBubble{background:#232B32;color:#E0E4E8;border-color:#4A5964}html.dark #linkBubble button{background:#29343E;color:#6CC3CF;border-color:#596976}
</style></head>
<body><div id="ed" contenteditable="true" role="textbox" aria-multiline="true" aria-label="Message body" spellcheck="true" data-ph="Write your message…"></div>
<div id="linkBubble" role="dialog" aria-label="Link actions" aria-describedby="linkAddress" hidden><span id="linkAddress" class="link-address"></span><div class="link-actions"><button id="editContextLink" type="button">Edit link</button><button id="removeContextLink" type="button">Remove link</button><button id="closeContextLink" type="button" aria-label="Close link actions">Close</button></div></div>
<script>
const ed = document.getElementById('ed');
const linkBubble=document.getElementById('linkBubble');
let saved = null;
let selectedImage = null;
let selectedTableCell = null;
let isPlainText = false;
let signatureBoundariesLost = false;
let quoteFoldingSupported = false;
let cloudLinkTarget = null;
let rewrite = null;
let typography = null;
let contextualLink=null,linkEditTarget=null,formattingTarget=null,dismissedLink=null,linkBubbleEnabled=false;
let emojiTarget=null;
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
// Keep the typed @token's DOM range so a WPF contact selection can replace it without destroying nearby formatting.
let mentionRange=null,mentionToken='',mentionMenuOpen=false;
function setMentionSuggestionsOpen(open){ mentionMenuOpen=!!open; }
function inspectMention(){
  const s=window.getSelection();
  if(s.rangeCount && s.isCollapsed && s.anchorNode && s.anchorNode.nodeType===3 && ed.contains(s.anchorNode)){
    const before=s.anchorNode.nodeValue.slice(0,s.anchorOffset);
    const match=/(?:^|\s)@([\p{L}\p{N}._-]{2,64})$/u.exec(before);
    if(match){
      mentionRange=document.createRange();mentionRange.setStart(s.anchorNode,s.anchorOffset-match[1].length-1);
      mentionRange.setEnd(s.anchorNode,s.anchorOffset);mentionToken='@'+match[1];
      post({t:'mention',q:match[1]});return;
    }
  }
  mentionRange=null;mentionToken='';mentionMenuOpen=false;post({t:'mentionClosed'});
}
function insertMention(name,address){
  const r=mentionRange;
  if(!r || r.toString()!==mentionToken || !address || !name) return false;
  r.deleteContents();
  const node=document.createTextNode('@'+name+' ');r.insertNode(node);r.setStartAfter(node);r.collapse(true);
  const s=window.getSelection();s.removeAllRanges();s.addRange(r);ed.focus();
  mentionRange=null;mentionToken='';mentionMenuOpen=false;post({t:'dirty'});return true;
}
function setHtml(h,bodySectionsLost){
  // Whole-draft restoration invalidates every range or node captured from the previous DOM.
  rewrite=null;mentionRange=null;mentionToken='';mentionMenuOpen=false;
  selectedImage=null;selectedTableCell=null;saved=null;
  clipboardPictureTarget=null;
  cloudLinkTarget=null;
  hideLinkBubble();linkEditTarget=null;formattingTarget=null;dismissedLink=null;
  emojiTarget=null;
  isPlainText=false;signatureBoundariesLost=bodySectionsLost===true;ed.contentEditable='true';ed.classList.remove('magpie-plain-text');
  ed.innerHTML = h && h.length ? h : '<p><br></p>';
  // A signature/quote-only draft still needs a real editable author position. Initialize it as
  // part of the loaded document, never as a temporary node added/removed around an undoable edit.
  const protectedStart=ed.querySelector('.magpie-signature,.magpie-quote');
  if(protectedStart){
    // Loose text after the protected boundary needs a stable element for native-edit guards.
    // Leave these spans unstyled and in the loaded document, including through Undo/Redo.
    for(let branch=protectedStart;branch&&branch!==ed;branch=branch.parentNode){
      for(let node=branch===protectedStart?branch:branch.nextSibling;node;){
        const next=node.nextSibling;
        if(node.nodeType===Node.TEXT_NODE&&node.nodeValue.length){
          const span=document.createElement('span');node.replaceWith(span);span.appendChild(node);
        }
        node=next;
      }
    }
    const walker=document.createTreeWalker(ed,NodeFilter.SHOW_ELEMENT|NodeFilter.SHOW_TEXT);
    let node,hasAuthorPosition=false;
    while((node=walker.nextNode())&&node!==protectedStart){
      const element=node.nodeType===Node.TEXT_NODE?node.parentElement:node;
      if(!element?.isContentEditable)continue;
      if((node.nodeType===Node.TEXT_NODE&&/[^\t\r\n ]/.test(node.textContent))
          || (node.nodeType===Node.ELEMENT_NODE&&!node.contains(protectedStart)&&node.matches('p,div,li,pre,blockquote,br,td,th,img,video,audio,object,table,hr'))){
        hasAuthorPosition=true;break;
      }
    }
    if(!hasAuthorPosition){const paragraph=document.createElement('p');paragraph.appendChild(document.createElement('br'));protectedStart.before(paragraph);}
  }
  // Only hosts with an expansion control may fold quotes. Serialization always keeps the full quote.
  ed.classList.toggle('quote-folded',quoteFoldingSupported&&!!ed.querySelector('.magpie-quote'));
  typography=null;
  const r = document.createRange();
  const first = ed.firstChild || ed;
  r.setStart(first, 0); r.collapse(true);
  const s = window.getSelection(); s.removeAllRanges(); s.addRange(r);
  saved = r.cloneRange();
  ed.focus();
}
function getHtml(){
  if(isPlainText){
    // Encode the plain editor's visible text as HTML for draft compatibility.
    // Composer sends only text/plain for drafts with IsPlainText=true.
    return ed.innerText.replace(/\r\n/g,'\n').split('\n').map(function(line){
      const p=document.createElement('p');p.textContent=line;return p.outerHTML;
    }).join('');
  }
  if(!typography) return ed.innerHTML;
  // Stamp a detached copy, preserving body/signature/quote boundaries and existing rich formatting.
  const copy=ed.cloneNode(true), originals=Array.from(ed.querySelectorAll('*')), copies=Array.from(copy.querySelectorAll('*'));
  copies.forEach(function(el,i){const style=window.getComputedStyle(originals[i]);el.style.fontFamily=style.fontFamily;el.style.fontSize=style.fontSize;el.style.fontWeight=style.fontWeight;});
  Array.from(copy.childNodes).forEach(function(n){if(n.nodeType===Node.TEXT_NODE && n.textContent){const span=document.createElement('span');applyFont(span,typography.body);n.replaceWith(span);span.appendChild(n);}});
  return copy.innerHTML;
}
function getNewText(includeEmbeddedContent){
  const source=ed.cloneNode(true), stop=source.querySelector('.magpie-signature,.magpie-quote');
  const range=document.createRange();range.selectNodeContents(source);if(stop) range.setEndBefore(stop);
  const copy=document.createElement('div');copy.appendChild(range.cloneContents());
  copy.querySelectorAll('img,video,audio,object').forEach(function(n){n.replaceWith(document.createTextNode(includeEmbeddedContent===false?'':'[Embedded content]'));});
  copy.querySelectorAll('br').forEach(function(n){n.replaceWith(document.createTextNode('\n'));});
  copy.querySelectorAll('p,div,li').forEach(function(n){n.appendChild(document.createTextNode('\n'));});
  return (copy.textContent||'').replace(/\r\n/g,'\n').trim();
}
function canReplaceSignatureBlock(){return !isPlainText&&!signatureBoundariesLost;}
function getBodySectionsLost(){return isPlainText||signatureBoundariesLost;}
function canReplaceBody(){return !getBodySectionsLost();}
function replaceSignatureBlock(blockHtml){
  // Flattened signature text cannot safely be identified later, even after returning to rich mode.
  // Never guess which unmarked text to replace/remove or append a duplicate signature after conversion.
  if(!canReplaceSignatureBlock())return false;
  // Replace only the signature, leaving the author's text and the quoted conversation untouched.
  const host=document.createElement('div');host.innerHTML=blockHtml||'';
  const next=host.querySelector('.magpie-signature');
  const old=ed.querySelector('.magpie-signature');
  if(next){
    if(old) old.replaceWith(next);
    else { const quote=ed.querySelector('.magpie-quote'); if(quote) quote.before(next); else ed.appendChild(next); }
    if(typography?.signature) applyProtectedFont(next,typography.signature);
  } else if(old) old.remove();
  else return false;
  post({t:'dirty'});return true;
}
function setDark(on){ document.documentElement.classList.toggle('dark', !!on); }
function setEditorSpellcheck(enabled){
  ed.spellcheck=!!enabled;
  ed.querySelectorAll('[spellcheck]').forEach(function(node){node.spellcheck=!!enabled;});
  return ed.spellcheck;
}
function setEditorPlainText(enabled,notify){
  const next=!!enabled;
  if(next===isPlainText) return true;
  cloudLinkTarget=null;
  clipboardPictureTarget=null;
  hideLinkBubble();linkEditTarget=null;formattingTarget=null;
  emojiTarget=null;
  // Unfold quotes before conversion: the text-only email must retain the entire thread.
  ed.classList.remove('quote-folded');
  const visibleText=ed.innerText;
  if(next){
    signatureBoundariesLost=true;
    ed.textContent=visibleText;
    ed.contentEditable='plaintext-only';
    ed.classList.add('magpie-plain-text');
  }else{
    ed.contentEditable='true';ed.classList.remove('magpie-plain-text');
    ed.replaceChildren(...visibleText.replace(/\r\n/g,'\n').split('\n').map(function(line){
      const p=document.createElement('p');p.textContent=line;return p;
    }));
  }
  isPlainText=next;selectedTableCell=null;selectedImage=null;
  const caret=document.createRange();caret.selectNodeContents(ed);caret.collapse(false);
  const selection=window.getSelection();selection.removeAllRanges();selection.addRange(caret);
  saved=caret.cloneRange();ed.focus();
  if(notify!==false)post({t:'dirty'});
  return true;
}
function setQuoteFoldingSupported(enabled){
  quoteFoldingSupported=enabled===true;
  if(!quoteFoldingSupported)ed.classList.remove('quote-folded');
  return quoteFoldingSupported;
}
function toggleQuoteFold(){
  if(!quoteFoldingSupported||isPlainText||!ed.querySelector('.magpie-quote')) return false;
  ed.classList.toggle('quote-folded');return ed.classList.contains('quote-folded');
}
function validEditorRange(range){ return !!range && ed.contains(range.startContainer) && ed.contains(range.endContainer); }
function captureCloudLink(){
  cloudLinkTarget=null;
  if(!validEditorRange(saved))return false;
  const range=saved.cloneRange();range.collapse(false);
  cloudLinkTarget={range:range,html:ed.innerHTML,plain:isPlainText};return true;
}
function cancelCloudLink(){cloudLinkTarget=null;}
function insertCloudLink(url,label){
  const target=cloudLinkTarget;cloudLinkTarget=null;
  if(!target||target.html!==ed.innerHTML||target.plain!==isPlainText||!validEditorRange(target.range))return false;
  let parsed;try{parsed=new URL(url);}catch{return false;}
  if(parsed.protocol!=='https:'||parsed.username||parsed.password||typeof label!=='string'||!label.length)return false;
  const node=document.createElement('a');node.href=url;node.textContent=label;
  ed.focus();selectEditorRange(target.range);
  // Browser edit commands retain native undo/redo; assigning innerHTML or using insertNode would bypass it.
  const changed=document.execCommand(isPlainText?'insertText':'insertHTML',false,isPlainText?label+': '+url:node.outerHTML);
  const selection=window.getSelection();
  if(changed&&selection.rangeCount)saved=selection.getRangeAt(0).cloneRange();
  if(changed)post({t:'dirty'});return changed;
}
function restore(){ if(validEditorRange(saved)){ const s = window.getSelection(); s.removeAllRanges(); s.addRange(saved); return true; } return false; }
function fmt(c, v){
  if(isPlainText && !['undo','redo','insertText'].includes(c)) return false;
  ed.focus();if(!restore()) return false;
  const changed=document.execCommand(c, false, v === undefined ? null : v);
  if(changed) post({t:'dirty'});return changed;
}
function captureFormattingSelection(){
  formattingTarget=null;
  if(isPlainText||!validEditorRange(saved)) return false;
  formattingTarget={range:saved.cloneRange(),html:ed.innerHTML};return true;
}
function cancelFormattingSelection(){ formattingTarget=null; }
function captureEmojiSelection(){
  emojiTarget=null;
  if(!validEditorRange(saved))return false;
  emojiTarget={range:saved.cloneRange(),html:ed.innerHTML,plain:isPlainText};return true;
}
function cancelEmojiSelection(){emojiTarget=null;}
function insertSelectedEmoji(value){
  const target=emojiTarget;
  if(typeof value!=='string'||!value.length||value.length>32||/[\u0000-\u001f\u007f]/.test(value)
      ||!target||target.plain!==isPlainText||target.html!==ed.innerHTML||!validEditorRange(target.range))return false;
  saved=target.range.cloneRange();emojiTarget=null;ed.focus();restore();
  const changed=document.execCommand('insertText',false,value);
  const selection=window.getSelection();
  if(selection.rangeCount&&validEditorRange(selection.getRangeAt(0)))saved=selection.getRangeAt(0).cloneRange();
  if(changed)post({t:'dirty'});return changed;
}
function applyEditorColour(command,colour){
  if(isPlainText||!['foreColor','hiliteColor'].includes(command)||typeof colour!=='string'
      ||(!/^#[0-9a-f]{6}$/i.test(colour)&&!(command==='hiliteColor'&&colour==='transparent'))) return false;
  const target=formattingTarget;
  if(!target || target.html!==ed.innerHTML||!validEditorRange(target.range)) return false;
  const range=target.range;
  if(!validEditorRange(range)) return false;
  saved=range.cloneRange();formattingTarget=null;
  ed.focus();restore();
  const changed=document.execCommand(command,false,colour);
  const selection=window.getSelection();
  if(selection.rangeCount && validEditorRange(selection.getRangeAt(0))) saved=selection.getRangeAt(0).cloneRange();
  if(changed) post({t:'dirty'});return changed;
}
function setParagraphStyle(style){
  if(isPlainText||!['p','h1','h2','h3','pre'].includes(style)) return false;
  ed.focus();restore();
  if(!document.execCommand('formatBlock',false,style)) return false;
  if(style==='pre'){
    const node=window.getSelection()?.anchorNode;
    const element=node?.nodeType===Node.ELEMENT_NODE?node:node?.parentElement;
    const block=element?.closest('pre');
    if(block&&ed.contains(block)){
      // Keep code formatting when the HTML is sent to clients without editor CSS.
      block.style.whiteSpace='pre-wrap';block.style.overflowWrap='anywhere';
      block.style.fontFamily='Consolas, monospace';block.style.padding='8px 10px';
      block.style.border='1px solid #DCE2E7';block.style.borderRadius='5px';
    }
  }
  post({t:'dirty'});return true;
}
function linkAtCaret(){
  if(isPlainText) return null;
  if(contextualLink&&ed.contains(contextualLink)) return contextualLink;
  const node=validEditorRange(saved)?saved.startContainer:window.getSelection()?.anchorNode;
  const element=node?.nodeType===Node.ELEMENT_NODE ? node : node?.parentElement;
  const link=element?.closest('a');
  return link && ed.contains(link) ? link : null;
}
function selectedLinkUrl(){ return linkAtCaret()?.getAttribute('href') || ''; }
function validLinkUrl(url){
  if(typeof url!=='string'||url.length>2048||/[\u0000-\u0020\u007f]/.test(url)) return false;
  let parsed;try{parsed=new URL(url);}catch(e){return false;}
  return ['https:','http:'].includes(parsed.protocol)?!!parsed.hostname:parsed.protocol==='mailto:'&&!!parsed.pathname;
}
function captureLinkSelection(){
  linkEditTarget=null;
  if(isPlainText||!validEditorRange(saved)) return false;
  linkEditTarget={range:saved.cloneRange(),link:linkAtCaret(),html:ed.innerHTML};return true;
}
function cancelLinkSelection(){ linkEditTarget=null;dismissLinkBubble(); }
function setSelectedLink(url){
  if(isPlainText||!validLinkUrl(url)) return false;
  const target=linkEditTarget;
  if(!target||target.html!==ed.innerHTML||!validEditorRange(target.range)||(target.link&&!ed.contains(target.link))) return false;
  const existing=target.link;
  const range=target.range;
  if(!validEditorRange(range)) return false;
  saved=range.cloneRange();ed.focus();restore();
  if(existing) existing.setAttribute('href',url);
  else if(saved.collapsed){
    const link=document.createElement('a');link.setAttribute('href',url);link.textContent=url;
    saved.insertNode(link);saved.setStartAfter(link);saved.collapse(true);
    const selection=window.getSelection();selection.removeAllRanges();selection.addRange(saved);
  }else if(!document.execCommand('createLink',false,url)) return false;
  linkEditTarget=null;dismissedLink=existing;hideLinkBubble();
  post({t:'dirty'});return true;
}
function removeSelectedLink(){
  if(isPlainText) return false;
  const existing=linkAtCaret();
  if(!existing) return false;
  const parent=existing.parentNode,index=Array.from(parent.childNodes).indexOf(existing),count=existing.childNodes.length;
  const range=validEditorRange(saved)?saved.cloneRange():null;
  // Moving nodes preserves their markup, but live DOM ranges may collapse while those nodes move.
  const start=range?[range.startContainer,range.startOffset]:null,end=range?[range.endContainer,range.endOffset]:null;
  while(existing.firstChild) parent.insertBefore(existing.firstChild,existing);
  parent.removeChild(existing);
  function mapped(point){
    if(point[0]===existing) return [parent,index+point[1]];
    if(point[0]===parent&&point[1]>index) return [parent,point[1]+count-1];
    return point;
  }
  if(start&&end){
    try{const restored=document.createRange();restored.setStart(...mapped(start));restored.setEnd(...mapped(end));saved=restored;}
    catch(e){saved=null;}
  }
  hideLinkBubble();linkEditTarget=null;ed.focus();restore();
  post({t:'dirty'});return true;
}
function hideLinkBubble(clearTarget){
  linkBubble.hidden=true;
  if(clearTarget!==false) contextualLink=null;
}
function dismissLinkBubble(){dismissedLink=contextualLink||linkAtCaret();hideLinkBubble();}
function setLinkBubbleEnabled(enabled){linkBubbleEnabled=!!enabled;if(!linkBubbleEnabled)hideLinkBubble();}
function showLinkBubble(link){
  if(!linkBubbleEnabled||isPlainText||!link||!ed.contains(link)) {hideLinkBubble();return false;}
  contextualLink=link;
  const label=document.getElementById('linkAddress');
  label.textContent=(link.getAttribute('href')||'No address').slice(0,2048);
  label.title=label.textContent;
  linkBubble.hidden=false;
  const bounds=link.getBoundingClientRect();
  linkBubble.style.left=Math.max(12,Math.min(bounds.left,window.innerWidth-linkBubble.offsetWidth-12))+'px';
  const below=bounds.bottom+7;
  linkBubble.style.top=Math.max(8,below+linkBubble.offsetHeight<window.innerHeight?below:bounds.top-linkBubble.offsetHeight-7)+'px';
  return true;
}
document.getElementById('editContextLink').addEventListener('click',function(){
  if(captureLinkSelection()){hideLinkBubble(false);post({t:'link'});}
});
document.getElementById('removeContextLink').addEventListener('click',function(){removeSelectedLink();});
document.getElementById('closeContextLink').addEventListener('click',function(){dismissLinkBubble();ed.focus();restore();});
document.addEventListener('pointerdown',function(e){if(!linkBubble.contains(e.target)&&!e.target.closest?.('#ed a')) dismissLinkBubble();});
document.addEventListener('scroll',function(){dismissLinkBubble();},true);
window.addEventListener('resize',function(){dismissLinkBubble();});
ed.addEventListener('click', function(e){
  const link=e.target.closest?.('a');
  if(link&&ed.contains(link)){
    e.preventDefault();
    const selection=window.getSelection();
    if(selection.rangeCount&&validEditorRange(selection.getRangeAt(0))) saved=selection.getRangeAt(0).cloneRange();
    if(!saved||!link.contains(saved.startContainer)) {
      saved=document.createRange();saved.selectNodeContents(link);
      selection.removeAllRanges();selection.addRange(saved);
    }
    dismissedLink=null;showLinkBubble(link);
  }
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
function tableGrid(table){
  // Use the visual grid, not tr.cells indexes: merged cells occupy more than one slot.
  const rows=Array.from(table.rows),grid=rows.map(function(){return [];});
  const positions=new Map();
  if(rows.length>100) return null;
  for(let r=0;r<rows.length;r++){
    let column=0;
    for(const cell of rows[r].cells){
      while(grid[r][column]) column++;
      const height=cell.rowSpan,width=cell.colSpan;
      if(height<1||width<1||height>20||width>20||r+height>rows.length||column+width>100) return null;
      positions.set(cell,{row:r,col:column,height:height,width:width});
      for(let y=r;y<r+height;y++) for(let x=column;x<column+width;x++){
        if(grid[y][x]) return null;
        grid[y][x]=cell;
      }
      column+=width;
    }
  }
  const columns=grid[0]?.length||0;
  if(!columns||grid.some(function(row){
    if(row.length!==columns) return true;
    for(let column=0;column<columns;column++) if(!row[column]) return true;
    return false;
  })) return null;
  return {rows:rows,grid:grid,positions:positions};
}
function appendMergedCellContent(destination,source){
  if(source.textContent.trim()||source.querySelector('img,table,hr')){
    if(destination.textContent.trim()||destination.querySelector('img,table,hr')) destination.appendChild(document.createElement('br'));
    while(source.firstChild) destination.appendChild(source.firstChild);
  }
}
function editMergedTableCell(action,cell,table){
  const layout=tableGrid(table),at=layout?.positions.get(cell);
  if(!at) return false;
  if(action==='mergeRight'||action==='mergeDown'){
    const horizontal=action==='mergeRight';
    const targetRow=at.row+(horizontal?0:at.height),targetCol=at.col+(horizontal?at.width:0);
    const adjacent=layout.grid[targetRow]?.[targetCol],next=layout.positions.get(adjacent);
    // Both cells must form one rectangle and stay in the same table section.
    if(!next||cell.parentElement.parentElement!==adjacent.parentElement.parentElement) return false;
    if(horizontal ? (next.row!==at.row||next.height!==at.height||next.col!==targetCol)
                  : (next.col!==at.col||next.width!==at.width||next.row!==targetRow)) return false;
    appendMergedCellContent(cell,adjacent);
    adjacent.remove();
    if(horizontal) cell.colSpan=at.width+next.width;else cell.rowSpan=at.height+next.height;
  }else if(action==='splitCell'){
    if(at.width===1&&at.height===1) return false;
    // Restore every covered grid position. Existing content stays in the top-left cell.
    cell.removeAttribute('rowspan');cell.removeAttribute('colspan');
    for(let y=at.row;y<at.row+at.height;y++) for(let x=at.col;x<at.col+at.width;x++){
      if(y===at.row&&x===at.col) continue;
      const tr=layout.rows[y],newCell=document.createElement(cell.tagName.toLowerCase());
      newCell.setAttribute('style',cell.getAttribute('style')||'border:1px solid #B8C0C8;padding:5px');
      newCell.appendChild(document.createElement('br'));
      const before=Array.from(tr.cells).find(function(existing){return layout.positions.get(existing)?.col>x;});
      tr.insertBefore(newCell,before||null);
    }
  }else return false;
  post({t:'dirty'});return true;
}
function editSpannedTable(action,cell,table,quiet){
  // Reflow the logical grid while moving the original DOM nodes. Never flatten or clone
  // merged content; its formatting, links and inline pictures remain attached to that node.
  const layout=tableGrid(table),selected=layout?.positions.get(cell);
  if(!selected) return false;
  const rows=layout.rows.slice(),parent=rows[0].parentElement;
  if(!rows.every(function(row){return row.parentElement===parent;})) return false;
  const anchors=Array.from(layout.positions,function([element,pos]){
    return {element:element,row:pos.row,col:pos.col,height:pos.height,width:pos.width};
  });
  let columns=layout.grid[0].length;
  if(action==='insertRowBefore'||action==='insertRowAfter'){
    const boundary=action==='insertRowBefore'?selected.row:selected.row+selected.height;
    if(rows.length>=100) return false;
    const template=rows[Math.min(boundary,rows.length-1)];
    rows.splice(boundary,0,template.cloneNode(false));
    for(const entry of anchors){
      if(entry.row>=boundary) entry.row++;
      else if(entry.row+entry.height>boundary) entry.height++;
    }
  }else if(action==='deleteRow'){
    if(rows.length===1){table.remove();if(!quiet)post({t:'dirty'});return true;}
    const removed=selected.row;
    rows.splice(removed,1);
    for(const entry of anchors){
      if(entry.row>removed) entry.row--;
      else if(entry.row+entry.height>removed){
        if(entry.height===1) entry.deleted=true;
        else entry.height--;
      }
    }
  }else if(action==='insertColumnBefore'||action==='insertColumnAfter'){
    const boundary=action==='insertColumnBefore'?selected.col:selected.col+selected.width;
    if(columns>=100) return false;
    columns++;
    for(const entry of anchors){
      if(entry.col>=boundary) entry.col++;
      else if(entry.col+entry.width>boundary) entry.width++;
    }
  }else if(action==='deleteColumn'){
    if(columns===1){table.remove();if(!quiet)post({t:'dirty'});return true;}
    const removed=selected.col;
    columns--;
    for(const entry of anchors){
      if(entry.col>removed) entry.col--;
      else if(entry.col+entry.width>removed){
        if(entry.width===1) entry.deleted=true;
        else entry.width--;
      }
    }
  }else return false;

  if(anchors.some(function(entry){return !entry.deleted&&(entry.height>20||entry.width>20);})) return false;
  const occupied=rows.map(function(){return Array(columns).fill(false);});
  for(const entry of anchors){
    if(entry.deleted) continue;
    if(entry.row<0||entry.col<0||entry.row+entry.height>rows.length||entry.col+entry.width>columns) return false;
    for(let y=entry.row;y<entry.row+entry.height;y++) for(let x=entry.col;x<entry.col+entry.width;x++){
      if(occupied[y][x]) return false;
      occupied[y][x]=true;
    }
  }
  for(let y=0;y<rows.length;y++) for(let x=0;x<columns;x++){
    if(occupied[y][x]) continue;
    const reference=rows[y].cells[0]||cell;
    const element=document.createElement(reference.tagName.toLowerCase());
    element.setAttribute('style',reference.getAttribute('style')||'border:1px solid #B8C0C8;padding:5px');
    element.appendChild(document.createElement('br'));
    anchors.push({element:element,row:y,col:x,height:1,width:1});
    occupied[y][x]=true;
  }
  for(let y=0;y<rows.length;y++){
    const starts=anchors.filter(function(a){return !a.deleted&&a.row===y;}).sort(function(a,b){return a.col-b.col;});
    rows[y].replaceChildren();
    for(const entry of starts){
      if(entry.height===1) entry.element.removeAttribute('rowspan');else entry.element.rowSpan=entry.height;
      if(entry.width===1) entry.element.removeAttribute('colspan');else entry.element.colSpan=entry.width;
      rows[y].appendChild(entry.element);
    }
  }
  parent.replaceChildren(...rows);
  if(!quiet){
    if(!ed.contains(selectedTableCell)) selectedTableCell=table.querySelector('td,th');
    post({t:'dirty'});
  }
  return true;
}
function deleteTableNative(action,cell,table){
  // Plan only the affected table off-document. The browser owns the one deletion/replacement
  // transaction, including its original nodes and all earlier/later native editing history.
  const holder=document.createElement('div'),copy=table.cloneNode(true);
  holder.appendChild(copy);
  const originalNodes=[table].concat(Array.from(table.querySelectorAll('*')));
  const copiedNodes=[copy].concat(Array.from(copy.querySelectorAll('*')));
  const origins=new Map(copiedNodes.map(function(node,index){return [node,originalNodes[index]];}));
  const copiedCell=copiedNodes[originalNodes.indexOf(cell)],row=cell.parentElement;
  const rowIndex=Array.from(table.rows).indexOf(row),column=Array.from(row.cells).indexOf(cell);
  if(action!=='deleteTable'&&(rowIndex<0||column<0||!copiedCell))return false;
  if(action==='deleteTable') copy.remove();
  else if(copy.querySelector('[rowspan],[colspan]')){
    if(!editSpannedTable(action,copiedCell,copy,true))return false;
  }else if(action==='deleteRow'){
    if(copy.rows.length===1)copy.remove();else copy.rows[rowIndex].remove();
  }else if(action==='deleteColumn'){
    if(row.cells.length===1)copy.remove();
    else Array.from(copy.rows).forEach(function(tr){if(tr.cells[column])tr.cells[column].remove();});
  }else return false;

  const remaining=copy.parentNode===holder;
  const nextCell=remaining?(copy.contains(copiedCell)?copiedCell:copy.querySelector('td,th')):null;
  const plannedNodes=remaining?[copy].concat(Array.from(copy.querySelectorAll('*'))):[];
  const records=plannedNodes.map(function(node){return protectedFonts.get(origins.get(node));});
  // Temporary provenance lives only on the inserted nodes, never on the live original table.
  // Native Undo keeps those same nodes; removing the attributes does not replace its references.
  let marker='data-magpie-table-edit-'+Date.now().toString(36)+'-'+Math.random().toString(36).slice(2);
  while(ed.querySelector('['+marker+']'))marker+='x';
  plannedNodes.forEach(function(node,index){node.setAttribute(marker,String(index));});
  const nextIndex=plannedNodes.indexOf(nextCell),guards=[],guarded=new Map();
  const selectionBefore=window.getSelection(),rangesBefore=[];
  for(let index=0;index<selectionBefore.rangeCount;index++)rangesBefore.push(selectionBefore.getRangeAt(index).cloneRange());
  const savedBefore=saved?.cloneRange()||null;
  let changed=false,replacement=null,selectionCell=null;
  function guard(node,block){
    if(node.nodeType!==Node.ELEMENT_NODE)return;
    let entry=guarded.get(node);
    if(!entry){
      entry={node:node,editable:node.getAttribute('contenteditable'),style:node.getAttribute('style'),block:false};
      guarded.set(node,entry);guards.push(entry);node.setAttribute('contenteditable','false');
    }
    if(block&&!entry.block){
      const display=getComputedStyle(node).display;
      if(display.startsWith('inline')||display==='contents'){
        const style=document.createElement('span').style;
        style.cssText=entry.style||'';style.setProperty('display','block','important');
        node.setAttribute('style',style.cssText);entry.block=true;
      }
    }
    node.querySelectorAll('[contenteditable]').forEach(function(child){
      if(guarded.has(child))return;
      const childEntry={node:child,editable:child.getAttribute('contenteditable'),style:child.getAttribute('style'),block:false};
      guarded.set(child,childEntry);guards.push(childEntry);
      child.setAttribute('contenteditable','false');
    });
  }
  try{
    // A table is one atomic selected object. Do not let native selection enter its first cell
    // or normalize a neighboring paragraph, signature or quote while replacing it.
    guard(table,false);
    for(let branch=table;branch&&branch!==ed;branch=branch.parentNode){
      Array.from(branch.parentNode.childNodes).forEach(function(node){if(node!==branch)guard(node,true);});
    }
    const range=document.createRange();range.selectNode(table);
    ed.focus();const selection=window.getSelection();selection.removeAllRanges();selection.addRange(range);
    changed=document.execCommand(remaining?'insertHTML':'delete',false,remaining?copy.outerHTML:null);
    if(changed&&remaining){
      ed.querySelectorAll('['+marker+']').forEach(function(node){
        const index=Number(node.getAttribute(marker));
        if(index===0)replacement=node;
        if(index===nextIndex)selectionCell=node;
        if(records[index])protectedFonts.set(node,{...records[index]});
      });
    }
  }finally{
    ed.querySelectorAll('['+marker+']').forEach(function(node){node.removeAttribute(marker);});
    guards.forEach(function(entry){
      if(entry.editable===null)entry.node.removeAttribute('contenteditable');else entry.node.setAttribute('contenteditable',entry.editable);
      if(entry.block){if(entry.style===null)entry.node.removeAttribute('style');else entry.node.setAttribute('style',entry.style);}
    });
  }
  if(!changed){
    const selection=window.getSelection();selection.removeAllRanges();
    rangesBefore.forEach(function(range){if(validEditorRange(range))selection.addRange(range);});
    saved=savedBefore&&validEditorRange(savedBefore)?savedBefore:null;
    return false;
  }
  selectedTableCell=selectionCell||(replacement?.querySelector('td,th'))||null;
  selectedImage=null;rewrite=null;mentionRange=null;mentionToken='';mentionMenuOpen=false;
  clipboardPictureTarget=null;cloudLinkTarget=null;hideLinkBubble();linkEditTarget=null;
  formattingTarget=null;dismissedLink=null;emojiTarget=null;
  const selection=window.getSelection();
  saved=selection.rangeCount&&validEditorRange(selection.getRangeAt(0))?selection.getRangeAt(0).cloneRange():null;
  post({t:'dirty'});return true;
}
function editTable(action){
  const cell=selectedTableCell;
  if(!cell||!ed.contains(cell)) return false;
  const row=cell.parentElement,table=cell.closest('table');
  if(!row||!table) return false;
  if(['deleteTable','deleteRow','deleteColumn'].includes(action)) return deleteTableNative(action,cell,table);
  if(['mergeRight','mergeDown','splitCell'].includes(action)) return editMergedTableCell(action,cell,table);
  // Use a logical cell grid when rows or columns are merged; preserve the original cell nodes.
  if(table.querySelector('[rowspan],[colspan]')) return editSpannedTable(action,cell,table);
  const rows=Array.from(table.rows),rowIndex=rows.indexOf(row),column=Array.from(row.cells).indexOf(cell);
  if(rowIndex<0||column<0) return false;
  if(action==='insertRowBefore'||action==='insertRowAfter'){
    const created=row.cloneNode(false);
    for(let i=0;i<row.cells.length;i++){const td=document.createElement('td');td.setAttribute('style','border:1px solid #B8C0C8;padding:5px');td.appendChild(document.createElement('br'));created.appendChild(td);}
    row.parentNode.insertBefore(created,action==='insertRowBefore'?row:row.nextSibling);
    selectedTableCell=created.cells[Math.min(column,created.cells.length-1)];
  }
  else if(action==='insertColumnBefore'||action==='insertColumnAfter'){
    for(const tr of rows){
      const td=document.createElement('td');td.setAttribute('style','border:1px solid #B8C0C8;padding:5px');td.appendChild(document.createElement('br'));
      const at=column+(action==='insertColumnAfter'?1:0);tr.insertBefore(td,tr.cells[at]||null);
    }
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
  block.setAttribute('lang',language);block.setAttribute('spellcheck',ed.spellcheck?'true':'false');
  post({t:'dirty'});return true;
}
function editorSearchPattern(query,matchCase){
  if(typeof query!=='string' || !query.length || query.length>200) return null;
  // Match the original string: Unicode case folding must not change DOM UTF-16 offsets.
  // Escape the user's literal query; the Unicode ignore-case flag is independent of the host locale.
  return new RegExp(query.replace(/[.*+?^$(){}|[\]\\]/g,'\\$&'),matchCase?'gu':'giu');
}
function editorMatches(query, matchCase){
  const pattern=editorSearchPattern(query,matchCase),matches=[];
  if(!pattern) return matches;
  // Match contiguous inline text, retaining original UTF-16 offsets for native DOM ranges.
  // Separators end a run instead of inventing characters that a replacement could consume.
  let parts=[],length=0;
  function flush(){
    if(!parts.length)return true;
    const text=parts.map(part=>part.node.nodeValue).join('');let index=0;
    pattern.lastIndex=0;
    for(let match;(match=pattern.exec(text))!==null;){
      const start=match.index,end=start+match[0].length;
      while(parts[index].end<=start)index++;
      const first=parts[index];
      while(parts[index].end<end)index++;
      const last=parts[index],range=document.createRange();
      range.setStart(first.node,start-first.start);range.setEnd(last.node,end-last.start);
      matches.push(range);
      if(matches.length>2000)return false;
    }
    parts=[];length=0;return true;
  }
  const folded=ed.classList.contains('quote-folded'),pending=[{node:ed,visible:true}];
  while(pending.length){
    const entry=pending.pop(),node=entry.node;
    if(entry.end){if(!flush())return null;continue;}
    if(node.nodeType===Node.TEXT_NODE){
      if(!entry.visible){if(!flush())return null;continue;}
      if(node.nodeValue.length){parts.push({node:node,start:length,end:length+node.nodeValue.length});length+=node.nodeValue.length;}
      continue;
    }
    if(node.nodeType!==Node.ELEMENT_NODE)continue;
    const style=getComputedStyle(node);
    const skipped=node.hidden||style.display==='none'||node.getAttribute('contenteditable')?.toLowerCase()==='false'
      || (folded&&node.classList.contains('magpie-quote'))
      || /^(br|hr|img|video|audio|object|iframe|input|textarea|select|button|canvas|svg|math|script|style|template|noscript)$/.test(node.localName);
    if(skipped){if(!flush())return null;continue;}
    const visible=style.visibility!=='hidden'&&style.visibility!=='collapse';
    const boundary=node!==ed&&(style.display!=='inline'&&style.display!=='contents'||!visible);
    if(boundary){if(!flush())return null;pending.push({end:true});}
    // Hidden visibility can be overridden by a visible descendant; display:none and readonly
    // subtrees above are excluded entirely. Neither kind can join the text on its two sides.
    for(let i=node.childNodes.length-1;i>=0;i--)pending.push({node:node.childNodes[i],visible:visible});
  }
  return flush()?matches:null;
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
  // Compare starts against an end-collapsed copy; START_TO_END compares the opposite pair of endpoints.
  const after=validEditorRange(saved)?saved.cloneRange():null;
  if(after)after.collapse(false);
  const index=matches.findIndex(function(range){
    return !after || range.compareBoundaryPoints(Range.START_TO_START,after)>=0;
  });
  const selected=index>=0?index:0;
  selectEditorRange(matches[selected]);
  return {count:matches.length,index:selected+1};
}
function replaceFoundText(query,replacement,matchCase){
  if(!validEditorRange(saved)||saved.collapsed||typeof replacement!=='string')return false;
  const matches=editorMatches(query,matchCase),selected=saved.toString();
  // A manually selected string spanning a hidden, readonly or block boundary is not a Find match.
  // Parent-element endpoints may enclose the same text plus empty structure: replace only the
  // eligible text range, preserving that structure rather than requiring identical endpoint nodes.
  const match=matches?.find(range=>range.toString()===selected
    &&range.compareBoundaryPoints(Range.START_TO_START,saved)>=0&&range.compareBoundaryPoints(Range.END_TO_END,saved)<=0);
  if(!match)return false;
  selectEditorRange(match);
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
  if (s.rangeCount && validEditorRange(s.getRangeAt(0))) {
    saved=s.getRangeAt(0).cloneRange();post({t:'sel',text:s.toString()});
    if(!linkEditTarget){
      const explicitLink=contextualLink;
      contextualLink=null;
      const link=linkAtCaret();
      if(!link)dismissedLink=null;
      if(link&&(s.isCollapsed||(explicitLink===link&&link.contains(saved.endContainer)))&&link!==dismissedLink)showLinkBubble(link);else hideLinkBubble();
    }
  }
});
function esc(t){ return t.replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;'); }
function toHtml(t){ return t.replace(/\r\n/g,'\n').split(/\n{2,}/).map(function(p){ return '<p'+(typography?' style="'+esc(fontCss(typography.body)).replace(/"/g,'&quot;')+'"':'')+'>' + esc(p).replace(/\n/g,'<br>') + '</p>'; }).join(''); }
function inlineHtml(t){ return esc(t).replace(/\r?\n/g,'<br>'); }
function topChild(n){ while(n && n.parentNode !== ed) n = n.parentNode; return n; }
function insertBody(text, replace){
  // Only generated body text passes through the browser's paste normalizer. The live signature
  // and quote keep their markup, identity and typography-reset records.
  const guards=[],guarded=new Map(),selection=window.getSelection();let changed=false,caret=null;
  function guard(node,block){
    if(node.nodeType!==Node.ELEMENT_NODE)return;
    let entry=guarded.get(node);
    if(!entry){
      entry={node:node,editable:node.getAttribute('contenteditable'),style:node.getAttribute('style'),block:false};
      guarded.set(node,entry);guards.push(entry);node.setAttribute('contenteditable','false');
    }
    if(block&&!entry.block){
      const display=getComputedStyle(node).display;
      if(display.startsWith('inline')||display==='contents'){
        // Build the temporary rule off-document. Mutating the live CSSOM can leave style=""
        // after removing an originally absent style attribute, breaking exact native Undo.
        const style=document.createElement('span').style;
        style.cssText=entry.style||'';style.setProperty('display','block','important');
        node.setAttribute('style',style.cssText);entry.block=true;
      }
    }
  }
  function guardTail(node){
    if(node.nodeType!==Node.ELEMENT_NODE)return;
    guard(node,true);
    // Explicit descendants can re-enable editing beneath a noneditable ancestor.
    node.querySelectorAll('[contenteditable]').forEach(function(child){guard(child,false);});
  }
  try{
    const range=document.createRange();let insertion;
    if(isPlainText){
      const current=ed.innerText;
      insertion=replace?text:current+(current.length?'\n\n':'')+text;
      range.selectNodeContents(ed);
    }else{
      const stop=ed.querySelector('.magpie-signature,.magpie-quote');
      ed.querySelectorAll('.magpie-signature,.magpie-quote').forEach(function(node){guard(node,false);});
      // Protect the entire retained tail, including siblings outside a nested quote wrapper.
      for(let branch=stop;branch&&branch!==ed;branch=branch.parentNode){
        for(let node=branch===stop?branch:branch.nextSibling;node;node=node.nextSibling)guardTail(node);
      }
      if(replace){range.setStart(ed,0);if(stop)range.setEndBefore(stop);else range.setEnd(ed,ed.childNodes.length);}
      else{if(stop)range.setStartBefore(stop);else range.setStart(ed,ed.childNodes.length);range.collapse(true);}
      // Keep the author body editable: guarding both sides leaves no native caret position.
      // Blink/WebKit consume this marker to separate an append from existing body formatting
      // inside the same undoable edit, without rewriting or temporarily wrapping those nodes.
      insertion=(!replace&&getNewText().length>0?'<br class="Apple-interchange-newline">':'')+toHtml(text);
    }
    ed.focus();selection.removeAllRanges();selection.addRange(range);
    changed=document.execCommand(isPlainText?'insertText':'insertHTML',false,insertion);
    if(changed&&selection.rangeCount)caret=selection.getRangeAt(0).cloneRange();
  }finally{
    guards.forEach(function(entry){
      if(entry.editable===null)entry.node.removeAttribute('contenteditable');else entry.node.setAttribute('contenteditable',entry.editable);
      if(entry.block){if(entry.style===null)entry.node.removeAttribute('style');else entry.node.setAttribute('style',entry.style);}
    });
  }
  if(!changed)return false;
  rewrite=null;mentionRange=null;mentionToken='';mentionMenuOpen=false;selectedImage=null;selectedTableCell=null;
  clipboardPictureTarget=null;cloudLinkTarget=null;hideLinkBubble();linkEditTarget=null;formattingTarget=null;dismissedLink=null;emojiTarget=null;
  // Keep the native caret inside the generated body; the boundary before an editable signature
  // can otherwise canonicalize into that signature when the user types next.
  saved=null;
  if(caret&&validEditorRange(caret)){caret.collapse(false);selection.removeAllRanges();selection.addRange(caret);saved=caret.cloneRange();}
  return true;
}
function cmd(mode, text){
  // Plain conversion loses the signature/quote boundary. This state is restored with saved drafts;
  // switching back to rich text cannot make a whole-body replacement safe again.
  if((mode==='replaceBody'||mode==='appendBody'||mode==='replyDraft')&&!canReplaceBody())return false;
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
    ed.focus();
    if(!document.execCommand(isPlainText?'insertText':'insertHTML',false,isPlainText?text:inlineHtml(text)))return false;
    if(s.rangeCount)saved=s.getRangeAt(0).cloneRange();
    rewrite = null;
  }
  else if (mode === 'insert' || mode === 'insertLiteral') {
    ed.focus();if(!restore()) return false;
    // Literal canned replies use inline breaks: wrapping them in <p> lets Chromium promote a
    // single newline to a paragraph boundary and changes the reply's visible text.
    const insertion=isPlainText?text:(mode==='insertLiteral'?inlineHtml(text):toHtml(text));
    if(!document.execCommand(isPlainText?'insertText':'insertHTML',false,insertion)) return false;
  }
  else if (mode === 'replaceBody') {
    if(!insertBody(text,true))return false;
  }
  else if(mode === 'appendBody') {
    if(!insertBody(text,false))return false;
  }
  post({t:'dirty'});
  return true;
}
ed.addEventListener('input', function(){ post({t:'dirty'});inspectMention(); });
let nativeAttachmentPaste=false;
let clipboardPictureTarget=null;
function setNativeAttachmentPaste(enabled){nativeAttachmentPaste=!!enabled;}
function clipboardHasFiles(data){
  return !!data && ((data.files?.length||0)>0 || Array.from(data.items||[]).some(item=>item.kind==='file'));
}
ed.addEventListener('paste',function(e){
  // Only the Windows host opts in; normal text/HTML paste and the Mac editor keep their existing behaviour.
  if(!nativeAttachmentPaste || !e.isTrusted || !clipboardHasFiles(e.clipboardData)) return;
  e.preventDefault();
  const selection=window.getSelection();
  if(selection.rangeCount && ed.contains(selection.anchorNode)) saved=selection.getRangeAt(0).cloneRange();
  post({t:'clipboardPaste'});
});
function captureClipboardPicture(){
  clipboardPictureTarget=null;
  if(isPlainText || !validEditorRange(saved)) return false;
  clipboardPictureTarget={range:saved.cloneRange(),html:ed.innerHTML};return true;
}
function cancelClipboardPicture(){clipboardPictureTarget=null;}
function insertClipboardPicture(data){
  if(isPlainText || typeof data!=='string' || data.length>11200000 || !/^data:image\/png;base64,[A-Za-z0-9+/]+=*$/.test(data)) return false;
  const target=clipboardPictureTarget;
  if(!target || target.html!==ed.innerHTML || !validEditorRange(target.range)) return false;
  saved=target.range.cloneRange();clipboardPictureTarget=null;
  ed.focus();restore();
  if(!document.execCommand('insertImage',false,data)) return false;
  post({t:'dirty'});return true;
}
document.addEventListener('keydown', function(e){
  if(e.key==='Escape'&&!linkBubble.hidden){e.preventDefault();dismissLinkBubble();ed.focus();restore();return;}
  if(mentionMenuOpen && ['ArrowUp','ArrowDown','Enter','Tab','Escape'].includes(e.key)){
    e.preventDefault();post({t:'mentionKey',key:e.key});return;
  }
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
