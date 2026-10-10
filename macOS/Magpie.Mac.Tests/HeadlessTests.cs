using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Magpie.Core;
using Magpie.Core.Mail;
using Magpie.Core.Models;
using Magpie.Core.Security;
using Magpie.Mac.Services;
using Magpie.Mac.ViewModels;
using Magpie.Mac.Views;

[assembly: AvaloniaTestApplication(typeof(Magpie.Mac.Tests.TestApp))]

namespace Magpie.Mac.Tests;

public static class TestApp
{
    public static AppBuilder BuildAvaloniaApp()
    {
        // Never a real WKWebView under the headless platform (also on the macOS CI runner).
        Environment.SetEnvironmentVariable("MAGPIE_HEADLESS", "1");
        return AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
    }
}

/// <summary>One engine for the test run, on a temporary data folder with an account, two folders and mail.</summary>
public static class TestEngine
{
    private sealed class MemoryKeys : IKeyStore
    {
        private byte[]? _key;
        public byte[]? Load() => _key;
        public void Save(byte[] key) => _key = key;
    }

    public static string AccountId { get; } = "acc1";

    public static MailEngine Ensure()
    {
        if (AppServices.Engine != null) return AppServices.Engine;
        var dir = Path.Combine(Path.GetTempPath(), "magpie-mac-test-" + Guid.NewGuid().ToString("N")[..8]);
        var e = new MailEngine(new AppPaths(dir), new KeychainProtector(new MemoryKeys()));
        e.Settings.Current.Accounts.Add(new Account { Id = AccountId, Email = "me@example.com", Kind = AccountKind.Imap, ImapHost = "imap.example.com", Color = "#14606E" });
        e.Settings.Save();
        var inbox = e.Store.UpsertFolder(new MailFolder { AccountId = AccountId, Path = "INBOX", Name = "Inbox", Role = FolderRole.Inbox });
        e.Store.UpsertFolder(new MailFolder { AccountId = AccountId, Path = "Sent", Name = "Sent", Role = FolderRole.Sent });
        var projects = e.Store.UpsertFolder(new MailFolder { AccountId = AccountId, Path = "Projects", Name = "Projects", Role = FolderRole.Other });
        var drafts = e.Store.UpsertFolder(new MailFolder { AccountId = AccountId, Path = "Drafts", Name = "Drafts", Role = FolderRole.Drafts });
        var trash = e.Store.UpsertFolder(new MailFolder { AccountId = AccountId, Path = "Trash", Name = "Trash", Role = FolderRole.Trash });
        var bulk = e.Store.UpsertFolder(new MailFolder { AccountId = AccountId, Path = "Bulk", Name = "Bulk", Role = FolderRole.Other });
        var now = DateTimeOffset.Now;
        e.Store.InsertMessages(new[]
        {
            Row(inbox, "t1", "anita@x.com", "Lunch on Friday?", now.AddMinutes(-5), MessageFlags.None),
            Row(inbox, "t2", "ravi@y.com", "Invoice 42", now.AddHours(-3), MessageFlags.Seen | MessageFlags.Flagged),
            Row(projects, "p1", "lee@z.com", "Roadmap notes", now.AddDays(-1), MessageFlags.Seen),
            Row(drafts, "d1", "me@example.com", "Draft plan", now.AddHours(-1), MessageFlags.Seen | MessageFlags.Draft),
            Row(trash, "x1", "shop@w.com", "Old receipt", now.AddDays(-2), MessageFlags.Seen),
        });
        e.Store.InsertMessages(Enumerable.Range(0, 405).Select(n => Row(bulk, "b" + n, "list@news.com", "Bulk " + n, now.AddMinutes(-n), MessageFlags.Seen)));
        e.SaveLocalDraft(new Draft { AccountId = AccountId, To = "anita@x.com", Subject = "Written offline", Html = "<p>Hi</p>" }, pendingUpload: true);
        AppServices.Engine = e;
        return e;
    }

    private static long _uid = 1;

    private static MessageRow Row(long folder, string thread, string from, string subject, DateTimeOffset date, MessageFlags flags) => new()
    {
        AccountId = AccountId, FolderId = folder, Uid = Interlocked.Increment(ref _uid), ThreadKey = thread,
        FromAddress = from, FromName = from.Split('@')[0], Subject = subject, Date = date, SortDate = date, Flags = flags,
        MessageId = Guid.NewGuid().ToString("N") + "@x.com", Preview = "Preview of " + subject, To = "me@example.com",
    };
}

