using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Magpie.App.Services;
using Magpie.Core;
using Magpie.Core.Ai;
using Magpie.Core.Mail;
using Magpie.Core.Models;
using Magpie.Core.Settings;

namespace Magpie.App.ViewModels;

public sealed class AttachmentItem
{
    public string Path { get; init; } = "";
    public string Name => System.IO.Path.GetFileName(Path);
    public string Size => HtmlRenderer.FormatSize(new FileInfo(Path).Exists ? new FileInfo(Path).Length : 0);
    public bool Carried { get; init; }
    /// <summary>For attachments carried over from the original (forward / draft): the MIME part itself.</summary>
    public MimeKit.MimeEntity? Part { get; init; }
    public string Display => Carried ? Path : $"{Name}  ·  {Size}";
}

/// <summary>Compose window state. The editor itself is a WebView2 page; the view supplies HTML on demand.</summary>
public partial class ComposeViewModel : ObservableObject
{
    private readonly MailEngine _e = AppServices.Engine;
    private readonly Draft _draft;
    private CancellationTokenSource? _aiCts;

    public ObservableCollection<Account> Accounts { get; } = new();
    public ObservableCollection<AttachmentItem> Attachments { get; } = new();
    public ObservableCollection<string> Tones { get; } = new() { "Friendly", "Professional", "Brief", "Warm", "Firm" };

    /// <summary>Set by the view: returns the editor HTML.</summary>
    public Func<Task<string>>? GetHtml { get; set; }
    /// <summary>Set by the view: inserts plain text at the cursor (replaceSelection) or replaces the new-text part of the body.</summary>
    public Func<string, string, Task>? EditorCommand { get; set; }
    public event Action? CloseRequested;

    [ObservableProperty] private Account? _from;
    [ObservableProperty] private string _to = "";
    [ObservableProperty] private string _cc = "";
    [ObservableProperty] private string _bcc = "";
    [ObservableProperty] private bool _showCcBcc;
    [ObservableProperty] private string _subject = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _dirty;
    [ObservableProperty] private bool _sending;

    // ── AI rail (S5) ──
    [ObservableProperty] private bool _showAiRail;
    [ObservableProperty] private bool _aiPanelOpen;
    [ObservableProperty] private bool _showDraftAi;
    [ObservableProperty] private bool _showRewriteAi;
    [ObservableProperty] private bool _aiNeedsSetup;
    [ObservableProperty] private string _prompt = "";
    [ObservableProperty] private string _tone = "Friendly";
    [ObservableProperty] private bool _aiBusy;
    [ObservableProperty] private string _aiPreview = "";
    [ObservableProperty] private string _aiError = "";
    [ObservableProperty] private string _previewKind = ""; // "draft" | "rewrite"
    [ObservableProperty] private string _selectedText = "";
    [ObservableProperty] private string _customRewrite = "";

    public string InitialHtml { get; }
    /// <summary>Id of this message's copy on this PC, once autosaved.</summary>
    public long? LocalDraftId => _draft.LocalDraftId;
    public string WindowTitle => string.IsNullOrWhiteSpace(Subject) ? "New message" : Subject;
    public bool IsReply => _draft.Mode is ComposeMode.Reply or ComposeMode.ReplyAll;
    public string ProviderLabel => _e.Ai.ProviderLabel;

    public ComposeViewModel(Draft draft, string? prefillText)
    {
        _draft = draft;
        foreach (var a in _e.Accounts) Accounts.Add(a);
        From = Accounts.FirstOrDefault(a => a.Id == draft.AccountId) ?? Accounts.FirstOrDefault();
        To = draft.To;
        Cc = draft.Cc;
        Bcc = draft.Bcc;
        ShowCcBcc = Cc.Length > 0 || Bcc.Length > 0;
        Subject = draft.Subject;
        foreach (var p in draft.AttachmentPaths) Attachments.Add(new AttachmentItem { Path = p });
        foreach (var part in draft.CarriedParts)
            Attachments.Add(new AttachmentItem { Path = (part as MimeKit.MimePart)?.FileName ?? "forwarded message", Carried = true, Part = part });

        var html = draft.Html;
        if (draft.Mode == ComposeMode.New && string.IsNullOrEmpty(html) && From != null)
            html = "<p><br></p>" + Composer.SignatureHtml(From, reply: false);
        if (!string.IsNullOrWhiteSpace(prefillText))
            html = TextToParagraphs(prefillText) + html;
        InitialHtml = html;
        // A message pulled back by Undo send / Cancel & edit exists nowhere else any more: treat it as unsaved
        // so closing the window asks before discarding it.
        StartsUnsaved = draft.Mode == ComposeMode.EditDraft && draft.SourceDraftRow == null && draft.LocalDraftId == null;
        RefreshAi();
        if (draft.LocalDraftId is { } localId)
        {
            _openedFrom = _e.Store.GetLocalDraft(localId);
            _pendingUpload = _openedFrom?.PendingUpload ?? false;
            Claim(localId);
        }
        _onSettings = () => Ui.Post(RefreshAi);
        _e.Settings.Changed += _onSettings;
        _constructed = true;
    }

