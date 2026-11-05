using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Magpie.App.Services;
using Magpie.Core;
using Magpie.Core.Ai;
using Magpie.Core.Mail;
using Magpie.Core.Models;
using Magpie.Core.Settings;

namespace Magpie.App.ViewModels;

public sealed class AttachmentItem : ObservableObject
{
    public string Path { get; init; } = "";
    private string? _outgoingName;
    public string Name => _outgoingName ?? System.IO.Path.GetFileName(Path);
    public void Rename(string name)
    {
        _outgoingName = name;
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Icon));
        OnPropertyChanged(nameof(Display));
    }
    public string Icon => System.IO.Path.GetExtension(Name).ToLowerInvariant() switch
    {
        ".png" or ".jpg" or ".jpeg" or ".gif" => "▧",
        ".pdf" => "▤",
        ".doc" or ".docx" or ".txt" => "▣",
        ".xls" or ".xlsx" or ".csv" => "▦",
        _ => "📎",
    };
    public string Size => HtmlRenderer.FormatSize(new FileInfo(Path).Exists ? new FileInfo(Path).Length : 0);
    public bool Carried { get; init; }
    /// <summary>For attachments carried over from the original (forward / draft): the MIME part itself.</summary>
    public MimeKit.MimeEntity? Part { get; init; }
    public string Display => Carried ? Name : $"{Name}  ·  {Size}";
    private ImageSource? _thumbnail;
    private bool _thumbnailLoaded;
    /// <summary>A small, decoded picture. Never loads the original full-size bitmap into the UI.</summary>
    public ImageSource? Thumbnail
    {
        get
        {
            if (_thumbnailLoaded) return _thumbnail;
            _thumbnailLoaded = true;
            if (Carried || ComposeSafety.AttachmentPreviewKind(Path) != "image") return null;
            try
            {
                var file = new FileInfo(Path);
                if (!file.Exists || file.Length > 32 * 1024 * 1024) return null;
                using var stream = file.OpenRead();
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.StreamSource = stream;
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = 64;
                bitmap.EndInit();
                bitmap.Freeze();
                _thumbnail = bitmap;
            }
            catch (Exception) { /* Missing or invalid pictures retain their file-type icon. */ }
            return _thumbnail;
        }
    }
    public Visibility ThumbnailVisibility => Thumbnail == null ? Visibility.Collapsed : Visibility.Visible;
    public bool CanCompress => !Carried && ComposeSafety.CanCompressImage(Path);
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
    [ObservableProperty] private string _priority = "Normal";
    [ObservableProperty] private string _replyTo = "";
    [ObservableProperty] private bool _requestReadReceipt;
    [ObservableProperty] private bool _warnExternalRecipients;
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
    public string AttachmentSummary
    {
        get
        {
            var files = Attachments.Where(a => !a.Carried).ToArray();
            var count = Attachments.Count;
            if (count == 0) return "No files attached";
            var size = files.Length > 0 ? ComposeSafety.EncodedSizeOfExistingFiles(files.Select(a => a.Path), "") : 0;
            var limit = ComposeSafety.SuggestedOutgoingLimitBytes(From);
            var countLabel = $"{count} file{(count == 1 ? "" : "s")}";
            return countLabel
                   + (files.Length > 0 ? $" · about {HtmlRenderer.FormatSize(size)} encoded / {HtmlRenderer.FormatSize(limit)} suggested cap" : "")
                   + (Attachments.Any(a => a.Carried) ? " + forwarded content" : "");
        }
    }
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
        Priority = draft.Priority;
        ReplyTo = draft.ReplyTo;
        RequestReadReceipt = draft.RequestReadReceipt;
        foreach (var p in draft.AttachmentPaths)
        {
            var item = new AttachmentItem { Path = p };
            if (draft.AttachmentNames.TryGetValue(p, out var outgoingName)) item.Rename(outgoingName);
            Attachments.Add(item);
        }
        foreach (var part in draft.CarriedParts)
            Attachments.Add(new AttachmentItem { Path = (part as MimeKit.MimePart)?.FileName ?? "forwarded message", Carried = true, Part = part });
        if (draft.AttachmentOrder.Count > 0)
        {
            var arranged = new List<AttachmentItem>();
            foreach (var position in draft.AttachmentOrder)
            {
                var item = position.Path != null
                    ? Attachments.FirstOrDefault(a => !a.Carried && string.Equals(a.Path, position.Path, StringComparison.OrdinalIgnoreCase))
                    : position.CarriedIndex is { } index && index >= 0 && index < draft.CarriedParts.Count
                        ? Attachments.FirstOrDefault(a => ReferenceEquals(a.Part, draft.CarriedParts[index])) : null;
                if (item != null && !arranged.Contains(item)) arranged.Add(item);
            }
            foreach (var item in Attachments)
                if (!arranged.Contains(item)) arranged.Add(item);
            Attachments.Clear();
            foreach (var item in arranged) Attachments.Add(item);
        }

        var html = draft.Html;
        if (draft.Mode == ComposeMode.New && string.IsNullOrEmpty(html) && From != null)
            html = "<p><br></p>" + Composer.SignatureHtml(From, reply: false);
        if (!string.IsNullOrWhiteSpace(prefillText))
            html = TextToParagraphs(prefillText) + html;
        InitialHtml = html;
        // A message pulled back by Undo send / Cancel & edit exists nowhere else any more: treat it as unsaved
        // so closing the window asks before discarding it.
        StartsUnsaved = draft.Mode == ComposeMode.EditDraft && draft.SourceDraftRow == null && draft.LocalDraftId == null;
        InitializeReplyDrafts(prefillText);
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
        StopReplyDraft(clear: true);
    }

    public static string TextToParagraphs(string text) => Composer.TextToParagraphs(text);

    partial void OnSubjectChanged(string value) { OnPropertyChanged(nameof(WindowTitle)); MarkEdited(); InvalidateReplyContext(); InvalidateLegacyAi(); }
    partial void OnToChanged(string value) { MarkEdited(); InvalidateReplyContext(); InvalidateLegacyAi(); }
    partial void OnCcChanged(string value) { MarkEdited(); InvalidateReplyContext(); InvalidateLegacyAi(); }
    partial void OnBccChanged(string value) { MarkEdited(); InvalidateReplyContext(); InvalidateLegacyAi(); }
    partial void OnPriorityChanged(string value) => MarkEdited();
    partial void OnReplyToChanged(string value) => MarkEdited();
    partial void OnRequestReadReceiptChanged(bool value) => MarkEdited();

    public void RefreshAi()
    {
        ShowDraftAi = _e.Ai.IsVisible(AiFeature.Draft);
        ShowRewriteAi = _e.Ai.IsVisible(AiFeature.Rewrite);
        RefreshReplyAi();
        InvalidateLegacyAi();
        // AW1: the Reply / Forward draft panel sits under the editor, so it no longer needs the assistant rail.
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
        var added = new List<string>();
        foreach (var p in paths.Where(File.Exists))
        {
            if (Attachments.Any(a => !a.Carried && string.Equals(a.Path, p, StringComparison.OrdinalIgnoreCase))) continue;
            var fi = new FileInfo(p);
            if (fi.Length > 25 * 1024 * 1024 && !Ui.Confirm("Large attachment", $"{fi.Name} is {HtmlRenderer.FormatSize(fi.Length)}. Many mail servers reject messages over 25 MB. Attach anyway?"))
                continue;
            Attachments.Add(new AttachmentItem { Path = p });
            added.Add(p);
            MarkEdited();
        }
        if (added.Count > 0) RecentComposeFiles.Remember(added);
        OnPropertyChanged(nameof(AttachmentSummary));
    }

    [RelayCommand]
    private void RemoveAttachment(AttachmentItem? a)
    {
        if (a == null) return;
        Attachments.Remove(a);
        if (a.Carried && a.Part != null) _draft.CarriedParts.Remove(a.Part);
        MarkEdited();
        OnPropertyChanged(nameof(AttachmentSummary));
        if (a.Carried) InvalidateReplyContext();   // a forward note may name this file
    }

    public void RenameAttachment(AttachmentItem attachment, string outgoingName)
    {
        if (attachment.Part is MimeKit.MimePart part) part.FileName = outgoingName;
        attachment.Rename(outgoingName);
        MarkEdited();
        InvalidateReplyContext();
    }

    /// <summary>Switch the outgoing attachment to a newly saved copy, leaving the original on disk.</summary>
    public void ReplaceAttachmentWithOptimizedCopy(AttachmentItem original, string copyPath)
    {
        var index = Attachments.IndexOf(original);
        if (index < 0 || original.Carried || !File.Exists(copyPath)) return;
        if (Attachments.Any(a => !a.Carried && string.Equals(a.Path, copyPath, StringComparison.OrdinalIgnoreCase))) return;
        Attachments[index] = new AttachmentItem { Path = copyPath };
        MarkEdited();
        OnPropertyChanged(nameof(AttachmentSummary));
        InvalidateReplyContext();
    }

    private void MoveAttachment(AttachmentItem? attachment, int change)
    {
        if (attachment == null) return;
        var index = Attachments.IndexOf(attachment);
        var target = index + change;
        if (index < 0 || target < 0 || target >= Attachments.Count) return;
        Attachments.Move(index, target);
        MarkEdited();
        InvalidateReplyContext();
    }

    [RelayCommand] private void MoveAttachmentUp(AttachmentItem? attachment) => MoveAttachment(attachment, -1);
    [RelayCommand] private void MoveAttachmentDown(AttachmentItem? attachment) => MoveAttachment(attachment, 1);

    private async Task<Draft?> BuildDraftAsync(bool validate = true)
    {
        if (From == null) { if (validate) Ui.Error("Send", "Add an account first."); return null; }
        if (validate)
        {
            var bad = Composer.InvalidAddresses(To).Concat(Composer.InvalidAddresses(Cc)).Concat(Composer.InvalidAddresses(Bcc))
                .Concat(Composer.InvalidAddresses(ReplyTo)).ToList();
            if (bad.Count > 0) { Ui.Error("Check the recipients", "These don't look like email addresses:\n\n" + string.Join("\n", bad)); return null; }
        }
        var html = GetHtml != null ? await GetHtml() : InitialHtml;
        if (_draft.Mode != ComposeMode.EditDraft) _draft.ComposeStyle = _e.Config.Typography.Resolve("composeBody");
        _draft.AccountId = From.Id;
        _draft.To = To;
        _draft.Cc = Cc;
        _draft.Bcc = Bcc;
        _draft.Subject = Subject;
        _draft.Priority = Priority;
        _draft.ReplyTo = ReplyTo;
        _draft.RequestReadReceipt = RequestReadReceipt;
        _draft.Html = html;
        _draft.AttachmentPaths = Attachments.Where(a => !a.Carried).Select(a => a.Path).ToList();
        _draft.AttachmentNames = Attachments.Where(a => !a.Carried && !string.Equals(a.Name, Path.GetFileName(a.Path), StringComparison.Ordinal))
            .ToDictionary(a => a.Path, a => a.Name, StringComparer.OrdinalIgnoreCase);
        _draft.AttachmentOrder = Attachments.Select(a => a.Carried
            ? new DraftAttachmentPosition { CarriedIndex = _draft.CarriedParts.IndexOf(a.Part!) }
            : new DraftAttachmentPosition { Path = a.Path }).ToList();
        return _draft;
    }

    /// <summary>Queues the message; <paramref name="when"/> null = now (after the undo window).</summary>
    public async Task SendAsync(DateTimeOffset? when, DateTimeOffset? remindIfNoReply)
    {
        // Claim the send before any await, so a double-click or a repeating Ctrl+Enter can't queue it twice.
        if (Sending || _queued) return;
        StopReplyDraft(clear: true);
        DiscardPreview();
        Sending = true;
        try
        {
            if (string.IsNullOrWhiteSpace(To) && string.IsNullOrWhiteSpace(Cc) && string.IsNullOrWhiteSpace(Bcc))
            {
                Ui.Error("Send", "Add at least one recipient.");
                return;
            }
            if (string.IsNullOrWhiteSpace(Subject) && !Ui.Confirm("No subject", "Send this message without a subject?")) return;
            var missingFiles = ComposeSafety.MissingFiles(Attachments.Where(a => !a.Carried).Select(a => a.Path));
            if (missingFiles.Count > 0)
            {
                Ui.Error("Missing attachment", "These files moved or were deleted. Remove them or attach them again:\n\n" + string.Join("\n", missingFiles));
                return;
            }
            var visibleCount = ComposeSafety.VisibleRecipientCount(To, Cc);
            if (visibleCount > 20)
            {
                var invalid = Composer.InvalidAddresses(To).Concat(Composer.InvalidAddresses(Cc))
                    .Concat(Composer.InvalidAddresses(Bcc)).ToArray();
                if (invalid.Length > 0)
                {
                    Ui.Error("Check recipients", "Correct these addresses before rearranging recipients:\n\n" + string.Join("\n", invalid));
                    return;
                }
                var choice = MessageBox.Show(Ui.ActiveWindow!,
                    $"This message has {visibleCount} visible recipients in To and Cc. They can see each other's addresses.\n\n"
                    + "Yes — Move all except the first recipient to Bcc, then review before sending.\n"
                    + "No — Keep recipients visible and continue sending.\n"
                    + "Cancel — Return to the message.",
                    "Large recipient list", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
                if (choice == MessageBoxResult.Cancel) return;
                if (choice == MessageBoxResult.Yes)
                {
                    var rearranged = ComposeSafety.MoveVisibleRecipientsToBcc(To, Cc, Bcc);
                    To = rearranged.To;
                    Cc = rearranged.Cc;
                    Bcc = rearranged.Bcc;
                    ShowCcBcc = true;
                    MessageBox.Show(Ui.ActiveWindow!, "Recipients were moved to Bcc. Check the To and Bcc fields, then press Send again when ready.",
                        "Recipients moved", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
            }
            var repeated = ComposeSafety.DuplicateRecipients(To, Cc, Bcc);
            if (repeated.Count > 0 && !Ui.Confirm("Duplicate recipients",
                    "These addresses appear more than once. Magpie will send only one copy to each:\n\n" + string.Join("\n", repeated) + "\n\nContinue?")) return;
            if (WarnExternalRecipients && From != null)
            {
                var external = ComposeSafety.ExternalRecipients(From.Email, To, Cc, Bcc);
                if (external.Count > 0 && !Ui.Confirm("External recipients",
                        "These addresses are outside the sending account's email domain:\n\n"
                        + string.Join("\n", external) + "\n\nReview carefully before sending. Continue?")) return;
            }
            var newText = GetNewText != null ? await GetNewText() : MimeText.HtmlToText(InitialHtml);
            if (ComposeSafety.MentionsAttachment(newText) && Attachments.Count == 0 &&
                !Ui.Confirm("Did you forget an attachment?", "Your message refers to an attachment, but no files are attached. Send anyway?")) return;
            if (string.IsNullOrWhiteSpace(newText) && Attachments.Count == 0 &&
                !Ui.Confirm("Empty message", "The message has no new text or attachments. Send anyway?")) return;
            var encoded = ComposeSafety.EncodedSizeOfExistingFiles(Attachments.Where(a => !a.Carried).Select(a => a.Path), "");
            var suggestedLimit = ComposeSafety.SuggestedOutgoingLimitBytes(From);
            if (encoded > suggestedLimit && !Ui.Confirm("Large message",
                    $"Attached files are estimated at {HtmlRenderer.FormatSize(encoded)} after email encoding, above the {HtmlRenderer.FormatSize(suggestedLimit)} guidance for this account. Your server's actual limit may differ. Send anyway?")) return;
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

    /// <summary>Export the current message as a complete MIME file without sending it or changing draft status.</summary>
    public async Task<bool> ExportEmlAsync(string destination)
    {
        await _saveGate.WaitAsync();
        var temporaryFile = Path.Combine(Path.GetDirectoryName(destination)!, ".magpie-export-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            if (_queued || From == null) return false;
            var draft = await BuildDraftAsync(validate: false);
            if (draft == null) return false;
            var message = Composer.Build(draft, From);
            await using (var output = new FileStream(temporaryFile, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await message.WriteToAsync(output);
            File.Move(temporaryFile, destination, overwrite: true);
            Status = "✓ Saved message as .eml";
            return true;
        }
        catch (Exception ex)
        {
            Ui.Error("Save message to file", ex.Message);
            return false;
        }
        finally
        {
            try { if (File.Exists(temporaryFile)) File.Delete(temporaryFile); }
            catch (Exception) { /* The message export already reported any primary error. */ }
            _saveGate.Release();
        }
    }

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
        var context = _legacyAiContext = LegacyAiFingerprint();
        bool Current() => !ct.IsCancellationRequested && !_closed && !_queued
            && context == LegacyAiFingerprint()
            && _e.Ai.Availability(kind == "rewrite" ? AiFeature.Rewrite : AiFeature.Draft) == AiAvailability.Ready;
        AiError = "";
        AiPreview = "";
        PreviewKind = kind;
        AiBusy = true;
        var sb = new StringBuilder();
        var acceptingTokens = true;
        try
        {
            var result = await Task.Run(() => call(t => Ui.Post(() => { if (acceptingTokens && Current()) { sb.Append(t); AiPreview = sb.ToString(); } }), ct), ct);
            acceptingTokens = false;
            if (Current()) AiPreview = result.Trim();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            acceptingTokens = false;
            if (Current())
            {
                AiError = ex is AiException ? ex.Message : "The AI provider could not be reached: " + ex.Message;
                PreviewKind = "";
            }
        }
        finally { acceptingTokens = false; if (!ct.IsCancellationRequested) AiBusy = false; }
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
        _legacyAiContext = "";
    }
}
