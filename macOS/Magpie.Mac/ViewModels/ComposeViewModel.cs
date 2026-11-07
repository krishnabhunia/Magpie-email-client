using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Magpie.Core;
using Magpie.Core.Mail;
using Magpie.Core.Models;
using Magpie.Mac.Services;

namespace Magpie.Mac.ViewModels;

public sealed class AttachmentItem
{
    public string Path { get; init; } = "";
    public bool Carried { get; init; }
    /// <summary>Attachments carried over from the original (forward / draft): the MIME part itself.</summary>
    public MimeKit.MimeEntity? Part { get; init; }
    public string Display => Carried ? Path : $"{System.IO.Path.GetFileName(Path)} · {HtmlRenderer.FormatSize(new FileInfo(Path).Exists ? new FileInfo(Path).Length : 0)}";
}

/// <summary>
/// A new message, reply or forward. The body is Core's HTML editor page (the same as on Windows) in the web view; the
/// view hands its HTML over on demand. Sending goes through Core's outbox, so Undo (for the seconds set in Settings)
/// and Send later work as on Windows.
/// </summary>
public sealed partial class ComposeViewModel : ObservableObject
{
    private static MailEngine E => AppServices.Engine;
    private readonly MailEngine _engine;
    private readonly Draft _draft;
    private bool _queued;
    private bool _initialized;
    private bool _refreshingSenders;
    private bool _detached;
    private int _senderRevision;

    public ObservableCollection<Account> Accounts { get; } = new();
    public ObservableCollection<SenderIdentity> SenderChoices { get; } = new();
    public ObservableCollection<AttachmentItem> Attachments { get; } = new();
    /// <summary>Send later choices, worked out from the clock each time the menu opens (a window can stay open for hours).</summary>
    public static List<TimePreset> SendLaterChoices(DateTime now) => TimePresets.For(now, sendLater: true);

