using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Magpie.Core.Mail;
using Magpie.Core.Settings;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace ProductivitySmoke;

internal static partial class Program
{
    private static void CheckEditorPage(string root)
    {
        // Dedicated profile: this never opens the user's mail, browser profile or Magpie data.
        var view = new WebView2();
        var window = new Window { Content = view, Width = 640, Height = 460, ShowActivated = false, Left = -3000, Top = -3000 };
        window.Show();
        try
        {
            WaitEditor(CoreWebView2Environment.CreateAsync(null, Path.Combine(root, "editor-profile")), out var environment);
            WaitEditor(Dispatcher.CurrentDispatcher.InvokeAsync(() => view.EnsureCoreWebView2Async(environment)).Task.Unwrap());
            var ready = false;
            view.CoreWebView2.WebMessageReceived += (_, e) =>
            {
                using var message = JsonDocument.Parse(e.TryGetWebMessageAsString());
                if (message.RootElement.GetProperty("t").GetString() == "ready") ready = true;
            };
            view.CoreWebView2.NavigateToString(EditorPage.Html);
            WaitEditorCondition(() => ready);
            string Script(string script)
            {
                WaitEditor(view.CoreWebView2.ExecuteScriptAsync(script), out var result);
                return result;
            }
            const string original = "<p>My own opening.</p><div class=\"magpie-signature\"><p>Regards, Example User</p></div><div class=\"magpie-quote\"><blockquote>Original quoted message</blockquote></div>";
            Script("setHtml(" + JsonSerializer.Serialize(original) + ")");
            Script(EditorPage.TypographyScript(new TypographySettings(), false));
            using (var style = JsonDocument.Parse(Script("JSON.stringify({family:getComputedStyle(ed.firstElementChild).fontFamily,size:getComputedStyle(ed.firstElementChild).fontSize,weight:getComputedStyle(ed.firstElementChild).fontWeight})")))
            using (var values = JsonDocument.Parse(style.RootElement.GetString()!))
                Assert(values.RootElement.GetProperty("family").GetString()!.Contains("Arial")
                    && values.RootElement.GetProperty("size").GetString() == "18px"
                    && values.RootElement.GetProperty("weight").GetString() == "400", "Real editor defaults are Arial18 normal");
            var typed = JsonSerializer.Deserialize<string>(Script("getNewText()"))!;
            Assert(typed == "My own opening.", "Real editor excludes protected signature and quote from typed-body detection");
            string Insert(string expected, string mode) => Script("cmd('replyDraft'," + JsonSerializer.Serialize(JsonSerializer.Serialize(new { text = "Generated answer.", expectedNewText = expected, mode })) + ")");
            Assert(Insert("stale text", "replaceBody") == "false", "Real editor rejects a stale insertion atomically");
            Assert(JsonSerializer.Deserialize<string>(Script("getNewText()")) == typed, "Rejected insertion preserves typed text");
            Assert(Insert(typed, "appendBody") == "true", "Real editor appends after explicit choice");
            var appended = JsonSerializer.Deserialize<string>(Script("getNewText()"))!;
            Assert(appended.StartsWith("My own opening.") && appended.Contains("Generated answer."), "Append retains the user's opening");
            var afterAppend = JsonSerializer.Deserialize<string>(Script("getHtml()"))!;
            Assert(afterAppend.Contains("font-size: 18px") && afterAppend.Split("class=\"magpie-signature\"").Length == 2
                && afterAppend.Split("class=\"magpie-quote\"").Length == 2, "Real HTML serializes explicit type and one signature/quote");
            var latest = JsonSerializer.Deserialize<string>(Script("getNewText()"))!;
            Assert(Insert(latest, "replaceBody") == "true" && JsonSerializer.Deserialize<string>(Script("getNewText()")) == "Generated answer.", "Replace changes only the new body");
            Script("setHtml('<p style=\"font:700 24px Georgia\">Existing formatted draft</p>')");
            Script(EditorPage.TypographyScript(new TypographySettings(), true));
            Assert(Script("getComputedStyle(ed.firstElementChild).fontSize") == "\"24px\""
                && Script("getComputedStyle(ed.firstElementChild).fontWeight") == "\"700\"", "Existing draft formatting survives new defaults");
            Script("setHtml('<p>New text</p><div class=\"magpie-signature\"><p><b>Existing bold signature</b></p></div><div class=\"magpie-quote\"><blockquote><b>Existing bold quote</b></blockquote></div>')");
            Script(EditorPage.TypographyScript(new TypographySettings(), false));
            Assert(Script("getComputedStyle(ed.querySelector('.magpie-signature b')).fontWeight") == "\"700\""
                && Script("getComputedStyle(ed.querySelector('.magpie-quote b')).fontWeight") == "\"700\"", "Reply defaults preserve existing rich signature and quote emphasis");
        }
        finally { view.Dispose(); window.Close(); }
    }

    private static void WaitEditor(Task task)
    {
        WaitEditorCondition(() => task.IsCompleted);
        task.GetAwaiter().GetResult();
    }
    private static void WaitEditor<T>(Task<T> task, out T result) { WaitEditor(task); result = task.GetAwaiter().GetResult(); }
    private static void WaitEditorCondition(Func<bool> done)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (!done() && timer.Elapsed < TimeSpan.FromSeconds(45))
        {
            var frame = new DispatcherFrame();
            var tick = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
            tick.Tick += (_, _) => { tick.Stop(); frame.Continue = false; };
            tick.Start();
            Dispatcher.PushFrame(frame);
        }
        Assert(done(), "WebView2 editor finished within the test deadline");
    }
}