    /// <summary>True when the content would be lost if the window closed without saving or sending.</summary>
    public bool StartsUnsaved { get; }

    private bool _queued;
    private bool _closed;
    private readonly bool _constructed;

    // ───────────────────────── drafts kept on this PC (design F1) ─────────────────────────
    //
    // Life cycle:
    //  · every edit schedules an autosave of the message on this PC (a local draft row);
    //  · while this window is open it "claims" its local draft, so the background upload never touches it;
    //  · autosave, Keep and Send take turns (_saveGate), so two of them never build the message at the same time;
    //  · Send / Keep / Discard first stop autosave and wait for one that is still running, then act;
    //  · Discard puts back the last version the user chose to keep: the one this window opened from this PC,
    //    or what the last "Keep / Save draft" stored — never an older one;
    //  · closing without changes just releases the claim (a draft waiting to upload keeps waiting).

    private readonly Ui.Debouncer _autosave = new(TimeSpan.FromSeconds(3));
    private Task? _autosaveTask;
    /// <summary>The last kept local version (as opened, or as saved by Keep while offline); restored on Discard.
    /// Null when there is nothing on this PC to go back to (new message, or the last Keep reached the server).</summary>
    private LocalDraft? _openedFrom;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    /// <summary>Counts edits, so a save only marks the window clean if nothing was typed while it ran.</summary>
    private int _edits;
    /// <summary>Whether the local copy should be uploaded to the server once this window lets go of it.</summary>
    private bool _pendingUpload;
    private long? _claimed;

    /// <summary>Call on every edit: marks the message changed and autosaves it on this PC a moment later.</summary>
    public void MarkEdited()
    {
        if (!_constructed) return;   // filling the fields from the draft is not an edit
        _edits++;
        Dirty = true;
        if (!_closed) _autosave.Run(() => _ = AutosaveAsync());
    }

    private async Task AutosaveAsync()
    {
        if (_closed || _queued || Sending || From == null || !Dirty) return;
        if (_autosaveTask is { IsCompleted: false }) { _autosave.Run(() => _ = AutosaveAsync()); return; }
        _autosaveTask = SaveLocalNowAsync();
        await _autosaveTask;
    }

    private async Task SaveLocalNowAsync()
    {
        // Keep or Send is building the message right now: try again in a moment.
        if (!await _saveGate.WaitAsync(0)) { if (!_closed) _autosave.Run(() => _ = AutosaveAsync()); return; }
        try
        {
            if (_closed || _queued) return;
            var d = await BuildDraftAsync(validate: false);
            if (d == null || _closed || _queued) return;
            var pending = _pendingUpload;
            // Send / Discard / Keep can't delete the row under us (they wait for the gate and stop autosave first),
            // so a missing row means the background upload took it just before this window opened: store it again.
            var id = await Task.Run(() => _e.SaveLocalDraft(d, pending));
            if (id == 0) return;
            if (!_closed) Claim(id);
            Status = "Saved on this PC · " + DateTime.Now.ToString("HH:mm");
        }
        catch (Exception ex) { Log.Warn("autosave: " + ex.Message); }
        finally { _saveGate.Release(); }
    }

    private void Claim(long id)
    {
        if (_claimed == id) return;
        if (_claimed is { } old) _e.ReleaseLocalDraft(old);
        _e.ClaimLocalDraft(id);
        _claimed = id;
    }

    private void ReleaseClaim()
    {
        if (_claimed is { } id) _e.ReleaseLocalDraft(id);
        _claimed = null;
    }

    /// <summary>Stops further autosaves and waits for one that is already writing.</summary>
    private async Task SettleAutosaveAsync(bool stop)
    {
        if (stop) _closed = true;
        var t = _autosaveTask;
        if (t != null) { try { await t; } catch { } }
    }

