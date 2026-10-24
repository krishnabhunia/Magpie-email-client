using System.Windows;
using System.Windows.Controls;

namespace Magpie.App.Views;

/// <summary>FR1: a generated draft never silently overwrites the user's own text.</summary>
internal static class ReplyInsertionDialog
{
    public static string? Ask(Window owner)
    {
        string? choice = null;
        var dialog = new Window
        {
            Owner = owner, Title = "Keep your typed text?", Width = 550, MinWidth = 420,
            SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Style = (Style)Application.Current.FindResource("Window.Dialog"),
        };
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock
        {
            Text = "Keep your typed text?", Style = (Style)Application.Current.FindResource("Text.Serif"),
            FontSize = 20, TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Your reply already contains text. Choose how to use the AI draft. Your signature and the quoted message stay in place.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 18),
        });
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var keep = new Button { Content = "Keep editing", IsCancel = true, IsDefault = true, Margin = new Thickness(0, 0, 8, 6), Style = (Style)Application.Current.FindResource("Button.Secondary") };
        var append = new Button { Content = "Add after my text", Margin = new Thickness(0, 0, 8, 6), Style = (Style)Application.Current.FindResource("Button.Secondary") };
        var replace = new Button { Content = "Replace body", Margin = new Thickness(0, 0, 0, 6), Style = (Style)Application.Current.FindResource("Button.Primary") };
        keep.Click += (_, _) => dialog.DialogResult = false;
        append.Click += (_, _) => { choice = "appendBody"; dialog.DialogResult = true; };
        replace.Click += (_, _) => { choice = "replaceBody"; dialog.DialogResult = true; };
        buttons.Children.Add(keep); buttons.Children.Add(append); buttons.Children.Add(replace);
        panel.Children.Add(buttons);
        dialog.Content = panel;
        Magpie.App.Services.Typography.SetRole(dialog, "dialogBody");
        Magpie.App.Services.Typography.SetRole(panel.Children[0], "dialogTitle");
        return dialog.ShowDialog() == true ? choice : null;
    }
}
