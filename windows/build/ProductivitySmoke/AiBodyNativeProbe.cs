using System.Text.Json;

namespace ProductivitySmoke;

internal static partial class Program
{
    // Temporary comparative evidence only. Remove this file and its independent call after the
    // production strategy is selected; the ordinary strict editor assertions remain the gate.
    private static async Task ProbeAiBodyBoundaryCandidatesAsync(Func<string, Task<string>> script)
    {
        const string protectedTail = "<div class='magpie-signature' contenteditable='true'><p style='font-family:Georgia;font-size:24px'>Signature <b>stays</b></p></div><div class='magpie-quote'><blockquote>Original quote</blockquote><p>Older hidden paragraph</p></div>";
        const string tailParagraph = "<p style='color:navy'>Unmarked tail stays too.</p>";
        const string author = "<p data-ai-existing style='color:purple;font-family:Georgia'><b>Existing opening.</b></p>";
        const string pixel = "<img alt='Inline image' width='16' height='16' src='data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7'>";
        var cases = new[]
        {
            (Name: "marked-tail-only", Html: author + protectedTail),
            (Name: "nested-editable-tail", Html: "<section>" + author + protectedTail + tailParagraph + "</section>"),
            (Name: "empty-body", Html: protectedTail + tailParagraph),
            (Name: "nested-empty-body", Html: "<section>" + protectedTail + tailParagraph + "</section>"),
            (Name: "raw-author-before-tail", Html: "Unwrapped opening." + protectedTail + tailParagraph),
            (Name: "raw-author-no-tail", Html: "Unwrapped opening."),
            (Name: "styled-inline-author", Html: "<span data-ai-existing contenteditable='true' style='font-family:Georgia;font-size:22px'><b>Inline opening.</b></span>" + protectedTail + tailParagraph),
            (Name: "formatted-author-no-tail", Html: author),
            (Name: "nested-raw-tail", Html: "<section style='font-family:Georgia;font-size:21px;font-weight:600;letter-spacing:0.4px;color:teal;line-height:1.6'>" + author + protectedTail + "Raw tail stays too.</section>"),
            (Name: "raw-tail-outside-wrapper", Html: "<section>" + author + protectedTail + "</section>Raw outside tail stays too."),
            (Name: "editable-tail-outside-wrapper", Html: "<section>" + author + protectedTail + "</section>" + tailParagraph),
            (Name: "empty-no-tail", Html: "<p><br></p>"),
            (Name: "inline-image-trailing-br", Html: "<p data-ai-existing style='font-family:Georgia'>Image opening." + pixel + "<br></p>" + protectedTail + tailParagraph)
        };
        await script("""
            window.runAiNativeProbe=function(fixture,mode,strategy){
              const text='Generated boundary answer.\n\nSecond boundary paragraph.';
              const bound=(value,limit=1600)=>typeof value==='string'&&value.length>limit?value.slice(0,limit)+' [truncated]':value;
              const normalize=value=>value.replace(/\r\n/g,'\n').replace(/\n+/g,'\n').trim();
              const nodeValue=node=>node.nodeType===Node.ELEMENT_NODE?node.outerHTML:node.nodeValue;
              const tailNodes=()=>{const stop=ed.querySelector('.magpie-signature,.magpie-quote'),nodes=[];if(!stop)return nodes;const walker=document.createTreeWalker(ed,NodeFilter.SHOW_ALL);let seen=false,node;while(node=walker.nextNode()){if(node===stop)seen=true;if(seen)nodes.push(node);}return nodes;};
              const tailText=nodes=>nodes.filter(node=>node.nodeType===Node.TEXT_NODE).map(node=>node.nodeValue).join('');
              function renderedBody(){
                const range=document.createRange(),stop=ed.querySelector('.magpie-signature,.magpie-quote'),selection=window.getSelection(),previous=[];
                range.selectNodeContents(ed);if(stop)range.setEndBefore(stop);
                for(let i=0;i<selection.rangeCount;i++)previous.push(selection.getRangeAt(i).cloneRange());
                try{selection.removeAllRanges();selection.addRange(range);return normalize(selection.toString());}
                finally{selection.removeAllRanges();previous.forEach(item=>selection.addRange(item));}
              }
              function point(node,offset){const element=node?.nodeType===Node.ELEMENT_NODE?node:node?.parentElement;return {node:node?.nodeName,offset:offset,element:element?.tagName,editable:element?.isContentEditable};}
              function normalizeLooseTailText(){
                const stop=ed.querySelector('.magpie-signature,.magpie-quote'),created=[];
                for(let branch=stop;branch&&branch!==ed;branch=branch.parentNode){
                  for(let node=branch===stop?branch:branch.nextSibling;node;){
                    const next=node.nextSibling;
                    if(node.nodeType===Node.TEXT_NODE&&node.nodeValue.length){
                      const span=document.createElement('span');node.replaceWith(span);span.appendChild(node);created.push(span);
                    }
                    node=next;
                  }
                }
                return created;
              }
              const inheritedProperties=['font-family','font-size','font-weight','font-style','font-stretch','font-variant','font-feature-settings','font-kerning','line-height','letter-spacing','word-spacing','color','text-transform','white-space','direction','writing-mode','text-align','text-indent','tab-size','word-break','overflow-wrap'];
              function inheritedTextStyle(node){const style=getComputedStyle(node.parentElement);return Object.fromEntries(inheritedProperties.map(name=>[name,style.getPropertyValue(name)]));}
              const result={fixture:fixture.name,mode:mode,candidate:strategy,ok:false,changed:false,calls:0,enabled:null,tailNodes:false,tailMarkup:false,tailText:false,author:false,body:false,undo:false,redo:false,blockGuards:0,guardsRestored:false};
              setDark(false);setQuoteFoldingSupported(true);setHtml(fixture.html,false);
              const loadedHtml=ed.innerHTML,loadedVisible=ed.innerText,loadedTail=tailNodes().map(node=>({node:node,value:nodeValue(node)}));
              const loadedTextStyles=loadedTail.filter(entry=>entry.node.nodeType===Node.TEXT_NODE).map(entry=>({node:entry.node,style:inheritedTextStyle(entry.node)}));
              const normalization={applied:strategy!=='C_full_tail',wrapped:0,visible:false,nodes:false,markup:false,styles:false,idempotent:false,ok:false};
              let normalizedHtml=loadedHtml,normalizedVisible=loadedVisible,styleChanges=[],normalizationError=null;
              try{
                if(normalization.applied)normalization.wrapped=normalizeLooseTailText().length;
                normalizedHtml=ed.innerHTML;normalizedVisible=ed.innerText;
                const normalizedTail=tailNodes(),originalNodes=new Set(loadedTail.map(entry=>entry.node)),retained=normalizedTail.filter(node=>originalNodes.has(node));
                normalization.visible=normalizedVisible===loadedVisible;
                normalization.nodes=retained.length===loadedTail.length&&retained.every((node,index)=>node===loadedTail[index].node);
                normalization.markup=loadedTail.every(entry=>ed.contains(entry.node)&&nodeValue(entry.node)===entry.value);
                styleChanges=loadedTextStyles.map(entry=>({text:entry.node.nodeValue,before:entry.style,after:inheritedTextStyle(entry.node)})).filter(entry=>JSON.stringify(entry.before)!==JSON.stringify(entry.after));
                normalization.styles=styleChanges.length===0;
                const secondPass=normalization.applied?normalizeLooseTailText():[],secondTail=tailNodes();
                normalization.idempotent=secondPass.length===0&&ed.innerHTML===normalizedHtml&&secondTail.length===normalizedTail.length&&secondTail.every((node,index)=>node===normalizedTail[index]);
                normalization.ok=normalization.visible&&normalization.nodes&&normalization.markup&&normalization.styles&&normalization.idempotent;
              }catch(ex){normalizationError=String(ex?.stack||ex);}
              result.normalization=normalization;
              if(!normalization.ok){
                result.diagnostic={stage:'tail-normalization',loaded:bound(loadedHtml),normalized:bound(normalizedHtml),visibleBefore:bound(loadedVisible),visibleAfter:bound(normalizedVisible),styleChanges:styleChanges.slice(0,3),error:bound(normalizationError)};
                setHtml('<p><br></p>',false);return result;
              }
              // Stable wrappers are part of the document before the real native Undo baseline.
              // Keep them attached through the edit, Undo and Redo; never unwrap them afterward.
              const beforeHtml=ed.innerHTML,beforeText=renderedBody(),expectedText=(mode==='appendBody'&&beforeText?beforeText+'\n':'')+normalize(text);
              const beforeTail=tailNodes().map(node=>({node:node,value:nodeValue(node)})),beforeTailText=tailText(beforeTail.map(entry=>entry.node));
              const beforeAuthor=Array.from(ed.querySelectorAll('[data-ai-existing]')).map(node=>({node:node,html:node.outerHTML}));
              const descriptor=Object.getOwnPropertyDescriptor(document,'execCommand'),native=document.execCommand,guards=[];
              let endpoints=null,afterHtml=beforeHtml,afterText=beforeText,afterTailText=beforeTailText,undoHtml=null,redoHtml=null,error=null;
              function guard(node){
                if(node.nodeType!==Node.ELEMENT_NODE||guards.some(entry=>entry.node===node))return;
                const entry={node:node,editable:node.getAttribute('contenteditable'),style:node.getAttribute('style'),block:false};guards.push(entry);
                node.setAttribute('contenteditable','false');
                const display=getComputedStyle(node).display;
                if(strategy==='F_stable_tail_spans_block'&&(display.startsWith('inline')||display==='contents')){entry.block=true;node.style.setProperty('display','block','important');result.blockGuards++;}
              }
              function restoreGuards(){
                guards.forEach(entry=>{
                  if(entry.editable===null)entry.node.removeAttribute('contenteditable');else entry.node.setAttribute('contenteditable',entry.editable);
                  if(entry.block){if(entry.style===null)entry.node.removeAttribute('style');else entry.node.setAttribute('style',entry.style);}
                });
                result.guardsRestored=guards.every(entry=>entry.node.getAttribute('contenteditable')===entry.editable&&entry.node.getAttribute('style')===entry.style);guards.length=0;
              }
              function restoreNative(){if(descriptor)Object.defineProperty(document,'execCommand',descriptor);else delete document.execCommand;}
              function tailStatus(){const current=tailNodes();return {nodes:current.length===beforeTail.length&&current.every((node,index)=>node===beforeTail[index].node),markup:beforeTail.every(entry=>ed.contains(entry.node)&&nodeValue(entry.node)===entry.value),text:tailText(current)===beforeTailText,count:current.length};}
              try{
                document.execCommand=function(command,ui,value){
                  if(command==='insertHTML'||command==='insertText'){
                    result.calls++;result.enabled=document.queryCommandEnabled(command);
                    const selection=window.getSelection(),range=selection.rangeCount?selection.getRangeAt(0):null;
                    endpoints=range?{start:point(range.startContainer,range.startOffset),end:point(range.endContainer,range.endOffset)}:null;
                  }
                  return native.call(document,command,ui,value);
                };
                {
                  const stop=ed.querySelector('.magpie-signature,.magpie-quote');
                  const marker=mode==='appendBody'&&getNewText().length>0?'<br class="Apple-interchange-newline">':'';
                  ed.querySelectorAll('.magpie-signature,.magpie-quote').forEach(guard);
                  for(let branch=stop;branch&&branch!==ed;branch=branch.parentNode){
                    for(let node=branch===stop?branch:branch.nextSibling;node;node=node.nextSibling)guard(node);
                  }
                  const range=document.createRange();
                  if(mode==='replaceBody'){
                    range.setStart(ed,0);if(stop)range.setEndBefore(stop);else range.setEnd(ed,ed.childNodes.length);
                  }else{
                    if(stop)range.setStartBefore(stop);else range.setStart(ed,ed.childNodes.length);range.collapse(true);
                  }
                  ed.focus();const selection=window.getSelection();selection.removeAllRanges();selection.addRange(range);
                  result.changed=document.execCommand('insertHTML',false,marker+toHtml(text));
                }
              }catch(ex){error=String(ex?.stack||ex);}
              finally{restoreGuards();restoreNative();}
              try{
                afterHtml=ed.innerHTML;afterText=renderedBody();afterTailText=tailText(tailNodes());
                const tail=tailStatus();result.tailNodes=tail.nodes;result.tailMarkup=tail.markup;result.tailText=tail.text;
                result.author=mode!=='appendBody'||beforeAuthor.every(entry=>ed.contains(entry.node)&&entry.node.outerHTML===entry.html);
                result.body=afterText===expectedText;result.markerGone=!afterHtml.includes('Apple-interchange-newline');
                if(result.changed){
                  ed.focus();const undone=native.call(document,'undo',false,null);undoHtml=ed.innerHTML;
                  result.nativeUndo=undone;result.undoExact=undoHtml===beforeHtml;
                  let tailAfter=tailStatus();result.undo=undone&&result.undoExact&&tailAfter.nodes&&tailAfter.markup&&tailAfter.text;
                  const redone=native.call(document,'redo',false,null);redoHtml=ed.innerHTML;
                  result.nativeRedo=redone;result.redoExact=redoHtml===afterHtml;
                  tailAfter=tailStatus();result.redo=redone&&result.redoExact&&tailAfter.nodes&&tailAfter.markup&&tailAfter.text;
                }
                result.ok=!error&&normalization.ok&&result.guardsRestored&&result.changed&&result.calls===1&&result.tailNodes&&result.tailMarkup&&result.tailText&&result.author&&result.body&&result.markerGone&&result.undo&&result.redo;
                if(!result.ok)result.diagnostic={endpoint:'body-boundary',selection:endpoints,before:bound(beforeHtml),after:bound(afterHtml),expectedText:bound(expectedText,300),actualText:bound(afterText,300),tailBefore:bound(beforeTailText,300),tailAfter:bound(afterTailText,300),undoHtml:result.undo?undefined:bound(undoHtml),redoHtml:result.redo?undefined:bound(redoHtml),error:bound(error)};
                return result;
              }catch(ex){result.diagnostic={endpoint:'body-boundary',selection:endpoints,before:bound(beforeHtml),after:bound(ed.innerHTML),error:bound(String(ex?.stack||ex))};return result;}
              finally{setHtml('<p><br></p>',false);}
            };
            """);
        var totals = new Dictionary<string, int>();
        var passes = new Dictionary<string, int>();
        try
        {
            foreach (var fixture in cases)
            foreach (var mode in new[] { "appendBody", "replaceBody" })
            foreach (var candidate in new[] { "C_full_tail", "E_stable_tail_spans", "F_stable_tail_spans_block" })
            {
                var json = await script("window.runAiNativeProbe(" + JsonSerializer.Serialize(new { name = fixture.Name, html = fixture.Html })
                    + "," + JsonSerializer.Serialize(mode) + "," + JsonSerializer.Serialize(candidate) + ")");
                Console.WriteLine("AI_NATIVE_PROBE " + json);
                using var result = JsonDocument.Parse(json);
                totals[candidate] = totals.GetValueOrDefault(candidate) + 1;
                if (result.RootElement.GetProperty("ok").GetBoolean()) passes[candidate] = passes.GetValueOrDefault(candidate) + 1;
            }
        }
        finally { await script("delete window.runAiNativeProbe;setHtml('<p><br></p>',false)"); }
        Console.WriteLine("AI_NATIVE_PROBE_SUMMARY " + JsonSerializer.Serialize(totals.Select(entry => new { candidate = entry.Key, passed = passes.GetValueOrDefault(entry.Key), total = entry.Value })));
        Console.WriteLine("AI_NATIVE_PROBE_MATRIX_COMPLETE cases=" + totals.Values.Sum());
    }
}
