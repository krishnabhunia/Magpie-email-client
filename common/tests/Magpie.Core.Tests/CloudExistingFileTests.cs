using System.Net;
using System.Text;
using System.Text.Json;
using Magpie.Core.CloudFiles;
using Magpie.Core.Models;
using Magpie.Core.Storage;

namespace Magpie.Core.Tests;

public class CloudExistingFileTests
{
    private static Account Account(AccountKind provider) => new()
    {
        Id = "existing-account", Kind = provider, Email = "owner@example.test",
    };

    private static CloudExistingFile File(AccountKind provider) => new(provider, "existing-file", "report.txt",
        provider == AccountKind.Gmail ? "https://drive.google.com/file/d/existing-file/view"
            : "https://onedrive.live.com/?id=existing-file", provider == AccountKind.Microsoft ? "owned-drive" : "");

    private static CloudExistingSharePlan Plan(AccountKind provider, string recipients = "alex@example.test, ben@example.test",
        CloudFileAccess role = CloudFileAccess.View) => CloudExistingSharePlan.Create(
        Account(provider), File(provider), "My reviewed file", recipients, "", "", role);

    private sealed class Auth : ICloudFileAuthorization, ICloudExistingFileAuthorization
    {
        public int ExistingCalls;
        public Task<CloudAuthorization> AuthorizeAsync(Account account, CancellationToken ct) =>
            throw new InvalidOperationException("Existing-file operations must never use private-copy upload credentials.");
        public Task<CloudAuthorization> AuthorizeExistingAsync(Account account, CancellationToken ct)
        {
            ExistingCalls++;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new CloudAuthorization("existing-browse-token", account.Email, "owner-id"));
        }
    }

    private sealed class Provider(AccountKind kind) : HttpMessageHandler
    {
        public List<(string Method, string Path, string Query, string Body)> Calls { get; } = new();
        public Dictionary<string, string> Grants { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool BroadAccess;
        public bool InheritedAccess;
        public bool WrongRecipient;
        public bool StaleFile;
        public bool NoOwner;
        public bool Pagination;
        public int FailGrantAt;
        public CancellationTokenSource? CancelAfterFirst;
        private string Role => kind == AccountKind.Gmail ? "reader" : "read";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var content = request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct);
            var path = request.RequestUri!.AbsolutePath;
            Calls.Add((request.Method.Method, path, request.RequestUri.Query, content));
            Assert.Equal("Bearer existing-browse-token", request.Headers.Authorization?.ToString());
            Assert.NotEqual(HttpMethod.Delete, request.Method);
            return kind == AccountKind.Gmail ? Google(request.Method.Method, path, content) : Microsoft(request.Method.Method, path, content);
        }

        private HttpResponseMessage Google(string method, string path, string body)
        {
            if (method == "GET" && path == "/drive/v3/files") return Json(new
            {
                files = new object[]
                {
                    GoogleFile(),
                    new { id = "shared-file", name = "shared.txt", mimeType = "text/plain", webViewLink = File(AccountKind.Gmail).WebUrl,
                        ownedByMe = false, trashed = false, capabilities = new { canShare = true }, owners = new[] { new { emailAddress = "someone@example.test" } } },
                    new { id = "folder", name = "folder", mimeType = "application/vnd.google-apps.folder", webViewLink = File(AccountKind.Gmail).WebUrl,
                        ownedByMe = true, trashed = false, capabilities = new { canShare = true }, owners = new[] { new { emailAddress = "owner@example.test" } } },
                    new { id = "shortcut", name = "shortcut.txt", mimeType = "application/vnd.google-apps.shortcut", webViewLink = File(AccountKind.Gmail).WebUrl,
                        ownedByMe = true, trashed = false, capabilities = new { canShare = true }, owners = new[] { new { emailAddress = "owner@example.test" } } },
                },
                nextPageToken = Pagination ? "more-items" : null,
            });
            if (method == "GET" && path == "/drive/v3/files/existing-file") return Json(GoogleFile());
            if (method == "GET" && path.EndsWith("/permissions"))
            {
                var grants = new List<object>();
                if (!NoOwner) grants.Add(new { id = "owner", type = "user", role = "owner", emailAddress = "owner@example.test" });
                grants.AddRange(Grants.Select(x => (object)new { id = x.Value, type = "user", role = Role,
                    emailAddress = WrongRecipient ? "wrong@example.test" : x.Key }));
                if (BroadAccess) grants.Add(new { id = "all", type = "anyone", role = "reader", emailAddress = "" });
                if (InheritedAccess) grants.Add(new { id = "inherited", type = "user", role = "reader",
                    emailAddress = "alex@example.test", permissionDetails = new[] { new { inherited = true } } });
                return Pagination ? Json(new { permissions = grants, nextPageToken = "next" })
                    : Json(new { permissions = grants });
            }
            if (method == "POST" && path.EndsWith("/permissions"))
            {
                if (FailGrantAt == Grants.Count + 1) return new(HttpStatusCode.Forbidden);
                using var json = JsonDocument.Parse(body);
                var recipient = json.RootElement.GetProperty("emailAddress").GetString()!;
                Assert.Equal("user", json.RootElement.GetProperty("type").GetString());
                Assert.Equal(Role, json.RootElement.GetProperty("role").GetString());
                var id = "grant" + (Grants.Count + 1);
                Grants.Add(recipient, id);
                if (Grants.Count == 1) CancelAfterFirst?.Cancel();
                return Json(new { id, type = "user", role = Role, emailAddress = WrongRecipient ? "wrong@example.test" : recipient });
            }
            throw new InvalidOperationException("Unexpected Google request " + method + " " + path);
        }

        private object GoogleFile() => new
        {
            id = "existing-file", name = StaleFile ? "renamed.txt" : "report.txt", mimeType = "text/plain",
            webViewLink = File(AccountKind.Gmail).WebUrl, ownedByMe = true, trashed = false,
            capabilities = new { canShare = true }, owners = new[] { new { emailAddress = "owner@example.test" } },
        };

        private HttpResponseMessage Microsoft(string method, string path, string body)
        {
            if (method == "GET" && path == "/v1.0/me/drive") return Json(new
            {
                id = "owned-drive", driveType = "personal", webUrl = "https://onedrive.live.com/?id=owned-drive",
                owner = new { user = new { id = "owner-id" } },
            });
            if (method == "GET" && path.EndsWith("/root/children") || method == "GET" && path.Contains("/root/search(")) return Json(new
            {
                value = new object[]
                {
                    MicrosoftFile(),
                    new { id = "foreign", name = "foreign.txt", webUrl = File(AccountKind.Microsoft).WebUrl,
                        file = new { }, parentReference = new { driveId = "other-drive" } },
                    new { id = "remote", name = "remote.txt", webUrl = File(AccountKind.Microsoft).WebUrl,
                        file = new { }, remoteItem = new { id = "remote" }, parentReference = new { driveId = "owned-drive" } },
                },
                next = "unused",
            });
            if (method == "GET" && path == "/v1.0/me/drive/items/existing-file") return Json(MicrosoftFile());
            if (method == "GET" && path.EndsWith("/permissions"))
            {
                var grants = new List<object>();
                if (!NoOwner) grants.Add(new { id = "owner", roles = new[] { "owner" }, grantedToV2 = new { user = new { id = "owner-id" } } });
                grants.AddRange(Grants.Select(x => (object)new { id = x.Value, roles = new[] { Role },
                    invitation = new { email = WrongRecipient ? "wrong@example.test" : x.Key } }));
                if (BroadAccess) grants.Add(new { id = "all", roles = new[] { "read" }, link = new { scope = "anonymous" } });
                if (InheritedAccess) grants.Add(new { id = "inherited", roles = new[] { "read" },
                    invitation = new { email = "alex@example.test" }, inheritedFrom = new { id = "shared-folder" } });
                return Pagination ? Json(new Dictionary<string, object> { ["value"] = grants, ["@odata.nextLink"] = "https://graph.microsoft.com/v1.0/me/drive/items/existing-file/permissions?$skiptoken=next" })
                    : Json(new { value = grants });
            }
            if (method == "POST" && path.EndsWith("/invite"))
            {
                if (FailGrantAt == Grants.Count + 1) return new(HttpStatusCode.Forbidden);
                using var json = JsonDocument.Parse(body);
                var root = json.RootElement;
                Assert.True(root.GetProperty("requireSignIn").GetBoolean());
                Assert.False(root.GetProperty("sendInvitation").GetBoolean());
                Assert.Equal(Role, root.GetProperty("roles")[0].GetString());
                var recipient = root.GetProperty("recipients")[0].GetProperty("email").GetString()!;
                var id = "grant" + (Grants.Count + 1);
                Grants.Add(recipient, id);
                if (Grants.Count == 1) CancelAfterFirst?.Cancel();
                return Json(new { value = new[] { new { id, roles = new[] { Role }, invitation = new { email = WrongRecipient ? "wrong@example.test" : recipient } } } });
            }
            throw new InvalidOperationException("Unexpected OneDrive request " + method + " " + path);
        }

        private object MicrosoftFile() => new
        {
            id = "existing-file", name = StaleFile ? "renamed.txt" : "report.txt",
            webUrl = File(AccountKind.Microsoft).WebUrl, file = new { },
            parentReference = new { driveId = "owned-drive" },
        };

        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json"),
        };
    }

    [Theory]
    [InlineData(AccountKind.Gmail)]
    [InlineData(AccountKind.Microsoft)]
    public async Task Browsing_returns_only_owned_files_and_never_mutates_anything(AccountKind kind)
    {
        var provider = new Provider(kind);
        var auth = new Auth();
        using var http = new HttpClient(provider);
        using var service = new CloudFilesService(http, auth);
        var files = await service.BrowseExistingAsync(Account(kind), "report");
        Assert.Equal(File(kind), Assert.Single(files.Files));
        Assert.False(files.HasMore);
        Assert.True(auth.ExistingCalls > 0);
        Assert.All(provider.Calls, request => Assert.Equal("GET", request.Method));
        Assert.Contains(provider.Calls, request => (request.Path + request.Query).Contains("report", StringComparison.Ordinal));
        var defaultPage = await service.BrowseExistingAsync(Account(kind));
        Assert.Single(defaultPage.Files);
    }

    [Theory]
    [InlineData(AccountKind.Gmail)]
    [InlineData(AccountKind.Microsoft)]
    public async Task Explicit_sharing_verifies_named_permissions_and_keeps_the_original_file(AccountKind kind)
    {
        var provider = new Provider(kind);
        using var http = new HttpClient(provider);
        using var service = new CloudFilesService(http, new Auth());
        var file = await service.ShareExistingAsync(Plan(kind));
        Assert.True(file.IsValid && file.IsExisting);
        Assert.Equal("My reviewed file", file.DisplayName);
        Assert.Equal("report.txt", file.FileName);
        Assert.Equal(2, file.PermissionIds.Count);
        Assert.Equal("grant1", provider.Grants["alex@example.test"]);
        Assert.Equal(2, provider.Calls.Count(x => x.Method == "POST"));
        await service.VerifyAccessAsync(Account(kind), file);
        Assert.DoesNotContain(provider.Calls, x => x.Method is "DELETE" or "PUT" or "PATCH");
        await Assert.ThrowsAsync<CloudSharingException>(() => service.RemoveNewFileAsync(Account(kind), file));
        Assert.DoesNotContain(provider.Calls, x => x.Method == "DELETE");
    }

    [Theory]
    [InlineData(AccountKind.Gmail)]
    [InlineData(AccountKind.Microsoft)]
    public async Task Already_shared_reviewed_people_are_not_invited_twice(AccountKind kind)
    {
        var provider = new Provider(kind);
        provider.Grants.Add("alex@example.test", "prior-grant");
        using var http = new HttpClient(provider);
        using var service = new CloudFilesService(http, new Auth());
        var file = await service.ShareExistingAsync(Plan(kind, "alex@example.test"));
        Assert.Equal("prior-grant", file.PermissionIds["alex@example.test"]);
        Assert.DoesNotContain(provider.Calls, x => x.Method == "POST");
    }

    [Theory]
    [InlineData(AccountKind.Gmail)]
    [InlineData(AccountKind.Microsoft)]
    public async Task Existing_public_or_unreviewed_access_blocks_changes(AccountKind kind)
    {
        foreach (var mode in new[] { "public", "unreviewed", "owner", "stale", "page" })
        {
            var provider = new Provider(kind)
            {
                BroadAccess = mode == "public", NoOwner = mode == "owner", StaleFile = mode == "stale", Pagination = mode == "page",
            };
            if (mode == "unreviewed") provider.Grants.Add("outsider@example.test", "old-grant");
            using var http = new HttpClient(provider);
            using var service = new CloudFilesService(http, new Auth());
            await Assert.ThrowsAsync<CloudSharingException>(() => service.ShareExistingAsync(Plan(kind)));
            Assert.DoesNotContain(provider.Calls, x => x.Method == "POST" || x.Method == "DELETE");
        }
    }

    [Theory]
    [InlineData(AccountKind.Gmail)]
    [InlineData(AccountKind.Microsoft)]
    public async Task Inherited_cloud_access_is_not_modified(AccountKind kind)
    {
        var provider = new Provider(kind) { InheritedAccess = true };
        using var http = new HttpClient(provider);
        using var service = new CloudFilesService(http, new Auth());
        await Assert.ThrowsAsync<CloudSharingException>(() => service.ShareExistingAsync(Plan(kind)));
        Assert.DoesNotContain(provider.Calls, x => x.Method == "POST");
    }

    [Theory]
    [InlineData(AccountKind.Gmail)]
    [InlineData(AccountKind.Microsoft)]
    public async Task Partial_grant_failure_reports_recovery_and_never_deletes_the_existing_file(AccountKind kind)
    {
        var provider = new Provider(kind) { FailGrantAt = 2 };
        using var http = new HttpClient(provider);
        using var service = new CloudFilesService(http, new Auth());
        var error = await Assert.ThrowsAsync<CloudSharingException>(() => service.ShareExistingAsync(Plan(kind)));
        Assert.True(error.CleanupRequired);
        Assert.Equal(File(kind).WebUrl, error.RecoveryUrl);
        Assert.Single(provider.Grants);
        Assert.DoesNotContain(provider.Calls, x => x.Method == "DELETE");
    }

    [Theory]
    [InlineData(AccountKind.Gmail)]
    [InlineData(AccountKind.Microsoft)]
    public async Task Cancel_after_grant_requires_manual_permission_review(AccountKind kind)
    {
        using var cancel = new CancellationTokenSource();
        var provider = new Provider(kind) { CancelAfterFirst = cancel };
        using var http = new HttpClient(provider);
        using var service = new CloudFilesService(http, new Auth());
        var error = await Assert.ThrowsAsync<CloudSharingException>(() => service.ShareExistingAsync(Plan(kind), cancel.Token));
        Assert.True(error.CleanupRequired);
        Assert.Equal(File(kind).WebUrl, error.RecoveryUrl);
    }

    [Fact]
    public void Invalid_existing_file_and_hidden_recipients_are_rejected_before_network()
    {
        Assert.Throws<InvalidOperationException>(() => CloudExistingSharePlan.Create(Account(AccountKind.Gmail),
            File(AccountKind.Gmail) with { WebUrl = "https://evil.example.test/file" }, "safe", "alex@example.test", "", "", CloudFileAccess.View));
        Assert.Throws<InvalidOperationException>(() => CloudExistingSharePlan.Create(Account(AccountKind.Gmail),
            File(AccountKind.Gmail), "safe", "alex@example.test", "", "hidden@example.test", CloudFileAccess.View));
        Assert.Throws<InvalidOperationException>(() => CloudExistingSharePlan.Create(Account(AccountKind.Gmail),
            File(AccountKind.Gmail), "unsafe\ncontrol", "alex@example.test", "", "", CloudFileAccess.View));
        Assert.True(CloudUploadedFile.SafeLinkTitle("Project Q4: A/B <reviewed>"));
    }

    [Fact]
    public void A_second_link_title_never_overwrites_or_deletes_an_existing_files_access_proof()
    {
        using var dir = new TempDir();
        var store = new MailStore(dir.File("cloud-existing.db"));
        var original = new CloudUploadedFile
        {
            AccountId = "existing-account", AccountEmail = "owner@example.test", Provider = AccountKind.Gmail,
            FileId = "existing-file", FileName = "report.txt", LinkTitle = "First label", IsExisting = true,
            WebUrl = File(AccountKind.Gmail).WebUrl, Recipients = new[] { "alex@example.test" },
            PermissionIds = new() { ["alex@example.test"] = "grant1" },
        };
        store.RememberCloudFile(original);
        store.RememberCloudFile(original with { LinkTitle = "A different reviewed label", CreatedAt = original.CreatedAt.AddMinutes(1) });
        var tracked = Assert.Single(store.CloudFilesIn(original.WebUrl));
        Assert.Equal("First label", tracked.DisplayName);
        Assert.Throws<InvalidOperationException>(() => store.RememberCloudFile(original with
        {
            Recipients = new[] { "outsider@example.test" }, PermissionIds = new() { ["outsider@example.test"] = "grant2" },
        }));
        Assert.Equal("grant1", Assert.Single(store.CloudFilesIn(original.WebUrl)).PermissionIds["alex@example.test"]);
    }
}
