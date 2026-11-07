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
            (Name: "nested-raw-tail", Html: "<section>" + author + protectedTail + "Raw tail stays too.</section>"),
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
              function authorEndpoint(stop){
                const walker=document.createTreeWalker(ed,NodeFilter.SHOW_ELEMENT|NodeFilter.SHOW_TEXT);let node,last=null;
                while((node=walker.nextNode())&&node!==stop){
                  const element=node.nodeType===Node.TEXT_NODE?node.parentElement:node;
                  if(!element?.isContentEditable)continue;
                  if(node.nodeType===Node.TEXT_NODE&&node.textContent.trim())last={node:node,offset:node.textContent.length,kind:'text-end'};
                  else if(node.nodeType===Node.ELEMENT_NODE&&node.tagName==='BR'){
                    const prefix=document.createRange();prefix.selectNodeContents(node.parentNode);prefix.setEndBefore(node);
                    const prior=prefix.cloneContents(),empty=!prior.textContent.trim()&&!prior.querySelector('img,video,audio,object,hr,br');
                    last={node:node.parentNode,offset:Array.from(node.parentNode.childNodes).indexOf(node)+(empty?0:1),kind:empty?'before-empty-br':'after-trailing-br'};
                  }else if(node.nodeType===Node.ELEMENT_NODE&&node.matches('img,video,audio,object,hr'))last={node:node.parentNode,offset:Array.from(node.parentNode.childNodes).indexOf(node)+1,kind:'after-media'};
                  else if(node.nodeType===Node.ELEMENT_NODE&&!node.contains(stop)&&node.childNodes.length===0&&node.matches('p,div,li,pre,blockquote,td,th'))last={node:node,offset:0,kind:'empty-element'};
                }
                return last||{node:ed,offset:0,kind:'empty-root'};
              }
              setDark(false);setQuoteFoldingSupported(true);setHtml(fixture.html,false);
              const beforeHtml=ed.innerHTML,beforeText=renderedBody(),expectedText=(mode==='appendBody'&&beforeText?beforeText+'\n':'')+normalize(text);
              const beforeTail=tailNodes().map(node=>({node:node,value:nodeValue(node)})),beforeTailText=tailText(beforeTail.map(entry=>entry.node));
              const beforeAuthor=Array.from(ed.querySelectorAll('[data-ai-existing]')).map(node=>({node:node,html:node.outerHTML}));
              const descriptor=Object.getOwnPropertyDescriptor(document,'execCommand'),native=document.execCommand,guards=[];
              const result={fixture:fixture.name,mode:mode,candidate:strategy,ok:false,changed:false,calls:0,enabled:null,tailNodes:false,tailMarkup:false,tailText:false,author:false,body:false,undo:false,redo:false};
              let endpoints=null,endpointKind='shipping-boundary',afterHtml=beforeHtml,afterText=beforeText,afterTailText=beforeTailText,undoHtml=null,redoHtml=null,error=null;
              function guard(node){if(node.nodeType!==Node.ELEMENT_NODE||guards.some(entry=>entry[0]===node))return;guards.push([node,node.getAttribute('contenteditable')]);node.setAttribute('contenteditable','false');}
              function restoreGuards(){guards.forEach(entry=>{if(entry[1]===null)entry[0].removeAttribute('contenteditable');else entry[0].setAttribute('contenteditable',entry[1]);});guards.length=0;}
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
                if(strategy==='A_shipping'){
                  result.changed=cmd('replyDraft',JSON.stringify({text:text,expectedNewText:getNewText(),mode:mode}));
                }else{
                  const stop=ed.querySelector('.magpie-signature,.magpie-quote'),inside=strategy==='B_author_end'||strategy==='D_author_end_full_tail';
                  const end=inside?authorEndpoint(stop):null;endpointKind=end?.kind||'body-boundary';
                  const marker=mode==='appendBody'&&getNewText().length>0?'<br class="Apple-interchange-newline">':'';
                  ed.querySelectorAll('.magpie-signature,.magpie-quote').forEach(guard);
                  if(strategy==='C_full_tail'||strategy==='D_author_end_full_tail'){
                    for(let branch=stop;branch&&branch!==ed;branch=branch.parentNode){
                      for(let node=branch===stop?branch:branch.nextSibling;node;node=node.nextSibling)guard(node);
                    }
                  }
                  const range=document.createRange();
                  if(mode==='replaceBody'){
                    range.setStart(ed,0);if(end)range.setEnd(end.node,end.offset);else if(stop)range.setEndBefore(stop);else range.setEnd(ed,ed.childNodes.length);
                  }else{
                    if(end)range.setStart(end.node,end.offset);else if(stop)range.setStartBefore(stop);else range.setStart(ed,ed.childNodes.length);range.collapse(true);
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
                result.ok=!error&&result.changed&&result.calls===1&&result.tailNodes&&result.tailMarkup&&result.tailText&&result.author&&result.body&&result.markerGone&&result.undo&&result.redo;
                if(!result.ok)result.diagnostic={endpoint:endpointKind,selection:endpoints,before:bound(beforeHtml),after:bound(afterHtml),expectedText:bound(expectedText,300),actualText:bound(afterText,300),tailBefore:bound(beforeTailText,300),tailAfter:bound(afterTailText,300),undoHtml:result.undo?undefined:bound(undoHtml),redoHtml:result.redo?undefined:bound(redoHtml),error:bound(error)};
                return result;
              }catch(ex){result.diagnostic={endpoint:endpointKind,selection:endpoints,before:bound(beforeHtml),after:bound(ed.innerHTML),error:bound(String(ex?.stack||ex))};return result;}
              finally{setHtml('<p><br></p>',false);}
            };
            """);
        var totals = new Dictionary<string, int>();
        var passes = new Dictionary<string, int>();
        try
        {
            foreach (var fixture in cases)
            foreach (var mode in new[] { "appendBody", "replaceBody" })
            foreach (var candidate in new[] { "A_shipping", "B_author_end", "C_full_tail", "D_author_end_full_tail" })
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
