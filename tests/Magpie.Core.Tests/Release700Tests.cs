using Magpie.Core.Models;
using Magpie.Core.Storage;
using Microsoft.Data.Sqlite;

namespace Magpie.Core.Tests;

public sealed class Release700Tests
{
    [Fact]
    public void Saved_views_survive_reopen_and_edit_preserves_identity()
    {
        using var dir = new TempDir();
        var (store, _, _) = Rows.NewStore(dir);
        var id = store.SaveInboxView("Work", "domain:example.com", "A", true);
        var reopened = new MailStore(dir.File("mail.db"));
        var view = Assert.Single(reopened.GetInboxViews());
        Assert.Equal(new InboxView(id, "Work", "domain:example.com", "A", true), view);
        Assert.Equal(id, reopened.SaveInboxView("Invoices", "subject:invoice", null, false, id));
        var edited = Assert.Single(store.GetInboxViews());
        Assert.Equal("Invoices", edited.Name);
        Assert.Null(edited.AccountId);
        Assert.False(edited.InboxOnly);
        reopened.DeleteInboxView(id);
        Assert.Empty(store.GetInboxViews());
    }

    [Theory]
    [InlineData("", "from:bob")]
    [InlineData("Name", "")]
    [InlineData("Name", "  ")]
    public void Invalid_views_are_not_persisted(string name, string query)
    {
        using var dir = new TempDir();
        var (store, _, _) = Rows.NewStore(dir);
        Assert.Throws<ArgumentException>(() => store.SaveInboxView(name, query, null, true));
        Assert.Empty(store.GetInboxViews());
    }

    [Fact]
    public void View_names_are_unique_case_insensitively_and_capacity_is_bounded()
    {
        using var dir = new TempDir();
        var (store, _, _) = Rows.NewStore(dir);
        store.SaveInboxView("Work", "from:bob", null, true);
        Assert.Throws<SqliteException>(() => store.SaveInboxView("work", "from:alice", null, true));
        for (var i = 1; i < 32; i++) store.SaveInboxView("View " + i, "subject:invoice", null, false);
        Assert.Throws<ArgumentException>(() => store.SaveInboxView("Too many", "from:bob", null, true));
        Assert.Equal(32, store.GetInboxViews().Count);
        var first = store.GetInboxViews()[0];
        store.SaveInboxView("Renamed", first.Query, first.AccountId, first.InboxOnly, first.Id);
        Assert.Equal(32, store.GetInboxViews().Count);
    }

    [Fact]
    public void Missing_view_edit_never_inserts_an_unexpected_replacement()
    {
        using var dir = new TempDir();
        var (store, _, _) = Rows.NewStore(dir);
        Assert.Throws<ArgumentException>(() => store.SaveInboxView("Deleted", "from:bob", null, true, 500));
        Assert.Empty(store.GetInboxViews());
    }

