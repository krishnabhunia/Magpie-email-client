using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Magpie.App;
using Magpie.App.ViewModels;
using Magpie.App.Views;
using Magpie.Core;
using Magpie.Core.Models;
using Magpie.Core.Security;

namespace ProductivitySmoke;

internal static class Program
{
    private static int _assertions;
    [STAThread]
    public static int Main(string[] args)
    {
        var root = Path.Combine(Path.GetTempPath(), "magpie-productivity-" + Guid.NewGuid().ToString("N"));
        try
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            using var engine = new MailEngine(new AppPaths(root), new TestProtector(), new RejectHttp());
            AppServices.Engine = engine;
            engine.Config.Accounts.Add(new Account { Id = "A", Email = "me@example.test" });
            var inbox = engine.Store.UpsertFolder(new MailFolder { AccountId = "A", Path = "INBOX", Name = "Inbox", Role = FolderRole.Inbox });
            var now = DateTimeOffset.Now;
            var rows = engine.Store.InsertMessages(new[]
            {
                new MessageRow { AccountId = "A", FolderId = inbox, Uid = 1, MessageId = "one", ThreadKey = "one",
                    FromAddress = "boss@example.test", Subject = "Invoice", Date = now, SortDate = now, Flags = MessageFlags.Flagged },
                new MessageRow { AccountId = "A", FolderId = inbox, Uid = 2, MessageId = "two", ThreadKey = "two",
                    FromAddress = "friend@other.test", Subject = "Lunch", Date = now.AddMinutes(-1), SortDate = now.AddMinutes(-1) }
            });
            foreach (var row in rows) engine.Store.SaveBody(row.Id, new MessageBody { Html = "<p>Cached locally</p>", Text = "Cached locally", ImagesComplete = true });
            var owner = new Window { Width = 200, Height = 100, ShowActivated = false, Left = -3000, Top = -3000 };
            owner.Show();
            var output = args.Length > 0 ? args[0] : Path.Combine(root, "captures");
            Directory.CreateDirectory(output);
            foreach (var theme in new[] { "Light", "Dark" })
            {
                app.Resources.MergedDictionaries.Clear();
                app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Magpie;component/Themes/" + theme + ".xaml") });
                app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Magpie;component/Styles/Magpie.xaml") });
                CheckPalette(owner, output, theme);
                CheckSavedView(owner, engine, output, theme);
            }
            var vm = new MainViewModel();
            vm.OpenWorkspaceView(vm.Smart.First(n => n.Kind == NavKind.Important));
            Assert(vm.Threads.Count == 1 && vm.Threads[0].Row.ThreadKey == "one", "Important uses pinned inbox thread");
            vm.OpenWorkspaceView(vm.Smart.First(n => n.Kind == NavKind.Other));
            Assert(vm.Threads.Count == 1 && vm.Threads[0].Row.ThreadKey == "two", "Other excludes pinned inbox thread");
            vm.SaveWorkspaceView("Only A", "domain:example.test", "A", true);
            Assert(vm.Threads.Count == 1 && vm.Threads[0].Row.ThreadKey == "one", "Saved query is applied in native view model");
            var id = vm.Current!.FolderId;
            vm.SaveWorkspaceView("Removed scope", "domain:example.test", "missing", true, id);
            Assert(vm.Threads.Count == 0, "Removed account does not broaden saved view");
            vm.RemoveWorkspaceView(id);
            Assert(vm.Current?.Kind == NavKind.Inbox, "Removing current view restores inbox");
            owner.Close();
            Console.WriteLine("PRODUCTIVITY_SMOKE_PASSED assertions=" + _assertions);
            app.Shutdown();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static void CheckPalette(Window owner, string output, string theme)
    {
        var executed = false;
        var palette = new CommandPaletteWindow(owner, new[]
        {
            new WorkspaceCommand("Reply", "Open a reply draft", "R", "respond", () => executed = true, () => false, "Pick a conversation first"),
            new WorkspaceCommand("Draft with AI", "Review before inserting", "", "write compose", () => executed = true)
        });
        Exception? failure = null;
        palette.Dispatcher.BeginInvoke(() =>
        {
            try
            {
                var query = Descendants(palette).OfType<TextBox>().Single();
                var list = Descendants(palette).OfType<ListBox>().Single();
                var run = Descendants(palette).OfType<Button>().Single(b => Equals(b.Content, "Run command"));
                Assert(list.Items.Count == 2 && !run.IsEnabled, "Unavailable command stays visible and cannot run");
                Capture(palette, Path.Combine(output, theme + "-commands.png"));
                query.Text = "draft ai";
                Assert(list.Items.Count == 1 && run.IsEnabled, "Palette matches multiple words");
                Assert(!executed, "Matching and selection never execute a command");
                run.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            catch (Exception ex) { failure = ex; palette.Close(); }
        }, DispatcherPriority.ApplicationIdle);
        Assert(palette.ShowDialog() == true, "Enter/button selects a command");
        if (failure != null) throw failure;
        Assert(!executed && palette.Picked?.Title == "Draft with AI", "Action is returned for execution after modal closes");
        palette.Picked!.Run();
        Assert(executed, "Chosen action executes only when caller requests it");
    }

    private static void CheckSavedView(Window owner, MailEngine engine, string output, string theme)
    {
        Exception? failure = null;
        var dialog = new InboxViewDialog(owner, "domain:example.test")
        {
            Save = (name, query, account, inbox) => engine.Store.SaveInboxView(name, query, account, inbox)
        };
        dialog.Dispatcher.BeginInvoke(() =>
        {
            try
            {
                var fields = Descendants(dialog).OfType<TextBox>().Where(t => t.Parent is StackPanel).ToArray();
                Assert(fields.Length == 2, "Name and query fields are present");
                fields[0].Text = theme + " view";
                var run = Descendants(dialog).OfType<Button>().Single(b => Equals(b.Content, "Save view"));
                Capture(dialog, Path.Combine(output, theme + "-saved-view.png"));
                run.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            catch (Exception ex) { failure = ex; dialog.Close(); }
        }, DispatcherPriority.ApplicationIdle);
        Assert(dialog.ShowDialog() == true, "Saved-view dialog completes");
        if (failure != null) throw failure;
        Assert(engine.Store.GetInboxViews().Any(v => v.Name == theme + " view" && v.InboxOnly), "Dialog stores a local view");
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            yield return child;
            foreach (var next in Descendants(child)) yield return next;
        }
    }

    private static void Capture(Window window, string path)
    {
        window.UpdateLayout();
        var target = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(target.ActualWidth), (int)Math.Ceiling(target.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(target);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    private static void Assert(bool value, string reason)
    {
        if (!value) throw new InvalidOperationException(reason);
        _assertions++;
    }

    private sealed class TestProtector : ISecretProtector
    {
        public byte[] Protect(byte[] plain) => plain;
        public byte[] Unprotect(byte[] cipher) => cipher;
    }

    private sealed class RejectHttp : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("The productivity smoke test must not make network requests.");
    }
}
