using Magpie.Core.Mail;
using Magpie.Core.Models;
using Magpie.Core.Security;
using Magpie.Core.Settings;
using Magpie.Core.Storage;
using MimeKit;
using Xunit;

namespace Magpie.Core.Tests;

/// <summary>2.1.0: settings backup (EX1), mail folder (DL1), download window (DS1).</summary>
public class Release210Tests
{
    // ───────────── EX1: settings backup ─────────────

    private static SettingsBackup.Contents Sample() => new()
    {
        SettingsJson = """{"Accounts":[{"Id":"a1","Email":"me@x.com","SignatureHtml":"<b>Me</b>"}],"QuickReplies":["Thanks!"]}""",
        Secrets = new() { ["account:a1:refresh"] = "token-1", ["ai:apikey"] = "sk-2" },
        MailFolder = @"D:\Magpie Mail",
        AppVersion = "2.1.0",
        Created = DateTimeOffset.Now,
    };

    [Fact]
    public void Backup_opens_with_its_password_and_holds_settings_secrets_and_mail_folder()
    {
        var file = SettingsBackup.Lock(Sample(), "correct horse");
        Assert.DoesNotContain("token-1", System.Text.Encoding.UTF8.GetString(file));
        var c = SettingsBackup.Unlock(file, "correct horse");
        Assert.Contains("me@x.com", c.SettingsJson);
        Assert.Equal("token-1", c.Secrets["account:a1:refresh"]);
        Assert.Equal("sk-2", c.Secrets["ai:apikey"]);
        Assert.Equal(@"D:\Magpie Mail", c.MailFolder);
        Assert.Contains(SettingsBackup.Describe(c), l => l.Contains("me@x.com"));
    }

    [Fact]
    public void Backup_refuses_a_wrong_password_a_changed_file_and_a_short_password()
    {
        var file = SettingsBackup.Lock(Sample(), "correct horse");
        Assert.Throws<SettingsBackup.WrongPasswordException>(() => SettingsBackup.Unlock(file, "wrong horse!"));
        var text = System.Text.Encoding.UTF8.GetString(file);
        var data = System.Text.Json.Nodes.JsonNode.Parse(text)!;
        var bytes = Convert.FromBase64String(data["data"]!.GetValue<string>());
        bytes[0] ^= 1;
        data["data"] = Convert.ToBase64String(bytes);
        Assert.Throws<SettingsBackup.WrongPasswordException>(() => SettingsBackup.Unlock(System.Text.Encoding.UTF8.GetBytes(data.ToJsonString()), "correct horse"));
        Assert.Throws<InvalidDataException>(() => SettingsBackup.Unlock("{\"hello\":1}"u8.ToArray(), "correct horse"));
        Assert.Throws<ArgumentException>(() => SettingsBackup.Lock(Sample(), "short"));
    }