    /// <summary>
    /// "Keep as draft" (and the Save draft button): saves to the server's Drafts folder, replacing earlier saves of
    /// this message; when that fails (offline, server error) the message stays on this PC and uploads by itself
    /// once this window has closed. Returns false only when there is nothing to save (no account).
    /// </summary>
    public async Task<bool> KeepAsDraftAsync(bool closing)
    {
        await SettleAutosaveAsync(stop: closing);
        await _saveGate.WaitAsync();
        try
        {
            if (_queued) return true;                     // it was sent meanwhile
            var edits = _edits;
            var d = await BuildDraftAsync(validate: false);
            if (d == null) { if (closing) Reopen(); return false; }
            Status = "Saving draft…";
            try
            {
                await _e.SaveDraftAsync(d);
                if (d.LocalDraftId is { } id) { ReleaseClaim(); _e.DeleteLocalDraft(id); d.LocalDraftId = null; }
                _pendingUpload = false;
                _openedFrom = null;                       // the server has it now; Discard must not bring back an older copy
                Status = "✓ Saved in Drafts";
            }
            catch (Exception ex)
            {
                Log.Info("draft kept on this PC (server save failed: " + ex.Message + ")");
                long id;
                try { id = await Task.Run(() => _e.SaveLocalDraft(d, pendingUpload: true)); }
                catch (Exception ex2)
                {
                    Log.Error("keep draft on this PC", ex2);
                    Status = "Couldn't save the draft";
                    Ui.Error("Keep as draft", "Magpie couldn't save this message, neither to the server nor on this PC:\n\n" + ex2.Message);
                    if (closing) Reopen();                 // the window stays open, keep autosaving
                    return false;
                }
                _pendingUpload = true;
                _openedFrom = _e.Store.GetLocalDraft(id);  // Discard from now on goes back to this kept version
                if (!closing && id != 0) Claim(id);
                Status = "Offline · saved on this PC " + DateTime.Now.ToString("HH:mm");
            }
            // Typing while the save ran is not saved yet: stay "changed" and autosave it.
            if (_edits == edits) Dirty = false;
            else if (!_closed) _autosave.Run(() => _ = AutosaveAsync());
            return true;
        }
        finally { _saveGate.Release(); }
    }

    /// <summary>
    /// "Discard": drops this window's changes. A draft opened from this PC goes back to how it was when opened;
    /// a new message's local copy is removed; a server draft it was opened from is left as it was.
    /// </summary>
    /// <summary>A close was cancelled after autosave had been stopped: resume it and re-claim this window's copy.</summary>
    private void Reopen()
    {
        _closed = false;
        if (_draft.LocalDraftId is { } id) Claim(id);
    }

    /// <returns>False if putting back the kept version failed (the window then stays open).</returns>
    public async Task<bool> DiscardAsync()
    {
        await SettleAutosaveAsync(stop: true);
        await _saveGate.WaitAsync();
        try
        {
            if (_queued) return true;
            var current = _draft.LocalDraftId;
            if (_openedFrom != null)
            {
                _e.Store.SaveLocalDraft(_openedFrom);        // puts the kept version back (re-creates it if needed)
                if (current is { } cur && cur != _openedFrom.Id) _e.Store.DeleteLocalDraft(cur);
                _e.NotifyLocalDraftsChanged();
            }
            else if (current is { } id)
                _e.DeleteLocalDraft(id);
            _draft.LocalDraftId = _openedFrom?.Id;
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("discard", ex);
            Ui.Error("Discard", "Magpie couldn't restore the saved version of this draft, so the window stays open:\n\n" + ex.Message);
            Reopen();
            return false;
        }
        finally { _saveGate.Release(); }
    }

    private readonly Action _onSettings;

    public void Detach()
    {
        _closed = true;
        ReleaseClaim();   // a draft still marked for upload is picked up by the background upload from now on
        _e.Settings.Changed -= _onSettings;
        _aiCts?.Cancel();
    }

    public static string TextToParagraphs(string text) => Composer.TextToParagraphs(text);

    partial void OnSubjectChanged(string value) { OnPropertyChanged(nameof(WindowTitle)); MarkEdited(); }
    partial void OnToChanged(string value) => MarkEdited();
    partial void OnCcChanged(string value) => MarkEdited();
    partial void OnBccChanged(string value) => MarkEdited();

