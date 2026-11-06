using System.Text.Json;
using System.Text.RegularExpressions;
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
using Magpie.Core.Settings;
using Microsoft.Web.WebView2.Core;

namespace Magpie.App.Views;

public partial class ComposeWindow : Window
{
    private readonly ComposeViewModel _vm;
    private bool _editorReady;
    private bool _plain;
    private bool _forceClose;
    private TextBox? _suggestTarget;
    private readonly bool _foldQuote;
    /// <summary>WebView2 couldn't start: the plain editor is the only editor (design CW1: the plain-text switch stays off).</summary>
    private bool _noWebView;
    private readonly bool _preserveDraftFormatting;
    private readonly Action _settingsChanged;
    private string _plainProtectedTail = "";

    // ───────────────────────── opening ─────────────────────────

    public static async void Open(ComposeMode mode, MessageRow? original, string? prefill = null)
    {
        if (AppServices.Updates?.IsProfileFrozen == true) return;
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

    /// <summary>Open a draft assistant without generating or sending anything.</summary>
    public static void OpenAiDraft()
    {
        if (AppServices.Updates?.IsProfileFrozen == true) return;
        var account = AppServices.Engine.Accounts.FirstOrDefault(a => a.Enabled);
        if (account == null) return;
        var window = new ComposeWindow(new Draft { AccountId = account.Id, Mode = ComposeMode.New }, null);
        if (!window._vm.ShowDraftAi) { window._vm.Detach(); return; }
        window._vm.AiPanelOpen = true;
        window.Show();
        window.Activate();
    }

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
        if (AppServices.Updates?.IsProfileFrozen == true) return;
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
        _vm.GetNewText = GetNewTextAsync;
        _vm.ChooseReplyInsertion = () => Task.FromResult(ReplyInsertionDialog.Ask(this));
        _vm.FocusAnswerBox = () => Dispatcher.BeginInvoke(new Action(ReplyAi.FocusAnswer), System.Windows.Threading.DispatcherPriority.Input);
        _vm.EditorCommand = EditorCommandAsync;
        _vm.AskForgotAttachment = AskForgotAttachmentAsync;
        _vm.AskOverLimit = AskOverLimitAsync;
        _preserveDraftFormatting = draft.Mode == ComposeMode.EditDraft;
        // Design CW1 (E2): replies start with the quoted message folded; forwards and re-opened drafts don't.
        _foldQuote = draft.Mode is ComposeMode.Reply or ComposeMode.ReplyAll && AppServices.Engine.Config.Compose.FoldQuote;
        WireRecipientBoxes();
        _settingsChanged = () => Ui.Post(() => _ = ApplyEditorTypographyAsync());
        AppServices.Engine.Settings.Changed += _settingsChanged;
        _vm.CloseRequested += () => { _forceClose = true; Close(); };
        StateChanged += (_, _) => MaxRestoreButton.Content = WindowState == WindowState.Maximized ? "" : "";
        Loaded += async (_, _) => await InitEditorAsync();
        Closing += OnClosing;
        Closed += (_, _) => { _vm.Detach(); ThemeManager.Changed -= OnThemeChanged; AppServices.Engine.Settings.Changed -= _settingsChanged; };
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
            var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            if (e.Key == Key.A && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && _vm.ShowAiRail)
            { e.Handled = true; _vm.ToggleAiPanelCommand.Execute(null); }
            // Design CW1 (E4): the same keys as inside the editor page, for when the focus is in a header field.
            else if (ctrl && !shift && e.Key == Key.S) { e.Handled = true; OnSaveDraft(this, new RoutedEventArgs()); }
            else if (ctrl && !shift && e.Key == Key.F && !ReplyAi.HasKeyboardFocus) { e.Handled = true; OpenFind(false); }
            else if (ctrl && !shift && e.Key == Key.H && !ReplyAi.HasKeyboardFocus) { e.Handled = true; OpenFind(true); }
            else if (ctrl && e.Key == Key.OemQuestion) { e.Handled = true; ComposePopups.Shortcuts(this); }
            else if (ctrl && shift && e.Key == Key.O) { e.Handled = true; OnAttach(this, new RoutedEventArgs()); }
            else if (ctrl && shift && e.Key == Key.C && !ToBox.HasFocusWithin && !CcBox.HasFocusWithin && !BccBox.HasFocusWithin) { e.Handled = true; OnEditorKey("cc"); }
            else if (ctrl && shift && e.Key == Key.B) { e.Handled = true; OnEditorKey("bcc"); }
            else if (e.Key == Key.Enter && ctrl && (Keyboard.Modifiers & ModifierKeys.Alt) != 0) { e.Handled = true; _ = _vm.SendAndArchiveAsync(); }
            else if (e.Key == Key.Escape && FindBar.Visibility == Visibility.Visible && FindBar.IsKeyboardFocusWithin) { e.Handled = true; OnFindClose(this, new RoutedEventArgs()); }
            else if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0 && ReplyAi.HasKeyboardFocus)
            {
                // AW1: Ctrl+Enter in the AI draft panel rewrites the draft; it never sends the email from there.
                e.Handled = true;
                if (_vm.GenerateReplyDraftCommand.CanExecute(null)) _ = _vm.GenerateReplyDraftCommand.ExecuteAsync(null);
            }
            else if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0) { e.Handled = true; _ = _vm.SendAsync(null, null); }
            else if (e.Key == Key.Escape && !SuggestPopup.IsOpen && !MentionPopup.IsOpen) { e.Handled = true; Close(); }
        };
        if (Application.Current.MainWindow is { IsVisible: true } main)
        {
            Left = main.Left + 80;
            Top = main.Top + 60;
            WindowStartupLocation = WindowStartupLocation.Manual;
        }
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        RestorePlacement();   // design CW1 (E5): the last size and place win over the offset above
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
        _noWebView = true;
        Editor.Visibility = Visibility.Collapsed;
        PlainEditor.Visibility = Visibility.Visible;
        PlainEditor.Text = MimeText.HtmlToText(_vm.InitialHtml);
        _plainProtectedTail = ProtectedTail(_vm.InitialHtml);
        PlainEditor.TextChanged += OnPlainChanged;
        PlainEditor.SelectionChanged += (_, _) => _vm.SelectedText = PlainEditor.SelectedText;
        _editorReady = true;
        _ = ApplyEditorTypographyAsync();
        _ = _vm.ReplyEditorReadyAsync();
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
                    await ApplyEditorTypographyAsync();
                    // Design CW1: spelling, folded quote, the paste question, the toolbar
                    await Editor.CoreWebView2.ExecuteScriptAsync($"setSpell({(AppServices.Engine.Config.Compose.CheckSpelling ? "true" : "false")})");
                    await Editor.CoreWebView2.ExecuteScriptAsync($"setAskPaste({(AppServices.Engine.Config.Compose.PastePicture != PastePictureChoice.InMessage ? "true" : "false")})");
                    if (_foldQuote) await Editor.CoreWebView2.ExecuteScriptAsync("foldQuote()");
                    BuildToolbar();
                    if (_vm.PlainText) { _vm.PlainText = false; await SwitchPlainTextAsync(true, ask: false); }
                    // Loading the initial HTML isn't an edit, but anything typed in To/Subject meanwhile is.
                    if (_vm.StartsUnsaved || _typedBeforeReady) _vm.MarkEdited(); else _vm.Dirty = false;
                    if (!string.IsNullOrWhiteSpace(_vm.To)) Editor.Focus(); else ToBox.Input.Focus();
                    await _vm.ReplyEditorReadyAsync();
                    break;
                case "dirty": _vm.MarkEdited(); break;
                case "sel": _vm.SelectedText = root.GetProperty("text").GetString() ?? ""; break;
                case "send": await _vm.SendAsync(null, null); break;
                case "link": OnLink(this, new RoutedEventArgs()); break;
                // Design CW1
                case "state": ApplyEditorState(root); break;
                case "linkat": ShowLinkBubble(root); break;
                case "img": ShowPictureBubble(root); break;
                case "table": ShowTableMenu(root); break;
                case "mention": ShowMentions(root); break;
                case "stats": _vm.UpdateStats(root.GetProperty("text").GetString() ?? ""); _ = RefreshSizeAsync(); break;   // pasted / deleted pictures count
                case "pastepic": OnPastedPicture(root.GetProperty("data").GetString() ?? ""); break;
                case "pastefiles": OnPastedFiles(); break;
                case "key": OnEditorKey(root.GetProperty("k").GetString() ?? ""); break;
            }
        }
        catch (Exception ex) { Log.Warn("editor message: " + ex.Message); }
    }

    private async Task<string> GetHtmlAsync()
    {
        // The plain editor holds the message once it is in use — an emptied body stays empty. Before any editor is ready
        // (a save racing the page load) the message is still what it was opened with.
        if (_plain) return ComposeViewModel.TextToParagraphs(PlainEditor.Text);
        if (!_editorReady) return _vm.InitialHtml;
        var json = await Editor.CoreWebView2.ExecuteScriptAsync("getHtml()");
        return JsonSerializer.Deserialize<string>(json) ?? "";
    }

    private async Task ApplyEditorTypographyAsync()
    {
        if (!_editorReady) return;
        try
        {
            var style = AppServices.Engine.Config.Typography.Resolve("composeBody");
            if (_plain)
            {
                PlainEditor.FontFamily = Services.Typography.ResolveFont(style.Family);
                PlainEditor.FontSize = style.Size;
                PlainEditor.FontWeight = FontWeights.Normal;
                return;
            }
            if (Editor.CoreWebView2 != null)
                await Editor.CoreWebView2.ExecuteScriptAsync(EditorPage.TypographyScript(AppServices.Engine.Config.Typography, _preserveDraftFormatting));
        }
        catch (Exception ex) { Log.Warn("editor typography: " + ex.Message); }
    }

    /// <summary>The signature + quoted part of <paramref name="html"/> as plain text (the plain editor never lets AI drafts or templates touch it).</summary>
    private static string ProtectedTail(string html)
    {
        var protectedStart = Regex.Match(html, "<(?:div|blockquote)\\b[^>]*class=[\"'][^\"']*\\bmagpie-(?:signature|quote)\\b", RegexOptions.IgnoreCase);
        return protectedStart.Success ? MimeText.HtmlToText(html[protectedStart.Index..]).TrimStart() : "";
    }

    private string PlainNewText()
    {
        var text = PlainEditor.Text;
        return _plainProtectedTail.Length > 0 && text.EndsWith(_plainProtectedTail, StringComparison.Ordinal)
            ? text[..^_plainProtectedTail.Length].TrimEnd() : text;
    }

    private async Task<string> GetNewTextAsync()
    {
        if (_plain) return PlainNewText();
        if (!_editorReady || Editor.CoreWebView2 == null) return "";
        var value = await Editor.CoreWebView2.ExecuteScriptAsync("getNewText()");
        return JsonSerializer.Deserialize<string>(value) ?? "";
    }

    private (int Start, int Length, string Text)? _plainRewrite;

    private async Task EditorCommandAsync(string mode, string text)
    {
        if (_plain)
        {
            if (mode == "signature") throw new InvalidOperationException("Switch back to rich text to change the signature.");
            if (mode == "replyDraft")
            {
                using var payload = JsonDocument.Parse(text);
                var root = payload.RootElement;
                var expected = root.GetProperty("expectedNewText").GetString() ?? "";
                if (PlainNewText() != expected) throw new InvalidOperationException("Your reply changed. Review the draft and choose Use draft again.");
                var insertion = root.GetProperty("text").GetString() ?? "";
                var how = root.GetProperty("mode").GetString();
                if (how is not ("replaceBody" or "appendBody")) throw new InvalidOperationException("Choose how to use the draft again.");
                if (_plainProtectedTail.Length > 0 && !PlainEditor.Text.EndsWith(_plainProtectedTail, StringComparison.Ordinal))
                    throw new InvalidOperationException("The signature or quoted message changed. Keep writing manually to preserve it.");
                var body = how == "appendBody" && expected.Trim().Length > 0 ? expected.TrimEnd() + "\n\n" + insertion : insertion;
                PlainEditor.Text = body + (_plainProtectedTail.Length > 0 ? "\n\n" + _plainProtectedTail : "");
                return;
            }
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
            else if (mode is "replaceBody" or "appendBody")
            {
                if (_plainProtectedTail.Length > 0 && !PlainEditor.Text.EndsWith(_plainProtectedTail, StringComparison.Ordinal))
                    throw new InvalidOperationException("The signature or quoted message changed. Keep writing manually to preserve it.");
                var body = mode == "appendBody" ? PlainNewText().TrimEnd() + "\n\n" + text : text;
                PlainEditor.Text = body + (_plainProtectedTail.Length > 0 ? "\n\n" + _plainProtectedTail : "");
            }
            else PlainEditor.SelectedText = text;
            return;
        }
        if (mode == "signature")
        {
            await Editor.CoreWebView2.ExecuteScriptAsync($"setSignature({JsonSerializer.Serialize(text)})");
            return;
        }
        var result = await Editor.CoreWebView2.ExecuteScriptAsync($"cmd({JsonSerializer.Serialize(mode)}, {JsonSerializer.Serialize(text)})");
        if (result == "false")
            throw new InvalidOperationException(mode == "replyDraft" ? "Your reply changed. Review the draft and choose Use draft again." : "Your message or selection changed. Select the text and rewrite it again.");
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
        // Design CW1 (D5): quick replies are here too
        var quick = _vm.QuickReplies;
        if (quick.Count > 0)
        {
            if (menu.Items.Count > 0) menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = "Quick replies", IsEnabled = false });
            foreach (var q in quick)
            {
                var mi = new MenuItem { Header = q.Length > 48 ? q[..48] + "…" : q };
                var text = q;
                mi.Click += (_, _) => _vm.InsertQuickReply(text);
                menu.Items.Add(mi);
            }
        }
        if (menu.Items.Count > 0) menu.Items.Add(new Separator());
        var saveAs = new MenuItem { Header = "Save this message as a template…" };
        saveAs.Click += (_, _) => OnSaveAsTemplate();
        menu.Items.Add(saveAs);
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
        if (token.Length < 2 || token.Contains('<') || token.Contains('@') && token.EndsWith('>')) { CloseSuggestions(); return; }
        var hits = AppServices.Engine.Contacts.Suggest(token);
        if (hits.Count == 0) { CloseSuggestions(); return; }
        SuggestList.ItemsSource = hits;
        SuggestList.SelectedIndex = 0;
        _suggestTarget = box;
        var owner = BoxOf(box);
        SuggestPopup.PlacementTarget = (UIElement?)owner ?? box;
        SuggestPopup.Width = Math.Max(320, owner?.ActualWidth ?? box.ActualWidth);
        SuggestPopup.IsOpen = true;
        if (owner != null) owner.SuggestionsOpen = true;
    }

    private void CloseSuggestions()
    {
        SuggestPopup.IsOpen = false;
        foreach (var b in new[] { ToBox, CcBox, BccBox, ReplyToBox }) b.SuggestionsOpen = false;
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
                CloseSuggestions();
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
        if (SuggestList.IsKeyboardFocusWithin || SuggestPopup.IsMouseOver) return;   // picking a suggestion: the pick replaces the typed text
        var wasOpen = SuggestPopup.IsOpen;
        CloseSuggestions();
        // The box skipped its own commit while the list was open; the focus went elsewhere, so commit now.
        if (wasOpen && sender is TextBox input) BoxOf(input)?.CommitInput();
    }

    /// <summary>Design CW1 (A1): the picked person becomes a chip in the field the user was typing in.</summary>
    private void AcceptSuggestion()
    {
        if (_suggestTarget == null || SuggestList.SelectedItem is not ContactSuggestion contact) { CloseSuggestions(); return; }
        var entry = Composer.FormatAddress(contact.Name, contact.Address);
        var owner = BoxOf(_suggestTarget);
        CloseSuggestions();
        if (owner != null) owner.AddChip(entry);
        else { _suggestTarget.Text = entry + ", "; _suggestTarget.CaretIndex = _suggestTarget.Text.Length; }
        _suggestTarget.Focus();
    }

    // ───────────────────────── chrome ─────────────────────────

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnMaximizeRestore(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
