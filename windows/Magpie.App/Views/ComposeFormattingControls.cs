using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Magpie.Core;

namespace Magpie.App.Views;

public partial class ComposeWindow
{
    private bool CanUseRichFormatting => !_plain && !_vm.IsPlainText && _editorReady && Editor.CoreWebView2 != null;

    private async Task ShowColourPaletteAsync(object sender, string command)
    {
        if (!CanUseRichFormatting || command is not ("foreColor" or "hiliteColor")) return;
        try
        {
            if (await Editor.CoreWebView2.ExecuteScriptAsync("captureFormattingSelection()") != "true") return;
            var menu = ComposeColourPalette.Create(command == "hiliteColor", async colour =>
            {
                if (!CanUseRichFormatting) return false;
                return await Editor.CoreWebView2.ExecuteScriptAsync($"applyEditorColour({JsonSerializer.Serialize(command)},{JsonSerializer.Serialize(colour)})") == "true";
            });
            menu.PlacementTarget = sender as UIElement ?? Editor;
            menu.Placement = PlacementMode.Bottom;
            menu.Closed += async (_, _) =>
            {
                try
                {
                    if (Editor.CoreWebView2 != null) await Editor.CoreWebView2.ExecuteScriptAsync("cancelFormattingSelection()");
                }
                catch (Exception ex) { Log.Warn("close compose colour palette: " + ex.Message); }
            };
            menu.IsOpen = true;
        }
        catch (Exception ex) { Log.Warn("compose colour palette: " + ex.Message); }
    }
}

/// <summary>Small native palette shared by production and the construction-only Windows smoke test.</summary>
public static class ComposeColourPalette
{
    private static readonly (string Name, string Colour)[] TextColours =
    [
        ("Black", "#23282E"), ("Slate", "#5A6068"), ("Grey", "#9AA0A6"), ("White", "#FFFFFF"),
        ("Red", "#B42318"), ("Orange", "#B54708"), ("Gold", "#9A6700"), ("Green", "#067647"),
        ("Teal", "#14606E"), ("Blue", "#175CD3"), ("Indigo", "#444CE7"), ("Purple", "#7A5AF8"),
        ("Pink", "#C11574"), ("Rose", "#BE185D"), ("Brown", "#795548"), ("Navy", "#173B67"),
    ];
    private static readonly (string Name, string Colour)[] HighlightColours =
    [
        ("Yellow", "#FFF0A6"), ("Green", "#BBF7D0"), ("Cyan", "#A5F3FC"), ("Blue", "#BFDBFE"),
        ("Lavender", "#DDD6FE"), ("Pink", "#FBCFE8"), ("Peach", "#FED7AA"), ("Grey", "#E5E7EB"),
        ("Bright yellow", "#FFFF00"), ("Lime", "#D9F99D"), ("Mint", "#99F6E4"), ("Sky", "#BAE6FD"),
        ("Lilac", "#E9D5FF"), ("Rose", "#FECDD3"), ("Sand", "#FDE68A"), ("White", "#FFFFFF"),
    ];

    public static ContextMenu Create(bool highlight, Func<string, Task<bool>> apply)
    {
        var title = highlight ? "Highlight colour" : "Text colour";
        var menu = new ContextMenu { MinWidth = 280 };
        AutomationProperties.SetName(menu, title + " palette");
        menu.Items.Add(new MenuItem { Header = title, IsEnabled = false });
        var swatches = new UniformGrid { Columns = 8, Margin = new Thickness(0, 4, 0, 6) };
        var error = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, MaxWidth = 260, Margin = new Thickness(0, 6, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        AutomationProperties.SetLiveSetting(error, AutomationLiveSetting.Polite);
        var applying = false;
        async Task SelectAsync(string value)
        {
            if (applying) return;
            applying = true;
            try
            {
                if (await apply(value)) menu.IsOpen = false;
                else { error.Text = "Selection changed. Select your text again and reopen the palette."; error.Visibility = Visibility.Visible; }
            }
            catch (Exception ex)
            {
                Log.Warn("apply compose colour: " + ex.Message);
                error.Text = "The colour could not be applied. Select your text and try again.";
                error.Visibility = Visibility.Visible;
            }
            finally { applying = false; }
        }
        foreach (var (name, colour) in highlight ? HighlightColours : TextColours)
        {
            var button = new Button
            {
                Width = 28, Height = 28, Margin = new Thickness(2), Padding = new Thickness(3),
                ToolTip = name + " · " + colour, Tag = colour,
                Content = new Border
                {
                    Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colour)),
                    BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Width = 18, Height = 18,
                },
            };
            AutomationProperties.SetName(button, name + ", " + colour);
            button.Click += async (_, _) => await SelectAsync(colour);
            button.PreviewKeyDown += (_, e) =>
            {
                var delta = e.Key switch { Key.Left => -1, Key.Right => 1, Key.Up => -8, Key.Down => 8, _ => 0 };
                if (delta == 0) return;
                var index = swatches.Children.IndexOf(button);
                ((Button)swatches.Children[(index + delta + swatches.Children.Count) % swatches.Children.Count]).Focus();
                e.Handled = true;
            };
            swatches.Children.Add(button);
        }
        menu.Items.Add(new MenuItem { Header = swatches, StaysOpenOnClick = true, Focusable = false });
        if (highlight)
        {
            var clear = new MenuItem { Header = "No highlight", StaysOpenOnClick = true };
            clear.Click += async (_, _) => await SelectAsync("transparent");
            menu.Items.Add(clear);
        }
        menu.Items.Add(new Separator());
        var custom = new StackPanel { Margin = new Thickness(0, 2, 0, 4) };
        custom.Children.Add(new TextBlock { Text = "Custom colour (six-digit hexadecimal)", Margin = new Thickness(0, 0, 0, 5) });
        var row = new DockPanel();
        var valueBox = new TextBox { Text = highlight ? "#FFF0A6" : "#14606E", MaxLength = 7, MinWidth = 120, VerticalContentAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(valueBox, "Custom " + title.ToLowerInvariant() + " hexadecimal value");
        var submit = new Button { Content = "Apply", Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(8, 0, 0, 0) };
        AutomationProperties.SetName(submit, "Apply custom " + title.ToLowerInvariant());
        async Task ApplyCustomAsync()
        {
            var value = valueBox.Text.Trim();
            if (value.Length != 7 || value[0] != '#' || !value.Skip(1).All(Uri.IsHexDigit))
            {
                error.Text = "Enter # followed by six hexadecimal digits, for example #14606E.";
                error.Visibility = Visibility.Visible; valueBox.Focus(); return;
            }
            await SelectAsync(value.ToUpperInvariant());
        }
        submit.Click += async (_, _) => await ApplyCustomAsync();
        valueBox.PreviewKeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await ApplyCustomAsync(); } };
        DockPanel.SetDock(submit, Dock.Right); row.Children.Add(submit); row.Children.Add(valueBox);
        custom.Children.Add(row); custom.Children.Add(error);
        menu.Items.Add(new MenuItem { Header = custom, StaysOpenOnClick = true, Focusable = false });
        menu.Opened += (_, _) => menu.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (menu.IsOpen && swatches.Children.Count > 0) ((Button)swatches.Children[0]).Focus();
        }));
        return menu;
    }
}
