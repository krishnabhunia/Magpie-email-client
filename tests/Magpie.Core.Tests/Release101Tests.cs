using Magpie.Core.Mail;
using Magpie.Core.Models;
using Magpie.Core.Settings;
using Magpie.Core.Storage;

namespace Magpie.Core.Tests;

/// <summary>1.0.1: AI presets (Q1), sidebar folder tree (Q2), drafts kept on this PC (F1).</summary>
public class Release101Tests
{
    // ── Q1 · AI presets ──

    [Theory]
    [InlineData(false, false, false, false, false, "Off")]
    [InlineData(false, true, true, true, true, "Off")]      // master off wins: nothing can call a model
    [InlineData(true, false, false, false, false, "A")]
    [InlineData(true, true, false, false, false, "B")]
    [InlineData(true, true, true, true, true, "C")]
    [InlineData(true, false, true, false, false, "Custom")]
    [InlineData(true, true, true, false, true, "Custom")]
    public void Preset_matches_switches(bool master, bool sum, bool draft, bool rewrite, bool replies, string expected) =>
        Assert.Equal(expected, AiPresets.Match(master, sum, draft, rewrite, replies));

    [Fact]
    public void Every_preset_round_trips_and_custom_is_not_applicable()
    {
        foreach (var p in AiPresets.All)
            Assert.Equal(p.Id, AiPresets.Match(p.Master, p.Summarise, p.Draft, p.Rewrite, p.Replies));
        Assert.False(AiPresets.TryGet("Custom", out _));
        Assert.True(AiPresets.TryGet("A", out var a));
        Assert.True(a.Master);
        Assert.False(a.Summarise || a.Draft || a.Rewrite || a.Replies);
    }

