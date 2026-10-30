using Magpie.Core.Mail;
using Magpie.Core.Models;
using Magpie.Core.Settings;
using Xunit;

namespace Magpie.Core.Tests;

/// <summary>LB1 C (queue #83): Gmail labels that fold — containers, the Labels group, More labels.</summary>
public class Release1010Tests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private static MailFolder F(string path, FolderRole role = FolderRole.Other, char d = '/') =>
        new() { Path = path, Name = path.Contains(d) ? path[(path.LastIndexOf(d) + 1)..] : path, Role = role, Delimiter = d, Synced = true };

    /// <summary>What Gmail lists for the design's example mailbox ("Clients" and "Finance" are \Noselect, so not stored).</summary>
    private static List<MailFolder> Gmail() => new()
    {
        F("INBOX", FolderRole.Inbox), F("[Gmail]/Sent Mail", FolderRole.Sent), F("[Gmail]/Drafts", FolderRole.Drafts),
        F("[Gmail]/All Mail", FolderRole.All), F("[Gmail]/Spam", FolderRole.Junk), F("[Gmail]/Trash", FolderRole.Trash),
        F("Clients/Acme"), F("Clients/Globex"), F("Finance/Bills"), F("Finance/Tax 2026"),
        F("Newsletters"), F("Receipts"), F("Travel"), F("Travel/2026"),
    };

    private static LabelLayoutState State(Dictionary<string, int>? unread = null, Dictionary<string, DateTimeOffset>? opened = null,
        HashSet<string>? always = null, bool onlyUnread = false) => new()
    {
        Unread = unread ?? new(), Opened = opened ?? Gmail().ToDictionary(f => f.Path, _ => Now.AddDays(-1)),
        AlwaysShow = always ?? new(), OnlyUnread = onlyUnread, Now = Now,
    };

    [Fact]
    public void Nested_labels_fold_under_parents_Gmail_only_lists_as_containers()
    {
        var rows = LabelLayout.Layout(Gmail(), gmail: true, State());
        var clients = Assert.Single(rows, r => r.Path == "Clients");
        Assert.Equal(SidebarRowKind.Container, clients.Kind);
        Assert.True(clients.HasChildren);
        Assert.Null(clients.Folder);
        Assert.Equal(LabelLayout.GroupKey, clients.ParentPath);
        Assert.Equal("Clients", rows.Single(r => r.Path == "Clients/Acme").ParentPath);
        Assert.Equal("Acme", rows.Single(r => r.Path == "Clients/Acme").Label);
        // A real parent label keeps being a folder row with its child under it.
        Assert.Equal(SidebarRowKind.Folder, rows.Single(r => r.Path == "Travel").Kind);
        Assert.Equal("Travel", rows.Single(r => r.Path == "Travel/2026").ParentPath);
        // Gmail's own container stays hidden; its folders stay on top, outside the group.
        Assert.DoesNotContain(rows, r => r.Path == "[Gmail]");
        Assert.Equal(new[] { "INBOX", "[Gmail]/Sent Mail", "[Gmail]/Drafts" }, rows.Take(3).Select(r => r.Path));
        Assert.All(rows.Where(r => r.Folder?.Role is FolderRole.Inbox or FolderRole.Trash), r => Assert.Equal("", r.ParentPath));
    }

    [Fact]
    public void Labels_sit_in_one_group_named_for_the_account_kind()
    {
        var rows = LabelLayout.Layout(Gmail(), gmail: true, State());
        var group = Assert.Single(rows, r => r.Kind == SidebarRowKind.Group);
        Assert.Equal("Labels", group.Label);
        Assert.True(rows.IndexOf(group) > rows.FindIndex(r => r.Path == "[Gmail]/Trash"));
        Assert.Equal(new[] { "Clients", "Finance", "Newsletters", "Receipts", "Travel" },
            rows.Where(r => r.ParentPath == LabelLayout.GroupKey).Select(r => r.Path));
        Assert.Equal(10, LabelLayout.Under(rows, LabelLayout.GroupKey).Count());

        var imap = LabelLayout.Layout(new[] { F("INBOX", FolderRole.Inbox), F("Sent", FolderRole.Sent), F("Projects") }, gmail: false, State());
        Assert.Equal("Folders", imap.Single(r => r.Kind == SidebarRowKind.Group).Label);
        // No group when there are no labels.
        Assert.DoesNotContain(LabelLayout.Layout(new[] { F("INBOX", FolderRole.Inbox) }, false, State()), r => r.Kind == SidebarRowKind.Group);
    }

    [Fact]
    public void Quiet_labels_go_into_More_labels_with_their_full_name()
    {
        var opened = Gmail().ToDictionary(f => f.Path, _ => Now.AddDays(-1));
        opened["Finance/Tax 2026"] = Now.AddDays(-45);      // quiet
        opened["Receipts"] = Now.AddDays(-31);              // quiet
        opened["Clients/Globex"] = Now.AddDays(-60);        // has unread mail: stays
        opened["Clients/Acme"] = Now.AddDays(-60);          // Always show: stays
        var rows = LabelLayout.Layout(Gmail(), true, State(new() { ["Clients/Globex"] = 2 }, opened, new() { "Clients/Acme" }));
        var more = Assert.Single(rows, r => r.Kind == SidebarRowKind.More);
        Assert.Equal(LabelLayout.GroupKey, more.ParentPath);
        var inMore = rows.Where(r => r.ParentPath == LabelLayout.MoreKey).ToList();
        Assert.Equal(new[] { "Finance / Tax 2026", "Receipts" }, inMore.Select(r => r.Label));
        Assert.DoesNotContain(rows, r => r.Path == "Receipts" && r.ParentPath != LabelLayout.MoreKey);
        Assert.Contains(rows, r => r.Path == "Clients/Globex" && r.ParentPath == "Clients");
        Assert.Contains(rows, r => r.Path == "Clients/Acme" && r.ParentPath == "Clients");
        // More labels come last in the group.
        Assert.Equal(rows.Count - 3, rows.IndexOf(more));
    }

    [Fact]
    public void A_container_goes_when_all_its_labels_are_in_More_and_a_parent_only_with_its_children()
    {
        var opened = Gmail().ToDictionary(f => f.Path, _ => Now.AddDays(-90));
        opened["Travel/2026"] = Now;   // the child is in use, so its quiet parent stays as its parent
        var rows = LabelLayout.Layout(Gmail(), true, State(opened: opened));
        Assert.DoesNotContain(rows, r => r.Path == "Clients" || r.Path == "Finance");
        Assert.Contains(rows, r => r.Path == "Travel" && r.ParentPath == LabelLayout.GroupKey && r.HasChildren);
        Assert.Contains(rows, r => r.Label == "Clients / Acme" && r.ParentPath == LabelLayout.MoreKey);
        // A folder never seen before isn't quiet (it counts from when it was first seen).
        var fresh = LabelLayout.Layout(Gmail(), true, State(opened: new()));
        Assert.DoesNotContain(fresh, r => r.Kind == SidebarRowKind.More);
    }

    [Fact]
    public void Only_labels_with_unread_mail_keeps_their_parents_and_no_More()
    {
        var opened = Gmail().ToDictionary(f => f.Path, _ => Now.AddDays(-90));
        var rows = LabelLayout.Layout(Gmail(), true, State(new() { ["Finance/Bills"] = 1, ["INBOX"] = 4 }, opened, onlyUnread: true));
        Assert.Equal(new[] { "Finance", "Finance/Bills" }, LabelLayout.Under(rows, LabelLayout.GroupKey).Select(r => r.Path));
        Assert.True(rows.Single(r => r.Path == "Finance").HasChildren);
        Assert.Contains(rows, r => r.Path == "[Gmail]/Trash");   // the account's own folders always show
    }

    [Fact]
    public void Inbox_children_and_INBOX_dot_servers_are_left_as_they_were()
    {
        var dotted = new[] { F("INBOX", FolderRole.Inbox, '.'), F("INBOX.Sent", FolderRole.Sent, '.'), F("INBOX.Work", d: '.'), F("INBOX.Work.Old", d: '.') };
        var rows = LabelLayout.Layout(dotted, false, State(opened: dotted.ToDictionary(f => f.Path, _ => Now)));
        Assert.DoesNotContain(rows, r => r.Kind == SidebarRowKind.Container);
        Assert.Contains(rows, r => r.Path == "INBOX.Work.Old" && r.ParentPath == "INBOX.Work");
    }

    [Fact]
    public void Old_settings_get_the_new_sidebar_lists()
    {
        var s = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{\"Sidebar\":{\"OpenFolders\":[\"a|x\"],\"LabelsClosed\":null}}", AppSettings.Json)!;
        SettingsStore.Normalise(s);
        Assert.NotNull(s.Sidebar.LabelsClosed);
        Assert.NotNull(s.Sidebar.AlwaysShowLabels);
        Assert.NotNull(s.Sidebar.LabelOpened);
        Assert.Equal("a|x", Assert.Single(s.Sidebar.OpenFolders));
    }

    [Fact]
    public void A_missing_parent_under_the_accounts_own_folders_only_folds()
    {
        var list = new[] { F("INBOX", FolderRole.Inbox), F("Archive", FolderRole.Archive), F("Archive/2023/Jan") };
        var rows = LabelLayout.Layout(list, false, State(opened: list.ToDictionary(f => f.Path, _ => Now)));
        var mid = Assert.Single(rows, r => r.Path == "Archive/2023");
        Assert.Equal(SidebarRowKind.Container, mid.Kind);
        Assert.Null(mid.Folder);
        Assert.Equal("Archive", mid.ParentPath);
        Assert.DoesNotContain(rows, r => r.Kind == SidebarRowKind.Group);
    }

    [Fact]
    public void Odd_paths_with_empty_parts_stay_flat_and_never_loop()
    {
        var list = new[] { F("INBOX", FolderRole.Inbox), F("/Receipts"), F("a//b") };
        var opened = list.ToDictionary(f => f.Path, _ => Now.AddDays(-90));
        foreach (var only in new[] { false, true })
        {
            var rows = LabelLayout.Layout(list, true, State(new() { ["a//b"] = 1 }, opened, onlyUnread: only));
            Assert.DoesNotContain(rows, r => r.Kind == SidebarRowKind.Container);
            Assert.DoesNotContain(rows, r => r.Path.Length == 0);
        }
    }

    [Fact]
    public void The_open_label_stays_in_its_place_under_the_filter_and_More()
    {
        var opened = Gmail().ToDictionary(f => f.Path, _ => Now.AddDays(-90));
        var keep = new HashSet<string> { "Receipts" };
        var rows = LabelLayout.Layout(Gmail(), true, new LabelLayoutState { Opened = opened, Keep = keep, Now = Now });
        Assert.Contains(rows, r => r.Path == "Receipts" && r.ParentPath == LabelLayout.GroupKey);
        var filtered = LabelLayout.Layout(Gmail(), true, new LabelLayoutState { Opened = opened, Keep = keep, OnlyUnread = true, Now = Now });
        Assert.Equal(new[] { "Receipts" }, LabelLayout.Under(filtered, LabelLayout.GroupKey).Select(r => r.Path));
    }
}
