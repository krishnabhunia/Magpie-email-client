using System.Text.Json;
using System.Web;
using Avalonia.Controls;
using Magpie.Core;
using Magpie.Core.Mail;
using Magpie.Core.Models;
using Magpie.Mac.Services;
using Magpie.Mac.ViewModels;

namespace Magpie.Mac.Views;

public partial class ComposeWindow : Window
{
    private readonly ComposeViewModel _vm;
    private readonly WebSurface _editor;
    private bool _editorReady;
    private bool _forceClose;

    // ───────────────────────── opening ─────────────────────────

    public static async void Open(ComposeMode mode, MessageRow? original)
    {
        var e = AppServices.Engine;
        if (e.Accounts.Count == 0) { AddAccountWindow.Open(null); return; }
        try
        {
            Draft d;
            if (original == null || mode == ComposeMode.New)
                d = new Draft { AccountId = (e.Accounts.FirstOrDefault(a => a.Enabled) ?? e.Accounts[0]).Id, Mode = ComposeMode.New };
            else
            {
                var account = e.AccountById(original.AccountId) ?? e.Accounts[0];
                var (body, mime) = await e.LoadAsync(original, mode == ComposeMode.Forward, CancellationToken.None);
                d = Composer.Prepare(mode, account, original, body, e.MyAddresses, mime);
            }
            Show(d);
        }
        catch (Exception ex)
        {
            Log.Error("open compose", ex);
            await Dialogs.Error("New message", Connector.Friendly(ex));
        }
    }

    public static void OpenDraft(Draft d) => Show(d);

    /// <summary>mailto:a@b.com?subject=…&amp;body=…&amp;cc=…</summary>
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
            AccountId = accountId is { Length: > 0 } && e.AccountById(accountId) != null ? accountId : e.Accounts[0].Id,
            To = string.Join(", ", new[] { to, query["to"] }.Where(s => !string.IsNullOrWhiteSpace(s))),
            Cc = query["cc"] ?? "",
            Bcc = query["bcc"] ?? "",
            Subject = query["subject"] ?? "",
        };
        var body = query["body"];
        d.Html = (string.IsNullOrEmpty(body) ? "<p><br></p>" : Composer.TextToParagraphs(body)) + Composer.SignatureHtml(e.AccountById(d.AccountId), reply: false);
        Show(d);
    }

    private static void Show(Draft d)
    {
        var w = new ComposeWindow(d);
        if (AppServices.MainWindow is { IsVisible: true } main) w.Show(main);
        else w.Show();
        w.Activate();
    }

    // ───────────────────────── window ─────────────────────────

    public ComposeWindow() : this(new Draft()) { }

    public ComposeWindow(Draft draft)
    {
        InitializeComponent();
        _vm = new ComposeViewModel(draft);
        DataContext = _vm;
        NativeMenu.SetMenu(this, MacMenus.ForWindow(this));
        _vm.CloseRequested += () => { _forceClose = true; Close(); };

        foreach (var box in new[] { ToBox, CcBox, BccBox })
        {
            box.AsyncPopulator = ComposeViewModel.SuggestAsync;
            box.TextSelector = (text, picked) => ComposeViewModel.ReplaceLastToken(text, picked);
        }

        var later = new MenuFlyout();
        foreach (var p in _vm.SendLaterChoices)
        {
            var item = new MenuItem { Header = $"{p.Label} · {TimePresets.Describe(p.When, DateTime.Now)}" };
            var preset = p;
            item.Click += (_, _) => _vm.SendLaterCommand.Execute(preset);
            later.Items.Add(item);
        }
        SendLaterButton.Flyout = later;
        AttachButton.Click += async (_, _) => await _vm.AddAttachmentsAsync(await Dialogs.PickFiles(this, "Attach files", many: true));

        _editor = new WebSurface();
        EditorHost.Child = _editor;
        _editor.Message += OnEditorMessage;
        _editor.Load(EditorPage.ForMac, "editor");
        _vm.GetHtml = GetHtmlAsync;
        App.ThemeChanged += OnThemeChanged;
    }

    public bool HasUnsavedContent => _vm.Dirty;

    private async void OnEditorMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var t = doc.RootElement.TryGetProperty("t", out var v) ? v.GetString() : null;
            switch (t)
            {
                case "ready":
                    await _editor.RunAsync($"setDark({(App.IsDark ? "true" : "false")})");
                    await _editor.RunAsync($"setHtml({JsonSerializer.Serialize(_vm.InitialHtml)})");
                    _editorReady = true;
                    break;
                case "dirty": _vm.Dirty = true; break;
                case "send": await _vm.SendAsync(null); break;
            }
        }
        catch (Exception ex) { Log.Warn("editor message: " + ex.Message); }
    }

    private void OnThemeChanged() => _ = _editor.RunAsync($"setDark({(App.IsDark ? "true" : "false")})");

    /// <summary>The editor's HTML (the original text when the editor isn't there, e.g. in a test without macOS).</summary>
    private async Task<string> GetHtmlAsync()
    {
        if (!_editor.IsNative || !_editorReady) return _vm.InitialHtml;
        return WebSurface.JsonText(await _editor.RunAsync("getHtml()"));
    }

    /// <summary>Quitting Magpie: keep or discard an unsent message first. False = Cancel.</summary>
    public async Task<bool> CloseForQuitAsync()
    {
        Activate();
        if (!await _vm.ConfirmCloseAsync()) return false;
        _forceClose = true;
        Close();
        return true;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_forceClose && _vm.Dirty)
        {
            e.Cancel = true;
            Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
            {
                if (!await _vm.ConfirmCloseAsync()) return;
                _forceClose = true;
                Close();
            });
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        App.ThemeChanged -= OnThemeChanged;
        _editor.DisposeView();
        base.OnClosed(e);
    }
}
