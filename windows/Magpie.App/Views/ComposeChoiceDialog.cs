using System.Windows;
using System.Windows.Controls;

namespace Magpie.App.Views;

/// <summary>
/// Design CW1: a question with two or three plainly worded buttons, each with its own id, and an optional "Always do this" tick — used by the
/// compose window for the forgot-attachment check, a pasted picture, the plain-text switch and Discard.
/// </summary>
internal static class ComposeChoiceDialog
{
    public sealed record Choice(string Text, string Id, bool Primary = false, bool Danger = false, bool Cancel = false);

    /// <summary>Returns the chosen id (null = closed / Esc) and whether the tick box was ticked.</summary>
    public static (string? Id, bool Remember) Ask(Window? owner, string title, string text, Choice[] choices, string? tickText = null)
    {
        string? chosen = null;
        var dialog = new Window
        {
            Owner = owner, Title = title, Width = 520, MinWidth = 420, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            Style = (Style)Application.Current.FindResource("Window.Dialog"),
        };
        var panel = new StackPanel { Margin = new Thickness(24) };
        var heading = new TextBlock { Text = title, Style = (Style)Application.Current.FindResource("Text.Serif"), FontSize = 20, TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(heading);
        panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 16) });
        CheckBox? tick = null;
        if (tickText != null)
        {
            tick = new CheckBox { Content = tickText, Margin = new Thickness(0, 0, 0, 14) };
            panel.Children.Add(tick);
        }
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        for (var i = 0; i < choices.Length; i++)
        {
            var c = choices[i];
            var b = new Button { Content = c.Text, Margin = new Thickness(0, 0, i == choices.Length - 1 ? 0 : 8, 6), IsCancel = c.Cancel, IsDefault = c.Primary };
            b.SetResourceReference(FrameworkElement.StyleProperty, c.Danger ? "Button.Danger" : c.Primary ? "Button.Primary" : "Button.Secondary");
            if (!c.Primary && !c.Danger) b.FontWeight = FontWeights.Normal;
            b.Click += (_, _) => { chosen = c.Id; dialog.DialogResult = !c.Cancel; };
            buttons.Children.Add(b);
        }
        panel.Children.Add(buttons);
        dialog.Content = panel;
        Services.Typography.SetRole(dialog, "dialogBody");
        Services.Typography.SetRole(heading, "dialogTitle");
        dialog.ShowDialog();
        return (chosen, tick?.IsChecked == true);
    }
}
