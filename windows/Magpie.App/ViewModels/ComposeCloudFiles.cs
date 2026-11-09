using Magpie.Core.CloudFiles;
using Magpie.Core.Models;

namespace Magpie.App.ViewModels;

public partial class ComposeViewModel
{
    /// <summary>Share a selected existing file without granting Magpie permission to delete or replace it.</summary>
    public Task<CloudUploadedFile?> ShareExistingCloudFileAsync(CloudExistingSharePlan plan,
        Func<CancellationToken, Task<CloudUploadedFile>> share,
        Func<CloudUploadedFile, Task<bool>> insert,
        CancellationToken ct = default) => _e.WithProfileOperationAsync(
            token => ShareExistingCloudFileCoreAsync(plan, share, insert, token), ct);

    private async Task<CloudUploadedFile?> ShareExistingCloudFileCoreAsync(CloudExistingSharePlan plan,
        Func<CancellationToken, Task<CloudUploadedFile>> share,
        Func<CloudUploadedFile, Task<bool>> insert, CancellationToken ct)
    {
        if (!TryBeginAttachmentOperation()) return null;
        var version = _edits;
        var from = From;
        var senderIdentity = (from?.Id, from?.Kind, from?.Email);
        var fields = (To, Cc, Bcc, Subject, IsPlainText);
        var client = _e.CloudFiles.SignIn?.ExistingConfigFor(plan.Account.Kind)?.ClientId;
        CloudUploadedFile? shared = null;
        var inserted = false;
        try
        {
            var recipients = CloudSharePlan.RecipientsFor(To, Cc, Bcc);
            if (!recipients.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(plan.Recipients))
                throw new InvalidOperationException("The recipients changed. Review existing-file sharing again.");
            shared = await share(ct);
            ct.ThrowIfCancellationRequested();
            var account = _e.AccountById(plan.Account.Id);
            if (!CanCompleteAttachmentOperation || _edits != version || !ReferenceEquals(From, from)
                || senderIdentity != (From?.Id, From?.Kind, From?.Email)
                || fields != (To, Cc, Bcc, Subject, IsPlainText) || account == null || account.Kind != plan.Account.Kind
                || !string.Equals(account.Email, plan.Account.Email, StringComparison.OrdinalIgnoreCase)
                || client != _e.CloudFiles.SignIn?.ExistingConfigFor(plan.Account.Kind)?.ClientId)
                throw new InvalidOperationException("The draft or account changed while sharing. Review existing-file permissions before retrying.");
            if (!shared.IsValid || !shared.IsExisting || shared.AccountId != plan.Account.Id
                || shared.Provider != plan.File.Provider || shared.FileId != plan.File.Id || shared.WebUrl != plan.File.WebUrl
                || shared.Access != plan.Access || shared.DisplayName != plan.LinkTitle
                || !shared.AccountEmail.Equals(plan.Account.Email, StringComparison.OrdinalIgnoreCase)
                || !shared.Recipients.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(recipients))
                throw new InvalidOperationException("The shared existing file does not match the explicit confirmation.");
            _e.Store.RememberCloudFile(shared);
            inserted = await insert(shared);
            if (!inserted)
                throw new InvalidOperationException("The editor or insertion point changed. No link was inserted.");
            MarkEdited();
            await _saveGate.WaitAsync();
            try
            {
                var draft = await BuildDraftAsync(validate: false);
                if (draft == null || _closed || _queued) throw new IOException("The cloud draft is no longer open.");
                var id = await Task.Run(() => _e.SaveLocalDraft(draft, _pendingUpload));
                if (id == 0) throw new IOException("The cloud draft was not saved.");
                Claim(id);
                Status = "Existing cloud link inserted · named access verified · draft saved on this PC";
            }
            catch { Status = "Cloud link inserted, but draft save failed. Keep this window open and save again."; }
            finally { _saveGate.Release(); }
            return shared;
        }
        catch (Exception ex) when (shared != null && !inserted)
        {
            // A named grant on an existing file is durable even if editor insertion fails.
            // Never remove that file or silently revoke its original permissions.
            // An earlier draft may already reference this same existing URL. Never erase its proof.
            throw new CloudSharingException("The existing file may already be shared, but no link was inserted. "
                + "Review its access in the cloud provider before trying again. " + ex.Message,
                cleanupRequired: true, recoveryUrl: shared.WebUrl);
        }
        finally { AttachmentOperationBusy = false; }
    }

