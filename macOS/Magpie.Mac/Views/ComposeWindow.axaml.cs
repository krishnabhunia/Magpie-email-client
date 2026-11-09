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
    private readonly Func<string, Task<string?>> _runEditor;
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
        // Its own window (not owned by the main one), as in Mail: it stays when the main window is hidden.
        var w = new ComposeWindow(d);
        w.Show();
        w.Activate();
    }

    // ───────────────────────── window ─────────────────────────

    public ComposeWindow() : this(new Draft()) { }

    public ComposeWindow(Draft draft) : this(draft, null) { }

    // A script transport lets the headless fixtures exercise the same host/editor protocol without WKWebView.
    internal ComposeWindow(Draft draft, Func<string, Task<string?>>? runEditor)
    {
        InitializeComponent();
        _vm = new ComposeViewModel(draft);
        DataContext = _vm;
        MacMenus.Attach(this);
        _vm.CloseRequested += () => { _forceClose = true; Close(); };

        foreach (var box in new[] { ToBox, CcBox, BccBox })
        {
            box.AsyncPopulator = ComposeViewModel.SuggestAsync;
            box.TextSelector = (text, picked) => ComposeViewModel.ReplaceLastToken(text, picked);
        }

        var later = new MenuFlyout();
        later.Opening += (_, _) => FillSendLater(later);
        FillSendLater(later);
        SendLaterButton.Flyout = later;
        AttachButton.Click += async (_, _) => await _vm.AddAttachmentsAsync(await Dialogs.PickFiles(this, "Attach files", many: true));

        _editor = new WebSurface();
        _runEditor = runEditor ?? _editor.RunAsync;
        EditorHost.Child = _editor;
        _editor.Message += OnEditorMessage;
        _editor.Load(MacMenus.WithKeyForwarding(EditorPage.ForMac), "editor");
        _vm.GetHtml = GetHtmlAsync;
        App.ThemeChanged += OnThemeChanged;
    }

    public bool HasUnsavedContent => _vm.Dirty;

    /// <summary>Send later: the choices from the clock now, every time the menu opens.</summary>
    private void FillSendLater(MenuFlyout menu)
    {
        menu.Items.Clear();
        var now = DateTime.Now;
        foreach (var p in ComposeViewModel.SendLaterChoices(now))
        {
            var item = new MenuItem { Header = $"{p.Label} · {TimePresets.Describe(p.When, now)}" };
            var preset = p;
            item.Click += (_, _) => _vm.SendLaterCommand.Execute(preset);
            menu.Items.Add(item);
        }
    }

    /// <summary>Brings forward the window already editing this draft kept on this Mac. False when none is open.</summary>
    public static bool ActivateLocal(long localDraftId)
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is not Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime life) return false;
        var w = life.Windows.OfType<ComposeWindow>().FirstOrDefault(c => c._vm.LocalDraftId == localDraftId);
        if (w == null) return false;
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Activate();
        return true;
    }

    /// <summary>⌘K in the editor: ask for the address (web or mail only); empty removes the link.</summary>
    private async Task AskLinkAsync()
    {
        if (!_editorReady) return;
        var text = await Dialogs.Prompt("Insert link", _vm.IsPlainText
            ? "Web or email address (inserted as literal text in this plain-text message)"
            : "Web or email address (leave empty to remove the link)", "https://", this);
        if (text == null) return;
        if (text.Trim().Length == 0 || text.Trim() == "https://")
        {
            if (!_vm.IsPlainText) await _runEditor("fmt('unlink')");
            return;
        }
        if (ComposeViewModel.AllowedLink(text) == null)
        {
            await Dialogs.Error("Insert link", "That doesn't look like a web or email address (https://… or mailto:…).", this);
            return;
        }
        await InsertLinkAsync(text);
    }

    internal async Task<bool> InsertLinkAsync(string text)
    {
        if (!_editorReady || ComposeViewModel.AllowedLink(text) is not { } url) return false;
        // text/plain cannot retain a hidden href. Insert the complete destination into the visible message.
        var command = _vm.IsPlainText ? "insertText" : "createLink";
        if (await _runEditor($"fmt('{command}', {JsonSerializer.Serialize(url)})") != "true") return false;
        _vm.Dirty = true;
        return true;
    }

    internal async Task InitializeEditorAsync()
    {
        _editorReady = false;
        await _runEditor($"setDark({(App.IsDark ? "true" : "false")})");
        // setHtml resets the shared editor to rich mode. Apply the persisted mode in the same script turn,
        // before accepting editor actions, so a reopened text-only draft never allows hidden link targets.
        var initialized = await _runEditor($"setHtml({JsonSerializer.Serialize(_vm.InitialHtml)}, {(_vm.BodySectionsLost ? "true" : "false")});setEditorPlainText({(_vm.IsPlainText ? "true" : "false")},false)");
        if (initialized != "true") throw new InvalidOperationException("The message editor could not restore its text format.");
        _editorReady = true;
    }

    private async void OnEditorMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var t = doc.RootElement.TryGetProperty("t", out var v) ? v.GetString() : null;
            switch (t)
            {
                case "menukey": MacMenus.TryRunKey(this, json); break;
                case "ready":
                    await InitializeEditorAsync();
                    break;
                case "dirty": _vm.Dirty = true; break;
                case "send": await _vm.SendAsync(null); break;
                case "link": await AskLinkAsync(); break;
            }
        }
        catch (Exception ex) { Log.Warn("editor message: " + ex.Message); }
    }

    private void OnThemeChanged() => _ = _runEditor($"setDark({(App.IsDark ? "true" : "false")})");

    /// <summary>The editor's HTML (the original text when the editor isn't there, e.g. in a test without macOS).</summary>
    private async Task<string> GetHtmlAsync()
    {
        if (!_editorReady) return _vm.InitialHtml;
        return WebSurface.JsonText(await _runEditor("getHtml()"));
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
        _vm.Detach();
        _editor.DisposeView();
        base.OnClosed(e);
    }
}
