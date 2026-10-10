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
        e.Store.UpsertFolder(new MailFolder { AccountId = AccountId, Path = "Projects", Name = "Projects", Role = FolderRole.Other });
        var now = DateTimeOffset.Now;
        e.Store.InsertMessages(new[]
        {
            Row(inbox, "t1", "anita@x.com", "Lunch on Friday?", now.AddMinutes(-5), MessageFlags.None),
            Row(inbox, "t2", "ravi@y.com", "Invoice 42", now.AddHours(-3), MessageFlags.Seen | MessageFlags.Flagged),
        });
        AppServices.Engine = e;
        return e;
    }

    private static MessageRow Row(long folder, string thread, string from, string subject, DateTimeOffset date, MessageFlags flags) => new()
    {
        AccountId = AccountId, FolderId = folder, Uid = Math.Abs(thread.GetHashCode()) % 100000 + 1, ThreadKey = thread,
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
        Assert.Equal(new[] { "Inbox", "Sent", "Projects" }, labels);
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
