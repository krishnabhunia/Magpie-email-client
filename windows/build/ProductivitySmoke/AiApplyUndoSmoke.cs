using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using Magpie.App.ViewModels;
using Magpie.App.Views;
using Magpie.Core.Mail;

namespace ProductivitySmoke;

internal static partial class Program
{
    private static async Task CheckAiApplyUndoAsync(Func<string, Task<string>> script)
    {
        const string original = "<p><b>My own opening.</b></p><div class=\"magpie-signature\"><p style=\"font-family:Georgia;font-size:24px\">Regards, <b>Example User</b></p></div><div class=\"magpie-quote\"><blockquote style=\"font-family:Tahoma;font-size:11px\">Original <b>quoted</b> message</blockquote><p>Entire older thread remains.</p></div>";
        await script("""
            window.aiRunBodyCommand=(mode,text)=>{
              const descriptor=Object.getOwnPropertyDescriptor(document,'execCommand'),native=document.execCommand;
              window.aiBodyEditDiagnostics={mode:mode};
              document.execCommand=function(command,ui,value){
                function state(){
                  const selection=window.getSelection(),range=selection.rangeCount?selection.getRangeAt(0):null;
                  const point=(node,offset)=>{const element=node?.nodeType===Node.ELEMENT_NODE?node:node?.parentElement;return {node:node?.nodeName,offset:offset,element:element?.tagName,editable:element?.isContentEditable,html:element?.outerHTML.slice(0,1500)};};
                  return {enabled:document.queryCommandEnabled(command),active:document.activeElement?.id,rootEditable:ed.isContentEditable,collapsed:selection.isCollapsed,start:range?point(range.startContainer,range.startOffset):null,end:range?point(range.endContainer,range.endOffset):null,html:ed.innerHTML.slice(0,5000)};
                }
                const attempt={command:command,insertion:typeof value==='string'?value.slice(0,1000):value,before:state()};
                window.aiBodyEditDiagnostics.native=attempt;
                try{return attempt.result=native.call(document,command,ui,value);}finally{attempt.after=state();}
              };
              try{return cmd(mode,text);}finally{if(descriptor)Object.defineProperty(document,'execCommand',descriptor);else delete document.execCommand;}
            };
            """);
        static string Read(string value) => JsonSerializer.Deserialize<string>(value) ?? "";
        static string Bounded(string value) => value.Length <= 4000 ? value : value[..4000] + " [truncated]";
        const string sameProtectedNodes = "(()=>{const current=Array.from(ed.querySelectorAll('.magpie-signature,.magpie-signature *,.magpie-quote,.magpie-quote *'));return current.length===window.aiUndoProtectedNodes.length&&current.every((node,index)=>node===window.aiUndoProtectedNodes[index]);})()";
        async Task CheckNativeStep(string command, string expected, string label)
        {
            var result = await script("fmt(" + JsonSerializer.Serialize(command) + ")");
            var actual = await script("ed.innerHTML");
            var sameNodes = await script(sameProtectedNodes);
            Assert(result == "true" && actual == expected && sameNodes == "true", label + "\nCommand result: " + result + "; same protected nodes: " + sameNodes
                + "\nExpected HTML: " + Bounded(expected) + "\nActual HTML: " + Bounded(actual));
        }
        foreach (var dark in new[] { false, true })
        foreach (var mode in new[] { "appendBody", "replaceBody" })
        {
            await script("setQuoteFoldingSupported(true);setDark(" + (dark ? "true" : "false") + ");setHtml(" + JsonSerializer.Serialize(original) + ")");
            await script("setTypography({family:'Arial',size:18},{family:'Arial',size:20,overrideExisting:true},{family:'Arial',size:19,overrideExisting:true},false)");
            var before = await script("ed.innerHTML");
            var oldBody = await script("ed.firstElementChild.outerHTML");
            Assert(await script("window.aiUndoBodyNode=ed.firstElementChild;window.aiUndoProtectedNodes=Array.from(ed.querySelectorAll('.magpie-signature,.magpie-signature *,.magpie-quote,.magpie-quote *'));(()=>{const cloud=captureCloudLink(),range=document.createRange();range.selectNodeContents(ed.firstElementChild);selectEditorRange(range);return cloud&&cmd('captureRewrite',range.toString());})()") == "true",
                "AI fixture captures real cloud and rewrite ranges before mutating the body");
            var protectedBefore = await script("JSON.stringify(Array.from(ed.querySelectorAll('.magpie-signature,.magpie-quote')).map(n=>n.outerHTML))");
            var expected = Read(await script("getNewText()"));
            var payload = JsonSerializer.Serialize(new { text = "Generated answer.\n\nSecond paragraph <literal>.", expectedNewText = expected, mode });
            var applied = await script("window.aiRunBodyCommand('replyDraft'," + JsonSerializer.Serialize(payload) + ")");
            var after = await script("ed.innerHTML");
            Assert(applied == "true", "AI " + mode + " uses a native edit transaction; result=" + applied
                + "\nNative command: " + Bounded(await script("JSON.stringify(window.aiBodyEditDiagnostics)"))
                + "\nBefore HTML: " + Bounded(before) + "\nAfter HTML: " + Bounded(after));
            var protectedAfter = await script("JSON.stringify(Array.from(ed.querySelectorAll('.magpie-signature,.magpie-quote')).map(n=>n.outerHTML))");
            var retainedNodes = await script(sameProtectedNodes);
            Assert(after != before && protectedAfter == protectedBefore && retainedNodes == "true",
                "AI " + mode + " preserves exact rich signature and entire folded quote markup (dark=" + dark + ")"
                + "; same protected nodes: " + retainedNodes
                + "\nBefore HTML: " + Bounded(before) + "\nAfter HTML: " + Bounded(after)
                + "\nExpected protected HTML: " + Bounded(protectedBefore) + "\nActual protected HTML: " + Bounded(protectedAfter));
            if (mode == "appendBody")
            {
                var retainedBody = await script("window.aiUndoBodyNode.outerHTML");
                Assert(await script("ed.firstElementChild===window.aiUndoBodyNode") == "true" && retainedBody == oldBody,
                    "Appending AI retains the original body node and all of its formatting\nExpected body HTML: " + Bounded(oldBody)
                    + "\nActual body HTML: " + Bounded(retainedBody));
            }
            Assert(await script("rewrite===null&&cloudLinkTarget===null&&cmd('replaceSelection','Stale rewrite')===false&&insertCloudLink('https://example.test/stale','Stale cloud link')===false") == "true"
                && await script("ed.innerHTML") == after, "AI body edits invalidate captured rewrite and cloud insertion targets without changing the accepted result");
            await CheckNativeStep("undo", before, "One native Undo restores the exact pre-AI document, including formatting, signature and quote");
            await CheckNativeStep("redo", after, "One native Redo reapplies only the accepted AI document");
            Assert(await script("fmt('insertText',' Later typed note.')") == "true", "A later manual edit remains a separate native edit");
            await CheckNativeStep("undo", after, "Undo of later typing preserves the accepted AI change");
            await CheckNativeStep("undo", before, "The preceding native Undo still reaches the AI change after later typing");
            await CheckNativeStep("redo", after, "Native Redo can restore the AI change independently of later typing");
            await script("setTypography({family:'Arial',size:18},{family:'Arial',size:16,overrideExisting:false},{family:'Arial',size:14,overrideExisting:false},false)");
            var signatureSize = await script("getComputedStyle(ed.querySelector('.magpie-signature p')).fontSize");
            var quoteSize = await script("getComputedStyle(ed.querySelector('.magpie-quote blockquote')).fontSize");
            Assert(signatureSize == "\"24px\"" && quoteSize == "\"11px\"",
                "AI native transactions and Undo/Redo retain protected typography reset records: signature=" + signatureSize + ", quote=" + quoteSize
                + "\nActual HTML: " + Bounded(await script("ed.innerHTML")));
        }
        await CheckAiBodyBoundariesAsync(script);
        Console.WriteLine("AI_APPLY_NATIVE_UNDO_SMOKE_PASSED");
    }