    /// <summary>
    /// Own upload/grant/insertion as one compose operation. Cleanup remains owned until every failure is reported.
    /// Delegates let the native smoke exercise this real lifecycle without credentials or remote files.
    /// </summary>
    public Task<CloudUploadedFile?> UploadCloudFileAsync(CloudSharePlan plan,
        Func<CancellationToken, Task<CloudUploadedFile>> upload,
        Func<CloudUploadedFile, Task<bool>> insert,
        Func<CloudUploadedFile, Task> remove,
        CancellationToken ct = default) => _e.WithProfileOperationAsync(
            token => UploadCloudFileCoreAsync(plan, upload, insert, remove, token), ct);

    private async Task<CloudUploadedFile?> UploadCloudFileCoreAsync(CloudSharePlan plan,
        Func<CancellationToken, Task<CloudUploadedFile>> upload,
        Func<CloudUploadedFile, Task<bool>> insert,
        Func<CloudUploadedFile, Task> remove,
        CancellationToken ct)
    {
        if (!TryBeginAttachmentOperation()) return null;
        var version = _edits;
        var from = From;
        var senderIdentity = (from?.Id, from?.Kind, from?.Email);
        var fields = (To, Cc, Bcc, Subject, IsPlainText);
        var client = _e.CloudFiles.SignIn?.ConfigFor(plan.Account.Kind)?.ClientId;
        CloudUploadedFile? uploaded = null;
        var inserted = false;
        try
        {
            var recipients = CloudSharePlan.RecipientsFor(To, Cc, Bcc);
            if (!recipients.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(plan.Recipients))
                throw new InvalidOperationException("The recipient list changed. Review the cloud-sharing confirmation again.");
            uploaded = await upload(ct);
            ct.ThrowIfCancellationRequested();
            var account = _e.AccountById(plan.Account.Id);
            if (!CanCompleteAttachmentOperation || _edits != version || !ReferenceEquals(From, from)
                || senderIdentity != (From?.Id, From?.Kind, From?.Email)
                || fields != (To, Cc, Bcc, Subject, IsPlainText) || account == null || account.Kind != plan.Account.Kind
                || !string.Equals(account.Email, plan.Account.Email, StringComparison.OrdinalIgnoreCase)
                || client != _e.CloudFiles.SignIn?.ConfigFor(plan.Account.Kind)?.ClientId)
                throw new InvalidOperationException("The draft, recipients, or cloud account changed during upload. The new copy will be removed; review the message and upload again.");
            if (!uploaded.IsValid || uploaded.AccountId != plan.Account.Id || uploaded.Provider != plan.Account.Kind
                || !uploaded.AccountEmail.Equals(plan.Account.Email, StringComparison.OrdinalIgnoreCase)
                || uploaded.Access != plan.Access || !uploaded.Recipients.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(recipients))
                throw new InvalidOperationException("The uploaded file does not match the sharing confirmation.");
            _e.Store.RememberCloudFile(uploaded);
            inserted = await insert(uploaded);
            if (!inserted)
                throw new InvalidOperationException("The message or insertion point changed. The new cloud copy will be removed; click in the message and try again.");
            // From here the link belongs to the visible draft. Never delete its target because a late cancellation
            // or local-save failure arrives after the editor acknowledged the insertion.
            MarkEdited();
            await _saveGate.WaitAsync();
            try
            {
                var draft = await BuildDraftAsync(validate: false);
                if (draft != null && !_closed && !_queued)
                {
                    var id = await Task.Run(() => _e.SaveLocalDraft(draft, _pendingUpload));
                    if (id == 0) throw new IOException("The cloud draft was not saved.");
                    Claim(id);
                }
                else throw new IOException("The cloud draft is no longer open.");
                Status = "Cloud link inserted · named access checked · saved on this PC";
            }
            catch
            {
                Status = "Cloud link inserted, but the draft could not be saved. Keep this window open and save the draft again.";
            }
            finally { _saveGate.Release(); }
            return uploaded;
        }
        catch
        {
            if (uploaded != null && !inserted)
            {
                try { await remove(uploaded); }
                catch (CloudSharingException) { throw; }
                catch { throw new CloudSharingException("The new cloud copy could not be removed. Open its cloud location and remove it or review its access before retrying.", true, uploaded.WebUrl); }
                _e.Store.ForgetCloudFile(uploaded.WebUrl);
            }
            throw;
        }
        finally { AttachmentOperationBusy = false; }
    }
}