public class HeadlessTests
{
    [AvaloniaFact]
    public void Main_window_shows_the_version_and_no_update_button_without_a_newer_version()
    {
        TestEngine.Ensure();
        AppServices.Updates ??= new MacUpdateService();
        var w = new MainWindow();
        w.AttachUpdates(AppServices.Updates);
        w.Show();
        var version = w.FindControl<TextBlock>("VersionLabel")!;
        Assert.Equal(AppServices.Current.ToString(), version.Text);
        Assert.False(AppServices.Updates.IsUpdateVisible);
        Assert.Equal("", AppServices.Updates.ButtonText);
        Assert.False(w.FindControl<Button>("UpdateButton")!.IsEffectivelyVisible);
        w.Close();
    }

    [AvaloniaFact]
    public void Sidebar_lists_all_inboxes_the_account_and_its_folders_with_unread_counts()
    {
        TestEngine.Ensure();
        var vm = new MainViewModel();
        Assert.Equal(SidebarKind.AllInboxes, vm.Sidebar[0].Kind);
        Assert.Contains(vm.Sidebar, s => s.IsHeader && s.Label == "me@example.com");
        var labels = vm.Sidebar.Where(s => s.Kind == SidebarKind.Folder).Select(s => s.Label).ToList();
        Assert.Equal("Inbox", labels[0]);
        Assert.Equal(new[] { "Bulk", "Drafts", "Inbox", "Projects", "Sent", "Trash" }, labels.OrderBy(l => l));
        Assert.Equal("All inboxes", vm.ListTitle);
        Assert.Equal(new[] { "Lunch on Friday?", "Invoice 42" }, vm.Threads.Select(t => t.Subject));
        Assert.True(vm.Threads[0].IsUnread);
        Assert.True(vm.Threads[1].IsPinned);
        vm.RefreshCounts();
    }

    [AvaloniaFact]
    public void Picking_an_account_heading_keeps_the_view_and_search_filters_the_list()
    {
        TestEngine.Ensure();
        var vm = new MainViewModel();
        var first = vm.Current;
        vm.Current = vm.Sidebar.First(s => s.IsHeader);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Equal(first?.Key, vm.Current?.Key);
        vm.SearchText = "invoice";
        vm.ReloadList();
        Assert.Equal(new[] { "Invoice 42" }, vm.Threads.Select(t => t.Subject));
    }

    [AvaloniaFact]
    public void Compose_window_add_account_and_settings_open_without_a_display()
    {
        TestEngine.Ensure();
        var compose = new ComposeWindow(new Draft { AccountId = TestEngine.AccountId, To = "anita@x.com", Subject = "Hi" });
        compose.Show();
        Assert.Equal("Hi", compose.Title);
        compose.Close();

        var add = new AddAccountWindow(null);
        add.Show();
        add.ViewModel.Email = "someone@gmail.com";
        Assert.Equal(AccountKind.Gmail, add.ViewModel.EffectiveKind);
        Assert.True(add.ViewModel.ShowGoogle);
        Assert.False(add.ViewModel.ShowPassword);
        add.ViewModel.Email = "someone@outlook.com";
        Assert.True(add.ViewModel.ShowMicrosoft);
        add.Close();

        var settings = new SettingsWindow();
        settings.Show();
        settings.SelectTab("Updates");
        Assert.Single(settings.ViewModel.Accounts);
        var file = Path.Combine(Path.GetTempPath(), "client-" + Guid.NewGuid().ToString("N")[..6] + ".json");
        File.WriteAllText(file, """{"installed":{"client_id":"1.apps.googleusercontent.com","client_secret":"GOCSPX-a"}}""");
        Assert.Null(settings.ViewModel.ImportGoogleJson(file));
        Assert.Equal("1.apps.googleusercontent.com", AppServices.Engine.Config.GoogleClientId);
        settings.Close();
    }

    [AvaloniaFact]
    public void Windows_use_the_placeholder_instead_of_a_real_web_view_when_headless()
    {
        TestEngine.Ensure();
        Assert.False(WebSurface.UseNativeView());
        var w = new MainWindow();
        Assert.False(w.ReaderSurface.IsNative);
        Assert.IsType<TextBlock>(w.ReaderSurface.Child);
    }

    [AvaloniaFact]
    public void After_the_button_installed_an_update_quitting_installs_nothing()
    {
        TestEngine.Ensure();
        var updates = new MacUpdateService();
        var engine = AppServices.Engine;
        engine.Config.Updates.AutoUpdate = true;
        var release = new Magpie.Core.Updates.ReleaseInfo { Version = Magpie.Core.Updates.AppVersion.TryParse("99.0.0")!, Tag = "v99.0.0" };
        updates.PretendReady(release, "/tmp/Magpie_99.0.0.dmg", new string('0', 64));
        Assert.True(updates.WouldInstallOnQuit);
        Assert.Equal("Update to v99.0.0", updates.ButtonText);
        updates.PretendInstalled();                       // what UpdateNowAsync does after a successful install
        Assert.False(updates.WouldInstallOnQuit);
        Assert.False(updates.IsUpdateVisible);
        updates.InstallOnQuit();                          // and the quit path does nothing (no second swap)
        Assert.Equal(UpdateState.Idle, updates.State);
    }

