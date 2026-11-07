using System.Text.Json;

namespace ProductivitySmoke;

internal static partial class Program
{
    private static async Task CheckTableUndoAsync(Func<string, Task<string>> script)
    {
        const string pixel = "<img alt='Retained table picture' width='16' height='16' src='data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7'>";
        const string rich = "<p><strong style='color:purple;font-size:21px'>Rich <a href='https://example.test/table?x=1&amp;y=2'>kept link</a></strong><br>" + pixel + "</p>";
        const string author = "<p style='color:navy'><b>Outside author</b> <span data-table-typing>Opening</span></p>";
        const string signature = "<div class='magpie-signature' contenteditable='true'><p style='font-family:Georgia;font-size:24px'>Signature <b contenteditable='true'>stays</b></p></div>";
        const string quote = "<div class='magpie-quote'><blockquote style='font-family:Tahoma;font-size:11px'>Original <em>quoted message</em></blockquote><p>Older tail stays.</p></div>";
        const string grid = "<table data-table-fixture style='border-collapse:collapse'><tbody><tr><td data-cell='drop'>Delete this</td><td>Top right</td></tr><tr><td data-cell='keep' data-table-keep>" + rich + "</td><td>Bottom right</td></tr><tr><td>Last left</td><td>Last right</td></tr></tbody></table>";
        static string Surround(string table, bool raw = false) => author + (raw ? "Raw prefix before table." : "") + table
            + (raw ? "Raw suffix after table." : "") + "<p><em>Outside following text.</em></p>" + signature + quote;
        var cases = new[]
        {
            (Name: "delete-table-rich", Html: Surround(grid), Cell: "drop", Action: "deleteTable", Rows: 0, Columns: 0, Second: "", Role: ""),
            (Name: "delete-row-rich", Html: Surround(grid), Cell: "drop", Action: "deleteRow", Rows: 2, Columns: 2, Second: "deleteRow", Role: ""),
            (Name: "delete-column-raw-neighbors", Html: Surround("<table data-table-fixture><tbody><tr><td data-cell='drop'>Delete first column</td><td data-cell='keep' data-table-keep>" + rich + "</td><td>Right top</td></tr><tr><td>Delete lower</td><td>Middle lower</td><td>Right lower</td></tr></tbody></table>", true), Cell: "drop", Action: "deleteColumn", Rows: 2, Columns: 2, Second: "deleteColumn", Role: ""),
            (Name: "delete-merged-anchor-row", Html: Surround("<table data-table-fixture><tbody><tr><td rowspan='2' data-cell='keep' data-table-keep>" + rich + "</td><td>Delete row mate</td></tr><tr><td>Surviving lower cell</td></tr></tbody></table>"), Cell: "keep", Action: "deleteRow", Rows: 1, Columns: 2, Second: "", Role: ""),
            (Name: "delete-merged-column", Html: Surround("<table data-table-fixture><tbody><tr><td colspan='2' data-cell='keep' data-table-keep>" + rich + "</td><td>Right top</td></tr><tr><td>Delete lower</td><td>Retained middle</td><td>Right lower</td></tr></tbody></table>"), Cell: "keep", Action: "deleteColumn", Rows: 2, Columns: 2, Second: "", Role: ""),
            (Name: "delete-last-row", Html: Surround("<table data-table-fixture><tbody><tr><td data-cell='drop'>" + rich + "</td><td>Last row mate</td></tr></tbody></table>"), Cell: "drop", Action: "deleteRow", Rows: 0, Columns: 0, Second: "", Role: ""),
            (Name: "delete-last-column", Html: Surround("<table data-table-fixture><tbody><tr><td data-cell='drop'>" + rich + "</td></tr><tr><td>Last column mate</td></tr></tbody></table>"), Cell: "drop", Action: "deleteColumn", Rows: 0, Columns: 0, Second: "", Role: ""),
            (Name: "signature-table-font-reset", Html: author + "<div class='magpie-signature'><p>Signature introduction stays.</p><table data-table-fixture><tbody><tr><td data-cell='drop'>Delete row</td></tr><tr><td data-cell='keep' data-table-keep><span data-table-font style='font-family:Georgia;font-size:24px'>Original signature table font</span>" + rich + "</td></tr></tbody></table></div>" + quote, Cell: "drop", Action: "deleteRow", Rows: 1, Columns: 1, Second: "", Role: "signature"),
            (Name: "quote-table-font-reset", Html: author + signature + "<div class='magpie-quote'><blockquote><p>Quote introduction stays.</p><table data-table-fixture><tbody><tr><td data-cell='drop'>Delete row</td></tr><tr><td data-cell='keep' data-table-keep><span data-table-font style='font-family:Tahoma;font-size:11px'>Original quote table font</span>" + rich + "</td></tr></tbody></table><p>Complete older quote stays.</p></blockquote></div>", Cell: "drop", Action: "deleteRow", Rows: 1, Columns: 1, Second: "", Role: "quote")
        };
        var initialFolding = await script("quoteFoldingSupported");
        var failures = new List<Exception>();
        await script("""
            window.tableUndoNodes=root=>{
              if(!root)return [];const nodes=[root],walk=document.createTreeWalker(root,NodeFilter.SHOW_ALL);
              while(walk.nextNode())nodes.push(walk.currentNode);return nodes;
            };
            window.tableUndoValue=node=>node.nodeType===Node.ELEMENT_NODE?node.outerHTML:node.nodeValue;
            window.tableUndoCurrent=()=>ed.querySelector('table[data-table-fixture]');
            window.tableUndoPlaceholder=()=>{
              const state=window.tableUndoState,table=window.tableUndoCurrent(),slot=state.slot;
              if(table)return {valid:table.parentNode===slot.parent&&table.previousSibling===slot.previous&&table.nextSibling===slot.next,node:null};
              if(slot.parent!==ed&&!ed.contains(slot.parent))return {valid:false,node:null};
              const at=slot.previous?slot.previous.nextSibling:slot.parent.firstChild;
              if(at===slot.next)return {valid:(!slot.previous||slot.previous.parentNode===slot.parent)&&(!slot.next||slot.next.parentNode===slot.parent),node:null};
              // Native delete may leave its caret BR exactly where the selected table stood.
              // No other extra node, existing BR, attribute, parent or position is accepted.
              const valid=at?.nodeType===Node.ELEMENT_NODE&&at.tagName==='BR'&&at.attributes.length===0&&at.childNodes.length===0
                &&at.parentNode===slot.parent&&at.previousSibling===slot.previous&&at.nextSibling===slot.next
                &&!state.originalNodes.includes(at)&&!state.outside?.some(entry=>entry.node===at);
              return {valid:!!valid,node:valid?at:null};
            };
            window.tableUndoOutside=()=>{
              const state=window.tableUndoState,table=window.tableUndoCurrent(),placeholder=window.tableUndoPlaceholder().node,nodes=[],walk=document.createTreeWalker(ed,NodeFilter.SHOW_ALL);
              while(walk.nextNode()){
                const node=walk.currentNode;
                if(node===placeholder||(table&&(node===table||table.contains(node)))||state.ancestors.some(entry=>entry.node===node))continue;
                nodes.push(node);
              }
              return nodes;
            };
            window.tableUndoCapture=()=>{
              const table=window.tableUndoCurrent(),ancestors=[];
              for(let node=table.parentElement;node&&node!==ed;node=node.parentElement)
                ancestors.push({node:node,attributes:JSON.stringify(Array.from(node.attributes,attribute=>[attribute.name,attribute.value]))});
              window.tableUndoState={original:table,originalNodes:window.tableUndoNodes(table),ancestors:ancestors,slot:{parent:table.parentNode,previous:table.previousSibling,next:table.nextSibling},
                kept:Array.from(table.querySelectorAll('[data-table-keep]'),cell=>({key:cell.getAttribute('data-cell'),html:cell.innerHTML}))};
              window.tableUndoState.outside=window.tableUndoOutside().map(node=>({node:node,value:window.tableUndoValue(node)}));
            };
            window.tableUndoObserve=()=>{
              const state=window.tableUndoState,table=window.tableUndoCurrent(),placeholder=window.tableUndoPlaceholder(),outside=window.tableUndoOutside(),layout=table?tableGrid(table):null;
              const noMarkers=Array.from(ed.querySelectorAll('*')).every(node=>Array.from(node.attributes).every(attribute=>!attribute.name.startsWith('data-magpie-table-edit-')));
              return {html:ed.innerHTML,tableCount:ed.querySelectorAll('table').length,rows:layout?.rows.length||0,columns:layout?.grid[0]?.length||0,
                outside:placeholder.valid&&outside.length===state.outside.length&&outside.every((node,index)=>node===state.outside[index].node&&window.tableUndoValue(node)===state.outside[index].value),
                caretPlaceholder:!!placeholder.node,originalSlot:placeholder.valid,
                ancestors:state.ancestors.every(entry=>ed.contains(entry.node)&&JSON.stringify(Array.from(entry.node.attributes,attribute=>[attribute.name,attribute.value]))===entry.attributes),
                kept:!!table&&state.kept.every(entry=>Array.from(table.querySelectorAll('[data-table-keep]')).some(cell=>cell.getAttribute('data-cell')===entry.key&&cell.innerHTML===entry.html)),
                selected:!!selectedTableCell&&ed.contains(selectedTableCell)&&selectedTableCell.closest('table')===table,selectedKey:selectedTableCell?.getAttribute('data-cell')||null,
                selectedNull:selectedTableCell===null,replaced:!!table&&table!==state.original,noMarkers:noMarkers};
            };
            window.tableUndoSameNodes=name=>{
              const current=window.tableUndoNodes(window.tableUndoCurrent()),expected=window.tableUndoState[name];
              const expectedPlaceholder=name==='afterNodes'?window.tableUndoState.afterPlaceholder:name==='secondNodes'?window.tableUndoState.secondPlaceholder:null;
              return current.length===expected.length&&current.every((node,index)=>node===expected[index])&&window.tableUndoPlaceholder().node===expectedPlaceholder;
            };
            window.tableUndoRun=action=>{
              const descriptor=Object.getOwnPropertyDescriptor(document,'execCommand'),native=document.execCommand,result={changed:false,calls:0,attempts:[]};
              const bound=(value,limit=1800)=>typeof value==='string'&&value.length>limit?value.slice(0,limit)+' [truncated]':value;
              function state(){
                const selection=window.getSelection(),range=selection.rangeCount?selection.getRangeAt(0):null;
                const point=(node,offset)=>{const element=node?.nodeType===Node.ELEMENT_NODE?node:node?.parentElement;return {node:node?.nodeName,offset:offset,parent:element?.tagName,editable:element?.isContentEditable};};
                return {html:bound(ed.innerHTML),rootEditable:ed.isContentEditable,active:document.activeElement?.id,start:range?point(range.startContainer,range.startOffset):null,end:range?point(range.endContainer,range.endOffset):null};
              }
              document.execCommand=function(command,ui,value){
                const attempt={command:command,enabled:document.queryCommandEnabled(command),insertion:bound(value),before:state()};result.calls++;result.attempts.push(attempt);
                try{return attempt.result=native.call(document,command,ui,value);}finally{attempt.after=state();}
              };
              try{result.changed=editTable(action);}catch(error){result.error=String(error?.stack||error);}
              finally{if(descriptor)Object.defineProperty(document,'execCommand',descriptor);else delete document.execCommand;}
              return result;
            };
            window.tableUndoType=text=>{
              const leaf=ed.querySelector('[data-table-typing]'),range=document.createRange();range.selectNodeContents(leaf);range.collapse(false);selectEditorRange(range);return fmt('insertText',text);
            };
            """);
        static string Bounded(string value) => value.Length <= 4500 ? value : value[..4500] + " [truncated]";
        async Task<string> Html() => JsonSerializer.Deserialize<string>(await script("ed.innerHTML")) ?? "";
        try
        {
            foreach (var fixture in cases)
            {
                try
                {
                    // Quote tables must be visible to use the same real context-menu path as the user.
                    await script("setDark(false);setQuoteFoldingSupported(false);setHtml(" + JsonSerializer.Serialize(fixture.Html) + ",false);setTypography({family:'Arial',size:18},{family:'Arial',size:16,overrideExisting:false},{family:'Arial',size:14,overrideExisting:false},false)");
                    if (fixture.Role.Length > 0)
                        await script("setTypography({family:'Arial',size:18},{family:'Arial',size:20,overrideExisting:true},{family:'Arial',size:19,overrideExisting:true},false)");
                    var initial = await Html();
                    Assert(await script("window.tableUndoType(' earlier')") == "true", fixture.Name + ": earlier typing creates a native history entry");
                    await script("window.tableUndoCapture()");
                    var before = await Html();
                    Assert(before != initial, fixture.Name + ": earlier native typing changes the document");
                    Assert(await script("(()=>{const cell=ed.querySelector('[data-cell=" + fixture.Cell + "]');cell.dispatchEvent(new MouseEvent('contextmenu',{bubbles:true,cancelable:true}));return selectedTableCell===cell;})()") == "true",
                        fixture.Name + ": the actual context-menu handler selects the target cell");
                    var native = await script("window.tableUndoRun(" + JsonSerializer.Serialize(fixture.Action) + ")");
                    var observed = await script("window.tableUndoObserve()");
                    var after = await Html();
                    var details = "\nFixture: " + fixture.Name + "; action=" + fixture.Action + "\nNative: " + Bounded(native)
                        + "\nBefore HTML: " + Bounded(before) + "\nAfter HTML: " + Bounded(after) + "\nObserved: " + Bounded(observed);
                    using (var result = JsonDocument.Parse(native))
                    using (var state = JsonDocument.Parse(observed))
                    {
                        var value = state.RootElement;
                        Assert(result.RootElement.GetProperty("changed").GetBoolean() && result.RootElement.GetProperty("calls").GetInt32() == 1 && before != after,
                            "A destructive table action uses exactly one successful native edit" + details);
                        Assert(value.GetProperty("outside").GetBoolean() && value.GetProperty("ancestors").GetBoolean() && value.GetProperty("noMarkers").GetBoolean(),
                            "A table edit preserves all outside element/text nodes and exact attributes, including raw siblings and protected content" + details);
                        Assert(value.GetProperty("rows").GetInt32() == fixture.Rows && value.GetProperty("columns").GetInt32() == fixture.Columns
                            && value.GetProperty("tableCount").GetInt32() == (fixture.Rows == 0 ? 0 : 1)
                            && (fixture.Rows == 0 ? value.GetProperty("selectedNull").GetBoolean()
                                : value.GetProperty("kept").GetBoolean() && value.GetProperty("replaced").GetBoolean() && value.GetProperty("selected").GetBoolean()
                                    && value.GetProperty("selectedKey").GetString() == "keep"),
                            "Table deletion preserves the expected grid, exact surviving rich cell HTML and a valid rebound selection" + details);
                    }
                    await script("window.tableUndoState.afterNodes=window.tableUndoNodes(window.tableUndoCurrent());window.tableUndoState.afterPlaceholder=window.tableUndoPlaceholder().node");
                    async Task History(string command, string expected, string nodes, string label, bool outside = true)
                    {
                        var changed = await script("fmt(" + JsonSerializer.Serialize(command) + ")");
                        var actual = await Html();
                        var identity = await script("window.tableUndoSameNodes(" + JsonSerializer.Serialize(nodes) + ")");
                        var status = await script("window.tableUndoObserve()");
                        using var parsed = JsonDocument.Parse(status);
                        Assert(changed == "true" && actual == expected && identity == "true" && parsed.RootElement.GetProperty("noMarkers").GetBoolean()
                            && (!outside || parsed.RootElement.GetProperty("outside").GetBoolean() && parsed.RootElement.GetProperty("ancestors").GetBoolean()),
                            fixture.Name + ": " + label + "; result=" + changed + "; same table nodes=" + identity
                            + "\nExpected HTML: " + Bounded(expected) + "\nActual HTML: " + Bounded(actual) + "\nObserved: " + Bounded(status));
                    }
                    await History("undo", before, "originalNodes", "one native Undo restores exact original HTML and original table nodes");
                    await History("undo", initial, "originalNodes", "the preceding Undo still reaches earlier typing", false);
                    await History("redo", before, "originalNodes", "earlier typing redoes independently");
                    await History("redo", after, "afterNodes", "one native Redo restores exact accepted HTML without provenance attributes");
                    Assert(await script("window.tableUndoType(' later')") == "true" && await Html() != after,
                        fixture.Name + ": later native typing remains a separate edit");
                    await History("undo", after, "afterNodes", "Undo of later typing preserves the table edit");
                    await History("undo", before, "originalNodes", "the next Undo restores the deleted content after later typing");
                    await History("redo", after, "afterNodes", "Redo still restores the exact accepted table after later typing");
                    if (fixture.Name == "delete-row-rich")
                    {
                        await script("(()=>{const range=document.createRange();range.selectNodeContents(ed.querySelector('[data-table-keep] a'));selectEditorRange(range);})()");
                        var colored = await script("fmt('foreColor','#0055aa')");
                        var formatted = await Html();
                        Assert(colored == "true" && formatted != after,
                            fixture.Name + ": later native color formatting changes retained cell text\nBefore HTML: " + Bounded(after) + "\nFormatted HTML: " + Bounded(formatted));
                        // Native formatting may introduce a span/font node. Track that actual result
                        // separately while keeping exact HTML and identity checks for both states.
                        await script("window.tableUndoState.formattedNodes=window.tableUndoNodes(window.tableUndoCurrent())");
                        await History("undo", after, "afterNodes", "Undo of later formatting preserves the accepted table");
                        await History("redo", formatted, "formattedNodes", "Redo of formatting on an already-connected table preserves the user's color");
                        await History("undo", after, "afterNodes", "the formatting Undo restores the table's original accepted styles");
                        await History("undo", before, "originalNodes", "the preceding table Undo still restores exact original content after formatting");
                        await History("redo", after, "afterNodes", "table Redo restores exact table styles before replaying later formatting");
                        await History("redo", formatted, "formattedNodes", "formatting Redo after table reconnection restores its exact final HTML");
                        await History("undo", after, "afterNodes", "restore the baseline table result before the existing second deletion");
                    }
                    if (fixture.Second.Length > 0)
                    {
                        // Do not open another menu or assign selectedTableCell: use the real rebound cell.
                        var second = await script("window.tableUndoRun(" + JsonSerializer.Serialize(fixture.Second) + ")");
                        var twice = await Html();
                        using var secondResult = JsonDocument.Parse(second);
                        Assert(secondResult.RootElement.GetProperty("changed").GetBoolean() && secondResult.RootElement.GetProperty("calls").GetInt32() == 1 && twice != after,
                            fixture.Name + ": a second destructive command uses the rebound selected cell\nNative: " + Bounded(second) + "\nBefore: " + Bounded(after) + "\nAfter: " + Bounded(twice));
                        await script("window.tableUndoState.secondNodes=window.tableUndoNodes(window.tableUndoCurrent());window.tableUndoState.secondPlaceholder=window.tableUndoPlaceholder().node");
                        await History("undo", after, "afterNodes", "Undo of the second table command restores the first result exactly");
                        await History("redo", twice, "secondNodes", "Redo of the second table command restores its own exact result");
                    }
                    if (fixture.Role.Length > 0)
                    {
                        await script("setTypography({family:'Arial',size:18},{family:'Arial',size:16,overrideExisting:false},{family:'Arial',size:14,overrideExisting:false},false)");
                        var counterpart = fixture.Role == "signature" ? ".magpie-quote blockquote" : ".magpie-signature p";
                        var font = await script("({size:getComputedStyle(ed.querySelector('[data-table-font]')).fontSize,family:getComputedStyle(ed.querySelector('[data-table-font]')).fontFamily,other:getComputedStyle(ed.querySelector(" + JsonSerializer.Serialize(counterpart) + ")).fontSize})");
                        using var fonts = JsonDocument.Parse(font);
                        Assert(fonts.RootElement.GetProperty("size").GetString() == (fixture.Role == "signature" ? "24px" : "11px")
                            && fonts.RootElement.GetProperty("family").GetString()!.Contains(fixture.Role == "signature" ? "Georgia" : "Tahoma", StringComparison.Ordinal)
                            && fonts.RootElement.GetProperty("other").GetString() == (fixture.Role == "signature" ? "11px" : "24px"),
                            fixture.Name + ": native Undo/Redo preserves cloned protected typography reset records (original 24px signature / 11px quote)\nFonts: " + font + "\nHTML: " + Bounded(await Html()));
                    }
                    Console.WriteLine("TABLE_UNDO_CASE_PASSED fixture=" + fixture.Name);
                }
                catch (Exception ex)
                {
                    failures.Add(new InvalidOperationException("Native table Undo fixture failed: " + fixture.Name, ex));
                }
            }
            if (failures.Count > 0) throw new AggregateException("Native destructive table Undo smoke failed", failures);
            Console.WriteLine("TABLE_UNDO_NATIVE_SMOKE_PASSED cases=" + cases.Length);
        }
        finally
        {
            await script("setQuoteFoldingSupported(" + initialFolding + ");setHtml('<p><br></p>',false);delete window.tableUndoNodes;delete window.tableUndoValue;delete window.tableUndoCurrent;delete window.tableUndoPlaceholder;delete window.tableUndoOutside;delete window.tableUndoCapture;delete window.tableUndoObserve;delete window.tableUndoSameNodes;delete window.tableUndoRun;delete window.tableUndoType;delete window.tableUndoState");
        }
    }
}
