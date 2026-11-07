using CommunityToolkit.Mvvm.ComponentModel;
using Magpie.Core;
using Magpie.Core.Mail;
using Magpie.Core.Models;

namespace Magpie.App.ViewModels;

public partial class ComposeViewModel
{
    private bool _restoringRevision;
    [ObservableProperty] private bool _revisionBusy;

    /// <summary>The view replaces the whole editor, clears stale selections, and preserves restored formatting.</summary>
    public Func<string, bool, Task>? RestoreRevisionEditor { get; set; }
    /// <summary>A plain fallback must not silently flatten a rich revision on the next autosave.</summary>
    public bool SupportsRichDraftRestore { get; set; } = true;

    /// <summary>Initial load/manual checkpoints supplement the revisions recorded by autosave.</summary>
    public async Task CaptureDraftRevisionAsync()
    {
        if (_closed || _queued || Sending || From == null) return;
        await _saveGate.WaitAsync();
        try
        {
            if (_closed || _queued || Sending) return;
            var draft = await BuildDraftAsync(validate: false);
            if (draft != null) await Task.Run(() => _e.CaptureDraftRevision(draft));
        }
        catch (Exception ex) { Log.Warn("draft history checkpoint: " + ex.Message); }
        finally { _saveGate.Release(); }
    }

    public async Task<IReadOnlyList<DraftRevision>> LoadDraftHistoryAsync()
    {
        await CaptureDraftRevisionAsync();
        if (From == null || string.IsNullOrWhiteSpace(_draft.MessageId)) return Array.Empty<DraftRevision>();
        var accountId = From.Id;
        var messageId = _draft.MessageId;
        return await Task.Run(() => _e.Store.GetDraftRevisions(accountId, messageId));
    }

    public async Task ClearDraftHistoryAsync()
    {
        await _saveGate.WaitAsync();
        try
        {
            if (_closed || _queued || Sending || From == null) return;
            var accountId = From.Id;
            var messageId = _draft.MessageId;
            await Task.Run(() => _e.Store.DeleteDraftRevisions(accountId, messageId));
        }
        finally { _saveGate.Release(); }
    }

    public async Task<Draft?> ReadDraftRevisionAsync(long revisionId)
    {
        if (From == null) return null;
        var accountId = From.Id;
        var messageId = _draft.MessageId;
        return await Task.Run(() => _e.Store.OpenDraftRevision(accountId, messageId, revisionId));
    }

    /// <summary>Restore content only, retaining this compose window's identity, claims and Keep/Discard baseline.</summary>
    public async Task<bool> RestoreDraftRevisionAsync(long revisionId)
    {
        if (RevisionBusy || AttachmentOperationBusy || _closed || _queued || Sending || From == null || RestoreRevisionEditor == null) return false;
        RevisionBusy = true;
        await _saveGate.WaitAsync();
        Draft? rollback = null;
        var wasDirty = Dirty;
        try
        {
            if (_closed || _queued || Sending || From == null) return false;
            var accountId = From.Id;
            var messageId = _draft.MessageId;
            var editVersion = _edits;
            // Read the target before checkpoint pruning can evict the oldest version.
            var revision = await Task.Run(() => _e.Store.OpenDraftRevision(accountId, messageId, revisionId));
            if (revision == null) throw new InvalidOperationException("That revision is no longer available on this PC.");
            if (!SupportsRichDraftRestore && !revision.IsPlainText)
                throw new InvalidOperationException("Rich-text revisions need the rich message editor to preserve formatting. Reopen this draft after the rich editor is available.");
            var current = await BuildDraftAsync(validate: false);
            if (current == null) return false;
            var checkpoint = await Task.Run(() => _e.CaptureDraftRevision(current));
            if (checkpoint == 0)
                throw new InvalidOperationException("The current message could not be checkpointed safely within the draft-history limits. Save a copy as .eml before changing it.");
            rollback = await Task.Run(() => _e.Store.OpenDraftRevision(accountId, messageId, checkpoint));
            if (rollback == null) throw new InvalidOperationException("The current draft could not be checkpointed safely.");
            if (_closed || _queued || Sending || From?.Id != accountId || _draft.MessageId != messageId || _edits != editVersion)
                throw new InvalidOperationException("The draft changed while history was loading. Review the current message and try restoring again.");

            _restoringRevision = true;
            StopReplyDraft(clear: true);
            DiscardPreview();
            SelectedText = "";
            await RestoreRevisionEditor(revision.Html, revision.IsPlainText);
            ApplyRevisionContent(revision);
            var id = await Task.Run(() => _e.SaveLocalDraft(_draft, _pendingUpload));
            if (id == 0) throw new InvalidOperationException("The restored draft could not be saved on this PC.");
            Claim(id);
            _restoringRevision = false;
            MarkEdited();
            Status = "Restored draft revision · previous content remains in history";
            return true;
        }
        catch
        {
            if (_restoringRevision && rollback != null)
            {
                try
                {
                    await RestoreRevisionEditor!(rollback.Html, rollback.IsPlainText);
                    ApplyRevisionContent(rollback);
                    var id = await Task.Run(() => _e.SaveLocalDraft(_draft, _pendingUpload));
                    if (id != 0) Claim(id);
                    Dirty = wasDirty;
                }
                catch (Exception ex)
                {
                    Log.Error("draft revision rollback", ex);
                    throw new InvalidOperationException("Restoring failed. The previous content is still available in Draft history; reopen that revision before continuing.", ex);
                }
            }
            throw;
        }
        finally
        {
            _restoringRevision = false;
            RevisionBusy = false;
            _saveGate.Release();
        }
    }

    private void ApplyRevisionContent(Draft revision)
    {
        RefreshSenderIdentities(revision.SenderAddress);
        _draft.SenderAddress = revision.SenderAddress;
        To = revision.To;
        Cc = revision.Cc;
        Bcc = revision.Bcc;
        Subject = revision.Subject;
        ReplyTo = revision.ReplyTo;
        Priority = revision.Priority;
        RequestReadReceipt = revision.RequestReadReceipt;
        RequestDeliveryReceipt = revision.RequestDeliveryReceipt;
        RestoreSecurity(revision.Security);
        ShowCcBcc = Cc.Length > 0 || Bcc.Length > 0;
        _draft.To = To;
        _draft.Cc = Cc;
        _draft.Bcc = Bcc;
        _draft.Subject = Subject;
        _draft.ReplyTo = ReplyTo;
        _draft.Priority = Priority;
        _draft.RequestReadReceipt = RequestReadReceipt;
        _draft.RequestDeliveryReceipt = RequestDeliveryReceipt;
        _draft.Html = revision.Html;
        _draft.IsPlainText = revision.IsPlainText;
        _draft.ComposeStyle = revision.ComposeStyle;
        _draft.CarriedParts = revision.CarriedParts;
        _draft.AttachmentPaths.Clear();
        _draft.AttachmentNames.Clear();
        _draft.AttachmentOrder = revision.CarriedParts.Select((_, index) => new DraftAttachmentPosition { CarriedIndex = index }).ToList();
        Attachments.Clear();
        foreach (var part in revision.CarriedParts)
            Attachments.Add(new AttachmentItem { Path = MimeAttachmentNames.Get(part) ?? "attached message", Carried = true, Part = part });
        OnPropertyChanged(nameof(IsPlainText));
        OnPropertyChanged(nameof(AttachmentSummary));
    }
}
