using System.Net;
using System.Security.Cryptography;
using System.Text;
using Magpie.Core.Mail;
using Magpie.Core.Models;
using Magpie.Core.Settings;
using Magpie.Core.Updates;

namespace Magpie.Core.Tests;

/// <summary>1.1.0: updates from GitHub (U1), folder counts (C2), toolbar settings (C3), sync progress (S1).</summary>
public class Release110Tests
{
    // ── U1 · versions ──

    [Theory]
    [InlineData("1.1.0", "1.0.1", 1)]
    [InlineData("v1.1.0", "1.1.0", 0)]
    [InlineData("1.1.0", "1.1.0-beta.2", 1)]
    [InlineData("1.1.0-beta.10", "1.1.0-beta.2", 1)]
    [InlineData("1.1.0-beta", "1.1.0-alpha", 1)]
    [InlineData("1.2", "1.10.0", -1)]
    [InlineData("1.0.1+f12c023", "1.0.1", 0)]
    public void Versions_compare_like_semver(string a, string b, int sign) =>
        Assert.Equal(sign, Math.Sign(AppVersion.TryParse(a)!.CompareTo(AppVersion.TryParse(b))));

    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("1")]
    public void Bad_versions_are_rejected(string text) => Assert.Null(AppVersion.TryParse(text));

    private const string ReleasesJson = """
        [
          { "tag_name": "v1.2.0-beta.1", "prerelease": true, "draft": false, "body": "beta", "html_url": "https://github.com/x/releases/tag/v1.2.0-beta.1",
            "assets": [ { "name": "Magpie.exe", "browser_download_url": "https://dl/b/Magpie.exe", "size": 10 },
                        { "name": "Magpie.exe.sha256", "browser_download_url": "https://dl/b/Magpie.exe.sha256" } ] },
          { "tag_name": "v1.1.1", "draft": true, "assets": [ { "name": "Magpie.exe", "browser_download_url": "https://dl/d/Magpie.exe" }, { "name": "Magpie.exe.sha256", "browser_download_url": "https://dl/d/s" } ] },
          { "tag_name": "v1.1.0", "prerelease": false, "draft": false, "body": "## 1.1.0\n- Colourful", "html_url": "https://github.com/x/releases/tag/v1.1.0", "published_at": "2026-09-28T06:00:00Z",
            "assets": [ { "name": "Magpie.exe", "browser_download_url": "https://dl/a/Magpie.exe", "size": 78000000 },
                        { "name": "Magpie.exe.sha256", "browser_download_url": "https://dl/a/Magpie.exe.sha256" },
                        { "name": "Magpie-Setup-1.1.0.exe", "browser_download_url": "https://dl/a/setup.exe" } ] },
          { "tag_name": "v1.0.1", "assets": [] }
        ]
        """;

    [Fact]
    public void Releases_without_the_exe_and_drafts_are_ignored_and_betas_are_opt_in()
    {
        var all = UpdateClient.ParseReleases(ReleasesJson);
        Assert.Equal(new[] { "v1.2.0-beta.1", "v1.1.0" }, all.Select(r => r.Info.Tag));
        var stable = UpdateClient.PickNewest(all, includePrerelease: false)!;
        Assert.Equal("1.1.0", stable.Version.ToString());
        Assert.Equal("https://dl/a/Magpie.exe", stable.ExeUrl);
        Assert.Equal(78000000, stable.ExeSize);
        Assert.Contains("Colourful", stable.Notes);
        Assert.Equal("1.2.0-beta.1", UpdateClient.PickNewest(all, includePrerelease: true)!.Version.ToString());
    }

    [Theory]
    [InlineData("ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789\r\n")]
    [InlineData("﻿abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789  Magpie.exe")]
    public void Checksum_files_in_either_format_are_read(string text) =>
        Assert.Equal("abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789", UpdateClient.ParseSha256(text));

    private sealed class FakeGitHub : HttpMessageHandler
    {
        public readonly Dictionary<string, byte[]> Files = new();
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            var url = request.RequestUri!.ToString();
            if (url.Contains("api.github.com"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ReleasesJson) });
            return Task.FromResult(Files.TryGetValue(url, out var b)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(b) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    [Fact]
    public async Task Finds_the_newer_release_and_downloads_a_verified_exe()
    {
        using var dir = new TempDir();
        var exe = Encoding.ASCII.GetBytes("MZ pretend this is Magpie 1.1.0");
        var hub = new FakeGitHub();
        hub.Files["https://dl/a/Magpie.exe"] = exe;
        hub.Files["https://dl/a/Magpie.exe.sha256"] = Encoding.ASCII.GetBytes(Convert.ToHexString(SHA256.HashData(exe)) + "\r\n");
        var client = new UpdateClient(new HttpClient(hub));

        Assert.Null(await client.FindNewerAsync(AppVersion.TryParse("1.1.0")!, false, default));
        var rel = await client.FindNewerAsync(AppVersion.TryParse("1.0.1")!, false, default);
        Assert.NotNull(rel);

        var progress = new List<double>();
        var path = await client.DownloadAsync(rel!, dir.Path, new SyncProgress(progress), default);
        Assert.Equal(exe, File.ReadAllBytes(path));
        Assert.EndsWith("Magpie-1.1.0.exe", path);
        Assert.Equal(1, progress.Last());

        var before = hub.Calls;
        await client.DownloadAsync(rel!, dir.Path, null, default);      // already there and verified: only the checksum is fetched
        Assert.Equal(before + 1, hub.Calls);
    }

    [Fact]
    public async Task A_download_that_does_not_match_its_checksum_is_thrown_away()
    {
        using var dir = new TempDir();
        var hub = new FakeGitHub();
        hub.Files["https://dl/a/Magpie.exe"] = Encoding.ASCII.GetBytes("tampered");
        hub.Files["https://dl/a/Magpie.exe.sha256"] = Encoding.ASCII.GetBytes(new string('0', 64));
        var client = new UpdateClient(new HttpClient(hub));
        var rel = UpdateClient.PickNewest(UpdateClient.ParseReleases(ReleasesJson), false)!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.DownloadAsync(rel, dir.Path, null, default));
        Assert.Empty(Directory.GetFiles(dir.Path));
    }

    private sealed class SyncProgress : IProgress<double>
    {
        private readonly List<double> _into;
        public SyncProgress(List<double> into) { _into = into; }
        public void Report(double value) => _into.Add(value);
    }

    // ── C2 · counts ──

    [Theory]
    [InlineData(FolderRole.Inbox, CountKind.UnreadAndTotal)]
    [InlineData(FolderRole.Other, CountKind.UnreadAndTotal)]
    [InlineData(FolderRole.Junk, CountKind.UnreadAndTotal)]
    [InlineData(FolderRole.Archive, CountKind.UnreadAndTotal)]
    [InlineData(FolderRole.Drafts, CountKind.CountOnly)]
    [InlineData(FolderRole.Trash, CountKind.CountOnly)]
    [InlineData(FolderRole.Flagged, CountKind.CountOnly)]
    [InlineData(FolderRole.Sent, CountKind.None)]
    public void Each_folder_role_has_its_kind_of_number(FolderRole role, CountKind kind) => Assert.Equal(kind, FolderCounts.KindOf(role));

    [Theory]
    [InlineData(3, 10, CountKind.UnreadAndTotal, CountsMode.UnreadAndTotal, "3", " / 10", false)]
    [InlineData(0, 1240, CountKind.UnreadAndTotal, CountsMode.UnreadAndTotal, "0", " / 1,240", true)]
    [InlineData(0, 0, CountKind.UnreadAndTotal, CountsMode.UnreadAndTotal, "", "", false)]
    [InlineData(3, 10, CountKind.UnreadAndTotal, CountsMode.UnreadOnly, "3", "", false)]
    [InlineData(0, 10, CountKind.UnreadAndTotal, CountsMode.UnreadOnly, "", "", false)]
    [InlineData(2, 15, CountKind.CountOnly, CountsMode.UnreadAndTotal, "15", "", false)]
    [InlineData(2, 15, CountKind.CountOnly, CountsMode.Off, "", "", false)]
    [InlineData(5, 9, CountKind.None, CountsMode.UnreadAndTotal, "", "", false)]
    [InlineData(12480, 25999, CountKind.UnreadAndTotal, CountsMode.UnreadAndTotal, "12.4k", " / 25.9k", false)]
    public void Numbers_read_as_designed(int unread, int total, CountKind kind, CountsMode mode, string main, string rest, bool dim)
    {
        var t = FolderCounts.Display(unread, total, kind, mode);
        Assert.Equal(main, t.Main);
        Assert.Equal(rest, t.Rest);
        Assert.Equal(dim, t.Dim);
    }

    [Fact]
    public void Conversations_are_counted_once_per_folder_and_tag()
    {
        using var dir = new TempDir();
        var (s, inbox, sent) = Rows.NewStore(dir);
        var t = Rows.Make("A", inbox, "t1", flags: MessageFlags.Seen);
        var tagged = Rows.Make("A", inbox, "t2");
        tagged.Tags = "Work";
        s.InsertMessages(new[]
        {
            t, Rows.Make("A", inbox, "t1"),                                  // one conversation, one unread reply
            tagged,
            Rows.Make("A", inbox, "t3", flags: MessageFlags.Seen | MessageFlags.Flagged),
            Rows.Make("A", sent, "t1", flags: MessageFlags.Seen),
        });
        Assert.Equal((2, 3), s.CountThreads(new[] { inbox }, DateTimeOffset.Now));
        Assert.Equal((1, 1), s.CountThreads(new[] { inbox, sent }, DateTimeOffset.Now, tag: "Work"));
        Assert.Equal((0, 1), s.CountThreads(new[] { inbox }, DateTimeOffset.Now, flaggedOnly: true));
        var byFolder = s.CountThreadsByFolder(DateTimeOffset.Now);
        Assert.Equal((2, 3), byFolder[inbox]);
        Assert.Equal((0, 1), byFolder[sent]);
    }

    // ── C3 · toolbar settings ──

    [Fact]
    public void Toolbar_defaults_and_repairs_old_or_edited_lists()
    {
        var a = new Appearance();
        Assert.Equal(Appearance.ToolbarIds, a.Toolbar.Select(b => b.Id));
        Assert.Equal(Appearance.DefaultVisible, a.Toolbar.Count(b => b.Visible));

        a.Toolbar = new() { new() { Id = "pin" }, new() { Id = "bogus" }, new() { Id = "pin", Visible = false }, new() { Id = "archive", Visible = false } };
        a.Normalise();
        Assert.Equal("pin", a.Toolbar[0].Id);
        Assert.True(a.Toolbar[0].Visible);
        Assert.Equal("archive", a.Toolbar[1].Id);
        Assert.False(a.Toolbar[1].Visible);
        Assert.Equal(Appearance.ToolbarIds.Length, a.Toolbar.Count);
        Assert.DoesNotContain(a.Toolbar, b => b.Id == "bogus");
    }

    [Fact]
    public void Settings_from_1_0_1_get_the_new_sections_with_defaults()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("settings.json"), "{\"SchemaVersion\":1,\"Accounts\":[]}");
        var s = new SettingsStore(dir.File("settings.json")).Load();
        Assert.True(s.Appearance.Colourful);
        Assert.Equal(CountsMode.UnreadAndTotal, s.Appearance.Counts);
        Assert.True(s.Appearance.ShowStatusBar);
        Assert.True(s.Updates.AutoCheck);
        Assert.False(s.Updates.IncludePrerelease);
        s.Appearance.ButtonStyle = ButtonStyle.IconOnly;
        s.Appearance.Toolbar[0].Visible = false;
        var store = new SettingsStore(dir.File("settings.json"));
        store.Save(s);
        var again = new SettingsStore(dir.File("settings.json")).Load();
        Assert.Equal(ButtonStyle.IconOnly, again.Appearance.ButtonStyle);
        Assert.False(again.Appearance.Toolbar[0].Visible);
    }
}

