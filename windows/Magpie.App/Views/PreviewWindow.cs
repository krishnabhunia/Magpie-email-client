using System.Windows;
using System.Windows.Controls;
using Magpie.App.Services;
using Magpie.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Magpie.App.Views;

/// <summary>Design HM1 (A5): a picture or PDF shown inside Magpie, without another app.</summary>
public static class PreviewWindow
{
    private const string Host = "preview.magpie.local";

    public static void Show(Window? owner, string path, string name)
    {
        var w = new Window { Title = name + " — Magpie", Width = 900, Height = 700, MinWidth = 420, MinHeight = 320 };
        w.Style = (Style)Application.Current.FindResource("Window.Dialog");
        w.ResizeMode = ResizeMode.CanResize;
        if (owner is { IsVisible: true }) { w.Owner = owner; w.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
        else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var root = new DockPanel();
        var bar = new DockPanel { Margin = new Thickness(16, 10, 16, 10) };
        var open = new Button { Content = "Open in its app", Style = (Style)Application.Current.FindResource("Button.Secondary"), Margin = new Thickness(8, 0, 0, 0) };
        open.Click += (_, _) => AttachmentDialog.Show(path, name);
        var close = new Button { Content = "Close", Style = (Style)Application.Current.FindResource("Button.Secondary"), IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
        close.Click += (_, _) => w.Close();
        DockPanel.SetDock(close, Dock.Right);
        DockPanel.SetDock(open, Dock.Right);
        bar.Children.Add(close);
        bar.Children.Add(open);
        bar.Children.Add(new TextBlock { Text = name, Style = (Style)Application.Current.FindResource("Text.Serif"), FontSize = 16, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        var web = new WebView2 { DefaultBackgroundColor = ThemeManager.WebBackground };
        root.Children.Add(web);
        w.Content = root;
        w.Loaded += async (_, _) =>
        {
            try
            {
                if (!await WebHost.InitAsync(web, scripts: true)) { AttachmentDialog.Show(path, name); w.Close(); return; }
                var core = web.CoreWebView2;
                core.SetVirtualHostNameToFolderMapping(Host, Path.GetDirectoryName(path)!, CoreWebView2HostResourceAccessKind.Allow);
                var url = $"https://{Host}/{Uri.EscapeDataString(Path.GetFileName(path))}";
                // Only the file itself; links inside a PDF open in the browser.
                core.NavigationStarting += (_, e) =>
                {
                    if (e.Uri.StartsWith($"https://{Host}/", StringComparison.OrdinalIgnoreCase)) return;
                    e.Cancel = true;
                    if (e.Uri.StartsWith("http", StringComparison.OrdinalIgnoreCase)) Ui.OpenExternal(e.Uri);
                };
                core.NewWindowRequested += (_, e) => { e.Handled = true; if (e.Uri.StartsWith("http", StringComparison.OrdinalIgnoreCase)) Ui.OpenExternal(e.Uri); };
                core.Navigate(url);
            }
            catch (Exception ex)
            {
                Log.Warn("preview: " + ex.Message);
                w.Close();
                AttachmentDialog.Show(path, name);
            }
        };
        w.Show();
    }
}