    private static async Task CheckAiBodyBoundariesAsync(Func<string, Task<string>> script)
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
            (Name: "inline-image-trailing-br", Html: "<p data-ai-existing style='font-family:Georgia'>Image opening." + pixel + "<br></p>" + protectedTail + tailParagraph),
            (Name: "editable-tail-descendants", Html: "<section>" + author
                + "<div class='magpie-signature' contenteditable='true'><p style='font-family:Georgia;font-size:24px'><span contenteditable=''>Signature <b contenteditable='true'>stays</b></span></p></div>"
                + "<div class='magpie-quote'><blockquote>Original quote</blockquote><p>Older hidden paragraph</p></div>"
                + "<p style='color:navy'><span contenteditable='plaintext-only' style='font-style:italic'>Unmarked editable tail stays too.</span></p></section>")
        };
        await script("window.aiRenderedBody=()=>{const range=document.createRange(),stop=ed.querySelector('.magpie-signature,.magpie-quote'),selection=window.getSelection(),previous=[];range.selectNodeContents(ed);if(stop)range.setEndBefore(stop);for(let index=0;index<selection.rangeCount;index++)previous.push(selection.getRangeAt(index).cloneRange());try{selection.removeAllRanges();selection.addRange(range);return selection.toString();}finally{selection.removeAllRanges();previous.forEach(item=>selection.addRange(item));}};");
        await script("""
            window.aiTailNodes=()=>{const stop=ed.querySelector('.magpie-signature,.magpie-quote'),nodes=[];if(!stop)return nodes;const walk=document.createTreeWalker(ed,NodeFilter.SHOW_ALL);let seen=false,node;while(node=walk.nextNode()){if(node===stop)seen=true;if(seen)nodes.push(node);}return nodes;};
            window.aiTailStyles=()=>window.aiTailNodes().filter(node=>node.nodeType===Node.TEXT_NODE).map(node=>{
              const style=getComputedStyle(node.parentElement),properties=['font-family','font-size','font-weight','font-style','font-stretch','font-variant','font-feature-settings','font-kerning','line-height','letter-spacing','word-spacing','color','text-transform','white-space','direction','writing-mode','text-align','text-indent','tab-size','word-break','overflow-wrap'];
              return [node.nodeValue,properties.map(name=>style.getPropertyValue(name))];
            });
            """);
        const string snapshot = "window.aiTailBefore=window.aiTailNodes().map(node=>[node,node.nodeType===Node.ELEMENT_NODE?node.outerHTML:node.nodeValue]);window.aiBodyBefore=Array.from(ed.querySelectorAll('[data-ai-existing]')).map(node=>[node,node.outerHTML]);window.aiTailStylesBefore=JSON.stringify(window.aiTailStyles());";
        const string tailUnchanged = "(()=>{const nodes=window.aiTailNodes();return nodes.length===window.aiTailBefore.length&&nodes.every((node,index)=>node===window.aiTailBefore[index][0]&&(node.nodeType===Node.ELEMENT_NODE?node.outerHTML:node.nodeValue)===window.aiTailBefore[index][1]);})()";
        const string tailStylesUnchanged = "JSON.stringify(window.aiTailStyles())===window.aiTailStylesBefore";
        static string Bounded(string value) => value.Length <= 4000 ? value : value[..4000] + " [truncated]";
        // Rendered selection text distinguishes a real paragraph break from adjacent inline text.
        // Break counts vary by block tag; the separate HTML and node checks remain byte-for-byte exact.
        static string Rendered(string value) => Regex.Replace((JsonSerializer.Deserialize<string>(value) ?? "").Replace("\r\n", "\n"), "\n+", "\n").Trim();
        async Task CheckReopen(string stage)
        {
            var html = await script("ed.innerHTML");
            var visible = await script("ed.innerText");
            var styles = await script("JSON.stringify(window.aiTailStyles())");
            await script("setHtml(getHtml(),false)");
            var reopened = await script("ed.innerHTML");
            Assert(reopened == html && await script("ed.innerText") == visible
                && await script("JSON.stringify(window.aiTailStyles())") == styles,
                stage + ": reopening serialized HTML is idempotent and preserves visible text and inherited tail typography"
                + "\nSaved HTML: " + Bounded(html) + "\nReopened HTML: " + Bounded(reopened));
        }
        foreach (var fixture in cases)
        foreach (var mode in new[] { "appendBody", "replaceBody" })
        {
            await script("setDark(false);setQuoteFoldingSupported(true);setHtml(" + JsonSerializer.Serialize(fixture.Html) + ")");
            // A deliberate whole-document reload creates fresh nodes. Establish the live-node Undo
            // baseline only afterward; normalization must not accumulate extra wrappers or paragraphs.
            await CheckReopen(fixture.Name + ", " + mode + ", before AI");
            if (fixture.Name.Contains("empty", StringComparison.Ordinal))
                Assert(await script("getNewText()") == "\"\"", fixture.Name + ": reopening an empty body does not add author text");
            await script(snapshot);
            var before = await script("ed.innerHTML");
            var expected = JsonSerializer.Deserialize<string>(await script("getNewText()"))!;
            var visibleBefore = Rendered(await script("window.aiRenderedBody()"));
            var payload = JsonSerializer.Serialize(new { text = "Generated boundary answer.\n\nSecond boundary paragraph.", expectedNewText = expected, mode });
            var changed = await script("window.aiRunBodyCommand('replyDraft'," + JsonSerializer.Serialize(payload) + ")");
            var after = await script("ed.innerHTML");
            var body = JsonSerializer.Deserialize<string>(await script("getNewText()"))!;
            var visibleBody = Rendered(await script("window.aiRenderedBody()"));
            var retainedTail = await script(tailUnchanged);
            var retainedStyles = await script(tailStylesUnchanged);
            var retainedBody = await script("window.aiBodyBefore.every(entry=>ed.contains(entry[0])&&entry[0].outerHTML===entry[1])");
            var expectedBody = (mode == "appendBody" && visibleBefore.Length > 0 ? visibleBefore + "\n" : "")
                + "Generated boundary answer.\nSecond boundary paragraph.";
            var bodyMatches = visibleBody == expectedBody;
            var details = "\nFixture: " + fixture.Name + "; mode=" + mode + "; changed=" + changed + "; retained tail=" + retainedTail
                + "; retained body=" + retainedBody + "; retained tail styles=" + retainedStyles + "\nBefore HTML: " + Bounded(before) + "\nAfter HTML: " + Bounded(after)
                + "\nNative command: " + Bounded(await script("JSON.stringify(window.aiBodyEditDiagnostics)"))
                + "\nBefore getNewText: " + JsonSerializer.Serialize(expected) + "\nAfter getNewText: " + JsonSerializer.Serialize(body)
                + "\nExpected rendered body: " + JsonSerializer.Serialize(expectedBody) + "\nActual rendered body: " + JsonSerializer.Serialize(visibleBody);
            Assert(changed == "true" && after != before && retainedTail == "true" && retainedStyles == "true" && bodyMatches
                && (mode != "appendBody" || retainedBody == "true") && !after.Contains("Apple-interchange-newline", StringComparison.Ordinal),
                "AI edits only the body at empty, nested, inline and unwrapped boundaries with exact guard restoration" + details);
            var undo = await script("fmt('undo')");
            var undone = await script("ed.innerHTML");
            Assert(undo == "true" && undone == before && await script(tailUnchanged) == "true" && await script(tailStylesUnchanged) == "true",
                "One boundary-case Undo restores exact original HTML and the same tail nodes" + details + "\nUndo HTML: " + Bounded(undone));
            var redo = await script("fmt('redo')");
            var redone = await script("ed.innerHTML");
            Assert(redo == "true" && redone == after && await script(tailUnchanged) == "true" && await script(tailStylesUnchanged) == "true",
                "One boundary-case Redo restores the accepted body without touching tail nodes" + details + "\nRedo HTML: " + Bounded(redone));
            await CheckReopen(fixture.Name + ", " + mode + ", after native Redo");
        }
        Console.WriteLine("AI_BODY_BOUNDARIES_NATIVE_UNDO_SMOKE_PASSED");
    }

    private static async Task CheckPlainFallbackAiUndoAsync()
    {
        const string original = "<p>My opening.</p><div class=\"magpie-signature\"><p>My signature</p></div><div class=\"magpie-quote\"><blockquote>Quoted text</blockquote></div>";
        foreach (var mode in new[] { "appendBody", "replaceBody" })
        foreach (var reply in new[] { false, true })
        {
            var window = new ComposeWindow(new Draft { AccountId = "A", Html = original }, null);
            var vm = (ComposeViewModel)window.DataContext;
            try
            {
                RecipientInvoke(window, "UsePlainEditor");
                var editor = (TextBox)window.FindName("PlainEditor");
                // WPF clears undo records until the real TextBox template attaches its TextView.
                // Realize only this fallback control: showing ComposeWindow would initialize WebView2.
                editor.ApplyTemplate();
                editor.Measure(new Size(620, 240));
                editor.Arrange(new Rect(0, 0, 620, 240));
                editor.UpdateLayout();
                var contentHost = editor.Template?.FindName("PART_ContentHost", editor);
                var renderScope = (contentHost as ScrollViewer)?.Content ?? (contentHost as Decorator)?.Child;
                Assert(renderScope is UIElement, "Fallback fixture realizes the actual TextBox render scope before recording edits"
                    + PlainFallbackUndoState(editor, ("Initial", editor.Text)));
                var initial = editor.Text;
                editor.Select(0, 0);
                editor.SelectedText = "Earlier typed text. ";
                var before = editor.Text;
                Assert(before != initial && editor.CanUndo,
                    "Earlier typing creates a real native undo entry before applying AI"
                    + PlainFallbackUndoState(editor, ("Initial", initial), ("Before AI", before)));
                var expected = (string)RecipientInvoke(window, "PlainNewText")!;
                var tail = (string)typeof(ComposeWindow).GetField("_plainProtectedTail", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                var command = reply ? "replyDraft" : mode;
                var value = reply ? JsonSerializer.Serialize(new { text = "Generated answer.", expectedNewText = expected, mode }) : "Generated answer.";
                await (Task)RecipientInvoke(window, "EditorCommandAsync", command, value)!;
                var after = editor.Text;
                string State() => "\nCommand=" + command + "; mode=" + mode
                    + "; changed=" + (after != before) + "; exactTail=" + after.EndsWith(tail, StringComparison.Ordinal)
                    + PlainFallbackUndoState(editor, ("Initial", initial), ("Before AI", before), ("After AI", after), ("Protected tail", tail), ("Current", editor.Text));
                Assert(after != before && after.EndsWith(tail, StringComparison.Ordinal) && editor.CanUndo,
                    "Native fallback AI " + command + " changes only the new body and creates a real undo entry" + State());
                RecipientInvoke(window, "OnFormat", new Button { Tag = "undo" }, new RoutedEventArgs());
                Assert(editor.Text == before && editor.CanRedo, "Fallback toolbar Undo restores the exact pre-AI text and protected tail" + State());
                RecipientInvoke(window, "OnFormat", new Button { Tag = "redo" }, new RoutedEventArgs());
                Assert(editor.Text == after, "Fallback toolbar Redo reapplies the accepted AI change" + State());
                editor.Undo();
                editor.Undo();
                Assert(editor.Text == initial, "Applying AI preserves the earlier typing entry in the native fallback undo manager" + State());
            }
            finally
            {
                typeof(ComposeWindow).GetField("_forceClose", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
                window.Close();
                vm.Detach();
            }
        }
        Console.WriteLine("PLAIN_FALLBACK_AI_UNDO_SMOKE_PASSED");
    }

    private static string PlainFallbackUndoState(TextBox editor, params (string Name, string Value)[] texts)
    {
        var contentHost = editor.Template?.FindName("PART_ContentHost", editor);
        var renderScope = (contentHost as ScrollViewer)?.Content ?? (contentHost as Decorator)?.Child;
        var result = $"\nCanUndo={editor.CanUndo}; CanRedo={editor.CanRedo}; IsUndoEnabled={editor.IsUndoEnabled}; UndoLimit={editor.UndoLimit}; "
            + $"IsLoaded={editor.IsLoaded}; template={editor.Template?.GetType().Name ?? "null"}; "
            + $"contentHost={contentHost?.GetType().Name ?? "null"}; renderScope={renderScope?.GetType().Name ?? "null"}";
        foreach (var (name, value) in texts)
        {
            var bounded = value.Length <= 2000 ? value : value[..2000] + " [truncated]";
            result += "\n" + name + " (length=" + value.Length + "): " + JsonSerializer.Serialize(bounded)
                + "\n" + name + " codepoints: " + string.Join(" ", value.EnumerateRunes().Take(256).Select(rune => "U+" + rune.Value.ToString("X4")));
        }
        return result;
    }
}