    [Fact]
    public void Restore_is_applied_at_the_next_start_and_replaces_settings_secrets_and_mail_folder()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Path);
        var protector = new FakeProtector();
        File.WriteAllText(paths.Settings, """{"Accounts":[]}""");
        new SecretVault(paths.Secrets, protector).Set("old", "gone");
        var mail = Path.Combine(dir.Path, "elsewhere");
        Directory.CreateDirectory(mail);
        var c = Sample();
        c.MailFolder = mail;

        SettingsBackup.StagePending(paths, c, protector);
        Assert.DoesNotContain("me@x.com", File.ReadAllText(paths.Settings));   // nothing changes until the next start
        Assert.True(SettingsBackup.ApplyPending(paths, protector));

        Assert.Contains("me@x.com", File.ReadAllText(paths.Settings));
        var vault = new SecretVault(paths.Secrets, protector);
        Assert.Equal("token-1", vault.Get("account:a1:refresh"));
        Assert.Null(vault.Get("old"));
        Assert.Equal(Path.GetFullPath(mail), paths.MailRoot);
        Assert.False(File.Exists(Path.Combine(dir.Path, SettingsBackup.PendingFile)));
        Assert.False(SettingsBackup.ApplyPending(paths, protector));   // only once
    }

    [Fact]
    public void Vault_exports_every_secret_and_replaces_them_all()
    {
        using var dir = new TempDir();
        var v = new SecretVault(dir.File("s.json"), new FakeProtector());
        v.Set("a", "1");
        v.Set("b", "2");
        var all = v.ExportAll();
        Assert.Equal("1", all["a"]);
        v.ReplaceAll(new() { ["c"] = "3" });
        Assert.Null(v.Get("a"));
        Assert.Equal("3", new SecretVault(dir.File("s.json"), new FakeProtector()).Get("c"));
    }

    // ───────────── DL1: mail folder ─────────────

    [Fact]
    public void Mail_folder_can_point_elsewhere_and_back()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Path);
        Assert.False(paths.CustomMailRoot);
        Assert.Equal(Path.Combine(dir.Path, "mail.db"), paths.Database);
        var other = Path.Combine(dir.Path, "other");
        paths.SetMailRoot(other);
        Assert.True(paths.CustomMailRoot);
        Assert.False(paths.MailRootAvailable);   // chosen folders are never created behind the user's back
        Assert.Equal(Path.Combine(Path.GetFullPath(other), "mail.db"), paths.Database);
        Assert.Equal(Path.GetFullPath(other), new AppPaths(dir.Path).MailRoot);   // remembered
        paths.SetMailRoot(null);
        Assert.False(paths.CustomMailRoot);
    }

    [Fact]
    public void Moving_mail_copies_database_and_messages_then_removes_the_old_copy()
    {
        using var dir = new TempDir();
        var from = Path.Combine(dir.Path, "from");
        var to = Path.Combine(dir.Path, "to");
        Directory.CreateDirectory(Path.Combine(from, "messages", "acct"));
        File.WriteAllText(Path.Combine(from, "mail.db"), "db");
        File.WriteAllText(Path.Combine(from, "mail.db-wal"), "wal");
        File.WriteAllText(Path.Combine(from, "messages", "acct", "1.eml"), "hello");
        File.WriteAllText(Path.Combine(from, "settings.json"), "{}");   // not mail: stays
        var reports = new List<(long, long)>();

        MailLocation.Move(from, to, replace: false, new SyncProgress(reports));

        Assert.Equal("db", File.ReadAllText(Path.Combine(to, "mail.db")));
        Assert.Equal("hello", File.ReadAllText(Path.Combine(to, "messages", "acct", "1.eml")));
        Assert.False(File.Exists(Path.Combine(from, "mail.db")));
        Assert.False(Directory.Exists(Path.Combine(from, "messages")));
        Assert.True(File.Exists(Path.Combine(from, "settings.json")));
        Assert.Equal(reports[^1].Item1, reports[^1].Item2);
        Assert.True(MailLocation.HasMail(to));
    }

    [Fact]
    public void Moving_onto_existing_mail_needs_replace()
    {
        using var dir = new TempDir();
        var from = Path.Combine(dir.Path, "from");
        var to = Path.Combine(dir.Path, "to");
        Directory.CreateDirectory(from);
        Directory.CreateDirectory(to);
        File.WriteAllText(Path.Combine(from, "mail.db"), "new");
        File.WriteAllText(Path.Combine(to, "mail.db"), "old");
        Assert.Throws<InvalidOperationException>(() => MailLocation.Move(from, to, replace: false));
        Assert.Equal("new", File.ReadAllText(Path.Combine(from, "mail.db")));
        MailLocation.Move(from, to, replace: true);
        Assert.Equal("new", File.ReadAllText(Path.Combine(to, "mail.db")));
    }

    [Fact]
    public void A_move_asked_for_now_waits_for_the_next_start()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Path);
        Assert.Null(MailLocation.PendingMove(paths));
        MailLocation.RequestMove(paths, Path.Combine(dir.Path, "x"));
        Assert.Equal(Path.GetFullPath(Path.Combine(dir.Path, "x")), MailLocation.PendingMove(paths));
        MailLocation.ClearPendingMove(paths);
        Assert.Null(MailLocation.PendingMove(paths));
    }

    private sealed class SyncProgress(List<(long, long)> list) : IProgress<(long done, long total)>
    {
        public void Report((long done, long total) value) => list.Add(value);
    }

    // ───────────── DS1: download window ─────────────

    [Fact]
    public void Only_emails_inside_the_download_window_are_picked_for_download()
    {
        using var dir = new TempDir();
        var (store, inbox, _) = Rows.NewStore(dir);
        store.InsertMessages(new[]
        {
            Rows.Make("A", inbox, "t1", date: DateTimeOffset.Now.AddDays(-10)),
            Rows.Make("A", inbox, "t2", date: DateTimeOffset.Now.AddDays(-200)),
        });
        Assert.Single(store.RowsWithoutBody(inbox, 50, DateTimeOffset.Now.AddDays(-90)));
        Assert.Equal(2, store.RowsWithoutBody(inbox, 50, null).Count);   // "Everything"
    }

    [Fact]
    public void Attachments_listed_before_download_map_to_the_downloaded_email()
    {
        var b = new BodyBuilder { TextBody = "hi" };
        b.Attachments.Add("a.pdf", new byte[] { 1, 2, 3 });
        b.Attachments.Add("b.txt", "text"u8.ToArray());
        var msg = new MimeMessage { Body = b.ToMessageBody() };
        var real = MimeText.ListAttachments(msg).Where(a => !a.Inline).ToList();

        Assert.Equal(real[0].Index, MimeText.ResolveIndex(msg, MimeText.PendingIndex));
        Assert.Equal(real[1].Index, MimeText.ResolveIndex(msg, MimeText.PendingIndex - 1));
        Assert.Equal(3, MimeText.ResolveIndex(msg, 3));   // real indices unchanged

        Assert.True(MimeText.NeedsDownload(new MessageBody { Attachments = { new AttachmentInfo { Index = -1, FileName = "a.pdf" } } }));
        Assert.False(MimeText.NeedsDownload(new MessageBody { Attachments = { new AttachmentInfo { Index = 2, FileName = "a.pdf" } } }));
        Assert.False(MimeText.NeedsDownload(null));
    }

    [Fact]
    public void New_accounts_download_90_days_without_attachments()
    {
        var a = new Account();
        Assert.Equal(90, a.SyncDays);
        Assert.False(a.DownloadAttachments);
    }
}
