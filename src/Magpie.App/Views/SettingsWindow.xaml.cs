using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Magpie.App.Services;
using Magpie.App.ViewModels;
using Magpie.Core;

namespace Magpie.App.Views;

public partial class SettingsWindow : Window
{
    private static SettingsWindow? _open;
    private readonly SettingsViewModel _vm;

    public static void Open(string? page)
    {
        if (_open != null)
        {
            if (page != null) _open._vm.GoTo(page);
            _open.Activate();
            return;
        }
        var w = new SettingsWindow(page);
        var owner = Application.Current.MainWindow;
        if (owner is { IsVisible: true }) w.Owner = owner;
        else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _open = w;
        w.Closed += (_, _) => _open = null;
        w.Show();
    }

    public SettingsWindow(string? page)
    {
        InitializeComponent();
        _vm = new SettingsViewModel(page);
        DataContext = _vm;
        ApiKeyBox.Password = _vm.ApiKey;
        _vm.ApiKeyChanged = false;
        _vm.Saved += Close;
        _vm.HighlightRequested += Highlight;
        _vm.SignatureAccountChanged += a => _ = LoadSignatureAsync(a);
        Action onAutoDelete = () => Ui.Post(_vm.LoadAutoDelete);
        AppServices.Engine.AutoDeleteChanged += onAutoDelete;
        Closed += (_, _) => AppServices.Engine.AutoDeleteChanged -= onAutoDelete;
        _vm.PropertyChanged += (_, a) => { if (a.PropertyName == nameof(SettingsViewModel.Page) && _vm.Page == "Signatures") _ = StartSignatureEditorAsync(); };
        Loaded += (_, _) => { if (_vm.Page == "Signatures") _ = StartSignatureEditorAsync(); };
        PreviewKeyDown += (_, e) =>
        {
            var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            if (ctrl && e.Key == Key.F) { SettingsSearchBox.Focus(); SettingsSearchBox.SelectAll(); e.Handled = true; }
        };
        FillKeys();
    }

    // ───────────────────────── search (design SS1) ─────────────────────────

