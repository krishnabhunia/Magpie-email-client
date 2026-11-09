using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Magpie.Core.Models;

namespace Magpie.Core.CloudFiles;

/// <summary>
/// Upload a fresh private copy, check its initial access, and grant only the reviewed named recipients.
/// No existing file is replaced; no anonymous link, notification, or inherited-permission change is requested.
/// </summary>
public sealed partial class CloudFilesService : IDisposable
{
    private const string GoogleApi = "https://www.googleapis.com/drive/v3";
    private const string GraphApi = "https://graph.microsoft.com/v1.0";
    private readonly HttpClient _http;
    private readonly ICloudFileAuthorization _authorization;
    private readonly bool _ownsHttp;
    public CloudFilesAuthorization? SignIn => _authorization as CloudFilesAuthorization;

    public CloudFilesService(HttpClient http, ICloudFileAuthorization authorization, bool ownsHttp = false)
    {
        _http = http;
        _authorization = authorization;
        _ownsHttp = ownsHttp;
    }

    public async Task<CloudUploadedFile> UploadAsync(CloudSharePlan plan, Stream file,
        IProgress<CloudUploadProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(file);
        var validated = CloudSharePlan.Create(plan.Account, plan.FileName, plan.Length,
            string.Join(", ", plan.Recipients), "", "", plan.Access);
        if (!file.CanRead || !file.CanSeek || file.Length - file.Position != validated.Length)
            throw new InvalidOperationException("The selected file changed. Choose it again before uploading.");
        var account = validated.Account;
        progress?.Report(new("Checking cloud account"));
        var auth = await _authorization.AuthorizeAsync(account, ct);
        if (!auth.Email.Equals(account.Email, StringComparison.OrdinalIgnoreCase))
            throw new CloudSharingException("The cloud account changed. Connect the selected account again.");
        var state = new UploadState(account.Kind);
        try
        {
            var uploaded = account.Kind == AccountKind.Gmail
                ? await UploadGoogleAsync(validated, file, auth, state, progress, ct)
                : await UploadMicrosoftAsync(validated, file, auth, state, progress, ct);
            ct.ThrowIfCancellationRequested();
            return uploaded;
        }
        catch (Exception ex)
        {
            // The user's cancellation must not cancel cleanup of the new resource created by that operation.
            var cleaned = await CleanupAsync(state, auth.AccessToken);
            if (!cleaned)
                throw new CloudSharingException("The cloud operation did not finish, and its new copy could not be removed. "
                    + "Open the cloud location below and remove the new Magpie upload or review its access before retrying.", true, state.RecoveryUrl);
            if (ex is OperationCanceledException) throw;
            if (ex is CloudSharingException) throw;
            if (ex is IOException)
                throw new CloudSharingException("The selected file could not be read completely. Any new cloud copy was removed; choose the file again.");
            throw new CloudSharingException("The cloud service did not complete the upload or sharing request. Any new cloud copy was removed. Check the connection and try again.");
        }
    }

