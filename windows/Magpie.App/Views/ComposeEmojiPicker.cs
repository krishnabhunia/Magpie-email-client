using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Magpie.App.Services;
using Magpie.Core;
using Magpie.Core.Mail;

namespace Magpie.App.Views;

public partial class ComposeWindow
{
    private async Task ShowEmojiPickerAsync(object sender)
    {
        if (!_editorReady) return;
        var originalText = _plain ? PlainEditor.Text : "";
        var start = _plain ? PlainEditor.SelectionStart : 0;
        var length = _plain ? PlainEditor.SelectionLength : 0;
        try
        {
            if (!_plain && (Editor.CoreWebView2 == null || await Editor.CoreWebView2.ExecuteScriptAsync("captureEmojiSelection()") != "true")) return;
            var history = new ComposeEmojiHistory(AppServices.Engine.Paths.LocalRoot);
            var menu = ComposeEmojiPicker.Create(history.Read(), async emoji =>
            {
                if (!ComposeEmojiCatalog.Contains(emoji)) return false;
                if (_plain)
                {
                    if (PlainEditor.Text != originalText) return false;
                    PlainEditor.Focus(); PlainEditor.Select(start, length); PlainEditor.SelectedText = emoji;
                    PlainEditor.CaretIndex = start + emoji.Length;
                    _vm.MarkEdited();
                }
                else if (Editor.CoreWebView2 == null || await Editor.CoreWebView2.ExecuteScriptAsync($"insertSelectedEmoji({JsonSerializer.Serialize(emoji)})") != "true") return false;
                history.Remember(emoji);
                return true;
            });
            menu.PlacementTarget = sender as UIElement ?? (_plain ? (UIElement)PlainEditor : Editor);
            menu.Placement = PlacementMode.Bottom;
            menu.Closed += async (_, _) =>
            {
                try { if (!_plain && Editor.CoreWebView2 != null) await Editor.CoreWebView2.ExecuteScriptAsync("cancelEmojiSelection()"); }
                catch (Exception ex) { Log.Warn("close emoji picker: " + ex.Message); }
            };
            menu.IsOpen = true;
        }
        catch (Exception ex) { Log.Warn("compose emoji picker: " + ex.Message); }
    }
}

/// <summary>Offline search, categories and locally remembered choices; emoji insert as text, never as remote images.</summary>
public static class ComposeEmojiPicker
{
    public static ContextMenu Create(IReadOnlyList<string> recent, Func<string, Task<bool>> insert)
    {
        var menu = new ContextMenu();
        AutomationProperties.SetName(menu, "Emoji and symbols picker");
        var panel = new StackPanel { Width = 330, Margin = new Thickness(4) };
        panel.Children.Add(new TextBlock { Text = "Common emoji and symbols", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        var search = new TextBox { MinHeight = 30, MaxLength = 200, Margin = new Thickness(0, 0, 0, 8), ToolTip = "Search by name or keyword, for example thanks, meeting or rupees" };
        AutomationProperties.SetName(search, "Search emoji by name or keyword");
        panel.Children.Add(new TextBlock { Text = "Search emoji", Margin = new Thickness(0, 0, 0, 3) });
        panel.Children.Add(search);
        var category = new ComboBox
        {
            ItemsSource = new[] { "All", "Recent" }.Concat(ComposeEmojiCatalog.Categories).ToArray(),
            SelectedIndex = 0, Margin = new Thickness(0, 0, 0, 8), MinHeight = 28,
        };
        AutomationProperties.SetName(category, "Emoji category");
        panel.Children.Add(category);
        var choices = new UniformGrid { Columns = 8 };
        var scroll = new ScrollViewer { Content = choices, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, MaxHeight = 240 };
        panel.Children.Add(scroll);
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        panel.Children.Add(status);
        var busy = false;
        void Rebuild()
        {
            choices.Children.Clear();
            var results = ComposeEmojiCatalog.Search(search.Text, category.SelectedItem as string, recent);
            foreach (var entry in results)
            {
                var button = new Button
                {
                    Content = entry.Value, FontFamily = new FontFamily("Segoe UI Emoji"), FontSize = 22,
                    Width = 36, Height = 36, Padding = new Thickness(0), Margin = new Thickness(1),
                    ToolTip = entry.Name, Tag = entry.Value,
                };
                AutomationProperties.SetName(button, entry.Name);
                button.Click += async (_, _) =>
                {
                    if (busy) return;
                    busy = true;
                    try
                    {
                        if (await insert(entry.Value)) menu.IsOpen = false;
                        else status.Text = "The message changed. Place the cursor again and reopen the picker.";
                    }
                    catch (Exception ex) { Log.Warn("insert emoji: " + ex.Message); status.Text = "The emoji could not be inserted. Try selecting the text again."; }
                    finally { busy = false; }
                };
                button.PreviewKeyDown += (_, e) =>
                {
                    var delta = e.Key switch { Key.Left => -1, Key.Right => 1, Key.Up => -8, Key.Down => 8, _ => 0 };
                    if (delta == 0) return;
                    var index = choices.Children.IndexOf(button) + delta;
                    if (index < 0) search.Focus();
                    else if (index < choices.Children.Count) { ((Button)choices.Children[index]).Focus(); ((Button)choices.Children[index]).BringIntoView(); }
                    e.Handled = true;
                };
                choices.Children.Add(button);
            }
            status.Text = results.Count > 0 ? results.Count + " choices · Enter inserts · Esc closes"
                : category.SelectedItem as string == "Recent" && string.IsNullOrWhiteSpace(search.Text)
                    ? "Recent choices appear here after you insert an emoji. History stays on this PC."
                    : "No matches. Try another keyword or category.";
        }
        search.TextChanged += (_, _) => Rebuild();
        category.SelectionChanged += (_, _) => Rebuild();
        search.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Down && choices.Children.Count > 0) { ((Button)choices.Children[0]).Focus(); e.Handled = true; }
        };
        menu.Items.Add(new MenuItem { Header = panel, StaysOpenOnClick = true, Focusable = false });
        menu.Opened += (_, _) => menu.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => { if (menu.IsOpen) search.Focus(); }));
        Rebuild();
        return menu;
    }
}