    /// <summary>⌘K in the editor: only web and mail links can be put in a message. Null = not allowed.</summary>
    public static string? AllowedLink(string? text)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0) return null;
        if (!t.Contains(':') && !t.Contains(' '))
        {
            if (t.Contains('@')) t = "mailto:" + t;                 // "anita@example.com"
            else if (t.Contains('.')) t = "https://" + t;           // "example.com"
        }
        return Uri.TryCreate(t, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" or "mailto" ? u.AbsoluteUri : null;
    }

    /// <summary>The draft on this Mac this window is editing, if any.</summary>
    public long? LocalDraftId => _draft.LocalDraftId;

    /// <summary>Set by the view: the editor's HTML.</summary>
    public Func<Task<string>>? GetHtml { get; set; }
    public event Action? CloseRequested;

    [ObservableProperty] private Account? _from;
    [ObservableProperty] private SenderIdentity? _selectedSender;
    [ObservableProperty] private string _to = "";
    [ObservableProperty] private string _cc = "";
    [ObservableProperty] private string _bcc = "";
    [ObservableProperty] private bool _showCcBcc;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(WindowTitle))] private string _subject = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _dirty;
    [ObservableProperty] private bool _sending;

    public string InitialHtml { get; }
    public string WindowTitle => string.IsNullOrWhiteSpace(Subject) ? "New message" : Subject;
    public bool ShowFrom => Accounts.Count > 1;
    public bool ShowSender => SenderChoices.Count > 1 || SelectedSender is { Available: false };
    public bool HasAttachments => Attachments.Count > 0;

    public ComposeViewModel(Draft draft)
    {
        _engine = E;
        _draft = draft;
        foreach (var a in E.Accounts) Accounts.Add(a);
        From = Accounts.FirstOrDefault(a => a.Id == draft.AccountId) ?? Accounts.FirstOrDefault();
        RefreshSenderIdentities(draft.SenderAddress);
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
        InitialHtml = html;
        // A message taken back by Undo exists nowhere else: closing asks before it is thrown away.
        Dirty = draft.Mode == ComposeMode.EditDraft && draft.SourceDraftRow == null && draft.LocalDraftId == null;
        // While this window edits a draft kept on this Mac, the background upload leaves it alone.
        if (draft.LocalDraftId is { } local) E.ClaimLocalDraft(local);
        Attachments.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasAttachments));
        _initialized = true;
        _engine.Settings.Changed += OnSettingsChanged;
    }

    partial void OnFromChanged(Account? value)
    {
        if (!_initialized) return;
        // An explicit account change selects that account's primary identity. Reopening or a settings refresh
        // instead retains the selected alias, including an unavailable one, until the user chooses another.
        RefreshSenderIdentities("");
        _senderRevision++;
        Dirty = true;
    }

    partial void OnSelectedSenderChanged(SenderIdentity? value)
    {
        if (!_initialized || _refreshingSenders) return;
        _senderRevision++;
        Dirty = true;
    }

    private void OnSettingsChanged() => AppServices.Post(() =>
    {
        if (!_detached) RefreshSenderIdentities(SelectedSender?.Address ?? _draft.SenderAddress);
    });

    private void RefreshSenderIdentities(string address)
    {
        var account = From == null ? null : _engine.AccountById(From.Id);
        var desired = string.IsNullOrWhiteSpace(address) ? account?.Email ?? From?.Email ?? "" : address;
        var choices = account == null ? new List<SenderIdentity>() : SenderIdentities.ForAccount(account).ToList();
        if (desired.Length > 0 && !choices.Any(s => s.Address.Equals(desired, StringComparison.OrdinalIgnoreCase)))
            choices.Add(new SenderIdentity(desired, "", Available: false));
        _refreshingSenders = true;
        try
        {
            // Clearing a bound ComboBox temporarily sets SelectedItem to null. Avoid both that churn and a
            // false unsaved-change prompt when unrelated settings are saved while this window is open.
            if (!SenderChoices.SequenceEqual(choices))
            {
                SenderChoices.Clear();
                foreach (var sender in choices) SenderChoices.Add(sender);
            }
            SelectedSender = SenderChoices.FirstOrDefault(s => s.Address.Equals(desired, StringComparison.OrdinalIgnoreCase));
            OnPropertyChanged(nameof(ShowSender));
        }
        finally { _refreshingSenders = false; }
    }

    partial void OnToChanged(string value) => Dirty = true;
    partial void OnCcChanged(string value) => Dirty = true;
    partial void OnBccChanged(string value) => Dirty = true;
    partial void OnSubjectChanged(string value) => Dirty = true;

    [RelayCommand] private void ShowCc() => ShowCcBcc = true;

    /// <summary>To / Cc / Bcc suggestions: people Magpie has seen, for the address being typed after the last comma.</summary>
    public static Task<IEnumerable<object>> SuggestAsync(string? text, CancellationToken ct)
    {
        var last = LastToken(text ?? "");
        if (last.Length < 1) return Task.FromResult(Enumerable.Empty<object>());
        return Task.Run<IEnumerable<object>>(() => E.Store.SearchContacts(last, 8).Select(c => (object)c.Display).ToList(), ct);
    }

    /// <summary>Picking a suggestion replaces only the address being typed.</summary>
    public static string ReplaceLastToken(string? text, string? picked)
    {
        text ??= "";
        if (string.IsNullOrEmpty(picked)) return text;
        var cut = Math.Max(text.LastIndexOf(','), text.LastIndexOf(';'));
        var head = cut >= 0 ? text[..(cut + 1)] + " " : "";
        return head + picked + ", ";
    }

    public static string LastToken(string text)
    {
        var cut = Math.Max(text.LastIndexOf(','), text.LastIndexOf(';'));
        return (cut >= 0 ? text[(cut + 1)..] : text).Trim();
    }

    public async Task AddAttachmentsAsync(IEnumerable<string> paths)
    {
        foreach (var p in paths.Where(File.Exists))
        {
            var fi = new FileInfo(p);
            if (fi.Length > 25 * 1024 * 1024 && !await Dialogs.Confirm("Large attachment", $"{fi.Name} is {HtmlRenderer.FormatSize(fi.Length)}. Many mail servers reject messages over 25 MB. Attach anyway?", "Attach"))
                continue;
            Attachments.Add(new AttachmentItem { Path = p });
            Dirty = true;
        }
    }

    [RelayCommand]
    private void RemoveAttachment(AttachmentItem? a)
    {
        if (a == null) return;
        Attachments.Remove(a);
        if (a.Carried && a.Part != null) _draft.CarriedParts.Remove(a.Part);
        Dirty = true;
    }

    private async Task<Draft?> BuildDraftAsync(bool validate)
    {
        if (From == null) { if (validate) await Dialogs.Error("Send", "Add an account first."); return null; }
        var accountId = From.Id;
        var sender = SelectedSender;
        var senderRevision = _senderRevision;
        if (sender is not { Available: true })
        {
            Status = "Choose a configured sender from Send as before saving or sending.";
            return null;
        }
        if (validate)
        {
            var bad = Composer.InvalidAddresses(To).Concat(Composer.InvalidAddresses(Cc)).Concat(Composer.InvalidAddresses(Bcc)).ToList();
            if (bad.Count > 0) { await Dialogs.Error("Check the recipients", "These don't look like email addresses:\n\n" + string.Join("\n", bad)); return null; }
        }
        var html = GetHtml != null ? await GetHtml() : InitialHtml;
        if (senderRevision != _senderRevision)
        {
            Status = "The sender changed while reading the message. Review it and try again.";
            return null;
        }
        var account = _engine.AccountById(accountId);
        if (account == null)
        {
            Status = "This account is no longer configured. Choose an account before saving or sending.";
            return null;
        }
        try { SenderIdentities.Resolve(account, sender.Address); }
        catch (InvalidOperationException ex) { Status = ex.Message; return null; }
        // Keep the original Draft: options managed on another platform (receipts, security, reply metadata)
        // must survive a Mac edit even when this window does not expose their controls.
        _draft.AccountId = accountId;
        _draft.SenderAddress = sender.Address;
        _draft.To = To;
        _draft.Cc = Cc;
        _draft.Bcc = Bcc;
        _draft.Subject = Subject;
        _draft.Html = html;
        _draft.AttachmentPaths = Attachments.Where(a => !a.Carried).Select(a => a.Path).ToList();
        return _draft;
    }

    [RelayCommand] private Task Send() => SendAsync(null);
    [RelayCommand] private Task SendLater(TimePreset? when) => when == null ? Task.CompletedTask : SendAsync(when.When);

    /// <summary>Queues the message in Core's outbox; <paramref name="when"/> null = now (after the undo wait).</summary>
    public async Task SendAsync(DateTimeOffset? when)
    {
        if (Sending || _queued) return;
        Sending = true;
        try
        {
            if (string.IsNullOrWhiteSpace(To) && string.IsNullOrWhiteSpace(Cc) && string.IsNullOrWhiteSpace(Bcc))
            {
                await Dialogs.Error("Send", "Add at least one recipient.");
                return;
            }
            if (string.IsNullOrWhiteSpace(Subject) && !await Dialogs.Confirm("No subject", "Send this message without a subject?", "Send")) return;
            var d = await BuildDraftAsync(validate: true);
            if (d == null) return;
            var undo = E.Config.UndoSendSeconds;
            var sendAt = when ?? DateTimeOffset.Now.AddSeconds(undo);
            var id = E.QueueSend(d, sendAt, null);
            _queued = true;
            if (d.SourceDraftRow is { } draftRow) E.DeleteDraft(draftRow);
            if (d.LocalDraftId is { } localId) { E.DeleteLocalDraft(localId); d.LocalDraftId = null; }
            Dirty = false;
            if (when != null) AppServices.Main?.ShowScheduled(id, sendAt, Subject);
            else AppServices.Main?.ShowUndo(id, undo, Subject);
            CloseRequested?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error("queue send", ex);
            await Dialogs.Error("Send", ex.Message);
        }
        finally { Sending = false; }
    }

    /// <summary>"Save draft" / "Keep as draft": the server's Drafts folder, or this Mac when offline (it uploads later).</summary>
    [RelayCommand]
    public async Task<bool> SaveDraftAsync()
    {
        if (_queued) return true;
        var d = await BuildDraftAsync(validate: false);
        if (d == null) return false;
        Status = "Saving draft…";
        try
        {
            await E.SaveDraftAsync(d);
            if (d.LocalDraftId is { } id) { E.DeleteLocalDraft(id); d.LocalDraftId = null; }
            Status = "✓ Saved in Drafts";
        }
        catch (Exception ex)
        {
            Log.Info("draft kept on this Mac (server save failed: " + ex.Message + ")");
            try
            {
                var id = await Task.Run(() => E.SaveLocalDraft(d, pendingUpload: true));
                if (id != 0) E.ClaimLocalDraft(id);
                Status = "Offline · saved on this Mac " + DateTime.Now.ToString("HH:mm");
            }
            catch (Exception ex2)
            {
                Log.Error("keep draft on this Mac", ex2);
                Status = "Couldn't save the draft";
                await Dialogs.Error("Keep as draft", "Magpie couldn't save this message, neither to the server nor on this Mac:\n\n" + ex2.Message);
                return false;
            }
        }
        Dirty = false;
        return true;
    }

    /// <summary>The window closed: a draft kept on this Mac may upload by itself again.</summary>
    public void Detach()
    {
        _detached = true;
        _engine.Settings.Changed -= OnSettingsChanged;
        if (_draft.LocalDraftId is { } local) _engine.ReleaseLocalDraft(local);
    }

    /// <summary>Closing with unsaved changes: Keep as draft, Discard, or Cancel (false = stay open).</summary>
    public async Task<bool> ConfirmCloseAsync()
    {
        if (!Dirty || _queued) return true;
        var answer = await Dialogs.Ask("Keep this message?", "You haven't sent this message.", new[] { "Cancel", "Discard", "Keep as draft" });
        return answer switch
        {
            "Keep as draft" => await SaveDraftAsync(),
            "Discard" => true,
            _ => false,
        };
    }
}