    public void RefreshAi()
    {
        ShowDraftAi = _e.Ai.IsVisible(AiFeature.Draft);
        ShowRewriteAi = _e.Ai.IsVisible(AiFeature.Rewrite);
        ShowAiRail = ShowDraftAi || ShowRewriteAi;
        if (!ShowAiRail) AiPanelOpen = false;
        AiNeedsSetup = ShowAiRail && (ShowDraftAi ? _e.Ai.Availability(AiFeature.Draft) : _e.Ai.Availability(AiFeature.Rewrite)) == AiAvailability.NotConfigured;
        OnPropertyChanged(nameof(ProviderLabel));
    }

    [RelayCommand] private void ToggleAiPanel() => AiPanelOpen = !AiPanelOpen;
    [RelayCommand] private void ShowCc() => ShowCcBcc = true;
    [RelayCommand] private void OpenAiSettings() => Views.SettingsWindow.Open("AI");

    public void AddAttachments(IEnumerable<string> paths)
    {
        foreach (var p in paths.Where(File.Exists))
        {
            var fi = new FileInfo(p);
            if (fi.Length > 25 * 1024 * 1024 && !Ui.Confirm("Large attachment", $"{fi.Name} is {HtmlRenderer.FormatSize(fi.Length)}. Many mail servers reject messages over 25 MB. Attach anyway?"))
                continue;
            Attachments.Add(new AttachmentItem { Path = p });
            MarkEdited();
        }
    }

    [RelayCommand]
    private void RemoveAttachment(AttachmentItem? a)
    {
        if (a == null) return;
        Attachments.Remove(a);
        if (a.Carried && a.Part != null) _draft.CarriedParts.Remove(a.Part);
        MarkEdited();
    }

    private async Task<Draft?> BuildDraftAsync(bool validate = true)
    {
        if (From == null) { if (validate) Ui.Error("Send", "Add an account first."); return null; }
        if (validate)
        {
            var bad = Composer.InvalidAddresses(To).Concat(Composer.InvalidAddresses(Cc)).Concat(Composer.InvalidAddresses(Bcc)).ToList();
            if (bad.Count > 0) { Ui.Error("Check the recipients", "These don't look like email addresses:\n\n" + string.Join("\n", bad)); return null; }
        }
        var html = GetHtml != null ? await GetHtml() : InitialHtml;
        _draft.AccountId = From.Id;
        _draft.To = To;
        _draft.Cc = Cc;
        _draft.Bcc = Bcc;
        _draft.Subject = Subject;
        _draft.Html = html;
        _draft.AttachmentPaths = Attachments.Where(a => !a.Carried).Select(a => a.Path).ToList();
        return _draft;
    }

    /// <summary>Queues the message; <paramref name="when"/> null = now (after the undo window).</summary>
    public async Task SendAsync(DateTimeOffset? when, DateTimeOffset? remindIfNoReply)
    {
        // Claim the send before any await, so a double-click or a repeating Ctrl+Enter can't queue it twice.
        if (Sending || _queued) return;
        Sending = true;
        try
        {
            if (string.IsNullOrWhiteSpace(To) && string.IsNullOrWhiteSpace(Cc) && string.IsNullOrWhiteSpace(Bcc))
            {
                Ui.Error("Send", "Add at least one recipient.");
                return;
            }
            if (string.IsNullOrWhiteSpace(Subject) && !Ui.Confirm("No subject", "Send this message without a subject?")) return;
            // Wait for an autosave that is building the message (it shares attachment streams with this build).
            await _saveGate.WaitAsync();
            Draft? d;
            DateTimeOffset sendAt;
            int undo;
            long id;
            try
            {
                d = await BuildDraftAsync();
                if (d == null) return;
                undo = _e.Config.UndoSendSeconds;
                sendAt = when ?? DateTimeOffset.Now.AddSeconds(undo);
                id = _e.QueueSend(d, sendAt, remindIfNoReply);
                _queued = true;
                if (d.SourceDraftRow is { } draftRow) _e.DeleteDraft(draftRow);
                if (d.LocalDraftId is { } localId) { ReleaseClaim(); _e.DeleteLocalDraft(localId); d.LocalDraftId = null; }
            }
            finally { _saveGate.Release(); }
            await SettleAutosaveAsync(stop: true);
            Dirty = false;
            var main = System.Windows.Application.Current.MainWindow as MainWindow;
            if (main?.DataContext is MainViewModel vm)
            {
                if (when != null) vm.ShowScheduled(id, sendAt, Subject);
                else vm.ShowUndo(id, undo, Subject);
            }
            CloseRequested?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error("queue send", ex);
            Ui.Error("Send", ex.Message);
        }
        finally { Sending = false; }
    }