    public async Task RemoveNewFileAsync(Account account, CloudUploadedFile file, CancellationToken ct = default)
    {
        if (file.IsExisting)
            throw new CloudSharingException("This is an existing cloud file. Magpie will never delete it as upload cleanup. Review its permissions in your cloud provider.",
                true, file.WebUrl);
        if (!file.IsValid || file.AccountId != account.Id || file.Provider != account.Kind
            || !file.AccountEmail.Equals(account.Email, StringComparison.OrdinalIgnoreCase))
            throw new CloudSharingException("The cloud file's account cannot be verified; remove the new copy in your cloud app.", true, file.WebUrl);
        using var cleanup = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cleanup.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var auth = await _authorization.AuthorizeAsync(account, cleanup.Token);
            var state = new UploadState(file.Provider)
            {
                DeleteUrl = file.Provider == AccountKind.Gmail ? GoogleApi + "/files/" + Esc(file.FileId)
                    : GraphApi + "/me/drive/items/" + Esc(file.ContainerId),
                RecoveryUrl = file.WebUrl,
            };
            if (await CleanupAsync(state, auth.AccessToken, cleanup.Token)) return;
        }
        catch { /* Report the remaining remote copy even when fresh authorization is no longer available. */ }
        throw new CloudSharingException("The new cloud copy could not be removed. Open its location and review or remove it before retrying.", true, file.WebUrl);
    }

    public async Task VerifyAccessAsync(Account account, CloudUploadedFile file, CancellationToken ct = default)
    {
        if (!file.IsValid || file.AccountId != account.Id || file.Provider != account.Kind
            || !file.AccountEmail.Equals(account.Email, StringComparison.OrdinalIgnoreCase))
            throw new CloudSharingException("The account used to share " + file.FileName + " is no longer available. Reconnect it or attach the file normally.");
        var auth = file.IsExisting ? await ExistingAuthorization().AuthorizeExistingAsync(account, ct)
            : await _authorization.AuthorizeAsync(account, ct);
        var grants = file.PermissionIds.ToDictionary(x => x.Value, x => x.Key, StringComparer.Ordinal);
        if (file.Provider == AccountKind.Gmail) await CheckGooglePermissionsAsync(file.FileId, auth, grants, file.Access, ct);
        else
        {
            auth = await MicrosoftDriveOwnerAsync(auth, null, ct);
            await CheckMicrosoftPermissionsAsync(file.FileId, auth, grants, file.Access, ct, file.IsExisting);
        }
    }

    private async Task<CloudUploadedFile> UploadGoogleAsync(CloudSharePlan plan, Stream file, CloudAuthorization auth,
        UploadState state, IProgress<CloudUploadProgress>? progress, CancellationToken ct)
    {
        using var generated = await JsonAsync(HttpMethod.Get, GoogleApi + "/files/generateIds?count=1&space=drive&type=files", auth.AccessToken, null, ct);
        var ids = generated.RootElement.GetProperty("ids");
        var id = ids.GetArrayLength() == 1 ? ids[0].GetString() : null;
        if (!CloudUploadedFile.SafeId(id)) throw new CloudSharingException("Google Drive did not provide a valid new-file identifier.");
        state.DeleteUrl = GoogleApi + "/files/" + Esc(id!);
        state.RecoveryUrl = "https://drive.google.com/file/d/" + Esc(id!) + "/view";
        using (var create = Request(HttpMethod.Post, GoogleApi + "/files?fields=id,webViewLink", auth.AccessToken,
            JsonContent(new { id, name = plan.FileName, mimeType = "application/octet-stream", writersCanShare = false })))
        using (var response = await _http.SendAsync(create, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            // A conflicting generated ID is not authority to delete an existing resource.
            if (response.StatusCode == HttpStatusCode.Conflict) state.DeleteUrl = null;
            CheckResponse(response);
            using var created = await ReadJsonAsync(response, ct);
            if (String(created.RootElement, "id") != id) throw new CloudSharingException("Google Drive returned a different file identity.");
            SetWebUrl(state, String(created.RootElement, "webViewLink"));
        }
        await CheckGooglePermissionsAsync(id!, auth, new Dictionary<string, string>(), plan.Access, ct);

        progress?.Report(new("Uploading file", 0, plan.Length));
        string session;
        using (var start = Request(HttpMethod.Patch,
            "https://www.googleapis.com/upload/drive/v3/files/" + Esc(id!) + "?uploadType=resumable&fields=id,webViewLink,size",
            auth.AccessToken, JsonContent(new { })))
        {
            start.Headers.TryAddWithoutValidation("X-Upload-Content-Type", "application/octet-stream");
            start.Headers.TryAddWithoutValidation("X-Upload-Content-Length", plan.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
            using var response = await _http.SendAsync(start, HttpCompletionOption.ResponseHeadersRead, ct);
            CheckResponse(response);
            session = response.Headers.Location?.AbsoluteUri ?? "";
            if (!Uri.TryCreate(session, UriKind.Absolute, out var uri) || uri.Scheme != "https"
                || uri.IdnHost != "www.googleapis.com" || !uri.IsDefaultPort || uri.UserInfo.Length != 0
                || !uri.AbsolutePath.StartsWith("/upload/drive/v3/files/", StringComparison.Ordinal))
                throw new CloudSharingException("Google Drive returned an unexpected upload location.");
        }
        using (var upload = Request(HttpMethod.Put, session, auth.AccessToken, new BoundedFileContent(file, plan.Length, progress)))
        using (var response = await _http.SendAsync(upload, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            CheckResponse(response);
            using var uploaded = await ReadJsonAsync(response, ct);
            if (String(uploaded.RootElement, "id") != id || Size(uploaded.RootElement) != plan.Length)
                throw new CloudSharingException("Google Drive did not confirm the complete file size and identity.");
            SetWebUrl(state, String(uploaded.RootElement, "webViewLink"));
        }

        var granted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var recipient in plan.Recipients.Where(x => !x.Equals(auth.Email, StringComparison.OrdinalIgnoreCase)))
        {
            progress?.Report(new("Granting named-recipient access", plan.Length, plan.Length));
            using var permission = await JsonAsync(HttpMethod.Post,
                GoogleApi + "/files/" + Esc(id!) + "/permissions?sendNotificationEmail=false&fields=id,type,role,emailAddress",
                auth.AccessToken, new { type = "user", role = plan.Access == CloudFileAccess.View ? "reader" : "writer", emailAddress = recipient }, ct);
            var root = permission.RootElement;
            var permissionId = String(root, "id");
            if (!CloudUploadedFile.SafeId(permissionId) || String(root, "type") != "user"
                || String(root, "role") != (plan.Access == CloudFileAccess.View ? "reader" : "writer")
                || !String(root, "emailAddress").Equals(recipient, StringComparison.OrdinalIgnoreCase))
                throw new CloudSharingException("Google Drive did not confirm the exact person and access requested.");
            granted.Add(recipient, permissionId);
        }
        await CheckGooglePermissionsAsync(id!, auth, granted.ToDictionary(x => x.Value, x => x.Key), plan.Access, ct);
        return Receipt(plan, id!, "", state.RecoveryUrl!, granted);
    }

    private async Task<CloudUploadedFile> UploadMicrosoftAsync(CloudSharePlan plan, Stream file, CloudAuthorization auth,
        UploadState state, IProgress<CloudUploadProgress>? progress, CancellationToken ct)
    {
        // A fresh per-upload folder preserves the original filename and gives cleanup an identity even if the
        // file-upload response is lost. Never upload into an existing or publicly shared folder.
        auth = await MicrosoftDriveOwnerAsync(auth, state, ct);
        var folderName = "Magpie upload " + Guid.NewGuid().ToString("N");
        state.DeleteUrl = GraphApi + "/me/drive/root:/" + Esc(folderName);
        string folderId;
        using (var request = Request(HttpMethod.Post, GraphApi + "/me/drive/root/children", auth.AccessToken,
            JsonContent(new Dictionary<string, object> { ["name"] = folderName, ["folder"] = new { }, ["@microsoft.graph.conflictBehavior"] = "fail" })))
        using (var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            if (response.StatusCode == HttpStatusCode.Conflict) state.DeleteUrl = null;
            CheckResponse(response);
            using var folder = await ReadJsonAsync(response, ct);
            folderId = String(folder.RootElement, "id");
            if (!CloudUploadedFile.SafeId(folderId) || String(folder.RootElement, "name") != folderName
                || !folder.RootElement.TryGetProperty("folder", out _))
                throw new CloudSharingException("OneDrive did not confirm the new private folder.");
            state.DeleteUrl = GraphApi + "/me/drive/items/" + Esc(folderId);
            SetWebUrl(state, String(folder.RootElement, "webUrl"));
        }
        await CheckMicrosoftPermissionsAsync(folderId, auth, new Dictionary<string, string>(), plan.Access, ct);
        progress?.Report(new("Uploading file", 0, plan.Length));
        string id;
        using (var request = Request(HttpMethod.Put,
            GraphApi + "/me/drive/items/" + Esc(folderId) + ":/" + Esc(plan.FileName) + ":/content?@microsoft.graph.conflictBehavior=fail",
            auth.AccessToken, new BoundedFileContent(file, plan.Length, progress)))
        using (var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            CheckResponse(response);
            using var uploaded = await ReadJsonAsync(response, ct);
            id = String(uploaded.RootElement, "id");
            if (!CloudUploadedFile.SafeId(id) || Size(uploaded.RootElement) != plan.Length)
                throw new CloudSharingException("OneDrive did not confirm the complete file size and identity.");
            var webUrl = String(uploaded.RootElement, "webUrl");
            if (!CloudUploadedFile.IsProviderWebUrl(AccountKind.Microsoft, webUrl))
                throw new CloudSharingException("OneDrive did not return a safe link to the uploaded file. No link was inserted.");
            state.RecoveryUrl = webUrl;
        }
        await CheckMicrosoftPermissionsAsync(id, auth, new Dictionary<string, string>(), plan.Access, ct);
        var granted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var recipient in plan.Recipients.Where(x => !x.Equals(auth.Email, StringComparison.OrdinalIgnoreCase)))
        {
            progress?.Report(new("Granting named-recipient access", plan.Length, plan.Length));
            using var result = await JsonAsync(HttpMethod.Post, GraphApi + "/me/drive/items/" + Esc(id) + "/invite", auth.AccessToken,
                new { recipients = new[] { new { email = recipient } }, requireSignIn = true, sendInvitation = false,
                    roles = new[] { plan.Access == CloudFileAccess.View ? "read" : "write" }, retainInheritedPermissions = true }, ct);
            if (!result.RootElement.TryGetProperty("value", out var permissions) || permissions.ValueKind != JsonValueKind.Array || permissions.GetArrayLength() != 1)
                throw new CloudSharingException("OneDrive did not confirm access for the requested person.");
            var permission = permissions[0];
            var permissionId = String(permission, "id");
            if (!CloudUploadedFile.SafeId(permissionId) || permission.TryGetProperty("error", out _)
                || HasUnsafeGrant(permission) || HasDifferentRecipient(permission, recipient)
                || !HasOnlyRole(permission, plan.Access == CloudFileAccess.View ? "read" : "write"))
                throw new CloudSharingException("OneDrive could not grant exactly the requested access. Your organization may restrict sharing with this recipient.");
            granted.Add(recipient, permissionId);
        }
        await CheckMicrosoftPermissionsAsync(id, auth, granted.ToDictionary(x => x.Value, x => x.Key), plan.Access, ct);
        return Receipt(plan, id, folderId, state.RecoveryUrl!, granted);
    }

    private async Task<CloudAuthorization> MicrosoftDriveOwnerAsync(CloudAuthorization auth, UploadState? state, CancellationToken ct)
    {
        // A personal OneDrive's owner ID can differ from /me's directory ID. Bind permission inspection to
        // the signed-in user's default drive, never a supplied shared-drive or organization-library ID.
        using var drive = await JsonAsync(HttpMethod.Get, GraphApi + "/me/drive?$select=owner,driveType,webUrl", auth.AccessToken, null, ct);
        var root = drive.RootElement;
        if (String(root, "driveType") is not ("personal" or "business"))
            throw new CloudSharingException("The account does not expose a personal OneDrive location. Attach the file normally or choose another account.");
        if (state != null) SetWebUrl(state, String(root, "webUrl"));
        if (root.TryGetProperty("owner", out var owner) && owner.TryGetProperty("user", out var user))
        {
            var ownerId = String(user, "id");
            if (!string.IsNullOrWhiteSpace(ownerId)) return auth with { DriveOwnerId = ownerId };
        }
        return auth;
    }

    private async Task CheckGooglePermissionsAsync(string id, CloudAuthorization auth, IReadOnlyDictionary<string, string> granted,
        CloudFileAccess access, CancellationToken ct)
    {
        using var response = await JsonAsync(HttpMethod.Get, GoogleApi + "/files/" + Esc(id)
            + "/permissions?pageSize=100&fields=nextPageToken,permissions(id,type,role,emailAddress,deleted)", auth.AccessToken, null, ct);
        var root = response.RootElement;
        if (root.TryGetProperty("nextPageToken", out _) || !root.TryGetProperty("permissions", out var permissions) || permissions.ValueKind != JsonValueKind.Array)
            throw new CloudSharingException("Google Drive could not confirm all permissions on the new file.");
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var permission in permissions.EnumerateArray())
        {
            if (permission.TryGetProperty("deleted", out var deleted) && deleted.ValueKind == JsonValueKind.True) continue;
            var permissionId = String(permission, "id");
            var owner = String(permission, "type") == "user" && String(permission, "role") == "owner"
                && String(permission, "emailAddress").Equals(auth.Email, StringComparison.OrdinalIgnoreCase);
            if (owner) continue;
            if (!granted.TryGetValue(permissionId, out var recipient) || String(permission, "type") != "user"
                || !String(permission, "emailAddress").Equals(recipient, StringComparison.OrdinalIgnoreCase)
                || String(permission, "role") != (access == CloudFileAccess.View ? "reader" : "writer"))
                throw new CloudSharingException("The new cloud file has additional or inherited access that Magpie cannot safely confirm. Use a private cloud location or attach the file normally.");
            found.Add(permissionId);
        }
        if (!found.SetEquals(granted.Keys)) throw new CloudSharingException("Google Drive has not confirmed all requested recipients. No link was inserted.");
    }

    private async Task CheckMicrosoftPermissionsAsync(string id, CloudAuthorization auth, IReadOnlyDictionary<string, string> granted,
        CloudFileAccess access, CancellationToken ct, bool strictIdentity = false)
    {
        using var response = await JsonAsync(HttpMethod.Get, GraphApi + "/me/drive/items/" + Esc(id) + "/permissions", auth.AccessToken, null, ct);
        var root = response.RootElement;
        if (root.TryGetProperty("@odata.nextLink", out _) || !root.TryGetProperty("value", out var permissions) || permissions.ValueKind != JsonValueKind.Array)
            throw new CloudSharingException("OneDrive could not confirm all permissions on the new file.");
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var permission in permissions.EnumerateArray())
        {
            var permissionId = String(permission, "id");
            if (!HasUnsafeGrant(permission) && BelongsOnlyToOwner(permission, auth)) continue;
            if (HasUnsafeGrant(permission) || !granted.TryGetValue(permissionId, out var recipient) || HasDifferentRecipient(permission, recipient)
                || strictIdentity && !MicrosoftRecipientEmail(permission).Equals(recipient, StringComparison.OrdinalIgnoreCase)
                || !HasOnlyRole(permission, access == CloudFileAccess.View ? "read" : "write"))
                throw new CloudSharingException("The new OneDrive file inherits access beyond the reviewed people. Magpie will not change your existing permissions; attach the file normally or use a private account.");
            found.Add(permissionId);
        }
        if (!found.SetEquals(granted.Keys)) throw new CloudSharingException("OneDrive has not confirmed all requested recipients. No link was inserted.");
    }

    private static bool HasUnsafeGrant(JsonElement permission) => permission.TryGetProperty("link", out _)
        || permission.TryGetProperty("inheritedFrom", out var inherited) && inherited.ValueKind != JsonValueKind.Null
        || ContainsProperty(permission, "group") || ContainsProperty(permission, "siteGroup");

    // Graph sometimes returns only an opaque identity ID. The result of our single-recipient invite binds
    // that ID; when email identities are supplied, all must still match the explicitly reviewed person.
    private static bool HasDifferentRecipient(JsonElement element, string recipient) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().Any(x => x.Name == "email"
            && x.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(x.Value.GetString())
            && !recipient.Equals(x.Value.GetString(), StringComparison.OrdinalIgnoreCase)
            || HasDifferentRecipient(x.Value, recipient)),
        JsonValueKind.Array => element.EnumerateArray().Any(x => HasDifferentRecipient(x, recipient)),
        _ => false,
    };

    private static bool ContainsProperty(JsonElement element, string name) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().Any(x => x.Name == name || ContainsProperty(x.Value, name)),
        JsonValueKind.Array => element.EnumerateArray().Any(x => ContainsProperty(x, name)),
        _ => false,
    };

    private static bool BelongsOnlyToOwner(JsonElement permission, CloudAuthorization auth)
    {
        var identities = new List<JsonElement>();
        foreach (var name in new[] { "grantedToV2", "grantedTo" })
            if (permission.TryGetProperty(name, out var identity) && identity.ValueKind == JsonValueKind.Object) identities.Add(identity);
        foreach (var name in new[] { "grantedToIdentitiesV2", "grantedToIdentities" })
            if (permission.TryGetProperty(name, out var items) && items.ValueKind == JsonValueKind.Array) identities.AddRange(items.EnumerateArray());
        return identities.Count > 0 && identities.All(identity =>
            identity.TryGetProperty("user", out var user)
                && (String(user, "id") == auth.UserId || String(user, "id") == auth.DriveOwnerId
                    || String(user, "email").Equals(auth.Email, StringComparison.OrdinalIgnoreCase))
            || identity.TryGetProperty("siteUser", out var siteUser)
                && String(siteUser, "loginName").Split('|').Last().Equals(auth.Email, StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasOnlyRole(JsonElement permission, string role) => permission.TryGetProperty("roles", out var roles)
        && roles.ValueKind == JsonValueKind.Array && roles.GetArrayLength() == 1 && roles[0].GetString() == role;

    private static CloudUploadedFile Receipt(CloudSharePlan plan, string fileId, string containerId, string url, Dictionary<string, string> grants)
    {
        var receipt = new CloudUploadedFile
        {
            AccountId = plan.Account.Id, AccountEmail = plan.Account.Email, Provider = plan.Account.Kind,
            FileId = fileId, ContainerId = containerId, FileName = plan.FileName, WebUrl = url,
            Recipients = plan.Recipients.ToArray(), PermissionIds = grants, Access = plan.Access,
        };
        if (!receipt.IsValid) throw new CloudSharingException("The provider did not return a safe file link. No link was inserted.");
        return receipt;
    }

    private static void SetWebUrl(UploadState state, string candidate)
    {
        if (CloudUploadedFile.IsProviderWebUrl(state.Provider, candidate)) state.RecoveryUrl = candidate;
    }

    private async Task<bool> CleanupAsync(UploadState state, string token, CancellationToken ct = default)
    {
        if (state.DeleteUrl == null) return true;
        using var cleanup = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cleanup.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using var request = Request(HttpMethod.Delete, state.DeleteUrl, token);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cleanup.Token);
            return response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound;
        }
        catch { return false; }
    }

    private async Task<JsonDocument> JsonAsync(HttpMethod method, string url, string token, object? body, CancellationToken ct)
    {
        using var request = Request(method, url, token, body == null ? null : JsonContent(body));
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        CheckResponse(response);
        return await ReadJsonAsync(response, ct);
    }

    private static void CheckResponse(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        var status = (int)response.StatusCode;
        throw new CloudSharingException(status switch
        {
            401 => "Cloud sign-in expired or was revoked. Connect the cloud account again.",
            403 => "The provider refused this operation. Check cloud permission, storage quota, API availability, and your organization's sharing policy.",
            409 => "The provider reported a name or file conflict. No existing item will be overwritten.",
            429 => "The cloud provider is temporarily limiting requests. Wait and try again.",
            _ => "The cloud provider did not complete the request (HTTP " + status + "). No sharing fallback will be applied.",
        });
    }

    internal static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        const int maximum = 256 * 1024;
        if (response.Content.Headers.ContentLength > maximum) throw new CloudSharingException("The provider response exceeded its safety limit.");
        using var input = await response.Content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await input.ReadAsync(buffer, ct)) > 0)
        {
            if (output.Length + count > maximum) throw new CloudSharingException("The provider response exceeded its safety limit.");
            output.Write(buffer, 0, count);
        }
        return JsonDocument.Parse(output.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
    }

    private static HttpRequestMessage Request(HttpMethod method, string url, string token, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, url) { Content = content };
        if (content != null && content.Headers.ContentType == null)
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }
    private static StringContent JsonContent(object value) => new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");
    private static string Esc(string value) => Uri.EscapeDataString(value);
    private static string String(JsonElement value, string field) => value.TryGetProperty(field, out var child) && child.ValueKind == JsonValueKind.String ? child.GetString() ?? "" : "";
    private static long Size(JsonElement value) => value.TryGetProperty("size", out var size)
        ? size.ValueKind == JsonValueKind.Number && size.TryGetInt64(out var number) ? number
            : size.ValueKind == JsonValueKind.String && long.TryParse(size.GetString(), out var parsed) ? parsed : -1 : -1;

    private sealed class UploadState(AccountKind provider)
    {
        public AccountKind Provider { get; } = provider;
        public string? DeleteUrl { get; set; }
        public string? RecoveryUrl { get; set; }
    }

    /// <summary>Streams a fixed-length file with an 80 KB buffer, rejecting truncation/growth and supporting cancellation.</summary>
    private sealed class BoundedFileContent(Stream file, long length, IProgress<CloudUploadProgress>? progress) : HttpContent
    {
        protected override bool TryComputeLength(out long computed) { computed = length; return true; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => CopyAsync(stream, CancellationToken.None);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct) => CopyAsync(stream, ct);
        private async Task CopyAsync(Stream output, CancellationToken ct)
        {
            var buffer = new byte[81920];
            long copied = 0;
            while (copied < length)
            {
                var count = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length - copied)), ct);
                if (count == 0) throw new IOException("The selected file was truncated during upload.");
                await output.WriteAsync(buffer.AsMemory(0, count), ct);
                copied += count;
                progress?.Report(new("Uploading file", copied, length));
            }
            if (await file.ReadAsync(buffer.AsMemory(0, 1), ct) != 0) throw new IOException("The selected file grew during upload.");
        }
    }

    public void Dispose() { if (_ownsHttp) _http.Dispose(); }
}