    [Fact]
    public void Links_from_emails_open_only_when_they_are_web_links()
    {
        Assert.True(Shell.IsWebLink("https://example.com/a?b=c", out var u));
        Assert.Equal("https://example.com/a?b=c", u.AbsoluteUri);
        Assert.True(Shell.IsWebLink("http://example.com", out _));
        Assert.False(Shell.IsWebLink("file:///Applications/Calculator.app", out _));
        Assert.False(Shell.IsWebLink("-a Calculator", out _));
        Assert.False(Shell.IsWebLink("/Applications/Calculator.app", out _));
        Assert.False(Shell.IsWebLink("javascript:alert(1)", out _));
        Assert.False(Shell.IsWebLink("smb://server/share", out _));
        Assert.False(Shell.IsWebLink(null, out _));
    }

    private static MainViewModel ViewOf(string folder)
    {
        var vm = new MainViewModel();
        vm.Current = vm.Sidebar.First(x => x.Kind == SidebarKind.Folder && x.Label == folder);
        return vm;
    }

    private static async Task Until(Func<bool> done)
    {
        for (var i = 0; i < 250 && !done(); i++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
        Assert.True(done());
    }

    [AvaloniaFact]
    public void Drafts_kept_on_this_Mac_are_listed_first_and_open_as_drafts()
    {
        TestEngine.Ensure();
        var vm = ViewOf("Drafts");
        Assert.True(vm.Threads[0].IsLocalDraft);
        Assert.Equal("Written offline", vm.Threads[0].Subject);
        Assert.Equal("On this Mac · uploads when online", vm.Threads[0].LocalBadge);
        Assert.Contains(vm.Threads, t => t.Subject == "Draft plan" && !t.IsLocalDraft);
        vm.Selected = vm.Threads[0];
        Assert.True(vm.Reader.HasThread);
        Assert.True(vm.Reader.IsAnyDraft);
        Assert.False(vm.Reader.ShowMessageActions);
        Assert.NotNull(AppServices.Engine.OpenLocalDraft(vm.Reader.LocalDraftId!.Value));   // what Edit draft opens
    }

    [AvaloniaFact]
    public async Task A_server_draft_shows_Edit_draft_instead_of_reply()
    {
        TestEngine.Ensure();
        var vm = ViewOf("Drafts");
        vm.Selected = vm.Threads.First(t => t.Subject == "Draft plan");
        await Until(() => vm.Reader.IsDraft);
        Assert.True(vm.Reader.IsAnyDraft);
        Assert.False(vm.Reader.ShowMessageActions);
        Assert.Equal("Delete", vm.Reader.DeleteText);
    }

    [AvaloniaFact]
    public async Task In_Trash_Delete_says_Delete_forever_and_elsewhere_it_moves_to_Trash()
    {
        TestEngine.Ensure();
        var vm = ViewOf("Trash");
        vm.Selected = vm.Threads.First(t => t.Subject == "Old receipt");
        await Until(() => vm.Reader.DeletesForever);
        Assert.Equal("Delete forever", vm.Reader.DeleteText);
        var inbox = new MainViewModel();
        inbox.Selected = inbox.Threads.First(t => t.Subject == "Invoice 42");
        await Until(() => inbox.Reader.Subject == "Invoice 42");
        Assert.False(inbox.Reader.DeletesForever);
        Assert.Equal("Delete", inbox.Reader.DeleteText);
    }

    [AvaloniaFact]
    public void A_search_in_All_inboxes_acts_on_the_folders_it_searched()
    {
        var e = TestEngine.Ensure();
        var folders = e.Folders(TestEngine.AccountId).ToDictionary(f => f.Name, f => f.Id);
        var vm = new MainViewModel();
        Assert.Equal(new[] { folders["Inbox"] }, vm.ActionFolders(TestEngine.AccountId));
        vm.SearchText = "roadmap";
        vm.ReloadList();
        Assert.Equal(new[] { "Roadmap notes" }, vm.Threads.Select(t => t.Subject));
        var scope = vm.ActionFolders(TestEngine.AccountId);
        Assert.Contains(folders["Projects"], scope);
        Assert.Contains(folders["Inbox"], scope);
        Assert.DoesNotContain(folders["Sent"], scope);
        Assert.DoesNotContain(folders["Drafts"], scope);
    }

    [AvaloniaFact]
    public void Long_folders_load_400_at_a_time()
    {
        TestEngine.Ensure();
        var vm = ViewOf("Bulk");
        Assert.Equal(MainViewModel.ListLimit, vm.Threads.Count);
        Assert.True(vm.HasMore);
        vm.LoadMoreCommand.Execute(null);
        Assert.Equal(405, vm.Threads.Count);
        Assert.False(vm.HasMore);
        vm.Current = vm.Sidebar.First(x => x.Label == "Inbox");    // a new view starts at 400 again
        vm.Current = vm.Sidebar.First(x => x.Kind == SidebarKind.Folder && x.Label == "Bulk");
        Assert.Equal(MainViewModel.ListLimit, vm.Threads.Count);
    }

    [AvaloniaFact]
    public async Task When_the_open_conversation_leaves_the_list_the_reader_is_cleared()
    {
        TestEngine.Ensure();
        var vm = ViewOf("Projects");
        vm.Selected = vm.Threads.Single();
        await Until(() => vm.Reader.Subject == "Roadmap notes");
        vm.SearchText = "nothing-matches-this";
        vm.ReloadList();
        Assert.Null(vm.Selected);
        Assert.False(vm.Reader.HasThread);
    }

    [AvaloniaFact]
    public void Signing_in_again_keeps_custom_servers_until_the_type_changes()
    {
        TestEngine.Ensure();
        var acc = new Account { Id = "re1", Email = "me@corp.example", Kind = AccountKind.Imap, ImapHost = "mail.internal.example", ImapPort = 1993, SmtpHost = "out.internal.example" };
        var vm = new AddAccountViewModel(acc);
        Assert.False(vm.WouldLookUpServers);
        Assert.Equal("mail.internal.example", vm.ImapHost);
        Assert.Equal(1993, vm.ImapPort);
        vm.Choice = vm.Choices.First(c => c.Value == AccountChoice.Google);
        Assert.Equal(AccountKind.Gmail, vm.EffectiveKind);
    }

    [Theory]
    [InlineData("https://example.com/x", "https://example.com/x")]
    [InlineData("example.com", "https://example.com/")]
    [InlineData("mailto:anita@x.com", "mailto:anita@x.com")]
    [InlineData("anita@x.com", "mailto:anita@x.com")]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("file:///etc/passwd", null)]
    [InlineData("", null)]
    public void Links_in_a_message_can_only_be_web_or_mail(string text, string? expected) =>
        Assert.Equal(expected, ComposeViewModel.AllowedLink(text));

