using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Magpie.App.Services;
using Magpie.Core;
using Magpie.Core.Mail;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Magpie.App.Views;

/// <summary>Structured Office text rendered locally, with no scripts, network, downloads or external navigation.</summary>
public static class OfficePreviewWindow
{
    public static void Show(Window? owner, string name, OfficePreviewDocument preview) => Create(owner, name, preview).Show();

    /// <summary>The optional environment permits isolated native UI tests without using the normal browser profile.</summary>
    public static Window Create(Window? owner, string name, OfficePreviewDocument preview, CoreWebView2Environment? environment = null)
    {
        Style S(string key) => (Style)Application.Current.FindResource(key);
        var window = new Window
        {
            Title = name + " — Office preview — Magpie", Width = 950, Height = 720,
            MinWidth = 480, MinHeight = 360, Style = S("Window.Dialog"), ResizeMode = ResizeMode.CanResize,
        };
        if (owner is { IsVisible: true }) { window.Owner = owner; window.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
        else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var root = new DockPanel { Margin = new Thickness(14) };
        var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var close = new Button { Content = "Close", Style = S("Button.Secondary"), IsCancel = true, Margin = new Thickness(12, 0, 0, 0) };
        close.Click += (_, _) => window.Close();
        DockPanel.SetDock(close, Dock.Right); bar.Children.Add(close);
        var notice = new TextBlock
        {
            Text = "Read-only preview · no macros, formulas or external resources are executed"
                + (preview.Truncated ? " · shortened for safety" : ""),
            TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Style = S("Text.Caption"),
        };
        bar.Children.Add(notice); DockPanel.SetDock(bar, Dock.Top); root.Children.Add(bar);
        var tabs = new TabControl();
        AutomationProperties.SetName(tabs, "Office preview format");
        var structured = new TabItem { Header = "Structured preview" };
        var plain = new TabItem { Header = "Plain text" };
        var web = new WebView2 { DefaultBackgroundColor = ThemeManager.WebBackground };
        AutomationProperties.SetName(web, "Read-only Office document preview");
        structured.Content = web;
        var text = new TextBox
        {
            Text = preview.PlainText, IsReadOnly = true, AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(12),
        };
        AutomationProperties.SetName(text, "Office document plain text");
        plain.Content = text;
        tabs.Items.Add(structured); tabs.Items.Add(plain); root.Children.Add(tabs);
        window.Content = root;
        var closed = false;
        var ready = false;
        void UsePlainText()
        {
            if (closed) return;
            structured.IsEnabled = false; tabs.SelectedItem = plain;
            notice.Text = "Structured preview is unavailable on this PC. Plain text is shown; the attachment was not opened externally.";
        }
        void RefreshTheme()
        {
            if (closed || !ready) return;
            web.DefaultBackgroundColor = ThemeManager.WebBackground;
            web.CoreWebView2?.NavigateToString(preview.HtmlForTheme(ThemeManager.IsDark));
        }
        ThemeManager.Changed += RefreshTheme;
        window.Closed += (_, _) =>
        {
            closed = true;
            ThemeManager.Changed -= RefreshTheme;
            web.Dispose();
        };
        window.Loaded += async (_, _) =>
        {
            try
            {
                if (environment == null)
                {
                    if (!await WebHost.InitAsync(web, scripts: false)) { UsePlainText(); return; }
                }
                else await web.EnsureCoreWebView2Async(environment);
                if (closed) return;
                var core = web.CoreWebView2;
                core.Settings.IsScriptEnabled = false;
                core.Settings.IsWebMessageEnabled = false;
                core.Settings.AreHostObjectsAllowed = false;
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.AreBrowserAcceleratorKeysEnabled = false;
                core.Settings.IsGeneralAutofillEnabled = false;
                core.Settings.IsPasswordAutosaveEnabled = false;
                core.ClearVirtualHostNameToFolderMapping(WebHost.Host);
                // Defense in depth: the generated page also has a default-src 'none' CSP and no resource URLs.
                core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
                core.WebResourceRequested += (_, e) =>
                    e.Response = core.Environment.CreateWebResourceResponse(Stream.Null, 403, "Blocked", "Content-Type: text/plain");
                core.NavigationStarting += (_, e) =>
                {
                    if (!string.Equals(e.Uri, "about:blank", StringComparison.OrdinalIgnoreCase)) e.Cancel = true;
                };
                core.NewWindowRequested += (_, e) => e.Handled = true;
                core.DownloadStarting += (_, e) => e.Cancel = true;
                core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
                ready = true;
                RefreshTheme();
            }
            catch (Exception ex)
            {
                Log.Warn("structured Office preview: " + ex.Message);
                UsePlainText();
            }
        };
        return window;
    }
}
