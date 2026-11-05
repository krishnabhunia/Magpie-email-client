using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Magpie.App.Services;
using Magpie.Core;
using Magpie.Core.Mail;
using Magpie.Core.Models;
using Magpie.Core.Settings;

namespace Magpie.App.ViewModels;

/// <summary>One entry of the Signature ▾ menu (design CW1, D3).</summary>
public sealed record SignatureChoice(string Id, string Name, bool IsCurrent);

/// <summary>
/// Design CW1 (15.0.0): the compose window's recipients rows, message options, attachments summary, signature switcher,
/// templates / quick replies, word count and Send &amp; Archive. The editor-side features live in ComposeWindow.
/// </summary>
public partial class ComposeViewModel
{
    // ───────────────────────── recipients (A2, A5, A6) ─────────────────────────

    [ObservableProperty] private string _replyTo = "";
    [ObservableProperty] private bool _showCc;
    [ObservableProperty] private bool _showBcc;
    [ObservableProperty] private bool _showReplyTo;
    /// <summary>A6: "23 people will see each other's addresses" — To + Cc above <see cref="Composer.ManyRecipients"/>.</summary>
    [ObservableProperty] private string _manyRecipientsText = "";

    partial void OnReplyToChanged(string value) { MarkEdited(); }

    [RelayCommand] private void OpenCc() => ShowCc = true;
    [RelayCommand] private void OpenBcc() => ShowBcc = true;
    [RelayCommand] private void OpenReplyTo() => ShowReplyTo = true;

    /// <summary>A6: moves everyone in To and Cc to Bcc (the first To address stays, so the message still has a visible recipient).</summary>
    [RelayCommand]
    private void MoveAllToBcc()
    {
        var all = Composer.SplitAddresses(To).Concat(Composer.SplitAddresses(Cc)).ToList();
        if (all.Count == 0) return;
        To = all[0];
        Cc = "";
        Bcc = Composer.MergeDuplicates(string.Join(", ", Composer.SplitAddresses(Bcc).Concat(all.Skip(1))));
        ShowBcc = true;
    }

    private void RefreshRecipientNote()
    {
        var n = Composer.VisibleRecipients(To, Cc);
        ManyRecipientsText = n > Composer.ManyRecipients ? $"{n} people will see each other's addresses." : "";
    }

    /// <summary>E6: an @mention in the text adds the person to To when they aren't a recipient yet. Returns true when added.</summary>
    public bool AddMention(string name, string address)
    {
        var have = Composer.SplitAddresses(To).Concat(Composer.SplitAddresses(Cc)).Concat(Composer.SplitAddresses(Bcc))
            .Select(p => Composer.NameAndAddress(p).Address);
        if (have.Contains(address, StringComparer.OrdinalIgnoreCase)) return false;
        var entry = string.IsNullOrWhiteSpace(name) ? address : (name.Contains(',') ? "\"" + name + "\"" : name) + " <" + address + ">";
        To = To.Trim().Length == 0 ? entry : To.TrimEnd().TrimEnd(',', ';') + ", " + entry;
        return true;
    }

    /// <summary>A4: what the address book chose.</summary>
    public void AddFromAddressBook(IEnumerable<string> to, IEnumerable<string> cc, IEnumerable<string> bcc)
    {
        static string Join(string field, IEnumerable<string> more)
        {
            var all = Composer.SplitAddresses(field).Concat(more).ToList();
            return Composer.MergeDuplicates(string.Join(", ", all));
        }
        var t = to.ToList(); var c = cc.ToList(); var b = bcc.ToList();
        if (t.Count > 0) To = Join(To, t);
        if (c.Count > 0) { Cc = Join(Cc, c); ShowCc = true; }
        if (b.Count > 0) { Bcc = Join(Bcc, b); ShowBcc = true; }
    }