public class SelfUpdateTests
{
    [Fact]
    public void Swap_keeps_the_old_exe_as_backup_and_rollback_restores_it()
    {
        using var dir = new TempDir();
        var cur = dir.File("Magpie.exe");
        var neu = dir.File("download.exe");
        File.WriteAllText(cur, "old");
        File.WriteAllText(neu, "new");
        Assert.True(SelfUpdate.CanReplace(cur));
        SelfUpdate.Swap(cur, neu);
        Assert.Equal("new", File.ReadAllText(cur));
        Assert.Equal("old", File.ReadAllText(SelfUpdate.BackupPathFor(cur)));
        Assert.True(SelfUpdate.Rollback(cur));
        Assert.Equal("old", File.ReadAllText(cur));
        Assert.False(File.Exists(SelfUpdate.BackupPathFor(cur)));
        Assert.False(SelfUpdate.Rollback(cur));
    }

    [Fact]
    public void A_failed_copy_leaves_the_original_in_place()
    {
        using var dir = new TempDir();
        var cur = dir.File("Magpie.exe");
        File.WriteAllText(cur, "old");
        Assert.ThrowsAny<Exception>(() => SelfUpdate.Swap(cur, dir.File("missing.exe")));
        Assert.Equal("old", File.ReadAllText(cur));
        Assert.False(File.Exists(SelfUpdate.BackupPathFor(cur)));
    }
}

