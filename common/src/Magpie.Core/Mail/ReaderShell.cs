namespace Magpie.Core.Mail;

public static class ReaderShell
{
    /// <summary>Loaded once per reader. Email bodies remain in their original sandboxed, script-free iframes.</summary>
    public static string Build(bool dark)
    {
        var html = HtmlRenderer.BuildConversation("", Array.Empty<RenderMessage>(), false, DateTimeOffset.Now, dark).Html;
        // Hover cards and message IDs refer only to the visible conversation, not retained hidden pages.
        html = html.Replace("document.getElementById(", "readerFind(", StringComparison.Ordinal)
            .Replace("document.querySelectorAll('iframe')", "readerRoot().querySelectorAll('iframe')", StringComparison.Ordinal)
            .Replace("new ResizeObserver(function(){ size(f); }).observe(d.body);",
                "f._magpieObserver=new ResizeObserver(function(){ size(f); }); f._magpieObserver.observe(d.body);", StringComparison.Ordinal);
        return html.Replace("function post(", Script + "\nfunction post(", StringComparison.Ordinal);
    }

    /// <summary>
    /// The call that shows <paramref name="page"/> in a loaded shell (the same payload the Windows reader sends):
    /// <paramref name="reuse"/> = the browser still holds this page, so only its key and fingerprint are sent; the
    /// shell answers false when it doesn't have it after all, and the caller sends it again in full.
    /// </summary>
    public static string PresentScript(ReaderPage page, long version, bool reuse, int hoverDelayMs) =>
        "magpiePresent(" + System.Text.Json.JsonSerializer.Serialize(new
        {
            key = page.Key, fingerprint = page.Fingerprint, body = reuse ? "" : page.Body, style = reuse ? "" : page.Style,
            bytes = page.Bytes, context = page.Context, retain = page.Retain, version, reuse,
            read = page.ReadStates,
            hoverDelay = Math.Clamp(hoverDelayMs, 50, 1000),
        }) + ")";

    private const string Script = """
        var READER={pages:new Map(),active:null,bytes:0,version:0,context:null,started:false};
        function readerRoot(){ return READER.active?READER.active.node:document; }
        function readerFind(id){ return readerRoot().querySelector('[id="'+id+'"]'); }
        function readerDispose(node){
          node.querySelectorAll('iframe').forEach(function(f){ if(f._magpieObserver) f._magpieObserver.disconnect(); });
          node.remove();
        }
        function readerDrop(key){
          var p=READER.pages.get(key); if(!p) return;
          readerDispose(p.node); READER.pages.delete(key); READER.bytes-=p.bytes;
        }
        function magpiePresent(p){
          if(p.version<READER.version) return false;
          if(p.reuse && (p.context!==READER.context || !READER.pages.has(p.key) || READER.pages.get(p.key).fp!==p.fingerprint)) return false;
          READER.version=p.version;
          if(!READER.started){ document.body.replaceChildren(); READER.started=true; }
          hmHide(); clearTimeout(HM.hide); HM.ctx=null;
          if(READER.active){
            READER.active.y=window.scrollY;
            READER.active.node.hidden=true;
            if(!READER.active.retain) readerDispose(READER.active.node);
          }
          if(p.context && p.context!==READER.context){
            READER.pages.forEach(function(v){readerDispose(v.node);});
            READER.pages.clear(); READER.bytes=0; READER.context=p.context;
          }
          var page=p.retain?READER.pages.get(p.key):null;
          if(page && page.fp!==p.fingerprint){ readerDrop(p.key); page=null; }
          if(!page){
            var node=document.createElement('div'); node.hidden=true; node.innerHTML=p.body;
            document.body.appendChild(node);
            page={node:node,fp:p.fingerprint,bytes:p.bytes,y:0,retain:p.retain,style:p.style};
            if(p.retain){ READER.pages.set(p.key,page); READER.bytes+=p.bytes; }
          }else{
            // Touch the page: evict the least recently viewed conversation first.
            READER.pages.delete(p.key); READER.pages.set(p.key,page);
          }
          READER.active=page;
          Object.keys(p.read||{}).forEach(function(id){
            var row=page.node.querySelector('[id="m'+id+'"]');
            if(row) row.classList.toggle('unread',!p.read[id]);
          });
          if(page.style) document.querySelector('style').textContent=page.style;
          HM_DELAY=p.hoverDelay;
          page.node.hidden=false;
          window.scrollTo(0,page.y);
          requestAnimationFrame(function(){
            if(READER.active===page) page.node.querySelectorAll('iframe').forEach(size);
          });
          while(READER.pages.size>8 || READER.bytes>32*1024*1024){
            var oldest=READER.pages.keys().next().value;
            if(READER.pages.get(oldest)===page) break; // The displayed page must remain; evict it when leaving.
            readerDrop(oldest);
          }
          return true;
        }
        """;
}
