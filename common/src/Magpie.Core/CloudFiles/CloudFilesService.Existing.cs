using System.Text.Json;
using Magpie.Core.Models;

namespace Magpie.Core.CloudFiles;

/// <summary>Browse only owned files, then separately consent to grant named people access on an existing file.</summary>
public sealed partial class CloudFilesService
{
    private const int BrowsePageSize = 50;

    private ICloudExistingFileAuthorization ExistingAuthorization() => _authorization as ICloudExistingFileAuthorization
        ?? throw new InvalidOperationException("Connect existing-file browsing independently of private-copy uploads.");

    public async Task<CloudExistingFilesPage> BrowseExistingAsync(Account account, string search = "", CancellationToken ct = default)
    {
        if (account.Kind is not (AccountKind.Gmail or AccountKind.Microsoft))
            throw new InvalidOperationException("Choose a Google Drive or OneDrive account.");
        search = search.Trim();
        if (search.Length > 100 || search.Any(char.IsControl))
            throw new InvalidOperationException("Use a search term of at most 100 characters, without control characters.");
        var auth = await ExistingAuthorization().AuthorizeExistingAsync(account, ct);
        if (!auth.Email.Equals(account.Email, StringComparison.OrdinalIgnoreCase))
            throw new CloudSharingException("The cloud account changed; reconnect before browsing.");
        return account.Kind == AccountKind.Gmail
            ? await BrowseGoogleAsync(auth, search, ct)
            : await BrowseMicrosoftAsync(auth, search, ct);
    }

    private async Task<CloudExistingFilesPage> BrowseGoogleAsync(CloudAuthorization auth, string search, CancellationToken ct)
    {
        var q = "trashed = false and 'me' in owners";
        if (search.Length > 0) q += " and name contains '" + search.Replace("\\", "\\\\").Replace("'", "\\'") + "'";
        var url = GoogleApi + "/files?spaces=drive&pageSize=" + BrowsePageSize
            + "&orderBy=modifiedTime%20desc&q=" + Esc(q)
            + "&fields=" + Esc("nextPageToken,files(id,name,mimeType,webViewLink,ownedByMe,trashed,capabilities(canShare),owners(emailAddress))");
        using var result = await JsonAsync(HttpMethod.Get, url, auth.AccessToken, null, ct);
        var root = result.RootElement;
        if (!root.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array || files.GetArrayLength() > BrowsePageSize)
            throw new CloudSharingException("Google Drive returned an invalid file listing.");
        var items = new List<CloudExistingFile>();
        foreach (var entry in files.EnumerateArray())
        {
            if (!OwnedGoogleFile(entry, auth.Email)) continue;
            var candidate = new CloudExistingFile(AccountKind.Gmail, String(entry, "id"), String(entry, "name"), String(entry, "webViewLink"));
            if (candidate.IsValid) items.Add(candidate);
        }
        return new(items, root.TryGetProperty("nextPageToken", out var next) && next.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(next.GetString()));
    }

    private static bool OwnedGoogleFile(JsonElement item, string email) =>
        item.TryGetProperty("ownedByMe", out var owned) && owned.ValueKind == JsonValueKind.True
        && (!item.TryGetProperty("trashed", out var trash) || trash.ValueKind == JsonValueKind.False)
        && String(item, "mimeType") is not ("application/vnd.google-apps.folder" or "application/vnd.google-apps.shortcut")
        && item.TryGetProperty("capabilities", out var capabilities) && capabilities.ValueKind == JsonValueKind.Object
        && capabilities.TryGetProperty("canShare", out var share) && share.ValueKind == JsonValueKind.True
        && item.TryGetProperty("owners", out var owners) && owners.ValueKind == JsonValueKind.Array
        && owners.GetArrayLength() == 1 && String(owners[0], "emailAddress").Equals(email, StringComparison.OrdinalIgnoreCase);

