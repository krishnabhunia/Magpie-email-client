using System.Windows;
using System.Windows.Controls;

namespace Magpie.App.Views;

/// <summary>A question with a few plain-English answers (more than Yes / No). Returns the chosen index, or -1 for Cancel.</summary>
public static class ChoiceDialog
{
    public static int Ask(Window? owner, string title, string message, params string[] choices)
    {
        var w = new Window { Title = title + " — Magpie", Width = 460, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize };
        w.Style = (Style)Application.Current.FindResource("Window.Dialog");
        if (owner is { IsVisible: true }) { w.Owner = owner; w.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
        else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var result = -1;
        var panel = new StackPanel { Margin = new Thickness(22, 18, 22, 18) };
        panel.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.FindResource("Text.Serif"), FontSize = 17 });
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 16) });
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(0, 0, 8, 0), Style = (Style)Application.Current.FindResource("Button.Secondary") };
        buttons.Children.Add(cancel);
        for (var i = 0; i < choices.Length; i++)
        {
            var index = i;
            var b = new Button
            {
                Content = choices[i], Margin = new Thickness(0, 0, i == choices.Length - 1 ? 0 : 8, 0),
                Style = (Style)Application.Current.FindResource(i == choices.Length - 1 ? "Button.Primary" : "Button.Secondary"),
            };
            b.Click += (_, _) => { result = index; w.DialogResult = true; };
            buttons.Children.Add(b);
        }
        panel.Children.Add(buttons);
        w.Content = panel;
        w.ShowDialog();
        return result;
    }
}
