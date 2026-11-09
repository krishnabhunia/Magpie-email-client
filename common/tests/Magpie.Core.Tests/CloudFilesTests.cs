using System.Net;
using System.Text;
using System.Text.Json;
using Magpie.Core.Auth;
using Magpie.Core.CloudFiles;
using Magpie.Core.Mail;
using Magpie.Core.Models;
using Magpie.Core.Security;
using Magpie.Core.Storage;
using MimeKit;

namespace Magpie.Core.Tests;

public class CloudFilesTests
{
    private static Account Account(AccountKind kind = AccountKind.Gmail) => new()
    {
        Id = "cloud-account", Kind = kind, Email = "owner@example.test", Enabled = false,
    };

    private static CloudSharePlan Plan(AccountKind kind = AccountKind.Gmail, CloudFileAccess access = CloudFileAccess.View,
        string recipients = "Alex <alex@example.test>, ben@example.test", long length = 4) =>
        CloudSharePlan.Create(Account(kind), "report.txt", length, recipients, "", "", access);

    internal static CloudUploadedFile Receipt(AccountKind kind = AccountKind.Gmail) => new()
    {
        AccountId = "cloud-account", AccountEmail = "owner@example.test", Provider = kind, FileId = "new-file",
        ContainerId = kind == AccountKind.Microsoft ? "new-folder" : "", FileName = "report.txt",
        WebUrl = kind == AccountKind.Gmail ? "https://drive.google.com/file/d/new-file/view" : "https://onedrive.live.com/?id=new-file",
        Recipients = new[] { "alex@example.test", "ben@example.test" },
        PermissionIds = new() { ["alex@example.test"] = "grant1", ["ben@example.test"] = "grant2" },
    };

    private sealed class Authorization : ICloudFileAuthorization
    {
        public int Calls;
        public bool Fail;
        public Task<CloudAuthorization> AuthorizeAsync(Account account, CancellationToken ct)
        {
            Calls++;
            ct.ThrowIfCancellationRequested();
            if (Fail) throw new ReauthRequiredException("Cloud consent removed");
            return Task.FromResult(new CloudAuthorization("test-cloud-token", account.Email, "owner-id"));
        }
    }

    private sealed record RequestRecord(string Method, Uri Uri, string? Authorization, byte[] Body, string? ContentType)
    {
        public string Text => Encoding.UTF8.GetString(Body);
    }

    private sealed class ProviderHttp(AccountKind provider) : HttpMessageHandler
    {
        public List<RequestRecord> Requests { get; } = new();
        public Dictionary<string, string> Grants { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string Role { get; set; } = provider == AccountKind.Gmail ? "reader" : "read";
        public string SessionUrl { get; set; } = "https://www.googleapis.com/upload/drive/v3/files/new-file?upload_id=test-session";
        public bool BroadAccess { get; set; }
        public bool FailCleanup { get; set; }
        public bool OmitGrantedPermission { get; set; }
        public bool ConflictCreate { get; set; }
        public bool OmitFileLink { get; set; }
        public bool WrongRecipient { get; set; }
        public int FailGrantAt { get; set; }
        public Action? DuringUpload { get; set; }
        public Action? AfterGrant { get; set; }
        public int UploadedBytes { get; private set; }
        public string FolderName { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var bytes = request.Content == null ? Array.Empty<byte>() : await request.Content.ReadAsByteArrayAsync(ct);
            var record = new RequestRecord(request.Method.Method, request.RequestUri!, request.Headers.Authorization?.ToString(), bytes,
                request.Content?.Headers.ContentType?.MediaType);
            Requests.Add(record);
            Assert.Equal("Bearer test-cloud-token", record.Authorization);
            if (request.Method == HttpMethod.Delete)
                return new(FailCleanup ? HttpStatusCode.Forbidden : HttpStatusCode.NoContent);
            return provider == AccountKind.Gmail ? Google(record) : Microsoft(record);
        }

        private HttpResponseMessage Google(RequestRecord r)
        {
            var path = r.Uri.AbsolutePath;
            if (path.EndsWith("/generateIds")) return Json(new { ids = new[] { "new-file" } });
            if (r.Method == "POST" && path == "/drive/v3/files")
            {
                if (ConflictCreate) return new(HttpStatusCode.Conflict);
                using var body = JsonDocument.Parse(r.Text);
                Assert.Equal("new-file", body.RootElement.GetProperty("id").GetString());
                Assert.False(body.RootElement.GetProperty("writersCanShare").GetBoolean());
                Assert.False(body.RootElement.TryGetProperty("parents", out _));
                return Json(new { id = "new-file", webViewLink = Receipt().WebUrl });
            }
            if (r.Method == "PATCH" && path.StartsWith("/upload/"))
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK);
                response.Headers.Location = new Uri(SessionUrl);
                return response;
            }
            if (r.Method == "PUT" && path.StartsWith("/upload/"))
            {
                UploadedBytes = r.Body.Length;
                DuringUpload?.Invoke();
                return Json(new { id = "new-file", size = r.Body.Length.ToString(), webViewLink = Receipt().WebUrl });
            }
            if (r.Method == "POST" && path.EndsWith("/permissions"))
            {
                if (FailGrantAt == Grants.Count + 1) return new(HttpStatusCode.Forbidden);
                using var body = JsonDocument.Parse(r.Text);
                var email = body.RootElement.GetProperty("emailAddress").GetString()!;
                var id = "grant" + (Grants.Count + 1);
                Grants.Add(email, id);
                Assert.Contains("sendNotificationEmail=false", r.Uri.Query);
                Assert.Equal("user", body.RootElement.GetProperty("type").GetString());
                Assert.Equal(Role, body.RootElement.GetProperty("role").GetString());
                AfterGrant?.Invoke();
                return Json(new { id, type = "user", role = Role, emailAddress = WrongRecipient ? "wrong@example.test" : email });
            }
            if (r.Method == "GET" && path.EndsWith("/permissions"))
            {
                var permissions = new List<object> { new { id = "owner", type = "user", role = "owner", emailAddress = "owner@example.test" } };
                if (!OmitGrantedPermission) permissions.AddRange(Grants.Select(x => (object)new { id = x.Value, type = "user", role = Role, emailAddress = WrongRecipient ? "wrong@example.test" : x.Key }));
                if (BroadAccess) permissions.Add(new { id = "public", type = "anyone", role = "reader" });
                return Json(new { permissions });
            }
            throw new InvalidOperationException("Unexpected fake Google call " + r.Method + " " + path);
        }