    private async Task<CloudExistingFilesPage> BrowseMicrosoftAsync(CloudAuthorization auth, string search, CancellationToken ct)
    {
        var driveId = await MicrosoftDriveIdAsync(auth, ct);
        var endpoint = search.Length == 0 ? GraphApi + "/me/drive/root/children"
            : GraphApi + "/me/drive/root/search(q='" + Esc(search.Replace("'", "''")) + "')";
        var url = endpoint + "?$select=" + Esc("id,name,webUrl,file,folder,remoteItem,parentReference")
            + "&$top=" + BrowsePageSize;
        using var result = await JsonAsync(HttpMethod.Get, url, auth.AccessToken, null, ct);
        var root = result.RootElement;
        if (!root.TryGetProperty("value", out var items) || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > BrowsePageSize)
            throw new CloudSharingException("OneDrive returned an invalid file listing.");
        var files = new List<CloudExistingFile>();
        foreach (var item in items.EnumerateArray())
        {
            if (!OwnedMicrosoftFile(item, driveId)) continue;
            var candidate = new CloudExistingFile(AccountKind.Microsoft, String(item, "id"), String(item, "name"), String(item, "webUrl"), driveId);
            if (candidate.IsValid) files.Add(candidate);
        }
        return new(files, root.TryGetProperty("@odata.nextLink", out var next) && next.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(next.GetString()));
    }

    private async Task<string> MicrosoftDriveIdAsync(CloudAuthorization auth, CancellationToken ct)
    {
        using var drive = await JsonAsync(HttpMethod.Get, GraphApi + "/me/drive?$select=id,driveType", auth.AccessToken, null, ct);
        var driveId = String(drive.RootElement, "id");
        if (!CloudUploadedFile.SafeId(driveId) || String(drive.RootElement, "driveType") is not ("personal" or "business"))
            throw new CloudSharingException("This account has no verifiable default personal or business OneDrive.");
        return driveId;
    }

    private static bool OwnedMicrosoftFile(JsonElement item, string driveId) =>
        item.TryGetProperty("file", out var file) && file.ValueKind == JsonValueKind.Object
        && !item.TryGetProperty("remoteItem", out _) && !item.TryGetProperty("folder", out _)
        && item.TryGetProperty("parentReference", out var parent) && parent.ValueKind == JsonValueKind.Object
        && String(parent, "driveId") == driveId;