public class ReaderLoadingTests
{
    [Fact]
    public void Missing_body_says_where_it_is_downloading_from_and_a_failure_offers_try_again()
    {
        var row = new MessageRow { Id = 7, FromName = "Rohan", FromAddress = "rohan@example.com", Subject = "Flat visit", Date = DateTimeOffset.Now };
        var loading = HtmlRenderer.BuildConversation("Flat visit", new List<RenderMessage> { new() { Row = row, Expanded = true, LoadingText = "Downloading from Gmail…" } }, false, DateTimeOffset.Now);
        Assert.Contains("Downloading from Gmail", System.Net.WebUtility.HtmlDecode(loading.Html));
        Assert.DoesNotContain("t:'retry'", loading.Html);

        var failed = HtmlRenderer.BuildConversation("Flat visit", new List<RenderMessage> { new() { Row = row, Expanded = true, LoadError = "No internet <connection>" } }, false, DateTimeOffset.Now);
        Assert.Contains("t:'retry'", failed.Html);
        Assert.Contains("No internet &lt;connection&gt;", System.Net.WebUtility.HtmlDecode(failed.Html));
    }
}

public class GroupedCountTests
{
    [Fact]
    public void Tags_and_categories_are_counted_per_conversation_in_one_pass()
    {
        using var dir = new TempDir();
        var (s, inbox, _) = Rows.NewStore(dir);
        var old = DateTimeOffset.Now.AddHours(-2);
        var a1 = Rows.Make("A", inbox, "t1", date: old, cat: Category.Newsletters); a1.Tags = "Work";
        var a2 = Rows.Make("A", inbox, "t1", date: DateTimeOffset.Now, flags: MessageFlags.Seen, cat: Category.People); a2.Tags = "Work,Bills";
        var b = Rows.Make("A", inbox, "t2", flags: MessageFlags.Seen, cat: Category.Notifications); b.Tags = "bills";
        s.InsertMessages(new[] { a1, a2, b, Rows.Make("A", inbox, "t3", cat: Category.People) });

        var tags = s.CountThreadsByTag(new[] { inbox }, DateTimeOffset.Now);
        Assert.Equal((1, 1), tags["Work"]);           // t1 once, unread because of its first message
        Assert.Equal((1, 2), tags["Bills"]);          // t1 + t2, case-insensitive
        var cats = s.CountThreadsByCategory(new[] { inbox }, DateTimeOffset.Now);
        Assert.Equal((2, 2), cats[Category.People]);   // t1 goes by its latest message (People), t3
        Assert.Equal((0, 1), cats[Category.Notifications]);
        Assert.False(cats.ContainsKey(Category.Newsletters));
    }

    [Fact]
    public void Light_outbox_read_leaves_the_message_out()
    {
        using var dir = new TempDir();
        using var e = new MailEngine(new AppPaths(dir.Path), new FakeProtector());
        e.Settings.Current.Accounts.Add(new Account { Id = "acc1", Email = "me@test.local", Enabled = false });
        e.QueueSend(new Draft { AccountId = "acc1", To = "x@y.test", Subject = "Later", Html = "<p>x</p>" }, DateTimeOffset.Now.AddHours(1), null);
        var full = Assert.Single(e.Outbox());
        var lite = Assert.Single(e.OutboxSummary());
        Assert.NotEmpty(full.Mime);
        Assert.Empty(lite.Mime);
        Assert.Equal("Later", lite.Subject);
    }
}