        private HttpResponseMessage Microsoft(RequestRecord r)
        {
            var path = r.Uri.AbsolutePath;
            if (r.Method == "GET" && path == "/v1.0/me/drive") return Json(new
            {
                webUrl = "https://onedrive.live.com/?id=root", driveType = "personal",
                owner = new { user = new { id = "drive-owner-different-from-directory" } },
            });
            if (r.Method == "POST" && path.EndsWith("/root/children"))
            {
                if (ConflictCreate) return new(HttpStatusCode.Conflict);
                using var body = JsonDocument.Parse(r.Text);
                FolderName = body.RootElement.GetProperty("name").GetString()!;
                Assert.StartsWith("Magpie upload ", FolderName);
                Assert.Equal("fail", body.RootElement.GetProperty("@microsoft.graph.conflictBehavior").GetString());
                return Json(new { id = "new-folder", name = FolderName, folder = new { }, webUrl = "https://onedrive.live.com/?id=new-folder" });
            }
            if (r.Method == "PUT" && path.EndsWith(":/content"))
            {
                Assert.Contains("/items/new-folder:/report.txt:/content", path);
                Assert.Contains("conflictBehavior=fail", r.Uri.Query);
                Assert.Equal("application/octet-stream", r.ContentType);
                UploadedBytes = r.Body.Length;
                DuringUpload?.Invoke();
                return Json(new { id = "new-file", name = "report.txt", size = r.Body.Length, webUrl = OmitFileLink ? "" : Receipt(AccountKind.Microsoft).WebUrl });
            }
            if (r.Method == "POST" && path.EndsWith("/invite"))
            {
                if (FailGrantAt == Grants.Count + 1) return new(HttpStatusCode.Forbidden);
                using var body = JsonDocument.Parse(r.Text);
                var root = body.RootElement;
                Assert.True(root.GetProperty("requireSignIn").GetBoolean());
                Assert.False(root.GetProperty("sendInvitation").GetBoolean());
                Assert.True(root.GetProperty("retainInheritedPermissions").GetBoolean());
                Assert.Equal(Role, root.GetProperty("roles")[0].GetString());
                var email = root.GetProperty("recipients")[0].GetProperty("email").GetString()!;
                var id = "grant" + (Grants.Count + 1);
                Grants.Add(email, id);
                AfterGrant?.Invoke();
                return Json(new { value = new[] { new { id, roles = new[] { Role }, invitation = new { email = WrongRecipient ? "wrong@example.test" : email } } } });
            }
            if (r.Method == "GET" && path.EndsWith("/permissions"))
            {
                var value = new List<object> { new { id = "owner", roles = new[] { "owner" }, grantedToV2 = new { user = new { id = "drive-owner-different-from-directory" } } } };
                if (!OmitGrantedPermission) value.AddRange(Grants.Select(x => (object)new { id = x.Value, roles = new[] { Role }, invitation = new { email = WrongRecipient ? "wrong@example.test" : x.Key } }));
                if (BroadAccess) value.Add(new { id = "public", roles = new[] { "read" }, link = new { scope = "anonymous" } });
                return Json(new { value });
            }
            throw new InvalidOperationException("Unexpected fake OneDrive call " + r.Method + " " + path);
        }
    }

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json"),
    };

    [Theory]
    [InlineData(AccountKind.Gmail, CloudFileAccess.View)]
    [InlineData(AccountKind.Gmail, CloudFileAccess.Edit)]
    [InlineData(AccountKind.Microsoft, CloudFileAccess.View)]
    [InlineData(AccountKind.Microsoft, CloudFileAccess.Edit)]
    public async Task Real_provider_request_flow_uploads_exact_bytes_and_grants_only_named_access_without_notifications(AccountKind kind, CloudFileAccess access)
    {
        var handler = new ProviderHttp(kind) { Role = kind == AccountKind.Gmail ? access == CloudFileAccess.View ? "reader" : "writer" : access == CloudFileAccess.View ? "read" : "write" };
        using var http = new HttpClient(handler);
        var auth = new Authorization();
        using var service = new CloudFilesService(http, auth);
        using var input = new MemoryStream(new byte[] { 0, 42, 13, 255 });
        var result = await service.UploadAsync(Plan(kind, access), input);
        Assert.True(result.IsValid);
        Assert.Equal(access, result.Access);
        Assert.Equal(4, handler.UploadedBytes);
        var upload = handler.Requests.Single(x => x.Method == "PUT");
        Assert.Equal(new byte[] { 0, 42, 13, 255 }, upload.Body);
        Assert.Equal(new[] { "alex@example.test", "ben@example.test" }, result.Recipients);
        Assert.Equal(2, result.PermissionIds.Count);
        Assert.DoesNotContain(handler.Requests, x => x.Method == "DELETE" || x.Uri.AbsolutePath.EndsWith("createLink"));
        Assert.DoesNotContain("test-cloud-token", JsonSerializer.Serialize(result));
        Assert.DoesNotContain("upload_id", JsonSerializer.Serialize(result));
        await service.VerifyAccessAsync(Account(kind), result);
        Assert.Equal(2, auth.Calls);
    }

    [Theory]
    [InlineData(AccountKind.Gmail)]
    [InlineData(AccountKind.Microsoft)]
    public async Task Partial_permission_failure_removes_only_the_fresh_resource(AccountKind kind)
    {
        var handler = new ProviderHttp(kind) { FailGrantAt = 2 };
        using var http = new HttpClient(handler);
        using var service = new CloudFilesService(http, new Authorization());
        using var input = new MemoryStream(new byte[4]);
        var ex = await Assert.ThrowsAsync<CloudSharingException>(() => service.UploadAsync(Plan(kind), input));
        Assert.False(ex.CleanupRequired);
        Assert.Single(handler.Grants);
        var removed = Assert.Single(handler.Requests, x => x.Method == "DELETE");
        Assert.EndsWith(kind == AccountKind.Gmail ? "/files/new-file" : "/items/new-folder", removed.Uri.AbsolutePath);
    }

    [Theory]
    [InlineData(AccountKind.Gmail)]
    [InlineData(AccountKind.Microsoft)]
    public async Task Cancellation_after_upload_deletes_new_resource_using_independent_cleanup_token(AccountKind kind)
    {
        using var cancel = new CancellationTokenSource();
        var handler = new ProviderHttp(kind) { DuringUpload = cancel.Cancel };
        using var http = new HttpClient(handler);
        using var service = new CloudFilesService(http, new Authorization());
        using var input = new MemoryStream(new byte[4]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.UploadAsync(Plan(kind), input, ct: cancel.Token));
        Assert.Single(handler.Requests, x => x.Method == "DELETE");
    }

    [Theory]
    [InlineData(AccountKind.Gmail)]
    [InlineData(AccountKind.Microsoft)]
    public async Task Cleanup_failure_is_explicit_and_exposes_only_safe_provider_location(AccountKind kind)
    {
        var handler = new ProviderHttp(kind) { FailGrantAt = 2, FailCleanup = true };
        using var http = new HttpClient(handler);
        using var service = new CloudFilesService(http, new Authorization());
        using var input = new MemoryStream(new byte[4]);
        var ex = await Assert.ThrowsAsync<CloudSharingException>(() => service.UploadAsync(Plan(kind), input));
        Assert.True(ex.CleanupRequired);
        Assert.True(CloudUploadedFile.IsProviderWebUrl(kind, ex.RecoveryUrl));
        Assert.DoesNotContain("test-cloud-token", ex.Message + ex.RecoveryUrl);
        Assert.DoesNotContain("upload_id", ex.Message + ex.RecoveryUrl);
    }

    [Theory]
    [InlineData(AccountKind.Gmail)]
    [InlineData(AccountKind.Microsoft)]
    public async Task Inherited_or_public_access_blocks_file_bytes_and_never_changes_default_permissions(AccountKind kind)
    {
        var handler = new ProviderHttp(kind) { BroadAccess = true };
        using var http = new HttpClient(handler);
        using var service = new CloudFilesService(http, new Authorization());
        using var input = new MemoryStream(new byte[4]);
        await Assert.ThrowsAsync<CloudSharingException>(() => service.UploadAsync(Plan(kind), input));
        Assert.Equal(0, handler.UploadedBytes);
        Assert.Empty(handler.Grants);
        Assert.DoesNotContain(handler.Requests, x => x.Method == "PATCH" || x.Method == "PUT");
        Assert.Single(handler.Requests, x => x.Method == "DELETE");
    }

    [Fact]
    public async Task Google_upload_session_cannot_redirect_content_or_tokens_to_another_host()
    {
        var handler = new ProviderHttp(AccountKind.Gmail) { SessionUrl = "https://attacker.example/upload" };
        using var http = new HttpClient(handler);
        using var service = new CloudFilesService(http, new Authorization());
        using var input = new MemoryStream(new byte[4]);
        await Assert.ThrowsAsync<CloudSharingException>(() => service.UploadAsync(Plan(), input));
        Assert.DoesNotContain(handler.Requests, x => x.Uri.Host == "attacker.example" || x.Method == "PUT");
        Assert.Single(handler.Requests, x => x.Method == "DELETE");
    }

    [Theory]
    [InlineData(AccountKind.Gmail)]
    [InlineData(AccountKind.Microsoft)]
    public async Task An_explicit_create_conflict_never_deletes_a_preexisting_item(AccountKind kind)
    {
        var handler = new ProviderHttp(kind) { ConflictCreate = true };
        using var http = new HttpClient(handler);
        using var service = new CloudFilesService(http, new Authorization());
        using var input = new MemoryStream(new byte[4]);
        await Assert.ThrowsAsync<CloudSharingException>(() => service.UploadAsync(Plan(kind), input));
        Assert.DoesNotContain(handler.Requests, x => x.Method == "DELETE");
    }

    [Theory]
    [InlineData(AccountKind.Gmail)]
    [InlineData(AccountKind.Microsoft)]
    public async Task Final_and_presend_checks_detect_missing_or_revoked_named_permissions(AccountKind kind)
    {
        var handler = new ProviderHttp(kind);
        using var http = new HttpClient(handler);
        using var service = new CloudFilesService(http, new Authorization());
        using var input = new MemoryStream(new byte[4]);
        var receipt = await service.UploadAsync(Plan(kind), input);
        handler.OmitGrantedPermission = true;
        await Assert.ThrowsAsync<CloudSharingException>(() => service.VerifyAccessAsync(Account(kind), receipt));
        Assert.DoesNotContain(handler.Requests, x => x.Method == "DELETE"); // a check never mutates an existing successful share
    }

    private sealed class ChangedLengthStream(byte[] bytes, long reportedLength) : MemoryStream(bytes)
    {
        public override long Length => reportedLength;
    }

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    public async Task Truncated_or_growing_source_is_rejected_without_leaving_a_cloud_copy(int actualLength)
    {
        var handler = new ProviderHttp(AccountKind.Gmail);
        using var http = new HttpClient(handler);
        using var service = new CloudFilesService(http, new Authorization());
        using var input = new ChangedLengthStream(new byte[actualLength], 4);
        await Assert.ThrowsAsync<CloudSharingException>(() => service.UploadAsync(Plan(), input));
        Assert.Single(handler.Requests, x => x.Method == "DELETE");
        Assert.Empty(handler.Grants);
    }

    [Fact]
    public void Confirmation_validates_bcc_privacy_names_recipients_file_size_and_provider()
    {
        Assert.Throws<InvalidOperationException>(() => CloudSharePlan.Create(Account(), "report.txt", 4, "alex@example.test", "", "hidden@example.test", CloudFileAccess.View));
        Assert.Equal(new[] { "alex@example.test" }, CloudSharePlan.RecipientsFor("alex@example.test", "alex@example.test", "alex@example.test"));
        Assert.Throws<InvalidOperationException>(() => CloudSharePlan.Create(Account(), "../escape.txt", 4, "alex@example.test", "", "", CloudFileAccess.View));
        Assert.Throws<InvalidOperationException>(() => Plan(length: 0));
        Assert.Throws<InvalidOperationException>(() => Plan(length: CloudSharePlan.MaximumBytes + 1));
        Assert.Throws<InvalidOperationException>(() => Plan(recipients: "not a mailbox"));
        Assert.Throws<InvalidOperationException>(() => Plan(AccountKind.Imap));
        Assert.False(CloudUploadedFile.IsProviderWebUrl(AccountKind.Gmail, "https://drive.google.com.attacker.example/file"));
        Assert.False(CloudUploadedFile.IsProviderWebUrl(AccountKind.Microsoft, "https://user:password@tenant.sharepoint.com/file"));
    }

    [Fact]
    public async Task Local_receipts_survive_draft_reopen_and_revision_restore_without_leaking_into_mime_and_gate_delayed_send()
    {
        using var dir = new TempDir();
        var sent = false;
        using var engine = new MailEngine(new AppPaths(dir.Path), new FakeProtector(), sendMessage: (_, _, _) => { sent = true; return Task.CompletedTask; });
        engine.Config.Accounts.Add(Account());
        var receipt = Receipt();
        engine.Store.RememberCloudFile(receipt);
        var draft = new Draft { AccountId = Account().Id, To = "alex@example.test", Subject = "Cloud attachment", Html = "<p><a href=\"" + receipt.WebUrl + "\">report.txt</a></p>" };
        var local = engine.SaveLocalDraft(draft, false);
        var reopened = engine.OpenLocalDraft(local)!;
        Assert.Equal(receipt.WebUrl, Assert.Single(engine.Store.CloudFilesIn(reopened.Html)).WebUrl);
        var revision = engine.Store.GetDraftRevisions(draft.AccountId, draft.MessageId).Single();
        var restored = engine.Store.OpenDraftRevision(draft.AccountId, draft.MessageId, revision.Id)!;
        Assert.Single(engine.Store.CloudFilesIn(restored.Html));
        using var mime = Composer.Build(reopened, Account());
        var serialized = Encoding.UTF8.GetString(Composer.ToBytes(mime));
        Assert.DoesNotContain("PermissionIds", serialized);
        Assert.DoesNotContain("ben@example.test", serialized);
        reopened.To = "new-person@example.test";
        Assert.Throws<InvalidOperationException>(() => engine.QueueSend(reopened, DateTimeOffset.Now, null));
        reopened.To = "alex@example.test"; reopened.Bcc = "hidden@example.test";
        Assert.Throws<InvalidOperationException>(() => engine.QueueSend(reopened, DateTimeOffset.Now, null));
        reopened.Bcc = "";
        engine.QueueSend(reopened, DateTimeOffset.Now.AddSeconds(-1), null);
        // There is deliberately no cloud credential in this fixture. The delayed outbox must stop before SMTP.
        await engine.ProcessOutboxAsync(CancellationToken.None);
        Assert.False(sent);
        Assert.Equal(OutboxStatus.Failed, engine.Store.GetOutbox().Single().Status);
        var reopenedStore = new MailStore(engine.Paths.Database);
        Assert.Equal(receipt.WebUrl, Assert.Single(reopenedStore.CloudFilesIn(reopened.Html)).WebUrl);
    }

    [Fact]
    public void Cloud_oauth_scopes_and_protected_keys_are_separate_from_mail()
    {
        using var dir = new TempDir();
        var vault = new SecretVault(dir.File("secrets.json"), new FakeProtector());
        using var http = new HttpClient(new FakeHttp());
        var cloud = new CloudFilesAuthorization(http, vault, kind => kind == AccountKind.Gmail ? OAuthService.Google("client", "secret") : OAuthService.Microsoft("client"));
        Assert.Contains(CloudFilesAuthorization.GoogleScope, cloud.ConfigFor(AccountKind.Gmail)!.Scopes);
        Assert.DoesNotContain("mail.google.com", cloud.ConfigFor(AccountKind.Gmail)!.Scopes);
        Assert.Contains("Files.ReadWrite", cloud.ConfigFor(AccountKind.Microsoft)!.Scopes);
        Assert.DoesNotContain("outlook.office.com", cloud.ConfigFor(AccountKind.Microsoft)!.Scopes);
        Assert.DoesNotContain("Files.ReadWrite.All", cloud.ConfigFor(AccountKind.Microsoft)!.Scopes);
        Assert.NotEqual(SecretVault.RefreshKey("A"), CloudFilesAuthorization.RefreshKey("A"));
        Assert.NotEqual(CloudFilesAuthorization.RefreshKey("A"), CloudFilesAuthorization.ExistingRefreshKey("A"));
        Assert.Contains(CloudFilesAuthorization.GoogleExistingScope, cloud.ExistingConfigFor(AccountKind.Gmail)!.Scopes);
        Assert.DoesNotContain(CloudFilesAuthorization.GoogleExistingScope, cloud.ConfigFor(AccountKind.Gmail)!.Scopes);
        Assert.Throws<ReauthRequiredException>(() => CloudFilesAuthorization.EnsureExistingScopes(AccountKind.Gmail, CloudFilesAuthorization.GoogleScope + " openid email"));
        CloudFilesAuthorization.EnsureExistingScopes(AccountKind.Gmail, CloudFilesAuthorization.GoogleExistingScope + " openid email");
        Assert.Throws<ReauthRequiredException>(() => CloudFilesAuthorization.EnsureScopes(AccountKind.Gmail, "openid email"));
        Assert.Throws<ReauthRequiredException>(() => CloudFilesAuthorization.EnsureScopes(AccountKind.Microsoft, "Files.ReadWrite"));
    }

    [Theory]
    [InlineData(AccountKind.Gmail)]
    [InlineData(AccountKind.Microsoft)]
    public async Task Provider_cannot_silently_grant_or_verify_a_different_recipient(AccountKind kind)
    {
        var handler = new ProviderHttp(kind) { WrongRecipient = true };
        using var http = new HttpClient(handler);
        using var service = new CloudFilesService(http, new Authorization());
        using var bytes = new MemoryStream(new byte[] { 1, 2, 3, 4 });
        await Assert.ThrowsAsync<CloudSharingException>(() => service.UploadAsync(Plan(kind), bytes));
        Assert.Single(handler.Requests, x => x.Method == "DELETE");
        handler.Grants["alex@example.test"] = "grant1";
        handler.Grants["ben@example.test"] = "grant2";
        await Assert.ThrowsAsync<CloudSharingException>(() => service.VerifyAccessAsync(Account(kind), Receipt(kind)));
    }

    [Fact]
    public async Task Missing_uploaded_file_url_never_falls_back_to_a_folder_or_drive_link()
    {
        var handler = new ProviderHttp(AccountKind.Microsoft) { OmitFileLink = true };
        using var http = new HttpClient(handler);
        using var service = new CloudFilesService(http, new Authorization());
        using var bytes = new MemoryStream(new byte[] { 1, 2, 3, 4 });
        await Assert.ThrowsAsync<CloudSharingException>(() => service.UploadAsync(Plan(AccountKind.Microsoft), bytes));
        Assert.Empty(handler.Grants);
        Assert.Single(handler.Requests, x => x.Method == "DELETE");
    }

    [Fact]
    public async Task Cleanup_after_lost_authorization_explicitly_reports_the_remaining_copy()
    {
        using var http = new HttpClient(new ProviderHttp(AccountKind.Gmail));
        using var service = new CloudFilesService(http, new Authorization { Fail = true });
        var ex = await Assert.ThrowsAsync<CloudSharingException>(() => service.RemoveNewFileAsync(Account(), Receipt()));
        Assert.True(ex.CleanupRequired);
        Assert.Equal(Receipt().WebUrl, ex.RecoveryUrl);
    }

    private static async Task SimulateBrowserRedirectAsync(string url)
    {
        var query = System.Web.HttpUtility.ParseQueryString(new Uri(url).Query);
        Assert.Equal("S256", query["code_challenge_method"]);
        using var browser = new HttpClient();
        await browser.GetStringAsync(query["redirect_uri"] + "?code=CLOUD-CODE&state=" + query["state"]);
    }

    [Fact]
    public async Task Existing_file_consent_has_independent_vault_token_and_does_not_expand_upload_or_mail_scopes()
    {
        using var dir = new TempDir();
        var vault = new SecretVault(dir.File("secrets.json"), new FakeProtector());
        vault.Set(SecretVault.RefreshKey("cloud-account"), "keep-mail-token");
        using var http = new HttpClient(new FakeHttp
        {
            Respond = (request, _) => request.RequestUri!.Host == "oauth2.googleapis.com"
                ? Json(new { access_token = "existing-token", refresh_token = "separate-existing-refresh", expires_in = 3600,
                    scope = CloudFilesAuthorization.GoogleExistingScope + " openid email" })
                : Json(new { sub = "owner-id", email = "owner@example.test", email_verified = true }),
        });
        var cloud = new CloudFilesAuthorization(http, vault, _ => OAuthService.Google("client", "secret"));
        string? loginUrl = null;
        var signIn = cloud.SignInExistingAsync(Account(), url => loginUrl = url, CancellationToken.None);
        Assert.NotNull(loginUrl);
        Assert.Contains(Uri.EscapeDataString(CloudFilesAuthorization.GoogleExistingScope), loginUrl);
        await SimulateBrowserRedirectAsync(loginUrl!);
        await signIn;
        Assert.True(cloud.IsExistingConnected(Account()));
        Assert.False(cloud.IsConnected(Account()));
        Assert.Equal("keep-mail-token", vault.Get(SecretVault.RefreshKey("cloud-account")));
        Assert.Null(vault.Get(CloudFilesAuthorization.RefreshKey("cloud-account")));
        Assert.Equal("separate-existing-refresh", vault.Get(CloudFilesAuthorization.ExistingRefreshKey("cloud-account")));
        Assert.DoesNotContain("separate-existing-refresh", File.ReadAllText(dir.File("secrets.json")));
        Assert.Equal("owner@example.test", (await cloud.AuthorizeExistingAsync(Account(), CancellationToken.None)).Email);
        await Assert.ThrowsAsync<ReauthRequiredException>(() => cloud.AuthorizeAsync(Account(), CancellationToken.None));
    }

    [Fact]
    public async Task Explicit_cloud_consent_refresh_and_account_binding_preserve_existing_mail_tokens()
    {
        using var dir = new TempDir();
        var vault = new SecretVault(dir.File("secrets.json"), new FakeProtector());
        vault.Set(SecretVault.RefreshKey("cloud-account"), "mail-refresh-kept");
        var refreshed = false;
        var wrongIdentity = false;
        var handler = new FakeHttp
        {
            Respond = (request, body) => request.RequestUri!.Host == "oauth2.googleapis.com"
                ? Json(new { access_token = "cloud-access", refresh_token = "cloud-refresh", expires_in = 3600,
                    scope = refreshed ? "" : CloudFilesAuthorization.GoogleScope + " openid email" })
                : Json(new { sub = "owner-id", email = wrongIdentity ? "wrong@example.test" : "owner@example.test", email_verified = true }),
        };
        using var http = new HttpClient(handler);
        var clientId = "desktop-client";
        OAuthConfig Config(AccountKind _) => OAuthService.Google(clientId, "desktop-secret");
        var cloud = new CloudFilesAuthorization(http, vault, Config);
        string? url = null;
        var signIn = cloud.SignInAsync(Account(), value => url = value, CancellationToken.None);
        Assert.NotNull(url);
        await SimulateBrowserRedirectAsync(url!);
        await signIn;
        Assert.True(cloud.IsConnected(Account()));
        Assert.Equal("mail-refresh-kept", vault.Get(SecretVault.RefreshKey("cloud-account")));
        Assert.Equal("cloud-refresh", vault.Get(CloudFilesAuthorization.RefreshKey("cloud-account")));
        Assert.DoesNotContain("cloud-refresh", File.ReadAllText(dir.File("secrets.json")));
        refreshed = true;
        var restarted = new CloudFilesAuthorization(http, vault, Config);
        Assert.Equal("owner@example.test", (await restarted.AuthorizeAsync(Account(), CancellationToken.None)).Email);
        Assert.Contains(handler.Requests, r => r.body.Contains("refresh_token=cloud-refresh"));
        Assert.DoesNotContain(handler.Requests, r => r.body.Contains("mail-refresh-kept"));
        wrongIdentity = true;
        await Assert.ThrowsAsync<ReauthRequiredException>(() => restarted.AuthorizeAsync(Account(), CancellationToken.None));
        clientId = "different-client";
        var before = handler.Requests.Count;
        Assert.False(restarted.IsConnected(Account()));
        await Assert.ThrowsAsync<ReauthRequiredException>(() => restarted.AuthorizeAsync(Account(), CancellationToken.None));
        Assert.Equal(before, handler.Requests.Count);
        Assert.Equal("mail-refresh-kept", vault.Get(SecretVault.RefreshKey("cloud-account")));
    }

    [Fact]
    public async Task Removing_an_account_during_browser_consent_does_not_save_new_cloud_tokens()
    {
        using var dir = new TempDir();
        var vault = new SecretVault(dir.File("secrets.json"), new FakeProtector());
        vault.Set(SecretVault.RefreshKey("cloud-account"), "mail-refresh-kept");
        using var http = new HttpClient(new FakeHttp
        {
            Respond = (request, _) => request.RequestUri!.Host == "oauth2.googleapis.com"
                ? Json(new { access_token = "cloud-access", refresh_token = "cloud-refresh", expires_in = 3600,
                    scope = CloudFilesAuthorization.GoogleScope + " openid email" })
                : Json(new { sub = "owner-id", email = "owner@example.test", email_verified = true }),
        });
        Account? current = Account();
        var cloud = new CloudFilesAuthorization(http, vault, _ => OAuthService.Google("client", "secret"), _ => current);
        string? url = null;
        var signIn = cloud.SignInAsync(Account(), value => url = value, CancellationToken.None);
        Assert.NotNull(url);
        current = null;
        await SimulateBrowserRedirectAsync(url!);
        await Assert.ThrowsAsync<InvalidOperationException>(() => signIn);
        Assert.Null(vault.Get(CloudFilesAuthorization.RefreshKey("cloud-account")));
        Assert.Equal("mail-refresh-kept", vault.Get(SecretVault.RefreshKey("cloud-account")));
    }

    [Fact]
    public async Task A_rotated_refresh_cannot_restore_secrets_after_the_account_was_removed()
    {
        using var dir = new TempDir();
        var vault = new SecretVault(dir.File("secrets.json"), new FakeProtector());
        Account? current = Account();
        var removeDuringRefresh = false;
        using var http = new HttpClient(new FakeHttp
        {
            Respond = (request, _) =>
            {
                if (request.RequestUri!.Host != "oauth2.googleapis.com")
                    return Json(new { sub = "owner-id", email = "owner@example.test", email_verified = true });
                if (removeDuringRefresh)
                {
                    current = null;
                    vault.RemovePrefix("account:cloud-account:");
                }
                return Json(new { access_token = "cloud-access", refresh_token = removeDuringRefresh ? "rotated-refresh" : "cloud-refresh",
                    expires_in = 3600, scope = CloudFilesAuthorization.GoogleScope + " openid email" });
            },
        });
        OAuthConfig Config(AccountKind _) => OAuthService.Google("client", "secret");
        var cloud = new CloudFilesAuthorization(http, vault, Config, _ => current);
        string? url = null;
        var signIn = cloud.SignInAsync(Account(), value => url = value, CancellationToken.None);
        await SimulateBrowserRedirectAsync(url!);
        await signIn;
        Assert.NotNull(vault.Get(CloudFilesAuthorization.RefreshKey("cloud-account")));
        removeDuringRefresh = true;
        var restarted = new CloudFilesAuthorization(http, vault, Config, _ => current);
        await Assert.ThrowsAsync<ReauthRequiredException>(() => restarted.AuthorizeAsync(Account(), CancellationToken.None));
        Assert.Null(vault.Get(CloudFilesAuthorization.RefreshKey("cloud-account")));
    }

    private sealed class DelayedCloudRefresh : HttpMessageHandler
    {
        public bool BlockRefresh;
        public int Requests;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            if (request.RequestUri!.Host == "oauth2.googleapis.com")
            {
                var body = await request.Content!.ReadAsStringAsync(ct);
                if (BlockRefresh && body.Contains("grant_type=refresh_token"))
                {
                    Entered.TrySetResult();
                    try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
                    catch (OperationCanceledException)
                    {
                        Cancelled.TrySetResult();
                        await Release.Task;
                        throw;
                    }
                }
                return Json(new { access_token = "cloud-access", refresh_token = "cloud-refresh", expires_in = 3600,
                    scope = CloudFilesAuthorization.GoogleScope + " openid email" });
            }
            if (request.RequestUri.Host == "openidconnect.googleapis.com")
                return Json(new { sub = "owner-id", email = "owner@example.test", email_verified = true });
            throw new InvalidOperationException("Unexpected provider request in profile-stop fixture.");
        }
    }

    [Fact]
    public async Task Version_stop_cancels_and_drains_cloud_token_refresh_and_rejects_late_permission_checks()
    {
        using var dir = new TempDir();
        var handler = new DelayedCloudRefresh();
        using var engine = new MailEngine(new AppPaths(dir.Path), new FakeProtector(), handler);
        engine.Config.Accounts.Add(Account());
        engine.Config.GoogleClientId = "desktop-client";
        engine.Config.GoogleClientSecret = "desktop-secret";
        var auth = engine.CloudFiles.SignIn!;
        string? url = null;
        var signIn = auth.SignInAsync(Account(), value => url = value, CancellationToken.None);
        await SimulateBrowserRedirectAsync(url!);
        await signIn;
        auth.Forget(Account().Id); // The next permission check must actually refresh the protected cloud token.
        handler.BlockRefresh = true;
        engine.Store.RememberCloudFile(Receipt());
        var verify = engine.VerifyCloudFileAccessAsync(Receipt().WebUrl, "alex@example.test, ben@example.test", "", "");
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var stopped = engine.StopForVersionChangeAsync(timeout.Token);
        try
        {
            await handler.Cancelled.Task.WaitAsync(timeout.Token);
            Assert.False(stopped.IsCompleted);
            var calls = handler.Requests;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.VerifyCloudFileAccessAsync("", "", "", ""));
            Assert.Equal(calls, handler.Requests);
        }
        finally { handler.Release.TrySetResult(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => verify);
        await stopped;
        Assert.True(engine.IsStoppedForVersionChange);
        Assert.Equal("cloud-refresh", engine.Vault.Get(CloudFilesAuthorization.RefreshKey(Account().Id)));
    }
}