    public async Task<CloudUploadedFile> ShareExistingAsync(CloudExistingSharePlan plan, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var validated = CloudExistingSharePlan.Create(plan.Account, plan.File, plan.LinkTitle,
            string.Join(", ", plan.Recipients), "", "", plan.Access);
        var auth = await ExistingAuthorization().AuthorizeExistingAsync(validated.Account, ct);
        if (!auth.Email.Equals(validated.Account.Email, StringComparison.OrdinalIgnoreCase))
            throw new CloudSharingException("The cloud account changed; reconnect before sharing.");
        var file = validated.File;
        var grantingStarted = false;
        try
        {
            // The file may have moved or changed permissions since the user searched. Re-read its identity.
            if (file.Provider == AccountKind.Gmail)
            {
                using var metadata = await JsonAsync(HttpMethod.Get, GoogleApi + "/files/" + Esc(file.Id) + "?fields="
                    + Esc("id,name,mimeType,webViewLink,ownedByMe,trashed,capabilities(canShare),owners(emailAddress)"), auth.AccessToken, null, ct);
                if (!OwnedGoogleFile(metadata.RootElement, auth.Email) || !SameFile(metadata.RootElement, file))
                    throw new CloudSharingException("The Google Drive file changed or is not owned by this account. Search and choose it again.");
            }
            else
            {
                var drive = await MicrosoftDriveIdAsync(auth, ct);
                using var metadata = await JsonAsync(HttpMethod.Get, GraphApi + "/me/drive/items/" + Esc(file.Id) + "?$select="
                    + Esc("id,name,webUrl,file,folder,remoteItem,parentReference"), auth.AccessToken, null, ct);
                if (drive != file.DriveId || !OwnedMicrosoftFile(metadata.RootElement, drive)
                    || !SameFile(metadata.RootElement, file))
                    throw new CloudSharingException("The OneDrive file changed or left this account's own drive. Search and choose it again.");
                auth = await MicrosoftDriveOwnerAsync(auth, null, ct);
            }

            // Fail closed before changing anything if a file has public, inherited, group, or
            // unreviewed existing access. Do not remove or silently modify prior permissions.
            var grants = file.Provider == AccountKind.Gmail
                ? await ExistingGoogleGrantsAsync(file.Id, auth, validated.Recipients, validated.Access, ct)
                : await ExistingMicrosoftGrantsAsync(file.Id, auth, validated.Recipients, validated.Access, ct);
            foreach (var recipient in validated.Recipients.Where(x => !x.Equals(auth.Email, StringComparison.OrdinalIgnoreCase)
                         && !grants.ContainsKey(x)))
            {
                ct.ThrowIfCancellationRequested();
                grantingStarted = true;
                if (file.Provider == AccountKind.Gmail)
                {
                    using var result = await JsonAsync(HttpMethod.Post, GoogleApi + "/files/" + Esc(file.Id)
                        + "/permissions?sendNotificationEmail=false&fields=id,type,role,emailAddress", auth.AccessToken,
                        new { type = "user", role = validated.Access == CloudFileAccess.View ? "reader" : "writer", emailAddress = recipient }, ct);
                    var permission = result.RootElement;
                    var id = String(permission, "id");
                    if (!CloudUploadedFile.SafeId(id) || String(permission, "type") != "user"
                        || String(permission, "role") != (validated.Access == CloudFileAccess.View ? "reader" : "writer")
                        || !String(permission, "emailAddress").Equals(recipient, StringComparison.OrdinalIgnoreCase))
                        throw new CloudSharingException("Google Drive did not confirm the exact person and access requested.");
                    grants.Add(recipient, id);
                }
                else
                {
                    using var result = await JsonAsync(HttpMethod.Post, GraphApi + "/me/drive/items/" + Esc(file.Id)
                        + "/invite", auth.AccessToken,
                        new { recipients = new[] { new { email = recipient } }, requireSignIn = true,
                            sendInvitation = false, roles = new[] { validated.Access == CloudFileAccess.View ? "read" : "write" },
                            retainInheritedPermissions = true }, ct);
                    if (!result.RootElement.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Array
                        || value.GetArrayLength() != 1)
                        throw new CloudSharingException("OneDrive did not confirm the exact recipient.");
                    var permission = value[0];
                    var id = String(permission, "id");
                    if (!CloudUploadedFile.SafeId(id) || permission.TryGetProperty("error", out _) || HasUnsafeGrant(permission)
                        || !MicrosoftRecipientEmail(permission).Equals(recipient, StringComparison.OrdinalIgnoreCase)
                        || !HasOnlyRole(permission, validated.Access == CloudFileAccess.View ? "read" : "write"))
                        throw new CloudSharingException("OneDrive could not verify the exact person's access.");
                    grants.Add(recipient, id);
                }
            }
            var byId = grants.ToDictionary(x => x.Value, x => x.Key, StringComparer.Ordinal);
            if (file.Provider == AccountKind.Gmail) await CheckGooglePermissionsAsync(file.Id, auth, byId, validated.Access, ct);
            else await CheckMicrosoftPermissionsAsync(file.Id, auth, byId, validated.Access, ct, strictIdentity: true);
            ct.ThrowIfCancellationRequested();
            var receipt = new CloudUploadedFile
            {
                AccountId = validated.Account.Id, AccountEmail = validated.Account.Email, Provider = file.Provider,
                FileId = file.Id, ContainerId = file.DriveId, FileName = file.Name, LinkTitle = validated.LinkTitle,
                WebUrl = file.WebUrl, Recipients = validated.Recipients.ToArray(), Access = validated.Access,
                PermissionIds = grants, IsExisting = true,
            };
            if (!receipt.IsValid) throw new CloudSharingException("The existing file did not produce a safe link.");
            return receipt;
        }
        catch (Exception ex) when (grantingStarted)
        {
            // Never delete an existing file or silently undo a permission the user may rely on.
            // An aborted provider request may already have changed access; direct the user to review it.
            throw new CloudSharingException("Existing-file sharing may have changed access, but Magpie could not finish verifying it. "
                + "No link was inserted. Review named access in the cloud provider before retrying. " + ex.Message,
                cleanupRequired: true, recoveryUrl: file.WebUrl);
        }
    }

    private static bool SameFile(JsonElement item, CloudExistingFile file) => String(item, "id") == file.Id
        && String(item, "name") == file.Name
        && String(item, file.Provider == AccountKind.Gmail ? "webViewLink" : "webUrl") == file.WebUrl;