    [Fact]
    public void Send_later_choices_follow_the_clock()
    {
        var morning = ComposeViewModel.SendLaterChoices(new DateTime(2026, 10, 12, 9, 0, 0));
        var night = ComposeViewModel.SendLaterChoices(new DateTime(2026, 10, 12, 22, 0, 0));
        Assert.Contains(morning, p => p.Label == "Later today");
        Assert.DoesNotContain(night, p => p.Label == "Later today");
    }

    [Fact]
    public void Address_suggestions_replace_only_the_address_being_typed()
    {
        Assert.Equal("ravi", ComposeViewModel.LastToken("anita@x.com, ravi"));
        Assert.Equal("anita@x.com, Ravi <ravi@y.com>, ", ComposeViewModel.ReplaceLastToken("anita@x.com, rav", "Ravi <ravi@y.com>"));
        Assert.Equal("Ravi <ravi@y.com>, ", ComposeViewModel.ReplaceLastToken("rav", "Ravi <ravi@y.com>"));
    }

    [AvaloniaFact]
    public void The_menu_bar_has_the_Mac_menus_and_keys()
    {
        TestEngine.Ensure();
        var w = new Window();
        var bar = MacMenus.ForWindow(w);
        var tops = bar.Items.OfType<NativeMenuItem>().ToList();
        Assert.Equal(new[] { "File", "Edit", "View", "Message", "Window" }, tops.Select(t => t.Header));
        NativeMenuItem Find(string top, string header) => tops.First(t => t.Header == top).Menu!.Items.OfType<NativeMenuItem>().First(i => i.Header == header);
        Assert.Equal(new KeyGesture(Key.N, KeyModifiers.Meta), Find("File", "New Message").Gesture);
        Assert.Equal(new KeyGesture(Key.R, KeyModifiers.Meta), Find("Message", "Reply").Gesture);
        Assert.Equal(new KeyGesture(Key.R, KeyModifiers.Meta | KeyModifiers.Shift), Find("Message", "Reply All").Gesture);
        Assert.Equal(new KeyGesture(Key.F, KeyModifiers.Meta | KeyModifiers.Shift), Find("Message", "Forward").Gesture);
        Assert.Equal(new KeyGesture(Key.F, KeyModifiers.Meta), Find("Edit", "Find").Gesture);
        var app = MacMenus.AppMenu().Items.OfType<NativeMenuItem>().Where(i => i is not NativeMenuItemSeparator).Select(i => i.Header).ToList();
        Assert.Equal(new[] { "About Magpie", "Settings…", "Check for Updates…" }, app);
    }
}
