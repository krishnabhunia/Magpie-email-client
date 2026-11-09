using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using Magpie.App.Services;
using Magpie.Core.Mail;

namespace Magpie.App.Views;

public partial class ComposeWindow
{
    private void OnProviderProtection(object sender, RoutedEventArgs e)
    {
        var protection = ProviderProtection.For(_vm.From);
        var dialog = new Window
        {
            Title = "Provider-managed protection", Owner = this, Width = 560, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };
        dialog.SetResourceReference(Window.BackgroundProperty, "Brush.Window.Background");
        dialog.SetResourceReference(Window.ForegroundProperty, "Brush.Text.Primary");
        var content = new StackPanel { Margin = new Thickness(22) };
        content.Children.Add(new TextBlock { Text = protection.Provider, FontSize = 19, FontWeight = FontWeights.SemiBold });
        content.Children.Add(new TextBlock
        {
            Text = protection.Explanation, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 14, 0, 18), LineHeight = 23,
        });
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        if (protection.Help is { } help)
        {
            var info = new Button { Content = "Provider help", Margin = new Thickness(0, 0, 8, 6), Padding = new Thickness(12, 7, 12, 7) };
            info.SetResourceReference(StyleProperty, "Button.Secondary");
            AutomationProperties.SetName(info, "Read " + protection.Provider + " protection help in your browser");
            info.Click += (_, _) => Ui.OpenExternal(help);
            actions.Children.Add(info);
        }
        if (protection.Website is { } website)
        {
            var open = new Button { Content = "Open " + protection.Provider, Margin = new Thickness(0, 0, 8, 6), Padding = new Thickness(12, 7, 12, 7) };
            open.SetResourceReference(StyleProperty, "Button.Secondary");
            AutomationProperties.SetName(open, "Open " + protection.Provider + " in your browser without sending or copying this message");
            open.Click += (_, _) => Ui.OpenExternal(website);
            actions.Children.Add(open);
        }
        var close = new Button { Content = "Close", IsCancel = true, Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 0, 0, 6) };
        close.SetResourceReference(StyleProperty, "Button.Primary");
        close.Click += (_, _) => dialog.Close();
        actions.Children.Add(close);
        content.Children.Add(actions);
        dialog.Content = content;
        dialog.ShowDialog();
    }
}
