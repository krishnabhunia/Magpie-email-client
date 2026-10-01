using System.Text.Json;
using System.Web;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Magpie.App.Services;
using Magpie.App.ViewModels;
using Magpie.Core;
using Magpie.Core.Mail;
using Magpie.Core.Models;
using Microsoft.Web.WebView2.Core;

namespace Magpie.App.Views;

public partial class ComposeWindow : Window
{
    private readonly ComposeViewModel _vm;
    private bool _editorReady;
    private bool _plain;
    private bool _forceClose;
    private TextBox? _suggestTarget;

    // ───────────────────────── opening ─────────────────────────

    public static async void Open(ComposeMode mode, MessageRow? original, string? prefill = null)
    {
        var e = AppServices.Engine;
        if (e.Accounts.Count == 0) { AddAccountWindow.ShowAdd(Application.Current.MainWindow); return; }
        try
        {
            Draft d;
            if (original == null || mode == ComposeMode.New)
                d = new Draft { AccountId = e.Accounts.First(a => a.Enabled).Id, Mode = ComposeMode.New };
            else
            {
                var account = e.AccountById(original.AccountId) ?? e.Accounts[0];
                var (body, mime) = await e.LoadAsync(original, mode == ComposeMode.Forward, CancellationToken.None);
                d = Composer.Prepare(mode, account, original, body, e.MyAddresses, mime);
            }
            Show(d, prefill);
        }
        catch (Exception ex)
        {
            Log.Error("open compose", ex);
            Ui.Error("New message", Connector.Friendly(ex));
        }
    }

    public static void OpenDraft(Draft d) => Show(d, null);

    /// <summary>Design HM1 (A6): a new email carrying only this one file of <paramref name="original"/>.</summary>
    public static async void OpenForwardOnly(MessageRow original, int attachmentIndex)
    {
        var e = AppServices.Engine;
        try
        {
            var account = e.AccountById(original.AccountId) ?? e.Accounts[0];
            var (body, mime) = await e.LoadAsync(original, true, CancellationToken.None);
            if (mime == null || MimeText.PartAt(mime, MimeText.ResolveIndex(mime, attachmentIndex)) is not { } part) return;
            var d = Composer.Prepare(ComposeMode.Forward, account, original, body, e.MyAddresses, mime);
            d.CarriedParts = new List<MimeKit.MimeEntity> { part };
            d.Html = "<p><br></p>" + Composer.SignatureHtml(account, reply: false);
            Show(d, null);
        }
        catch (Exception ex)
        {
            Log.Error("forward file", ex);
            Ui.Error("Forward", Connector.Friendly(ex));
        }
    }

    /// <summary>Brings forward the window already editing this local draft. False when none is open.</summary>
    public static bool ActivateLocal(long localDraftId)
    {
        foreach (var w in Application.Current.Windows.OfType<ComposeWindow>())
        {
            if (w._vm.LocalDraftId != localDraftId) continue;
            if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
            w.Activate();
            return true;
        }
        return false;
    }

    /// <summary>mailto:a@b.com?subject=..&amp;body=..&amp;cc=..</summary>
    public static void OpenMailto(string mailto, string? accountId = null)
    {
        var e = AppServices.Engine;
        if (e.Accounts.Count == 0) return;
        var rest = mailto.Substring("mailto:".Length);
        var q = rest.IndexOf('?');
        var to = Uri.UnescapeDataString(q >= 0 ? rest[..q] : rest);
        var query = HttpUtility.ParseQueryString(q >= 0 ? rest[(q + 1)..] : "");
        var d = new Draft
        {
            AccountId = accountId ?? e.Accounts[0].Id,
            To = string.Join(", ", new[] { to, query["to"] }.Where(s => !string.IsNullOrWhiteSpace(s))),
            Cc = query["cc"] ?? "",
            Bcc = query["bcc"] ?? "",
            Subject = query["subject"] ?? "",
        };
        var body = query["body"];
        d.Html = (string.IsNullOrEmpty(body) ? "<p><br></p>" : ComposeViewModel.TextToParagraphs(body))
                 + Composer.SignatureHtml(e.AccountById(d.AccountId), reply: false);
        Show(d, null);
    }

    private static void Show(Draft d, string? prefill)
    {
        var w = new ComposeWindow(d, prefill);
        w.Show();
        w.Activate();
    }

    // ───────────────────────── window ─────────────────────────