    [Fact]
    public void Saved_view_schema_is_present_even_with_a_newer_database_version()
    {
        using var dir = new TempDir();
        Rows.NewStore(dir);
        using (var connection = new SqliteConnection("Data Source=" + dir.File("mail.db")))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE inbox_views; PRAGMA user_version=50;";
            command.ExecuteNonQuery();
        }
        var store = new MailStore(dir.File("mail.db"));
        store.SaveInboxView("Restored", "domain:example.com", null, true);
        Assert.Single(store.GetInboxViews());
        using var check = new SqliteConnection("Data Source=" + dir.File("mail.db"));
        check.Open();
        using var read = check.CreateCommand();
        read.CommandText = "PRAGMA user_version";
        Assert.Equal(50L, read.ExecuteScalar());
    }

    [Fact]
    public void Exact_domain_does_not_match_suffixes_or_subdomains()
    {
        using var dir = new TempDir();
        var (store, inbox, _) = Rows.NewStore(dir);
        store.InsertMessages(new[]
        {
            Rows.Make("A", inbox, "exact", from: "boss@Example.COM"),
            Rows.Make("A", inbox, "suffix", from: "boss@badexample.com"),
            Rows.Make("A", inbox, "subdomain", from: "boss@news.example.com"),
            Rows.Make("A", inbox, "malformed", from: "example.com")
        });
        var result = store.ListThreads(new ListQuery { FolderIds = new[] { inbox }, Search = SearchQuery.Parse("domain:@example.com") }, DateTimeOffset.Now);
        Assert.Equal("exact", Assert.Single(result).ThreadKey);
    }

    [Fact]
    public void Exact_labels_support_spaces_and_literal_SQL_wildcards()
    {
        using var dir = new TempDir();
        var (store, inbox, _) = Rows.NewStore(dir);
        var rows = store.InsertMessages(new[] { Rows.Make("A", inbox, "exact"), Rows.Make("A", inbox, "partial"), Rows.Make("A", inbox, "wildcard") });
        store.SetTags(rows[0].Id, "Team Work,100%_done");
        store.SetTags(rows[1].Id, "Team Workforce,100xxdone");
        store.SetTags(rows[2].Id, "Other");
        Assert.Equal("exact", Assert.Single(store.ListThreads(new ListQuery { FolderIds = new[] { inbox }, Search = SearchQuery.Parse("label:\"team work\" tag:100%_done") }, DateTimeOffset.Now)).ThreadKey);
        Assert.Empty(store.ListThreads(new ListQuery { FolderIds = new[] { inbox }, Search = SearchQuery.Parse("label:' OR 1=1 --") }, DateTimeOffset.Now));
    }

    [Fact]
    public void Split_query_and_account_scope_apply_before_pagination()
    {
        using var dir = new TempDir();
        var (store, inbox, _) = Rows.NewStore(dir);
        var other = store.UpsertFolder(new MailFolder { AccountId = "B", Path = "INBOX", Name = "Inbox", Role = FolderRole.Inbox });
        var now = DateTimeOffset.Now;
        var messages = Enumerable.Range(0, 20).Select(i => Rows.Make("A", inbox, "noise" + i, from: "news@noise.com", date: now.AddMinutes(-i))).ToList();
        messages.Add(Rows.Make("A", inbox, "wanted", from: "billing@example.com", subject: "Invoice", date: now.AddDays(-1)));
        messages.Add(Rows.Make("B", other, "other-account", from: "billing@example.com", subject: "Invoice", date: now));
        store.InsertMessages(messages);
        var view = new InboxView(1, "Bills", "domain:example.com subject:invoice", "A", true);
        var result = store.ListThreads(new ListQuery { FolderIds = new[] { inbox }, Search = SearchQuery.Parse(view.Query + " is:unread"), Limit = 1 }, now);
        Assert.Equal("wanted", Assert.Single(result).ThreadKey);
        Assert.Empty(store.ListThreads(new ListQuery { FolderIds = Array.Empty<long>(), Search = SearchQuery.Parse(view.Query) }, now));
    }

    [Fact]
    public void Reminder_search_keeps_account_scope_and_includes_snoozed_threads()
    {
        using var dir = new TempDir();
        var (store, inbox, _) = Rows.NewStore(dir);
        store.InsertMessages(new[] { Rows.Make("A", inbox, "invoice", subject: "Invoice") });
        store.SetSnooze("A", "invoice", new[] { inbox }, DateTimeOffset.Now.AddDays(1));
        Assert.True(store.ThreadMatchesSearch("A", "invoice", SearchQuery.Parse("subject:invoice")));
        Assert.False(store.ThreadMatchesSearch("B", "invoice", SearchQuery.Parse("subject:invoice")));
        Assert.False(store.ThreadMatchesSearch("A", "invoice", SearchQuery.Parse("subject:lunch")));
    }

    [Fact]
    public void Important_and_other_partition_threads_before_limit()
    {
        using var dir = new TempDir();
        var (store, inbox, _) = Rows.NewStore(dir);
        var now = DateTimeOffset.Now;
        store.InsertMessages(new[] {
            Rows.Make("A", inbox, "important", flags: MessageFlags.Flagged, date: now.AddHours(-2)),
            Rows.Make("A", inbox, "important", date: now.AddHours(-1)),
            Rows.Make("A", inbox, "other", date: now) });
        Assert.Equal("important", Assert.Single(store.ListThreads(new ListQuery { FolderIds = new[] { inbox }, FlaggedOnly = true, Limit = 1 }, now)).ThreadKey);
        Assert.Equal("other", Assert.Single(store.ListThreads(new ListQuery { FolderIds = new[] { inbox }, UnflaggedOnly = true, Limit = 1 }, now)).ThreadKey);
    }

    [Fact]
    public void Reply_cancels_followup_before_deadline_but_not_always_reminders()
    {
        using var dir = new TempDir();
        var (store, inbox, _) = Rows.NewStore(dir);
        var now = DateTimeOffset.Now;
        var followup = store.AddReminder(new Reminder { AccountId = "A", ThreadKey = "reply", After = now.AddMinutes(-2), Due = now.AddDays(1) });
        var always = store.AddReminder(new Reminder { AccountId = "A", ThreadKey = "always", After = now.AddMinutes(-2), Due = now.AddDays(1), Always = true });
        store.InsertMessages(new[] { Rows.Make("A", inbox, "reply", date: now.AddMinutes(-1)), Rows.Make("A", inbox, "always", date: now.AddMinutes(-1)) });
        var result = store.AdvanceReminders(now, new[] { "me@test.local" });
        Assert.Equal(1, result.Completed);
        Assert.Empty(result.Due);
        Assert.Equal(followup, Assert.Single(store.GetReminders(ReminderState.Done)).Id);
        Assert.Equal(always, Assert.Single(store.GetReminders(ReminderState.Waiting)).Id);
        Assert.Equal(0, store.AdvanceReminders(now, new[] { "me@test.local" }).Completed);
    }

    [Fact]
    public void Own_mail_and_other_account_reply_do_not_cancel_followup()
    {
        using var dir = new TempDir();
        var (store, inbox, _) = Rows.NewStore(dir);
        var other = store.UpsertFolder(new MailFolder { AccountId = "B", Path = "INBOX", Name = "Inbox", Role = FolderRole.Inbox });
        var now = DateTimeOffset.Now;
        store.AddReminder(new Reminder { AccountId = "A", ThreadKey = "same-key", After = now.AddMinutes(-2), Due = now.AddHours(1) });
        store.InsertMessages(new[] {
            Rows.Make("A", inbox, "same-key", from: "ME@test.local", date: now.AddMinutes(-1)),
            Rows.Make("B", other, "same-key", date: now.AddMinutes(-1)) });
        Assert.Equal(0, store.AdvanceReminders(now, new[] { "me@test.local" }).Completed);
        Assert.Single(store.GetReminders(ReminderState.Waiting));
    }

    [Fact]
    public void Due_reminder_is_claimed_once_and_dismissed_reminder_stays_dismissed()
    {
        using var dir = new TempDir();
        var (store, _, _) = Rows.NewStore(dir);
        var now = DateTimeOffset.Now;
        var active = store.AddReminder(new Reminder { AccountId = "A", ThreadKey = "active", After = now.AddHours(-1), Due = now.AddMinutes(-1) });
        var dismissed = store.AddReminder(new Reminder { AccountId = "A", ThreadKey = "dismissed", After = now.AddHours(-1), Due = now.AddMinutes(-1) });
        store.SetReminderState(dismissed, ReminderState.Done);
        Assert.Equal(active, Assert.Single(store.AdvanceReminders(now, new[] { "me@test.local" }).Due).Id);
        Assert.Empty(store.AdvanceReminders(now, new[] { "me@test.local" }).Due);
    }

    [Theory]
    [InlineData("draft ai", "Draft with AI", "Review before inserting", "compose", true)]
    [InlineData("ctrl k", "Commands", "Open palette", "Ctrl K", true)]
    [InlineData("invoice", "Go to Bills", "subject:invoice", "saved view", true)]
    [InlineData("draft delete", "Draft with AI", "Review before inserting", "compose", false)]
    [InlineData("", "Inbox", "", "", true)]
    public void Palette_matches_all_words_locally(string query, string title, string details, string aliases, bool expected)
        => Assert.Equal(expected, WorkspaceCommandSearch.Matches(query, title, details, aliases));
}
