using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Magpie.App.Views;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace ProductivitySmoke;

internal static partial class Program
{
    private static async Task CheckFormattingControlsAsync(Func<string, Task<string>> script, WebView2 view)
    {
        CheckNativeColourPalettes();
        await script("setLinkBubbleEnabled(true);setHtml('<p><strong>Colour me</strong> and keep <em>emphasis</em></p>')");
        await script("findInEditor('Colour',true)");
        Assert(await script("captureFormattingSelection()") == "true", "Colour palette captures the exact rich selection");
        await script("window.getSelection().removeAllRanges()");
        Assert(await script("applyEditorColour('foreColor','#B42318')") == "true"
            && await script("getComputedStyle(window.getSelection().anchorNode.parentElement).color") == "\"rgb(180, 35, 24)\""
            && await script("window.getSelection().toString()") == "\"Colour\"",
            "Choosing a text colour restores the selection after focus leaves the editor");
        Assert(await script("ed.textContent") == "\"Colour me and keep emphasis\""
            && await script("ed.querySelector('strong')?.textContent") == "\"Colour me\""
            && await script("ed.querySelector('em')?.textContent") == "\"emphasis\"",
            "Changing text colour preserves visible text and unrelated rich formatting");
        await script("captureFormattingSelection()");
        Assert(await script("applyEditorColour('hiliteColor','#A5F3FC')") == "true"
            && await script("getComputedStyle(ed.querySelector('[style*=\"background-color\"]')).backgroundColor") == "\"rgb(165, 243, 252)\"",
            "The highlight palette applies the selected colour rather than a fixed yellow");
        await script("captureFormattingSelection()");
        Assert(await script("applyEditorColour('hiliteColor','transparent')") == "true"
            && await script("Array.from(ed.querySelectorAll('*')).every(n=>getComputedStyle(n).backgroundColor==='rgba(0, 0, 0, 0)')") == "true",
            "No highlight clears the selected highlight without clearing other formatting");
        var coloured = await script("getHtml()");
        await script("captureFormattingSelection()");
        Assert(await script("applyEditorColour('foreColor','url(https://invalid.example)')") == "false"
            && await script("applyEditorColour('insertHTML','#FFFFFF')") == "false"
            && await script("getHtml()") == coloured, "Colour commands reject non-colours and unsupported formatting commands");
        await script("captureFormattingSelection();ed.appendChild(document.createTextNode(' changed'))");
        Assert(await script("applyEditorColour('foreColor','#000000')") == "false", "A stale palette selection cannot format changed draft content");
        await script("setHtml('<p>Replacement document</p>')");
        Assert(await script("applyEditorColour('foreColor','#000000')") == "false", "Restoring a draft invalidates outstanding colour selections");

        const string linked = "<p>Before <a href=\"https://example.com/original\"><strong>Important</strong> link</a> after</p>";
        await script("setHtml(" + JsonSerializer.Serialize(linked) + ")");
        await script("ed.querySelector('a').dispatchEvent(new MouseEvent('click',{bubbles:true,cancelable:true}))");
        Assert(await script("!linkBubble.hidden&&linkBubble.getAttribute('role')==='dialog'") == "true"
            && await script("document.getElementById('linkAddress').textContent") == "\"https://example.com/original\"",
            "Clicking a link opens its accessible contextual address and edit/remove bubble");
        Assert(await script("getHtml().includes('linkBubble')") == "false", "Contextual link controls are not included in sent or saved message HTML");
        var editRequested = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<CoreWebView2WebMessageReceivedEventArgs> onMessage = (_, e) =>
        {
            using var message = JsonDocument.Parse(e.TryGetWebMessageAsString());
            if (message.RootElement.GetProperty("t").GetString() == "link") editRequested.TrySetResult(true);
        };
        view.CoreWebView2.WebMessageReceived += onMessage;
        try
        {
            await script("document.getElementById('editContextLink').click()");
            await editRequested.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert(await script("setSelectedLink('https://example.com/edited')") == "true"
                && await script("ed.querySelector('a').getAttribute('href')") == "\"https://example.com/edited\""
                && await script("ed.querySelector('a strong').textContent") == "\"Important\""
                && await script("ed.textContent") == "\"Before Important link after\"",
                "Contextual Edit changes only the address and preserves the rich display text");
        }
        finally { view.CoreWebView2.WebMessageReceived -= onMessage; }
        foreach (var url in new[] { "javascript:alert(1)", "data:text/html,hello", "file:///private", "https://example.com/\nspoof", "mailto:", "https://" })
        {
            await script("captureLinkSelection()");
            Assert(await script("setSelectedLink(" + JsonSerializer.Serialize(url) + ")") == "false", "Link editor rejects unsafe or incomplete address " + url.Replace('\n', ' '));
        }
        await script("captureLinkSelection();ed.appendChild(document.createTextNode(' more'))");
        Assert(await script("setSelectedLink('https://example.com/stale')") == "false", "Link edit snapshots reject changed drafts");
        await script("cancelLinkSelection();setHtml(" + JsonSerializer.Serialize(linked) + ")");
        await script("ed.querySelector('a').dispatchEvent(new MouseEvent('click',{bubbles:true,cancelable:true}));document.getElementById('removeContextLink').click()");
        Assert(await script("ed.querySelector('a')===null&&ed.querySelector('strong').textContent==='Important'&&ed.textContent==='Before Important link after'") == "true"
            && await script("linkBubble.hidden") == "true", "Contextual Remove unwraps the link without losing its text or emphasis");
        await script("setHtml(" + JsonSerializer.Serialize(linked) + ");ed.querySelector('a').dispatchEvent(new MouseEvent('click',{bubbles:true,cancelable:true}))");
        await script("document.dispatchEvent(new KeyboardEvent('keydown',{key:'Escape',bubbles:true,cancelable:true}));document.dispatchEvent(new Event('selectionchange'))");
        Assert(await script("linkBubble.hidden") == "true", "Escape dismisses the link bubble without reopening on focus restoration");
        await script("setDark(true);ed.querySelector('a').dispatchEvent(new MouseEvent('click',{bubbles:true,cancelable:true}))");
        Assert(await script("getComputedStyle(linkBubble).backgroundColor") == "\"rgb(35, 43, 50)\"", "Contextual link bubble supports dark mode");
        await script("setDark(false);setEditorPlainText(true)");
        var plain = await script("getHtml()");
        Assert(await script("captureFormattingSelection()") == "false"
            && await script("applyEditorColour('foreColor','#B42318')") == "false"
            && await script("captureLinkSelection()") == "false"
            && await script("setSelectedLink('https://example.com')") == "false"
            && await script("removeSelectedLink()") == "false"
            && await script("linkBubble.hidden&&getHtml()===JSON.parse(" + JsonSerializer.Serialize(plain) + ")") == "true",
            "Plain-text mode disables colour and link editing without altering the message");
        await script("setHtml('<p><br></p>');setLinkBubbleEnabled(false)");
        Console.WriteLine("FORMATTING_CONTROLS_SMOKE_PASSED");
    }