    private void OnSearchKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _vm.SearchText.Length > 0) { _vm.SearchText = ""; e.Handled = true; }
        else if (e.Key == Key.Enter && _vm.SearchHits.Count > 0) { _vm.GoToHitCommand.Execute(_vm.SearchHits[0]); e.Handled = true; }
    }

    private void OnSearchClear(object sender, RoutedEventArgs e) { _vm.SearchText = ""; SettingsSearchBox.Focus(); }

    /// <summary>Scrolls to the setting and flashes it yellow for a moment.</summary>
    private void Highlight(string anchor)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (FindName(anchor) is not FrameworkElement el) return;
            el.BringIntoView();
            var target = el;
            var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0, 0xFF, 0xE0, 0x66));
            var anim = new System.Windows.Media.Animation.ColorAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(1600) };
            anim.KeyFrames.Add(new System.Windows.Media.Animation.LinearColorKeyFrame(System.Windows.Media.Color.FromArgb(0xB0, 0xFF, 0xE0, 0x66), TimeSpan.FromMilliseconds(150)));
            anim.KeyFrames.Add(new System.Windows.Media.Animation.LinearColorKeyFrame(System.Windows.Media.Color.FromArgb(0xB0, 0xFF, 0xE0, 0x66), TimeSpan.FromMilliseconds(900)));
            anim.KeyFrames.Add(new System.Windows.Media.Animation.LinearColorKeyFrame(System.Windows.Media.Color.FromArgb(0, 0xFF, 0xE0, 0x66), TimeSpan.FromMilliseconds(1600)));
            switch (target)
            {
                case Border b: { var old = b.Background; b.Background = brush; anim.Completed += (_, _) => b.Background = old; break; }
                case Panel p: { var old = p.Background; p.Background = brush; anim.Completed += (_, _) => p.Background = old; break; }
                case ItemsControl ic: { var old = ic.Background; ic.Background = brush; anim.Completed += (_, _) => ic.Background = old; break; }
                default: return;
            }
            brush.BeginAnimation(System.Windows.Media.SolidColorBrush.ColorProperty, anim);
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // ───────────────────────── About Me (design A1) ─────────────────────────

    /// <summary>"What's this email about?" — then Magpie's own compose window opens, pre-filled.</summary>
    private void OnFeedbackEmail(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = FeedbackLink, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        menu.Items.Add(new MenuItem { Header = "What's this email about?", IsEnabled = false, FontWeight = FontWeights.SemiBold });
        menu.Items.Add(new Separator());
        foreach (var kind in SettingsViewModel.FeedbackKinds)
        {
            var k = kind;
            var mi = new MenuItem { Header = k };
            mi.Click += (_, _) => _vm.ComposeFeedback(k);
            menu.Items.Add(mi);
        }
        menu.IsOpen = true;
    }

    private void OnApiKeyChanged(object sender, RoutedEventArgs e)
    {
        _vm.ApiKey = ApiKeyBox.Password;
        _vm.ApiKeyChanged = true;
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        // The signature editor reports edits a moment later; take its latest text before saving.
        if (_sigReady)
        {
            try
            {
                var json = await SigEditor.CoreWebView2.ExecuteScriptAsync("getHtml()");
                _vm.SetSignatureHtml(System.Text.Json.JsonSerializer.Deserialize<string>(json) ?? "");
            }
            catch (Exception ex) { Log.Warn("signature editor: " + ex.Message); }
        }
        _vm.SaveCommand.Execute(null);
    }

    // ───────────────────────── signature editor (design B6) ─────────────────────────

    private bool _sigStarted, _sigReady;

    private async Task StartSignatureEditorAsync()
    {
        if (_sigStarted) return;
        _sigStarted = true;
        try
        {
            if (!await WebHost.InitAsync(SigEditor, scripts: true)) { UsePlainSignature(); return; }
            SigEditor.DefaultBackgroundColor = ThemeManager.IsDark ? System.Drawing.Color.FromArgb(255, 0x1E, 0x23, 0x28) : System.Drawing.Color.White;
            SigEditor.AllowExternalDrop = false;
            var core = SigEditor.CoreWebView2;
            core.NavigationStarting += (_, e) => { if (!WebHost.IsOwnPage(e.Uri)) e.Cancel = true; };
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.WebMessageReceived += OnSignatureMessage;
            core.Navigate(WebHost.Publish(SignatureEditorPage.Html, "signature"));
        }
        catch (Exception ex)
        {
            Log.Error("signature editor", ex);
            UsePlainSignature();
        }
    }

    private void UsePlainSignature()
    {
        SigEditor.Visibility = Visibility.Collapsed;
        SigPlain.Visibility = Visibility.Visible;
    }

    private async void OnSignatureMessage(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(e.TryGetWebMessageAsString());
            var root = doc.RootElement;
            switch (root.GetProperty("t").GetString())
            {
                case "ready":
                    await SigEditor.CoreWebView2.ExecuteScriptAsync($"setDark({(ThemeManager.IsDark ? "true" : "false")})");
                    _sigReady = true;
                    await LoadSignatureAsync(_vm.SignatureAccount);
                    break;
                case "change":
                    _vm.SetSignatureHtml(root.GetProperty("html").GetString() ?? "");
                    break;
                case "link":
                    OnSigLink(this, new RoutedEventArgs());
                    break;
            }
        }
        catch (Exception ex) { Log.Warn("signature editor message: " + ex.Message); }
    }

    private async Task LoadSignatureAsync(EditableAccount? a)
    {
        if (!_sigReady) return;
        await SigEditor.CoreWebView2.ExecuteScriptAsync($"setHtml({System.Text.Json.JsonSerializer.Serialize(a?.SignatureHtml ?? "")})");
    }

    private Task SigExec(string command, string? value = null)
    {
        if (!_sigReady) return Task.CompletedTask;
        var arg = value == null ? "" : ", " + System.Text.Json.JsonSerializer.Serialize(value);
        return SigEditor.CoreWebView2.ExecuteScriptAsync($"fmt({System.Text.Json.JsonSerializer.Serialize(command)}{arg})");
    }

    private void OnSigFormat(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string cmd }) _ = SigExec(cmd);
    }

    private void OnSigFont(object sender, SelectionChangedEventArgs e)
    {
        if (SigFont.SelectedItem is string font) _ = SigExec("fontName", font);
    }

    private void OnSigColour(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string hex }) _ = SigExec("foreColor", hex);
    }

    private void OnSigLink(object sender, RoutedEventArgs e)
    {
        var url = TextPromptDialog.Ask(this, "Add a link", "Web address, or an email address", "https://");
        if (string.IsNullOrWhiteSpace(url) || url == "https://") return;
        if (url.Contains('@') && !url.Contains("://") && !url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) url = "mailto:" + url;
        else if (!url.Contains("://") && !url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) url = "https://" + url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https" or "mailto")) { Ui.Error("Add a link", "That doesn't look like a web or email address."); return; }
        _ = SigExec("createLink", u.ToString());
    }

    private void OnSigImage(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Add a picture", Filter = "Pictures|*.png;*.jpg;*.jpeg;*.gif|All files|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var info = new FileInfo(dlg.FileName);
            if (info.Length > 1024 * 1024) { Ui.Error("Add a picture", "That picture is over 1 MB. A logo for a signature is best kept under 100 KB — it goes out with every email."); return; }
            var type = Path.GetExtension(dlg.FileName).ToLowerInvariant() switch { ".png" => "image/png", ".gif" => "image/gif", ".jpg" or ".jpeg" => "image/jpeg", _ => "" };
            if (type.Length == 0) { Ui.Error("Add a picture", "Use a PNG, JPG or GIF picture."); return; }
            var data = "data:" + type + ";base64," + Convert.ToBase64String(File.ReadAllBytes(dlg.FileName));
            if (_sigReady) _ = SigEditor.CoreWebView2.ExecuteScriptAsync($"insertImage({System.Text.Json.JsonSerializer.Serialize(data)})");
        }
        catch (Exception ex) { Ui.Error("Add a picture", ex.Message); }
    }
    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void OnAddAccount(object sender, RoutedEventArgs e)
    {
        Close();
        AddAccountWindow.ShowAdd(Application.Current.MainWindow);
    }

    private void OnReauth(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is EditableAccount a) AddAccountWindow.ShowReauth(this, a.Original);
    }

    private void OnImportGoogle(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Google OAuth client (*.json)|*.json", Title = "Choose the client_secret….json you downloaded" };
        if (dlg.ShowDialog(this) != true) return;
        var err = _vm.ImportGoogleJson(dlg.FileName);
        if (err != null) Ui.Error("Import", err, this);
    }

    private void FillKeys()
    {
        var keys = new (string key, string what)[]
        {
            ("Ctrl+N", "New message"), ("Ctrl+Enter", "Send (in a new message)"), ("R", "Reply"), ("A  or  Shift+R", "Reply all"), ("F", "Forward"),
            ("E", "Archive"), ("Delete", "Move to Trash"), ("S", "Snooze"), ("L", "Set aside / back to Inbox"), ("P", "Pin / unpin"), ("U", "Mark as unread"),
            ("J  /  K", "Next / previous conversation"), ("/  or  Ctrl+F", "Search"), ("Esc", "Clear search / selection / close a new message"), ("F5", "Check for mail"),
            ("Ctrl+Shift+Enter", "Send now (a message waiting to be sent)"), ("Ctrl+Z", "Undo send (the newest one)"),
            ("Ctrl+Shift+←  /  →", "Sidebar narrower / wider (narrowest = icon rail)"), ("Ctrl+Shift+B", "Show / hide the sidebar"),
            ("Click a sender's initials", "Tick a conversation for a bulk action"),
        };
        for (int i = 0; i < keys.Length; i++)
        {
            KeysGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var k = new TextBlock { Text = keys[i].key, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 4) };
            var w = new TextBlock { Text = keys[i].what, Margin = new Thickness(0, 4, 0, 4) };
            Grid.SetRow(k, i);
            Grid.SetRow(w, i);
            Grid.SetColumn(w, 1);
            KeysGrid.Children.Add(k);
            KeysGrid.Children.Add(w);
        }
    }
}