    /// <summary>The "Save draft" button: same as Keep — server Drafts, or this PC when offline.</summary>
    public Task<bool> SaveDraftAsync() => KeepAsDraftAsync(closing: false);

    public void InsertTemplate(QuickTemplate t) => _ = EditorCommand?.Invoke("insert", t.Body);

    // ───────────────────────── AI: write a draft (C2/C3) ─────────────────────────

    private ThreadForAi? ReplyContext()
    {
        if (!IsReply || string.IsNullOrEmpty(_draft.ThreadKey) || From == null) return null;
        var rows = _e.Store.GetThread(From.Id, _draft.ThreadKey);
        if (rows.Count == 0) return null;
        return AiService.BuildThread(Subject, rows.Select(r => (r, _e.Store.GetBody(r.Id))).ToList());
    }

    [RelayCommand]
    private async Task GenerateDraft()
    {
        if (string.IsNullOrWhiteSpace(Prompt)) { AiError = "Say what the email should say, e.g. \"accept Friday, ask for the agenda\"."; return; }
        if (_e.Ai.Availability(AiFeature.Draft) == AiAvailability.NotConfigured) { AiNeedsSetup = true; return; }
        var context = ReplyContext();
        if (_e.Ai.NeedsConsent(AiFeature.Draft) && !Views.ConsentDialog.Ask(AiFeature.Draft, context?.Included ?? 0)) return;
        await RunAsync("draft", (onToken, ct) =>
        {
            var name = From?.DisplayName is { Length: > 0 } n ? n : From?.Email ?? "";
            return _e.Ai.DraftAsync(Prompt, Tone, context, name, onToken, ct);
        });
    }

    // ───────────────────────── AI: rewrite selection (C4) ─────────────────────────

    [RelayCommand]
    private async Task Rewrite(string? kind)
    {
        if (string.IsNullOrWhiteSpace(SelectedText)) { AiError = "Select some text in your message first."; return; }
        if (_e.Ai.Availability(AiFeature.Rewrite) == AiAvailability.NotConfigured) { AiNeedsSetup = true; return; }
        if (_e.Ai.NeedsConsent(AiFeature.Rewrite) && !Views.ConsentDialog.Ask(AiFeature.Rewrite, 0)) return;
        var k = Enum.TryParse<RewriteKind>(kind, out var parsed) ? parsed : RewriteKind.Custom;
        if (k == RewriteKind.Custom && string.IsNullOrWhiteSpace(CustomRewrite)) { AiError = "Type how to change it, e.g. \"make it sound more confident\"."; return; }
        var text = SelectedText;
        if (EditorCommand == null) return;
        try { await EditorCommand("captureRewrite", text); }
        catch (Exception ex) { AiError = ex.Message; return; }
        await RunAsync("rewrite", (onToken, ct) => _e.Ai.RewriteAsync(text, k, CustomRewrite, onToken, ct));
    }

    private async Task RunAsync(string kind, Func<Action<string>, CancellationToken, Task<string>> call)
    {
        _aiCts?.Cancel();
        _aiCts = new CancellationTokenSource();
        var ct = _aiCts.Token;
        AiError = "";
        AiPreview = "";
        PreviewKind = kind;
        AiBusy = true;
        var sb = new StringBuilder();
        try
        {
            var result = await Task.Run(() => call(t => Ui.Post(() => { if (!ct.IsCancellationRequested) { sb.Append(t); AiPreview = sb.ToString(); } }), ct), ct);
            if (!ct.IsCancellationRequested) AiPreview = result.Trim();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AiError = ex is AiException ? ex.Message : "The AI provider could not be reached: " + ex.Message;
            PreviewKind = "";
        }
        finally { if (!ct.IsCancellationRequested) AiBusy = false; }
    }

    /// <summary>Nothing is inserted until the user chooses — the result is a preview card.</summary>
    [RelayCommand]
    private async Task AcceptPreview(string? how)
    {
        if (string.IsNullOrWhiteSpace(AiPreview) || EditorCommand == null) return;
        var mode = PreviewKind == "rewrite" ? "replaceSelection" : how == "replace" ? "replaceBody" : "insert";
        try
        {
            await EditorCommand(mode, AiPreview);
            MarkEdited();
            DiscardPreview();
        }
        catch (Exception ex) { AiError = ex.Message; }
    }

    [RelayCommand]
    private void DiscardPreview()
    {
        _aiCts?.Cancel();
        AiBusy = false;
        AiPreview = "";
        PreviewKind = "";
    }
}