    private static void CheckNativeColourPalettes()
    {
        foreach (var highlight in new[] { false, true })
        {
            string? applied = null;
            var menu = ComposeColourPalette.Create(highlight, colour => { applied = colour; return Task.FromResult(true); });
            var grid = (UniformGrid)((MenuItem)menu.Items[1]).Header;
            var swatches = grid.Children.OfType<Button>().ToArray();
            Assert(swatches.Length == 16 && swatches.Select(b => b.Tag).Distinct().Count() == 16
                && swatches.All(b => !string.IsNullOrWhiteSpace(AutomationProperties.GetName(b))),
                (highlight ? "Highlight" : "Text colour") + " palette provides sixteen named, keyboard-focusable swatches");
            swatches[4].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(applied == swatches[4].Tag?.ToString(), "Native palette dispatches the chosen swatch value");
            var custom = menu.Items.OfType<MenuItem>().Select(item => item.Header).OfType<StackPanel>().Single();
            var row = custom.Children.OfType<DockPanel>().Single();
            var input = row.Children.OfType<TextBox>().Single();
            var submit = row.Children.OfType<Button>().Single();
            input.Text = "#zzzzzz"; applied = null;
            submit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(applied == null, "Native custom colour field rejects invalid hexadecimal values");
            input.Text = "#123abc";
            submit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(applied == "#123ABC", "Native custom colour field accepts and normalizes a valid colour");
            if (highlight)
            {
                menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "No highlight")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Assert(applied == "transparent", "Highlight palette exposes an explicit no-highlight action");
            }
        }
    }
}