    private async Task<Dictionary<string, string>> ExistingGoogleGrantsAsync(string id, CloudAuthorization auth,
        IReadOnlyCollection<string> recipients, CloudFileAccess access, CancellationToken ct)
    {
        using var response = await JsonAsync(HttpMethod.Get, GoogleApi + "/files/" + Esc(id)
            + "/permissions?pageSize=100&fields=nextPageToken,permissions(id,type,role,emailAddress,deleted,permissionDetails(inherited))", auth.AccessToken, null, ct);
        var root = response.RootElement;
        if (root.TryGetProperty("nextPageToken", out _) || !root.TryGetProperty("permissions", out var permissions)
            || permissions.ValueKind != JsonValueKind.Array)
            throw new CloudSharingException("Google Drive could not review every existing permission. No changes were made.");
        var grants = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ownerFound = false;
        foreach (var permission in permissions.EnumerateArray())
        {
            if (permission.TryGetProperty("deleted", out var deleted) && deleted.ValueKind == JsonValueKind.True) continue;
            if (InheritedGoogleGrant(permission))
                throw new CloudSharingException("The existing Drive file has inherited access. Review it in Drive before sharing.");
            var address = String(permission, "emailAddress");
            if (String(permission, "type") == "user" && String(permission, "role") == "owner"
                && address.Equals(auth.Email, StringComparison.OrdinalIgnoreCase)) { ownerFound = true; continue; }
            if (!recipients.Contains(address, StringComparer.OrdinalIgnoreCase)
                || String(permission, "type") != "user"
                || String(permission, "role") != (access == CloudFileAccess.View ? "reader" : "writer")
                || !CloudUploadedFile.SafeId(String(permission, "id"))
                || !grants.TryAdd(address, String(permission, "id")))
                throw new CloudSharingException("Existing Google Drive access includes unreviewed people, groups, or roles. "
                    + "Magpie will not change existing permissions; review them in Drive first.");
        }
        if (!ownerFound) throw new CloudSharingException("Drive did not confirm the file owner. No changes were made.");
        return grants;
    }

    private async Task<Dictionary<string, string>> ExistingMicrosoftGrantsAsync(string id, CloudAuthorization auth,
        IReadOnlyCollection<string> recipients, CloudFileAccess access, CancellationToken ct)
    {
        using var response = await JsonAsync(HttpMethod.Get, GraphApi + "/me/drive/items/" + Esc(id) + "/permissions",
            auth.AccessToken, null, ct);
        var root = response.RootElement;
        if (root.TryGetProperty("@odata.nextLink", out _) || !root.TryGetProperty("value", out var permissions)
            || permissions.ValueKind != JsonValueKind.Array)
            throw new CloudSharingException("OneDrive could not review every existing permission. No changes were made.");
        var grants = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ownerFound = false;
        foreach (var permission in permissions.EnumerateArray())
        {
            if (!HasUnsafeGrant(permission) && BelongsOnlyToOwner(permission, auth)) { ownerFound = true; continue; }
            var address = MicrosoftRecipientEmail(permission);
            if (HasUnsafeGrant(permission) || !recipients.Contains(address, StringComparer.OrdinalIgnoreCase)
                || !HasOnlyRole(permission, access == CloudFileAccess.View ? "read" : "write")
                || !CloudUploadedFile.SafeId(String(permission, "id")) || !grants.TryAdd(address, String(permission, "id")))
                throw new CloudSharingException("Existing OneDrive access is public, inherited, or not limited to the reviewed people. "
                    + "Magpie will not change existing permissions; review them in OneDrive first.");
        }
        if (!ownerFound) throw new CloudSharingException("OneDrive did not confirm the file owner. No changes were made.");
        return grants;
    }

    private static string MicrosoftRecipientEmail(JsonElement permission)
    {
        var addresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (permission.TryGetProperty("invitation", out var invitation) && invitation.ValueKind == JsonValueKind.Object)
        {
            var email = String(invitation, "email");
            if (email.Length > 0) addresses.Add(email);
        }
        foreach (var property in new[] { "grantedToV2", "grantedTo" })
            if (permission.TryGetProperty(property, out var identity)) AddAddress(identity, addresses);
        foreach (var property in new[] { "grantedToIdentitiesV2", "grantedToIdentities" })
            if (permission.TryGetProperty(property, out var identities) && identities.ValueKind == JsonValueKind.Array)
                foreach (var identity in identities.EnumerateArray()) AddAddress(identity, addresses);
        return addresses.Count == 1 ? addresses.Single() : "";
    }

    private static void AddAddress(JsonElement identity, HashSet<string> addresses)
    {
        if (identity.ValueKind != JsonValueKind.Object) return;
        if (identity.TryGetProperty("user", out var user))
        {
            var email = String(user, "email");
            if (email.Length > 0) addresses.Add(email);
        }
        if (identity.TryGetProperty("siteUser", out var siteUser))
        {
            var email = String(siteUser, "email");
            if (email.Length > 0) addresses.Add(email);
        }
    }
}
