using System.Windows;
using System.Windows.Controls;
using Magpie.App.Services;
using Magpie.Core;
using Magpie.Core.Mail;

namespace Magpie.App.Views;

/// <summary>
/// The signature editor (design B6), in its own window with a normal WebView2 — the same kind of editor as the compose
/// window. When WebView2 can't start, a plain-text box is used instead.
/// </summary>
public partial class SignatureEditorWindow : Window
{
    private bool _ready, _plain;
    private string _html;
    public string? Result { get; private set; }

    public static readonly string[] Fonts = { "Segoe UI", "Arial", "Calibri", "Georgia", "Times New Roman", "Verdana", "Courier New" };
    public static readonly string[] Colours = { "#23282E", "#14606E", "#1D4ED8", "#4B3F86", "#B3261E", "#B45309", "#1B6B2E", "#5A6068" };

    /// <summary>Opens the editor for <paramref name="email"/>'s signature; returns the new HTML, or null when cancelled.</summary>
    public static string? Edit(Window owner, string email, string html)
    {
        var w = new SignatureEditorWindow(email, html) { Owner = owner };
        return w.ShowDialog() == true ? w.Result : null;
    }

    private SignatureEditorWindow(string email, string html)
    {
        InitializeComponent();
        _html = html;
        Heading.Text = "Signature for " + email;
        FontBox.ItemsSource = Fonts;
        ColourList.ItemsSource = Colours;
        Loaded += async (_, _) => await StartAsync();
    }

    private async Task StartAsync()
    {
        try
        {
            if (!await WebHost.InitAsync(Editor, scripts: true)) { UsePlain(); return; }
            Editor.DefaultBackgroundColor = ThemeManager.IsDark ? System.Drawing.Color.FromArgb(255, 0x1E, 0x23, 0x28) : System.Drawing.Color.White;
            Editor.AllowExternalDrop = false;
            var core = Editor.CoreWebView2;
            core.NavigationStarting += (_, e) => { if (!WebHost.IsOwnPage(e.Uri)) e.Cancel = true; };
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.WebMessageReceived += OnMessage;
            core.Navigate(WebHost.Publish(SignatureEditorPage.Html, "signature"));
        }
        catch (Exception ex)
        {
            Log.Error("signature editor", ex);
            UsePlain();
        }
    }

    private void UsePlain()
    {
        _plain = true;
        Editor.Visibility = Visibility.Collapsed;
        Plain.Visibility = Visibility.Visible;
        Plain.Text = MimeText.HtmlToText(_html).Trim();
        Plain.Focus();
    }

    private async void OnMessage(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(e.TryGetWebMessageAsString());
            var root = doc.RootElement;
            switch (root.GetProperty("t").GetString())
            {
                case "ready":
                    await Editor.CoreWebView2.ExecuteScriptAsync($"setDark({(ThemeManager.IsDark ? "true" : "false")})");
                    await Editor.CoreWebView2.ExecuteScriptAsync($"setHtml({System.Text.Json.JsonSerializer.Serialize(_html)})");
                    await Editor.CoreWebView2.ExecuteScriptAsync("document.getElementById('ed').focus()");
                    _ready = true;
                    Editor.Focus();
                    break;
                case "change":
                    _html = root.GetProperty("html").GetString() ?? "";
                    break;
                case "link":
                    OnLink(this, new RoutedEventArgs());
                    break;
            }
        }
        catch (Exception ex) { Log.Warn("signature editor message: " + ex.Message); }
    }

    private async void OnDone(object sender, RoutedEventArgs e)
    {
        if (_plain)
        {
            var t = Plain.Text.Trim();
            Result = t.Length == 0 ? "" : t.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\r\n", "\n").Replace("\n", "<br>");
        }
        else if (_ready)
        {
            try
            {
                var json = await Editor.CoreWebView2.ExecuteScriptAsync("getHtml()");
                Result = System.Text.Json.JsonSerializer.Deserialize<string>(json) ?? "";
            }
            catch (Exception ex) { Log.Warn("signature editor: " + ex.Message); Result = _html; }
        }
        else Result = _html;
        DialogResult = true;
    }

    private Task Exec(string command, string? value = null)
    {
        if (!_ready) return Task.CompletedTask;
        var arg = value == null ? "" : ", " + System.Text.Json.JsonSerializer.Serialize(value);
        return Editor.CoreWebView2.ExecuteScriptAsync($"fmt({System.Text.Json.JsonSerializer.Serialize(command)}{arg})");
    }

    private void OnFormat(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string cmd }) _ = Exec(cmd);
    }

    private void OnFont(object sender, SelectionChangedEventArgs e)
    {
        if (FontBox.SelectedItem is string font) _ = Exec("fontName", font);
    }

    private void OnColour(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string hex }) _ = Exec("foreColor", hex);
    }

    private void OnLink(object sender, RoutedEventArgs e)
    {
        var url = TextPromptDialog.Ask(this, "Add a link", "Web address, or an email address", "https://");
        if (string.IsNullOrWhiteSpace(url) || url == "https://") return;
        if (url.Contains('@') && !url.Contains("://") && !url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) url = "mailto:" + url;
        else if (!url.Contains("://") && !url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) url = "https://" + url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https" or "mailto")) { Ui.Error("Add a link", "That doesn't look like a web or email address.", this); return; }
        _ = Exec("createLink", u.ToString());
    }

    private void OnImage(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Add a picture", Filter = "Pictures|*.png;*.jpg;*.jpeg;*.gif|All files|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var info = new FileInfo(dlg.FileName);
            if (info.Length > 1024 * 1024) { Ui.Error("Add a picture", "That picture is over 1 MB. A logo for a signature is best kept under 100 KB — it goes out with every email.", this); return; }
            var type = Path.GetExtension(dlg.FileName).ToLowerInvariant() switch { ".png" => "image/png", ".gif" => "image/gif", ".jpg" or ".jpeg" => "image/jpeg", _ => "" };
            if (type.Length == 0) { Ui.Error("Add a picture", "Use a PNG, JPG or GIF picture.", this); return; }
            var data = "data:" + type + ";base64," + Convert.ToBase64String(File.ReadAllBytes(dlg.FileName));
            if (_ready) _ = Editor.CoreWebView2.ExecuteScriptAsync($"insertImage({System.Text.Json.JsonSerializer.Serialize(data)})");
        }
        catch (Exception ex) { Ui.Error("Add a picture", ex.Message, this); }
    }
}
