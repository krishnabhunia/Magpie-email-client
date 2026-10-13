using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input.Platform;

namespace Magpie.Mac.Services;

/// <summary>
/// The Edit menu (Undo, Redo, Cut, Copy, Paste, Select All). The menu's ⌘ keys reach the menu before the control
/// with the keyboard, so each item acts on that control itself: a text field, or a web page (the reading pane, the
/// compose editor), where the selection is copied through Avalonia's clipboard and pasted as plain text.
/// </summary>
public static class EditCommands
{
    public static async void Run(string command)
    {
        try
        {
            var window = Dialogs.ActiveWindow();
            var web = WebSurface.Focused;
            if (web != null && TopLevel.GetTopLevel(web) == window) { await OnPage(web, window, command); return; }
            if (window?.FocusManager?.GetFocusedElement() is TextBox box)
            {
                switch (command)
                {
                    case "undo": box.Undo(); break;
                    case "redo": box.Redo(); break;
                    case "cut": box.Cut(); break;
                    case "copy": box.Copy(); break;
                    case "paste": box.Paste(); break;
                    case "selectAll": box.SelectAll(); break;
                }
            }
        }
        catch (Exception ex) { Magpie.Core.Log.Warn("edit command: " + ex.Message); }
    }

    /// <summary>The selected text, also inside an email's own frame in the reading pane.</summary>
    private const string SelectionScript =
        "(function(){var s=window.getSelection();var t=s?s.toString():'';if(!t){document.querySelectorAll('iframe').forEach(function(f){" +
        "try{var x=f.contentWindow.getSelection().toString();if(x)t=x;}catch(e){}});}return t;})()";

    private static async Task OnPage(WebSurface web, Window? window, string command)
    {
        var clipboard = window?.Clipboard;
        switch (command)
        {
            case "copy" or "cut":
                var text = WebSurface.JsonText(await web.RunAsync(SelectionScript));
                if (text.Length > 0 && clipboard != null) await clipboard.SetTextAsync(text);
                if (command == "cut") await web.RunAsync("document.execCommand('delete')");
                break;
            case "paste":
                var paste = clipboard == null ? null : await clipboard.TryGetTextAsync();
                if (!string.IsNullOrEmpty(paste)) await web.RunAsync("document.execCommand('insertText', false, " + JsonSerializer.Serialize(paste) + ")");
                break;
            case "selectAll": await web.RunAsync("document.execCommand('selectAll')"); break;
            case "undo": await web.RunAsync("document.execCommand('undo')"); break;
            case "redo": await web.RunAsync("document.execCommand('redo')"); break;
        }
    }
}
