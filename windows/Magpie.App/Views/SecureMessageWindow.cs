using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Magpie.App.Services;
using Magpie.Core.Mail;
using Magpie.Core.Security;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Win32;
using MimeKit;

namespace Magpie.App.Views;

/// <summary>Decrypted content stays in memory. InPrivate WebView has no file mapping, scripts, network or navigation.</summary>
public static class SecureMessageWindow
{
    public const string DocumentUri = "https://secure-reader.magpie.invalid/message.html";
    private static Task<CoreWebView2Environment>? _environment;

    /// <summary>Takes ownership of result. An optional environment supports isolated native smoke tests.</summary>
    public static Window Create(Window? owner, SecureMailReadResult result, CoreWebView2Environment? environment = null)
    {
        Style S(string key) => (Style)Application.Current.FindResource(key);
        var window = new Window { Title = "Message security — Magpie", Width = 1000, Height = 800,
            MinWidth = 600, MinHeight = 450, Style = S("Window.Dialog"), ResizeMode = ResizeMode.CanResize };
        if (owner is { IsVisible: true }) { window.Owner = owner; window.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
        else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var root = new DockPanel { Margin = new Thickness(16) }; window.Content = root;
        var top = new StackPanel { Margin = new Thickness(0, 0, 0, 10) }; DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        top.Children.Add(new TextBlock { Text = result.Message.Subject ?? "(no subject)", FontSize = 20, FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis });
        var status = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), MaxHeight = 170,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 7, 0, 7),
            Text = result.Summary + string.Concat(result.Signatures.Select(s => "\nSigner: " + s.Email + " · " + s.Algorithm + "\nFingerprint: " + s.Fingerprint)) };
        AutomationProperties.SetName(status, "Message signature validity and trust"); top.Children.Add(status);
        top.Children.Add(new TextBlock { Text = "Body signatures do not protect the outer Subject or address headers. Decrypted content is not saved in the message cache. External images, links and scripts are blocked.",
            TextWrapping = TextWrapping.Wrap, FontSize = 11.5 });
        if (result.Encrypted && result.Signatures.Count == 0)
            top.Children.Add(new TextBlock { Text = "This message has no verified sender signature. Encryption alone does not authenticate the sender.", TextWrapping = TextWrapping.Wrap,
                FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 0) });
        var bottom = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) }; DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        var close = new Button { Content = "Close", Style = S("Button.Secondary"), IsCancel = true, Margin = new Thickness(0, 0, 10, 0) };
        close.Click += (_, _) => window.Close(); bottom.Children.Add(close);
        var body = MimeText.Extract(result.Message);
        var attachments = body.Attachments.Where(a => !a.Inline).ToArray();
        var chooseAttachment = new ComboBox { ItemsSource = attachments, DisplayMemberPath = "FileName", MinWidth = 220, MaxWidth = 400, Margin = new Thickness(0, 0, 8, 0),
            Visibility = attachments.Length > 0 ? Visibility.Visible : Visibility.Collapsed };
        AutomationProperties.SetName(chooseAttachment, "Decrypted attachment"); bottom.Children.Add(chooseAttachment);
        if (attachments.Length > 0) chooseAttachment.SelectedIndex = 0;
        var save = new Button { Content = "Save attachment…", Style = S("Button.Secondary"), Visibility = chooseAttachment.Visibility };
        bottom.Children.Add(save);
        var tabs = new TabControl(); AutomationProperties.SetName(tabs, "Secure message view"); root.Children.Add(tabs);
        var formatted = new TabItem { Header = "Message" }; var plain = new TabItem { Header = "Plain text" };
        var web = new WebView2 { DefaultBackgroundColor = ThemeManager.WebBackground };
        AutomationProperties.SetName(web, "Isolated secure message"); formatted.Content = web;
        var text = new TextBox { Text = body.Text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(12) };
        AutomationProperties.SetName(text, "Secure message plain text"); plain.Content = text;
        tabs.Items.Add(formatted); tabs.Items.Add(plain);
        var closed = false; var initialized = false; var saving = false;
        byte[] html = Array.Empty<byte>();
        var responses = new List<MemoryStream>();
        void PlainFallback()
        {
            if (closed) return;
            formatted.IsEnabled = false; tabs.SelectedItem = plain;
            status.Text += "\nThe isolated HTML viewer is unavailable. Plain text is shown in memory.";
            try { web.Dispose(); } catch { }
        }
        void ReleaseResponses() { foreach (var response in responses) response.Dispose(); responses.Clear(); }
        save.Click += async (_, _) =>
        {
            if (closed || saving || chooseAttachment.SelectedItem is not Magpie.Core.Models.AttachmentInfo attachment) return;
            if (MessageBox.Show(window, "Save this decrypted attachment as an ordinary file? Anyone who can read that file can read its contents.",
                "Save decrypted attachment", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            var name = attachment.FileName;
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            var dialog = new SaveFileDialog { Title = "Save decrypted attachment", FileName = Path.GetFileName(name), Filter = "All files|*.*" };
            if (dialog.ShowDialog(window) != true) return;
            saving = true; save.IsEnabled = false;
            var temporary = Path.Combine(Path.GetDirectoryName(dialog.FileName)!, ".magpie-save-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                var entity = MimeText.PartAt(result.Message, attachment.Index) ?? throw new InvalidOperationException();
                await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    if (entity is MimePart part) await part.Content!.DecodeToAsync(file);
                    else if (entity is MessagePart attached && attached.Message != null) await attached.Message.WriteToAsync(file);
                    else throw new InvalidOperationException();
                }
                File.Move(temporary, dialog.FileName, true);
            }
            catch { Ui.Error("Save attachment", "The attachment could not be saved. Check the destination and available space."); }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
                saving = false; if (!closed) save.IsEnabled = true;
            }
        };
        window.Closing += (_, e) => { if (saving) e.Cancel = true; };
        window.Closed += (_, _) =>
        {
            closed = true;
            try { web.Dispose(); }
            finally
            {
                ReleaseResponses(); CryptographicOperations.ZeroMemory(html); html = Array.Empty<byte>();
                text.Clear(); body.Html = ""; body.Text = ""; body.Images.Clear(); result.Dispose();
            }
        };
        window.Loaded += async (_, _) =>
        {
            if (closed || initialized) return; initialized = true;
            try
            {
                if (environment == null)
                {
                    if (!WebHost.RuntimeAvailable(out _)) { PlainFallback(); return; }
                    _environment ??= CoreWebView2Environment.CreateAsync(null, Path.Combine(WebHost.DataDir, "SecureReader"));
                    environment = await _environment;
                }
                if (closed) return;
                var options = environment.CreateCoreWebView2ControllerOptions();
                options.ProfileName = "SecureMail"; options.IsInPrivateModeEnabled = true;
                await web.EnsureCoreWebView2Async(environment, options);
                if (closed) { web.Dispose(); return; }
                var core = web.CoreWebView2;
                if (!core.Profile.IsInPrivateModeEnabled) { PlainFallback(); return; }
                core.Settings.IsScriptEnabled = false; core.Settings.IsWebMessageEnabled = false;
                core.Settings.AreHostObjectsAllowed = false; core.Settings.AreDevToolsEnabled = false;
                core.Settings.AreDefaultContextMenusEnabled = false; core.Settings.AreBrowserAcceleratorKeysEnabled = false;
                core.Settings.IsStatusBarEnabled = false; core.Settings.IsGeneralAutofillEnabled = false; core.Settings.IsPasswordAutosaveEnabled = false;
                core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
                core.WebResourceRequested += (_, e) =>
                {
                    if (!closed && e.Request.Uri == DocumentUri && e.Request.Method == "GET" && e.ResourceContext == CoreWebView2WebResourceContext.Document)
                    {
                        var response = new MemoryStream(html, writable: false); responses.Add(response);
                        e.Response = core.Environment.CreateWebResourceResponse(response, 200, "OK",
                            "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\n"
                            + "Content-Security-Policy: default-src 'none'; img-src data:; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'\r\n");
                    }
                    else e.Response = core.Environment.CreateWebResourceResponse(Stream.Null, 403, "Blocked", "Cache-Control: no-store\r\n");
                };
                core.NavigationStarting += (_, e) => { if (closed || e.Uri != DocumentUri) e.Cancel = true; };
                core.FrameNavigationStarting += (_, e) => e.Cancel = true;
                core.NavigationCompleted += (_, _) => ReleaseResponses();
                core.NewWindowRequested += (_, e) => e.Handled = true;
                core.DownloadStarting += (_, e) => { e.Cancel = true; e.Handled = true; };
                core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
                var safe = HtmlRenderer.SanitizeBody(body.Html, MimeText.InlineImages(result.Message), allowRemote: false, logFailure: false).html;
                var colors = ThemeManager.IsDark ? "color:#e8e6e1;background:#1b1e22" : "color:#14181c;background:#fff";
                html = Encoding.UTF8.GetBytes("<!doctype html><html><head><meta charset=\"utf-8\"><style>body{font:15px system-ui;padding:18px;overflow-wrap:anywhere;" + colors
                    + "}img{max-width:100%}a{pointer-events:none}</style></head><body>" + safe + "</body></html>");
                core.Navigate(DocumentUri);
            }
            catch { PlainFallback(); }
        };
        return window;
    }
}