    // ───────────────────────── message options (D1, D2, B16) ─────────────────────────

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(PriorityLabel), nameof(IsHighPriority), nameof(IsLowPriority))] private MailPriority _priority;
    [ObservableProperty] private bool _readReceipt;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsRichText))] private bool _plainText;

    public string PriorityLabel => Priority switch { MailPriority.High => "High", MailPriority.Low => "Low", _ => "Normal" };
    public bool IsHighPriority => Priority == MailPriority.High;
    public bool IsLowPriority => Priority == MailPriority.Low;
    public bool IsRichText => !PlainText;

    partial void OnPriorityChanged(MailPriority value) => MarkEdited();
    partial void OnReadReceiptChanged(bool value) => MarkEdited();

    [RelayCommand] private void SetPriority(string? which) => Priority = which switch { "High" => MailPriority.High, "Low" => MailPriority.Low, _ => MailPriority.Normal };
    [RelayCommand] private void ToggleReadReceipt() => ReadReceipt = !ReadReceipt;

    // ───────────────────────── attachments (C1, C2, C5) ─────────────────────────

    [ObservableProperty] private string _attachmentSummary = "";
    [ObservableProperty] private bool _attachmentsOver;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(AttachmentBarWidth))] private double _attachmentFraction;
    /// <summary>The filled part of the 200 px size bar.</summary>
    public double AttachmentBarWidth => Math.Round(AttachmentFraction * 200);
    [ObservableProperty] private bool _hasAttachments;

    private long AttachmentLimit => Composer.AttachmentLimit(From);
    private string _lastHtmlForSize = "";

    /// <summary>The size bar: "2 files · 2.4 MB of 25 MB (Gmail)". <paramref name="html"/> counts the pictures inside the message.</summary>
    public void RefreshAttachmentSummary(string? html = null)
    {
        if (html != null) _lastHtmlForSize = html;
        var limit = AttachmentLimit;
        var bytes = Composer.AttachedBytes(Attachments.Where(a => !a.Carried).Select(a => a.Path), Attachments.Where(a => a.Carried).Select(a => a.CarriedSize), _lastHtmlForSize);
        var files = Attachments.Count;
        HasAttachments = files > 0;
        AttachmentsOver = bytes > limit;
        AttachmentFraction = limit > 0 ? Math.Min(1, bytes / (double)limit) : 0;
        var kind = From?.Kind switch { AccountKind.Gmail => "Gmail", AccountKind.Microsoft => "Outlook", _ => "this account" };
        if (files == 0 && bytes == 0) { AttachmentSummary = ""; return; }
        var head = files == 0 ? "Pictures in the message" : files == 1 ? "1 file" : $"{files} files";
        AttachmentSummary = $"{head} · {HtmlRenderer.FormatSize(bytes)} of {HtmlRenderer.FormatSize(limit)} ({kind})"
                            + (AttachmentsOver ? $" — remove {HtmlRenderer.FormatSize(bytes - limit)} or send a link" : "");
    }

    /// <summary>C5: the last files attached from this PC, newest first, that still exist.</summary>
    public IReadOnlyList<string> RecentAttachments => _e.Config.Compose.RecentAttachments.ToList();

    private void NoteRecent(IEnumerable<string> paths)
    {
        var any = false;
        foreach (var p in paths) { _e.Config.Compose.NoteAttachment(p); any = true; }
        if (any) _e.Settings.Save(notify: false);
    }

    /// <summary>C4: a picture pasted or dropped as data (base64 data: URI) attached as a file.</summary>
    public string? AttachDataUri(string dataUri)
    {
        try
        {
            var comma = dataUri.IndexOf(',');
            if (!dataUri.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) || comma < 0) return null;
            var type = dataUri[5..dataUri.IndexOf(';')];
            var ext = type.Split('/')[1].Split('+')[0];
            var dir = Path.Combine(_e.Paths.MailRoot, "attachments-pasted");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "picture-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6] + "." + ext);
            File.WriteAllBytes(path, Convert.FromBase64String(dataUri[(comma + 1)..]));
            Attachments.Add(new AttachmentItem { Path = path });
            MarkEdited();
            return path;
        }
        catch (Exception ex) { Log.Warn("attach pasted picture: " + ex.Message); return null; }
    }

    // ───────────────────────── signatures (D3) ─────────────────────────

    public ObservableCollection<SignatureChoice> Signatures { get; } = new();
    /// <summary>Id of the signature in the message ("" = none).</summary>
    [ObservableProperty] private string _currentSignatureId = "";

    private void LoadSignatures()
    {
        Signatures.Clear();
        if (From == null) return;
        foreach (var s in From.Signatures) Signatures.Add(new SignatureChoice(s.Id, s.Name, s.Id == CurrentSignatureId));
        Signatures.Add(new SignatureChoice("", "None", CurrentSignatureId.Length == 0));
    }

    /// <summary>Puts this signature into the message in place of the current one ("" = none). <paramref name="manual"/> = the user
    /// picked it in Signature ▾, so it goes in even when the account's "add to new messages / replies" switch is off; an automatic
    /// swap (From changed) respects those switches.</summary>
    public async Task SelectSignatureAsync(string id, bool manual = true)
    {
        if (From == null || EditorCommand == null) return;
        var html = id.Length == 0 ? "" : Composer.SignatureHtmlFor(From, id, reply: IsReply || _draft.Mode == ComposeMode.Forward);
        if (manual && id.Length > 0 && html.Length == 0 && From.Signatures.Any(s => s.Id == id))
            html = HtmlRenderer.SanitizeBody(From.Signatures.First(s => s.Id == id).Html, new Dictionary<string, string>(), allowRemote: true).html;   // the switch is off: the user asked for it anyway
        if (!manual && id.Length > 0 && html.Length == 0) id = "";   // the account doesn't add a signature to this kind of message
        try { await EditorCommand("signature", html); }
        catch (Exception ex) { Log.Warn("signature swap: " + ex.Message); return; }
        CurrentSignatureId = id;
        LoadSignatures();
        MarkEdited();
    }

    partial void OnFromChanged(Account? oldValue, Account? newValue)
    {
        if (!_constructed || newValue == null) return;
        // D3: changing the From account swaps to that account's default signature and its "Replies go to".
        var def = newValue.DefaultSignature?.Id ?? "";
        if (oldValue == null || oldValue.Id != newValue.Id)
        {
            if (string.IsNullOrWhiteSpace(ReplyTo) || ReplyTo == oldValue?.ReplyTo) ReplyTo = newValue.ReplyTo;
            if (!string.IsNullOrWhiteSpace(ReplyTo)) ShowReplyTo = true;
            _ = SelectSignatureAsync(CurrentSignatureId.Length == 0 && oldValue?.Signatures.Count > 0 ? "" : def, manual: false);
        }
        RefreshAttachmentSummary();
        MarkEdited();
    }

    // ───────────────────────── templates, quick replies (D4, D5) ─────────────────────────

    public IReadOnlyList<string> QuickReplies => _e.Config.QuickReplies.ToList();

    public void InsertQuickReply(string text) => _ = EditorCommand?.Invoke("insert", text);

    /// <summary>D4: "Save this message as a template…" — the new text (without signature and quote) under the given name.</summary>
    public async Task<bool> SaveAsTemplateAsync(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || GetNewText == null) return false;
        var body = Composer.TemplateBody(await GetNewText());
        if (body.Length == 0) { Ui.Error("Save as template", "Write the text first — the template is the message's own text, without the signature and the quoted email."); return false; }
        var templates = _e.Config.Templates;
        var existing = templates.FirstOrDefault(t => t.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            if (!Ui.Confirm("Save as template", $"A template called \"{existing.Name}\" exists. Replace it?")) return false;
            existing.Body = body;
        }
        else templates.Add(new QuickTemplate { Name = name.Trim(), Body = body });
        _e.Settings.Save();
        Status = "✓ Saved as a template";
        return true;
    }

    // ───────────────────────── word count (D8) ─────────────────────────

    [ObservableProperty] private string _wordsText = "";
    private int _words, _chars;

    /// <summary>Called by the editor as the text changes.</summary>
    public void UpdateStats(string newText)
    {
        _words = Composer.WordCount(newText);
        _chars = (newText ?? "").Length;
        WordsText = _words == 0 ? "" : _words == 1 ? "1 word" : $"{_words:N0} words";
        RefreshAttachmentSummary();
    }

    public string WordsTip => _words == 0 ? "" : $"{_words:N0} words · {_chars:N0} characters (your text only)";

    // ───────────────────────── send & archive (D6), save to file (D9) ─────────────────────────

    public bool CanSendAndArchive => (IsReply || _draft.Mode == ComposeMode.Forward) && !string.IsNullOrEmpty(_draft.ThreadKey);

    /// <summary>The thread to archive once the undo window has passed (set for Send &amp; Archive). The folders to archive from are
    /// decided when Send is pressed (the view the user replied from), not when the timer fires.</summary>
    public (string AccountId, string ThreadKey)? ArchiveAfterSend { get; private set; }

    public Task SendAndArchiveAsync()
    {
        if (!CanSendAndArchive) return SendAsync(null, null);
        ArchiveAfterSend = (_draft.AccountId, _draft.ThreadKey);
        return SendAsync(null, null);
    }

    /// <summary>
    /// D7: the footer's Discard — unlike closing without saving, this removes the message everywhere: the copy on this PC
    /// and the server draft it was opened from. Nothing is put back. False if that failed (the window stays open).
    /// </summary>
    public async Task<bool> DiscardForeverAsync()
    {
        await SettleAutosaveAsync(stop: true);
        await _saveGate.WaitAsync();
        try
        {
            if (_queued) return true;
            ReleaseClaim();
            foreach (var id in new[] { _draft.LocalDraftId, _openedFrom?.Id }.Where(x => x != null).Select(x => x!.Value).Distinct())
                _e.DeleteLocalDraft(id);
            _draft.LocalDraftId = null;
            _openedFrom = null;
            if (_draft.SourceDraftRow is { } row) { _e.DeleteDraft(row); _draft.SourceDraftRow = null; }
            Dirty = false;
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("discard", ex);
            Ui.Error("Discard", "Magpie couldn't remove this draft, so the window stays open:\n\n" + ex.Message);
            Reopen();
            return false;
        }
        finally { _saveGate.Release(); }
    }

    /// <summary>D9: the message as a .eml file.</summary>
    public async Task<bool> SaveToFileAsync(string path)
    {
        // Same turn-taking as Keep and Send: an autosave building the message shares the attachment streams.
        await _saveGate.WaitAsync();
        try
        {
            var d = await BuildDraftAsync(validate: false);
            if (d == null || From == null) return false;
            var bytes = Composer.ToEml(d, From);
            await File.WriteAllBytesAsync(path, bytes);
        }
        finally { _saveGate.Release(); }
        Status = "✓ Saved to file";
        return true;
    }

    // ───────────────────────── checks before sending (C2, C3) ─────────────────────────

    /// <summary>Set by the view: asks the forgot-attachment question; true = send anyway.</summary>
    public Func<Task<bool>>? AskForgotAttachment { get; set; }
    /// <summary>Set by the view: asks about sending over the attachment limit; true = send anyway.</summary>
    public Func<string, Task<bool>>? AskOverLimit { get; set; }

    /// <summary>Runs the pre-send questions. False = don't send.</summary>
    private async Task<bool> ChecksBeforeSendAsync()
    {
        if (_e.Config.Compose.AttachmentCheck && Attachments.Count == 0 && GetNewText != null && AskForgotAttachment != null)
        {
            string text;
            try { text = await GetNewText(); } catch { text = ""; }
            if (Composer.MentionsAttachment(text) && !await AskForgotAttachment()) return false;
        }
        if (AttachmentsOver && AskOverLimit != null && !await AskOverLimit(AttachmentSummary)) return false;
        return true;
    }

    private void InitFeatures(Draft draft)
    {
        ReplyTo = draft.ReplyTo;
        if (draft.Mode == ComposeMode.New && string.IsNullOrWhiteSpace(ReplyTo) && From != null) ReplyTo = From.ReplyTo;
        ShowCc = Cc.Length > 0;
        ShowBcc = Bcc.Length > 0;
        ShowReplyTo = ReplyTo.Length > 0;
        Priority = draft.Priority;
        ReadReceipt = draft.ReadReceipt || (draft.Mode != ComposeMode.EditDraft && _e.Config.Compose.AlwaysReadReceipt);
        PlainText = draft.PlainText;
        CurrentSignatureId = From?.DefaultSignature?.Id ?? "";
        if (draft.Mode == ComposeMode.EditDraft && !draft.Html.Contains("magpie-signature", StringComparison.OrdinalIgnoreCase)) CurrentSignatureId = "";
        LoadSignatures();
        Attachments.CollectionChanged += OnAttachmentsChanged;
        RefreshAttachmentSummary(draft.Html);
        RefreshRecipientNote();
    }

    private void OnAttachmentsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RefreshAttachmentSummary();
        if (e.NewItems != null) NoteRecent(e.NewItems.OfType<AttachmentItem>().Where(a => !a.Carried).Select(a => a.Path));
    }

    /// <summary>Copies the option fields into the draft (called from BuildDraftAsync).</summary>
    private void FillDraftOptions(Draft d)
    {
        d.ReplyTo = ReplyTo.Trim();
        d.Priority = Priority;
        d.ReadReceipt = ReadReceipt;
        d.PlainText = PlainText;
        d.To = Composer.MergeDuplicates(d.To);
        d.Cc = Composer.MergeDuplicates(d.Cc);
        d.Bcc = Composer.MergeDuplicates(d.Bcc);
    }
}