    [Fact]
    public void Sidebar_state_defaults_and_survives_a_settings_roundtrip()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(dir.File("settings.json"));
        var s = store.Load();
        Assert.True(s.Sidebar.FoldersOpen && s.Sidebar.AccountsOpen && s.Sidebar.TagsOpen);
        s.Sidebar.TagsOpen = false;
        s.Sidebar.OpenAccounts.Add("acc1");
        s.Sidebar.OpenFolders.Add("acc1|Travel");
        var changed = 0;
        store.Changed += () => changed++;
        store.Save(notify: false);
        Assert.Equal(0, changed);                       // UI-state saves don't rebuild the app
        var again = new SettingsStore(dir.File("settings.json")).Load();
        Assert.False(again.Sidebar.TagsOpen);
        Assert.Equal(new[] { "acc1" }, again.Sidebar.OpenAccounts);
        Assert.Equal(new[] { "acc1|Travel" }, again.Sidebar.OpenFolders);
    }

    [Fact]
    public void Old_settings_without_sidebar_section_get_defaults()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("settings.json"), "{\"SchemaVersion\":1,\"Accounts\":[],\"Sidebar\":null}");
        var s = new SettingsStore(dir.File("settings.json")).Load();
        Assert.NotNull(s.Sidebar);
        Assert.True(s.Sidebar.FoldersOpen);
        Assert.Empty(s.Sidebar.OpenFolders);
    }

    // ── Q2 · folder tree ──

    private static MailFolder F(string path, FolderRole role = FolderRole.Other, char delim = '/')
    {
        var name = delim == '\0' ? path : path.Split(delim).Last();
        return new MailFolder { AccountId = "A", Path = path, Name = name, Role = role, Delimiter = delim };
    }

    [Fact]
    public void Folders_nest_under_parents_depth_first_with_special_folders_first()
    {
        var list = new[]
        {
            F("Travel/2026"), F("Receipts"), F("Trash", FolderRole.Trash), F("Travel"), F("INBOX", FolderRole.Inbox),
            F("Travel Plans"), F("Sent", FolderRole.Sent), F("INBOX/Invoices"), F("Travel/2026/Pune"),
        };
        var order = FolderTree.Order(list).Select(x => $"{new string('-', x.Depth)}{x.Folder.Path}").ToList();
        Assert.Equal(new[]
        {
            "INBOX", "-INBOX/Invoices", "Sent", "Trash", "Receipts", "Travel", "-Travel/2026", "--Travel/2026/Pune", "Travel Plans",
        }, order);
    }

    [Fact]
    public void Folder_whose_parent_is_not_listed_is_top_level()
    {
        // Gmail: "[Gmail]" itself is not selectable, its children are.
        var list = new[] { F("INBOX", FolderRole.Inbox), F("[Gmail]/Sent Mail", FolderRole.Sent), F("[Gmail]/Starred", FolderRole.Flagged) };
        var order = FolderTree.Order(list);
        Assert.All(order, x => Assert.Equal(0, x.Depth));
        Assert.All(order, x => Assert.Equal("", x.Parent));
        Assert.Equal("INBOX", order[0].Folder.Path);
    }

    [Fact]
    public void Dot_delimited_and_flat_servers_work()
    {
        // INBOX plus a real subfolder of it, alongside ordinary top-level folders: Work nests under Inbox.
        var dotted = FolderTree.Order(new[] { F("INBOX", FolderRole.Inbox, '.'), F("INBOX.Work", FolderRole.Other, '.'), F("Receipts", FolderRole.Other, '.') });
        Assert.Equal("INBOX.Work", dotted[1].Folder.Path);
        Assert.Equal(1, dotted[1].Depth);
        Assert.Equal("INBOX", dotted[1].Parent);
        var flat = FolderTree.Order(new[] { F("Notes", FolderRole.Other, '\0'), F("INBOX", FolderRole.Inbox, '\0') });
        Assert.Equal(new[] { "INBOX", "Notes" }, flat.Select(x => x.Folder.Path));
    }

    [Fact]
    public void Duplicate_paths_are_listed_once()
    {
        var order = FolderTree.Order(new[] { F("A"), F("A"), F("A/B") });
        Assert.Equal(2, order.Count);
    }

    // ── F1 · drafts kept on this PC ──

    [Fact]
    public void Local_draft_store_insert_update_list_delete()
    {
        using var dir = new TempDir();
        var s = new MailStore(dir.File("mail.db"));
        var d = new LocalDraft { AccountId = "A", Mime = new byte[] { 1, 2, 3 }, Subject = "Hi", ToText = "x@y.test", Preview = "hello" };
        var id = s.SaveLocalDraft(d);
        Assert.True(id > 0);
        d.Subject = "Hi again";
        d.PendingUpload = true;
        Assert.Equal(id, s.SaveLocalDraft(d));
        var all = s.GetLocalDrafts();
        Assert.Single(all);
        Assert.Equal("Hi again", all[0].Subject);
        Assert.True(all[0].PendingUpload);
        Assert.Single(s.GetLocalDrafts(pendingOnly: true));
        Assert.Equal(1, s.CountLocalDrafts());
        s.DeleteLocalDraft(id);
        Assert.Empty(s.GetLocalDrafts());
    }

    [Fact]
    public void Updating_a_draft_deleted_meanwhile_stores_it_again()
    {
        using var dir = new TempDir();
        var s = new MailStore(dir.File("mail.db"));
        var d = new LocalDraft { AccountId = "A", Mime = new byte[] { 1 }, Subject = "keep me" };
        var id = s.SaveLocalDraft(d);
        s.DeleteLocalDraft(id);
        var id2 = s.SaveLocalDraft(d);             // d.Id still points at the deleted row
        Assert.NotEqual(0, id2);
        Assert.Equal("keep me", Assert.Single(s.GetLocalDrafts()).Subject);
    }

    [Fact]
    public void Existing_v1_database_gains_the_local_drafts_table()
    {
        using var dir = new TempDir();
        var path = dir.File("mail.db");
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + path))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "CREATE TABLE folders(id INTEGER PRIMARY KEY); PRAGMA user_version=1;";
            cmd.ExecuteNonQuery();
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var s = new MailStore(path);
        s.SaveLocalDraft(new LocalDraft { AccountId = "A", Mime = new byte[] { 1 } });
        Assert.Single(s.GetLocalDrafts());
    }

    [Fact]
    public void Engine_saves_and_reopens_a_local_draft_with_its_content()
    {
        using var dir = new TempDir();
        using var e = new MailEngine(new AppPaths(dir.Path), new FakeProtector());
        var acc = new Account { Id = "acc1", Email = "me@test.local", DisplayName = "Me", Enabled = false };
        e.Settings.Current.Accounts.Add(acc);

        var attachment = dir.File("notes.txt");
        File.WriteAllText(attachment, "attached text");
        var d = new Draft
        {
            AccountId = acc.Id, Mode = ComposeMode.Reply, To = "Anita <anita@vendor.test>", Cc = "sandeep@vendor.test",
            Subject = "Re: Vendor migration", Html = "<p>Thursday works for me.</p>", ThreadKey = "t1",
            AttachmentPaths = { attachment }, SourceDraftRow = 42,
        };
        var id = e.SaveLocalDraft(d, pendingUpload: false);
        Assert.Equal(id, d.LocalDraftId);
        e.SaveLocalDraft(d, pendingUpload: false);   // autosave again: same row
        var only = Assert.Single(e.LocalDrafts());
        Assert.Equal("Re: Vendor migration", only.Subject);
        Assert.Contains("Thursday works", only.Preview);

        var back = e.OpenLocalDraft(id)!;
        Assert.Equal(ComposeMode.EditDraft, back.Mode);
        Assert.Equal(id, back.LocalDraftId);
        Assert.Equal(42, back.SourceDraftRow);
        Assert.Equal("t1", back.ThreadKey);
        Assert.Contains("anita@vendor.test", back.To);
        Assert.Contains("sandeep@vendor.test", back.Cc);
        Assert.Contains("Thursday works", back.Html);
        Assert.Contains(back.CarriedParts, p => (p as MimeKit.MimePart)?.FileName == "notes.txt");

        e.DeleteLocalDraft(id);
        Assert.Empty(e.LocalDrafts());
    }

    [Fact]
    public void Servers_that_keep_every_folder_under_INBOX_show_them_top_level()
    {
        // Courier / cPanel Dovecot: INBOX.Sent, INBOX.Trash, INBOX.Work, INBOX.Work.2026
        var order = FolderTree.Order(new[]
        {
            F("INBOX", FolderRole.Inbox, '.'), F("INBOX.Sent", FolderRole.Sent, '.'), F("INBOX.Trash", FolderRole.Trash, '.'),
            F("INBOX.Work", FolderRole.Other, '.'), F("INBOX.Work.2026", FolderRole.Other, '.'),
        }).Select(x => $"{new string('-', x.Depth)}{x.Folder.Path}").ToList();
        Assert.Equal(new[] { "INBOX", "INBOX.Sent", "INBOX.Trash", "INBOX.Work", "-INBOX.Work.2026" }, order);
    }

    [Fact]
    public void Special_folders_under_INBOX_are_never_hidden_inside_it()
    {
        var order = FolderTree.Order(new[]
        {
            F("INBOX", FolderRole.Inbox), F("INBOX/Sent", FolderRole.Sent), F("INBOX/Clients"), F("Archive", FolderRole.Archive),
        });
        Assert.Equal(0, order.Single(x => x.Folder.Path == "INBOX/Sent").Depth);
        Assert.Equal(1, order.Single(x => x.Folder.Path == "INBOX/Clients").Depth);
    }

    [Fact]
    public void Autosave_after_the_row_was_deleted_can_be_told_not_to_recreate_it()
    {
        using var dir = new TempDir();
        var s = new MailStore(dir.File("mail.db"));
        var d = new LocalDraft { AccountId = "A", Mime = new byte[] { 1 } };
        var id = s.SaveLocalDraft(d);
        s.DeleteLocalDraft(id);                         // sent or discarded meanwhile
        Assert.Equal(0, s.SaveLocalDraft(d, insertIfMissing: false));
        Assert.Empty(s.GetLocalDrafts());
    }

    [Fact]
    public void Local_copies_of_server_drafts_are_not_counted_twice()
    {
        using var dir = new TempDir();
        var s = new MailStore(dir.File("mail.db"));
        s.SaveLocalDraft(new LocalDraft { AccountId = "A", Mime = new byte[] { 1 } });
        s.SaveLocalDraft(new LocalDraft { AccountId = "A", Mime = new byte[] { 1 }, SourceDraftRow = 7 });
        Assert.Equal(2, s.GetLocalDrafts().Count);
        Assert.Equal(1, s.CountLocalDrafts());
    }

    [Fact]
    public void A_draft_keeps_one_Message_ID_across_saves_so_the_server_copy_is_replaced()
    {
        var acc = new Account { Id = "a", Email = "me@test.local" };
        var d = new Draft { AccountId = "a", To = "x@y.test", Subject = "s", Html = "<p>1</p>" };
        var first = Composer.Build(d, acc).MessageId;
        d.Html = "<p>2</p>";
        Assert.Equal(first, Composer.Build(d, acc).MessageId);
        Assert.EndsWith("test.local", first);
        var back = Composer.FromMime(Composer.Build(d, acc), "a", "t");
        Assert.Equal(first, back.MessageId);
    }

    [Fact]
    public void An_open_draft_stays_pending_but_is_claimed_by_its_window()
    {
        using var dir = new TempDir();
        using var e = new MailEngine(new AppPaths(dir.Path), new FakeProtector());
        e.Settings.Current.Accounts.Add(new Account { Id = "acc1", Email = "me@test.local", Enabled = false });
        var d = new Draft { AccountId = "acc1", Subject = "offline", Html = "<p>x</p>" };
        var id = e.SaveLocalDraft(d, pendingUpload: true);
        e.OpenLocalDraft(id);
        Assert.Single(e.Store.GetLocalDrafts(pendingOnly: true));   // still uploads if the window crashes
        Assert.False(e.IsLocalDraftOpen(id));
        e.ClaimLocalDraft(id);
        Assert.True(e.IsLocalDraftOpen(id));                       // background upload skips it meanwhile
        e.ReleaseLocalDraft(id);
        Assert.False(e.IsLocalDraftOpen(id));
    }

    // ── review 3 ──

    [Fact]
    public void Pasted_pictures_survive_reopening_a_saved_draft()
    {
        var acc = new Account { Id = "a", Email = "me@test.local" };
        var png = Convert.ToBase64String(new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4 });
        var d = new Draft { AccountId = "a", To = "x@y.test", Subject = "pic", Html = $"<p>see</p><img src=\"data:image/png;base64,{png}\">" };
        var msg = Composer.FromBytes(Composer.ToBytes(Composer.Build(d, acc)));
        Assert.DoesNotContain("data:image", msg.HtmlBody);                  // sent as an inline part
        var back = Composer.FromMime(msg, "a", "");
        Assert.Contains($"data:image/png;base64,{png}", back.Html);         // editor gets the picture back
        Assert.DoesNotContain("cid:", back.Html);
        Assert.Empty(back.CarriedParts);                                    // not duplicated as an attachment
    }

    [Fact]
    public void Sending_drops_older_copies_of_the_same_message_kept_on_this_PC()
    {
        using var dir = new TempDir();
        using var e = new MailEngine(new AppPaths(dir.Path), new FakeProtector());
        e.Settings.Current.Accounts.Add(new Account { Id = "acc1", Email = "me@test.local", Enabled = false });
        var old = new Draft { AccountId = "acc1", To = "x@y.test", Subject = "v1", Html = "<p>1</p>" };
        var staleId = e.SaveLocalDraft(old, pendingUpload: true);
        Assert.False(string.IsNullOrEmpty(Assert.Single(e.LocalDrafts()).MessageId));

        var open = new Draft { AccountId = "acc1", To = "x@y.test", Subject = "claimed", Html = "<p>c</p>", MessageId = old.MessageId };
        var openId = e.SaveLocalDraft(open, pendingUpload: false);
        e.ClaimLocalDraft(openId);                                          // another window is editing this copy

        var newer = new Draft { AccountId = "acc1", To = "x@y.test", Subject = "v2", Html = "<p>2</p>", MessageId = old.MessageId };
        e.QueueSend(newer, DateTimeOffset.Now.AddHours(1), null);
        var left = Assert.Single(e.LocalDrafts());
        Assert.Equal(openId, left.Id);                                      // stale copy gone, open one left to its window
        Assert.NotEqual(staleId, left.Id);
    }

    [Fact]
    public void Background_upload_only_deletes_a_copy_nobody_saved_again()
    {
        using var dir = new TempDir();
        var s = new MailStore(dir.File("mail.db"));
        var d = new LocalDraft { AccountId = "A", Mime = new byte[] { 1 }, PendingUpload = true };
        var id = s.SaveLocalDraft(d);
        var seen = s.GetLocalDraft(id)!.Updated;
        s.SaveLocalDraft(d);                                               // saved again while uploading
        Assert.False(s.DeleteLocalDraftIfUnchanged(id, seen));
        Assert.True(s.DeleteLocalDraftIfUnchanged(id, s.GetLocalDraft(id)!.Updated));
        Assert.Empty(s.GetLocalDrafts());
    }

    [Fact]
    public void Existing_v2_database_gains_the_message_id_column()
    {
        using var dir = new TempDir();
        var path = dir.File("mail.db");
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + path))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE local_drafts(id INTEGER PRIMARY KEY AUTOINCREMENT, account_id TEXT NOT NULL, mime BLOB NOT NULL,
                  subject TEXT NOT NULL DEFAULT '', to_text TEXT NOT NULL DEFAULT '', preview TEXT NOT NULL DEFAULT '',
                  thread_key TEXT NOT NULL DEFAULT '', source_draft_row INTEGER NULL, pending_upload INTEGER NOT NULL DEFAULT 0, updated INTEGER NOT NULL);
                INSERT INTO local_drafts(account_id,mime,updated) VALUES('A', x'01', 1);
                PRAGMA user_version=2;
                """;
            cmd.ExecuteNonQuery();
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var s = new MailStore(path);
        Assert.Equal("", Assert.Single(s.GetLocalDrafts()).MessageId);      // kept, with an empty id
        s.SaveLocalDraft(new LocalDraft { AccountId = "A", Mime = new byte[] { 2 }, MessageId = "m@x" });
        Assert.Contains(s.GetLocalDrafts(), l => l.MessageId == "m@x");
    }

    [Fact]
    public void Inline_picture_marked_as_attachment_is_not_carried_twice()
    {
        var msg = new MimeKit.MimeMessage();
        var html = new MimeKit.TextPart("html") { Text = "<html><body><p>hi</p><img src=\"cid:pic1@x\"></body></html>" };
        var img = new MimeKit.MimePart("image", "png")
        {
            Content = new MimeKit.MimeContent(new MemoryStream(new byte[] { 9, 8, 7 })),
            ContentId = "pic1@x", ContentDisposition = new MimeKit.ContentDisposition(MimeKit.ContentDisposition.Attachment), FileName = "pic.png",
        };
        var other = new MimeKit.MimePart("application", "pdf")
        {
            Content = new MimeKit.MimeContent(new MemoryStream(new byte[] { 1 })),
            ContentDisposition = new MimeKit.ContentDisposition(MimeKit.ContentDisposition.Attachment), FileName = "a.pdf",
        };
        var mixed = new MimeKit.Multipart("mixed") { html, img, other };
        msg.Body = mixed;
        var d = Composer.FromMime(msg, "a", "");
        Assert.Contains("data:image/png;base64,CQgH", d.Html);
        Assert.Equal("a.pdf", ((MimeKit.MimePart)Assert.Single(d.CarriedParts)).FileName);
    }
}
