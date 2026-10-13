using System.Windows;
using System.Windows.Controls;

namespace Magpie.App.Views;

/// <summary>Asks for a password (design EX1: a settings backup), typed twice when <c>confirm</c> is set. Null = cancelled.</summary>
public static class PasswordDialog
{
    public static string? Ask(Window? owner, string title, string message, bool confirm, int minLength, string okText)
    {
        var w = new Window { Title = title + " — Magpie", Width = 460, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize };
        w.Style = (Style)Application.Current.FindResource("Window.Dialog");
        if (owner is { IsVisible: true }) { w.Owner = owner; w.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
        else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var panel = new StackPanel { Margin = new Thickness(22, 18, 22, 18) };
        panel.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.FindResource("Text.Serif"), FontSize = 17 });
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 14) });
        panel.Children.Add(new TextBlock { Text = confirm ? "Password for this backup" : "Password of the backup", Style = (Style)Application.Current.FindResource("Text.Caption"), Margin = new Thickness(0, 0, 0, 5) });
        var first = new PasswordBox { Padding = new Thickness(8, 6, 8, 6) };
        panel.Children.Add(first);
        PasswordBox? second = null;
        if (confirm)
        {
            panel.Children.Add(new TextBlock { Text = "Type it again", Style = (Style)Application.Current.FindResource("Text.Caption"), Margin = new Thickness(0, 10, 0, 5) });
            second = new PasswordBox { Padding = new Thickness(8, 6, 8, 6) };
            panel.Children.Add(second);
        }
        var hint = new TextBlock { Style = (Style)Application.Current.FindResource("Text.Caption"), Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(hint);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(0, 0, 8, 0), Style = (Style)Application.Current.FindResource("Button.Secondary") };
        var ok = new Button { Content = okText, IsDefault = true, IsEnabled = false, Style = (Style)Application.Current.FindResource("Button.Primary") };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        panel.Children.Add(buttons);

        void Check()
        {
            var p = first.Password;
            var good = p.Length >= minLength && (second == null || second.Password == p);
            ok.IsEnabled = good;
            hint.Text = p.Length < minLength ? $"At least {minLength} characters."
                      : second != null && second.Password != p ? "The two don't match yet." : confirm ? "Keep it safe: Magpie can't recover it." : "";
        }
        first.PasswordChanged += (_, _) => Check();
        if (second != null) second.PasswordChanged += (_, _) => Check();
        Check();

        string? result = null;
        ok.Click += (_, _) => { result = first.Password; w.DialogResult = true; };
        w.Content = panel;
        w.Loaded += (_, _) => first.Focus();
        w.ShowDialog();
        return result;
    }
}