    public ComposeWindow(Draft draft, string? prefill)
    {
        InitializeComponent();
        _vm = new ComposeViewModel(draft, prefill);
        DataContext = _vm;
        _vm.GetHtml = GetHtmlAsync;
        _vm.EditorCommand = EditorCommandAsync;
        _vm.CloseRequested += () => { _forceClose = true; Close(); };
        StateChanged += (_, _) => MaxRestoreButton.Content = WindowState == WindowState.Maximized ? "" : "";
        Loaded += async (_, _) => await InitEditorAsync();
        Closing += OnClosing;
        Closed += (_, _) => { _vm.Detach(); ThemeManager.Changed -= OnThemeChanged; };
        Editor.DefaultBackgroundColor = ThemeManager.WebBackground;
        ThemeManager.Changed += OnThemeChanged;
        _vm.PropertyChanged += (_, a) =>
        {
            if (!_editorReady && a.PropertyName is nameof(ComposeViewModel.To) or nameof(ComposeViewModel.Cc)
                    or nameof(ComposeViewModel.Bcc) or nameof(ComposeViewModel.Subject))
                _typedBeforeReady = true;
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0) { e.Handled = true; _ = _vm.SendAsync(null, null); }
            else if (e.Key == Key.Escape && !SuggestPopup.IsOpen) { e.Handled = true; Close(); }
        };
        if (Application.Current.MainWindow is { IsVisible: true } main)
        {
            Left = main.Left + 80;
            Top = main.Top + 60;
            WindowStartupLocation = WindowStartupLocation.Manual;
        }
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
    }

    private async Task InitEditorAsync()
    {
        try
        {
            if (!await WebHost.InitAsync(Editor, scripts: true)) { UsePlainEditor(); return; }
            Editor.AllowExternalDrop = false;
            var core = Editor.CoreWebView2;
            core.NavigationStarting += (_, e) => { if (!WebHost.IsOwnPage(e.Uri)) e.Cancel = true; };
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.WebMessageReceived += OnEditorMessage;
            core.Navigate(WebHost.Publish(EditorPage.Html, "editor"));
        }
        catch (Exception ex)
        {
            Log.Error("editor init", ex);
            UsePlainEditor();
        }
    }

    /// <summary>Dark theme (design B1): the editor page follows; what is sent carries no theme colours.</summary>
    private void OnThemeChanged()
    {
        Editor.DefaultBackgroundColor = ThemeManager.WebBackground;
        if (_plain || !_editorReady || Editor.CoreWebView2 == null) return;
        _ = Editor.CoreWebView2.ExecuteScriptAsync($"setDark({(ThemeManager.IsDark ? "true" : "false")})");
    }

    private void UsePlainEditor()
    {
        _plain = true;
        Editor.Visibility = Visibility.Collapsed;
        PlainEditor.Visibility = Visibility.Visible;
        PlainEditor.Text = MimeText.HtmlToText(_vm.InitialHtml);
        PlainEditor.TextChanged += (_, _) => _vm.MarkEdited();
        _editorReady = true;
    }

    private async void OnEditorMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.TryGetWebMessageAsString());
            var root = doc.RootElement;
            switch (root.GetProperty("t").GetString())
            {
                case "ready":
                    await Editor.CoreWebView2.ExecuteScriptAsync($"setDark({(ThemeManager.IsDark ? "true" : "false")})");
                    await Editor.CoreWebView2.ExecuteScriptAsync($"setHtml({JsonSerializer.Serialize(_vm.InitialHtml)})");
                    _editorReady = true;
                    // Loading the initial HTML isn't an edit, but anything typed in To/Subject meanwhile is.
                    if (_vm.StartsUnsaved || _typedBeforeReady) _vm.MarkEdited(); else _vm.Dirty = false;
                    if (!string.IsNullOrWhiteSpace(_vm.To)) Editor.Focus(); else ToBox.Focus();
                    break;
                case "dirty": _vm.MarkEdited(); break;
                case "sel": _vm.SelectedText = (root.GetProperty("text").GetString() ?? "").Trim(); break;
                case "send": await _vm.SendAsync(null, null); break;
                case "link": OnLink(this, new RoutedEventArgs()); break;
            }
        }
        catch (Exception ex) { Log.Warn("editor message: " + ex.Message); }
    }

    private async Task<string> GetHtmlAsync()
    {
        if (_plain || !_editorReady) return ComposeViewModel.TextToParagraphs(PlainEditor.Text is { Length: > 0 } t ? t : MimeText.HtmlToText(_vm.InitialHtml));
        var json = await Editor.CoreWebView2.ExecuteScriptAsync("getHtml()");
        return JsonSerializer.Deserialize<string>(json) ?? "";
    }

    private (int Start, int Length, string Text)? _plainRewrite;

    private async Task EditorCommandAsync(string mode, string text)
    {
        if (_plain)
        {
            if (mode == "captureRewrite")
            {
                if (PlainEditor.SelectionLength == 0 || PlainEditor.SelectedText != text)
                    throw new InvalidOperationException("Select the text again before rewriting it.");
                _plainRewrite = (PlainEditor.SelectionStart, PlainEditor.SelectionLength, PlainEditor.Text);
                return;
            }
            if (mode == "replaceSelection")
            {
                if (_plainRewrite is not { } saved || saved.Text != PlainEditor.Text)
                    throw new InvalidOperationException("Your message changed. Select the text and rewrite it again.");
                PlainEditor.Select(saved.Start, saved.Length);
                PlainEditor.SelectedText = text;
                _plainRewrite = null;
            }
            else if (mode == "replaceBody") PlainEditor.Text = text + "\n\n" + PlainEditor.Text;
            else PlainEditor.SelectedText = text;
            return;
        }
        var result = await Editor.CoreWebView2.ExecuteScriptAsync($"cmd({JsonSerializer.Serialize(mode)}, {JsonSerializer.Serialize(text)})");
        if (result == "false")
            throw new InvalidOperationException("Your message or selection changed. Select the text and rewrite it again.");
    }

    private async void OnFormat(object sender, RoutedEventArgs e)
    {
        if (_plain || !_editorReady || (sender as FrameworkElement)?.Tag is not string command) return;
        await Editor.CoreWebView2.ExecuteScriptAsync($"fmt({JsonSerializer.Serialize(command)})");
    }

    private async void OnLink(object sender, RoutedEventArgs e)
    {
        if (_plain || !_editorReady) return;
        var url = TextPromptDialog.Ask(this, "Insert link", "Web address", "https://");
        if (string.IsNullOrWhiteSpace(url) || url == "https://") return;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https" or "mailto")) { Ui.Error("Insert link", "That doesn't look like a web address.", this); return; }
        await Editor.CoreWebView2.ExecuteScriptAsync($"fmt('createLink', {JsonSerializer.Serialize(u.ToString())})");
    }

    // ───────────────────────── send / schedule / draft ─────────────────────────

    private async void OnSend(object sender, RoutedEventArgs e) => await _vm.SendAsync(null, null);

    private void OnSendLater(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Top };
        foreach (var p in TimePresets.For(DateTime.Now, sendLater: true))
        {
            var mi = new MenuItem { Header = $"{p.Label}  ·  {TimePresets.Describe(p.When, DateTime.Now)}" };
            mi.Click += async (_, _) => await _vm.SendAsync(p.When, null);
            menu.Items.Add(mi);
        }
        var pick = new MenuItem { Header = "Pick date & time…" };
        pick.Click += async (_, _) =>
        {
            var when = PickTimeDialog.Ask(this, "Send later", "Magpie sends it at this time. Your PC needs to be on with Magpie running (it can be in the tray).", DateTime.Now.AddDays(1).Date.AddHours(8));
            if (when != null) await _vm.SendAsync(when, null);
        };
        menu.Items.Add(pick);
        menu.Items.Add(new Separator());
        var remind = new MenuItem { Header = "Send now, remind me if nobody replies…" };
        remind.Click += async (_, _) =>
        {
            var when = PickTimeDialog.Ask(this, "Remind me if nobody replies", "If nobody has replied by then, the conversation comes back to the top of your inbox.", DateTime.Now.AddDays(3).Date.AddHours(9));
            if (when != null) await _vm.SendAsync(null, when);
        };
        menu.Items.Add(remind);
        menu.IsOpen = true;
    }

    private void OnAttach(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "Attach files" };
        if (dlg.ShowDialog(this) == true) _vm.AddAttachments(dlg.FileNames);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files) _vm.AddAttachments(files);
    }

    private void OnTemplates(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Top };
        foreach (var t in AppServices.Engine.Config.Templates)
        {
            var mi = new MenuItem { Header = t.Name };
            mi.Click += (_, _) => _vm.InsertTemplate(t);
            menu.Items.Add(mi);
        }
        if (menu.Items.Count > 0) menu.Items.Add(new Separator());
        var manage = new MenuItem { Header = "Manage templates…" };
        manage.Click += (_, _) => SettingsWindow.Open("Templates");
        menu.Items.Add(manage);
        menu.IsOpen = true;
    }

    private async void OnSaveDraft(object sender, RoutedEventArgs e) => await _vm.SaveDraftAsync();

    private bool _asking;
    private bool _typedBeforeReady;

    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_forceClose || !_vm.Dirty) return;
        e.Cancel = true;
        if (await AskToKeepAsync("Keep this message as a draft?\n\nYes — keep it (it goes to your Drafts folder; if you are offline it waits on this PC and uploads later).\nNo — discard it."))
        {
            _forceClose = true;
            // Close() may not be called from inside this window's own Closing event (WPF throws), so post it.
            _ = Dispatcher.BeginInvoke(new Action(Close));
        }
    }

    /// <summary>Yes = save as draft, No = discard, Cancel = keep editing. Returns true when the window may close.</summary>
    private async Task<bool> AskToKeepAsync(string question)
    {
        if (_asking) return false;
        _asking = true;
        try
        {
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            var answer = MessageBox.Show(this, question, "Magpie", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel) return false;
            if (answer == MessageBoxResult.No) return await _vm.DiscardAsync();
            // Keep: server Drafts, or this PC when offline — only "no account" can stop it.
            return await _vm.KeepAsDraftAsync(closing: true);
        }
        finally { _asking = false; }
    }

    /// <summary>Has content that would be lost if the window closed now.</summary>
    public bool HasUnsavedContent => !_forceClose && _vm.Dirty;

    /// <summary>Called when Magpie quits: asks about an unsent message first. False = the user cancelled quitting.</summary>
    public async Task<bool> CloseForExitAsync()
    {
        if (!_forceClose && _vm.Dirty && !await AskToKeepAsync("Magpie is closing. Keep this message as a draft?\n\nYes — keep it (Drafts, or on this PC if offline).\nNo — discard it."))
            return false;
        _forceClose = true;
        Close();
        return true;
    }

    // ───────────────────────── contact suggestions ─────────────────────────

    private static string LastToken(string text)
    {
        var i = text.LastIndexOfAny(new[] { ',', ';' });
        return (i >= 0 ? text[(i + 1)..] : text).Trim();
    }

    private void OnAddressChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox box || !box.IsKeyboardFocusWithin) return;
        var token = LastToken(box.Text);
        if (token.Length < 2 || token.Contains('<') || token.Contains('@') && token.EndsWith('>')) { SuggestPopup.IsOpen = false; return; }
        var hits = AppServices.Engine.Store.SearchContacts(token);
        if (hits.Count == 0) { SuggestPopup.IsOpen = false; return; }
        SuggestList.ItemsSource = hits.Select(c => new { Name = string.IsNullOrEmpty(c.Name) ? c.Address : c.Name, c.Address, Contact = c }).ToList();
        SuggestList.SelectedIndex = 0;
        _suggestTarget = box;
        SuggestPopup.PlacementTarget = box;
        SuggestPopup.Width = Math.Max(320, box.ActualWidth);
        SuggestPopup.IsOpen = true;
    }

    private void OnAddressKey(object sender, KeyEventArgs e)
    {
        if (!SuggestPopup.IsOpen) return;
        switch (e.Key)
        {
            case Key.Down:
                SuggestList.SelectedIndex = Math.Min(SuggestList.Items.Count - 1, SuggestList.SelectedIndex + 1);
                e.Handled = true;
                break;
            case Key.Up:
                SuggestList.SelectedIndex = Math.Max(0, SuggestList.SelectedIndex - 1);
                e.Handled = true;
                break;
            case Key.Enter:
            case Key.Tab:
                AcceptSuggestion();
                e.Handled = true;
                break;
            case Key.Escape:
                SuggestPopup.IsOpen = false;
                e.Handled = true;
                break;
        }
    }

    private void OnSuggestKey(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Tab) { AcceptSuggestion(); e.Handled = true; }
    }

    private void OnSuggestionClick(object sender, MouseButtonEventArgs e) => AcceptSuggestion();

    private void OnAddressLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!SuggestList.IsKeyboardFocusWithin && !SuggestPopup.IsMouseOver) SuggestPopup.IsOpen = false;
    }

    private void AcceptSuggestion()
    {
        if (_suggestTarget == null || SuggestList.SelectedItem == null) { SuggestPopup.IsOpen = false; return; }
        var contact = (Contact)SuggestList.SelectedItem.GetType().GetProperty("Contact")!.GetValue(SuggestList.SelectedItem)!;
        var text = _suggestTarget.Text;
        var i = text.LastIndexOfAny(new[] { ',', ';' });
        var head = i >= 0 ? text[..(i + 1)] + " " : "";
        var entry = string.IsNullOrEmpty(contact.Name) ? contact.Address : $"{(contact.Name.Contains(',') ? "\"" + contact.Name + "\"" : contact.Name)} <{contact.Address}>";
        _suggestTarget.Text = head + entry + ", ";
        _suggestTarget.CaretIndex = _suggestTarget.Text.Length;
        SuggestPopup.IsOpen = false;
        _suggestTarget.Focus();
    }

    // ───────────────────────── chrome ─────────────────────────

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnMaximizeRestore(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
