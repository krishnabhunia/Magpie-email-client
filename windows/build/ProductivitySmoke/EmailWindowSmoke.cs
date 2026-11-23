using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Magpie.App;
using Magpie.App.ViewModels;
using Magpie.App.Views;
using Magpie.Core;
using Magpie.Core.Mail;
using Magpie.Core.Models;
using Magpie.Core.Settings;

namespace ProductivitySmoke;

internal static partial class Program
{
    /// <summary>
    /// Design OW1 (option A), in this theme: the reading pane's head is the shared view with every toolbar button, and
    /// an email's own window builds with the same head — all buttons but "New window" — and shows the email's subject.
    /// The window is opened without its browser: the page is the reader's, which the cached-reader check covers.
    /// </summary>
    private static void CheckEmailWindowLook(MainWindow window)
    {
        var vm = (MainViewModel)window.DataContext;
        var head = window.FindName("ReaderHead") as ReaderHeadView;
        Assert(head != null && ReferenceEquals(head.DataContext, vm.Reader), "The reading pane's head is the shared view, bound to the reader");
        Assert(head!.FindName("SubjectText") is TextBlock && head.FindName("DeleteChip") is FrameworkElement && head.FindName("ToolbarItems") is ItemsControl,
            "The shared head has the subject, the delete-date chip and the toolbar");
        Assert(vm.ToolbarButtons.Count == Appearance.DefaultVisible && vm.ToolbarButtons.Any(b => b.Id == "window" && b.Name == EmailWindow.ButtonName),
            "The toolbar has New window among its buttons by default");
        Assert(head.ToolbarCount == vm.ToolbarButtons.Count, "The reading pane shows every toolbar button");
        Assert(window.FindName("ThreadList") is ListBox list && list.ItemTemplate.LoadContent() is FrameworkElement row
            && LogicalTreeHelper.FindLogicalNode(row, "RowAvatar") is Grid,
            "The mail row names its avatar, so a double-click on it is told apart from one on the email");

        var engine = AppServices.Engine;
        var latest = engine.Store.GetThread("A", "one")[^1];
        var before = ThreadWindow.OpenCount;
        var w = ThreadWindow.Open(vm, new ThreadRow { AccountId = "A", ThreadKey = "one", Count = 1, Latest = latest }, activate: false, browser: false);
        try
        {
            Assert(w.IsVisible && w.ShowInTaskbar && ThreadWindow.OpenCount == before + 1, "An email opens in a window of its own, with its place on the taskbar");
            Assert(w.Title == EmailWindow.Title("Invoice") && w.MinWidth == EmailWindowPlacement.MinWidth && w.ResizeMode == ResizeMode.CanResize,
                "The window is named after the subject and can be resized (" + w.Title + ")");
            PumpUntil(() => w.ReaderModel.Messages.Count == 1);
            w.UpdateLayout();
            w.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
            Assert(ReferenceEquals(w.Head.DataContext, w.ReaderModel) && !ReferenceEquals(w.ReaderModel, vm.Reader) && w.ReaderModel.HasThread,
                "The window has a reader of its own; the reading pane keeps showing what it showed");
            Assert(w.Head.FindName("SubjectText") is TextBlock { Text: "Invoice", IsVisible: true } && w.Head.ActualHeight > 0,
                "The window's head shows the email's subject");
            Assert(w.Head.ToolbarCount == vm.ToolbarButtons.Count - 1, "The window's toolbar has every button but New window");
            var buttons = Descendants(w.Head).OfType<Button>().Where(b => b.Tag is ToolbarButtonVm).ToList();
            Assert(buttons.Count == w.Head.ToolbarCount && buttons.All(b => b.IsVisible && b.ActualWidth > 0 && ((ToolbarButtonVm)b.Tag).Id != "window"),
                $"The window's toolbar buttons are laid out ({buttons.Count} of {w.Head.ToolbarCount})");
            Assert(w.PreviousButton.ToolTip as string == EmailWindow.PreviousTip && w.NextButton.ToolTip as string == EmailWindow.NextTip,
                "Previous and Next say what they do and their keys");
        }
        finally { w.Close(); }
        Assert(ThreadWindow.OpenCount == before && !Application.Current.Windows.Cast<Window>().Contains(w), "Closing the window forgets it");
        Console.WriteLine("EMAIL_WINDOW_LOOK_SMOKE_PASSED");
    }

    /// <summary>
    /// Design OW1 (option A) in the native view models: one window per conversation, where it is in its list, previous /
    /// next, actions on the folders of the list it was opened from (whatever the main window shows by then), and the
    /// size and place the next window opens with.
    /// </summary>
    private static void CheckEmailWindow(MainViewModel vm, MailEngine engine, long inbox)
    {
        var now = DateTimeOffset.Now;
        var rows = engine.Store.InsertMessages(Enumerable.Range(1, 3).Select(i => new MessageRow
        {
            AccountId = "A", FolderId = inbox, Uid = 970 + i, MessageId = "window-" + i, ThreadKey = "window-" + i, FromAddress = "rajesh@buildwell.test",
            To = "me@example.test", Subject = "Handover " + i, Date = now.AddMinutes(10 - i), SortDate = now.AddMinutes(10 - i), Flags = MessageFlags.Seen,
        }).ToArray());
        // A conversation of two emails, older than the three above.
        rows.AddRange(engine.Store.InsertMessages(Enumerable.Range(1, 2).Select(i => new MessageRow
        {
            AccountId = "A", FolderId = inbox, Uid = 980 + i, MessageId = "window-talk-" + i, ThreadKey = "window-talk", FromAddress = "rajesh@buildwell.test",
            To = "me@example.test", Subject = "Snag list", Date = now.AddHours(-5 + i), SortDate = now.AddHours(-5 + i), Flags = MessageFlags.Seen,
        }).ToArray()));
        foreach (var row in rows) engine.Store.SaveBody(row.Id, new MessageBody { Html = "<p>Cached locally</p>", Text = "Cached locally", ImagesComplete = true });
        engine.Config.EmailWindow = new EmailWindowPlacement();
        engine.Config.OpenInWindowOnDoubleClick = true;
        var inboxNav = vm.Smart.First(n => n.Kind == NavKind.Inbox);
        vm.SearchText = "";
        vm.NavigateCommand.Execute(inboxNav);
        vm.ReloadList();
        ThreadRow Row(int i) => vm.Threads.Single(t => t.Row.ThreadKey == "window-" + i).Row;
        var keys = vm.Threads.Where(t => t.LocalDraftId == null).Select(t => EmailWindow.Key(t.Row.AccountId, t.Row.ThreadKey)).Distinct().ToList();
        Assert(keys.Count >= 3 && keys.Take(3).SequenceEqual(new[] { EmailWindow.Key("A", "window-1"), EmailWindow.Key("A", "window-2"), EmailWindow.Key("A", "window-3") }),
            "The three new conversations lead the Inbox, newest first");
        Assert(ThreadWindow.OpenCount == 0, "No email window is open before the check");

        var first = ThreadWindow.Open(vm, Row(1), activate: false, browser: false);
        ThreadWindow? second = null, third = null, talk = null;
        try
        {
            Assert(ThreadWindow.OpenCount == 1 && ReferenceEquals(ThreadWindow.Open(vm, Row(1), activate: false, browser: false), first) && ThreadWindow.OpenCount == 1,
                "Opening the same email again brings its window forward instead of making another");
            Assert(first.ConversationKey == keys[0] && first.Title == EmailWindow.Title("Handover 1"), "The window shows the email that was opened");
            Assert(first.NavText.Text == EmailWindow.Position(keys, keys[0], vm.ListTitle) && first.NavText.Text.StartsWith($"1 of {keys.Count} in ", StringComparison.Ordinal),
                "The window says where the email is in its list (" + first.NavText.Text + ")");
            Assert(!first.PreviousButton.IsEnabled && first.NextButton.IsEnabled, "At the top of the list only Next is offered");
            PumpUntil(() => first.ReaderModel.Messages.Count == 1 && first.ReaderModel.ThreadKey == "window-1");

            // Next: the same window shows the following email.
            Assert(first.Step(1) && first.ConversationKey == keys[1] && ThreadWindow.OpenCount == 1, "Next shows the following email in the same window");
            PumpUntil(() => first.ReaderModel.ThreadKey == "window-2" && first.ReaderModel.Messages.Count == 1);
            first.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
            Assert(first.Title == EmailWindow.Title("Handover 2") && first.NavText.Text == EmailWindow.Position(keys, keys[1], vm.ListTitle) && first.PreviousButton.IsEnabled,
                "The title and the position follow (" + first.Title + " · " + first.NavText.Text + ")");

            // The email it left can have a window of its own again; stepping back makes way for that window.
            second = ThreadWindow.Open(vm, Row(1), activate: false, browser: false);
            Assert(!ReferenceEquals(second, first) && ThreadWindow.OpenCount == 2 && second.ConversationKey == keys[0], "Another email gets another window");
            Assert(first.Step(-1) && first.ConversationKey == keys[1] && ThreadWindow.OpenCount == 2,
                "Previous does not show an email twice: its own window comes forward instead");
            second.Close();
            second = null;
            Assert(ThreadWindow.OpenCount == 1, "Closing one window leaves the other");

            // Two emails of one conversation, each opened from its own row (the "emails with one address" list): the
            // conversation's one window goes to the email that was opened last.
            var emails = engine.Store.GetThread("A", "window-talk");
            Assert(emails.Count == 2, "The conversation of two emails is there");
            var talkRow = new ThreadRow { AccountId = "A", ThreadKey = "window-talk", Count = 2, Latest = emails[^1] };
            talk = ThreadWindow.Open(vm, talkRow, focus: emails[1], activate: false, browser: false);
            ReaderPage? shown = null;
            talk.ReaderModel.PageReady += page => shown = page;
            Assert(ReferenceEquals(ThreadWindow.Open(vm, talkRow, focus: emails[0], activate: false, browser: false), talk) && ThreadWindow.OpenCount == 2,
                "Another email of the same conversation uses the conversation's window");
            PumpUntil(() => shown?.Focus == emails[0].Id);
            Assert(shown!.Key == "A\nwindow-talk" && shown.Focus == emails[0].Id && talk.ReaderModel.Messages.Count == 2,
                "That window goes to the email that was just opened");
            talk.Close();
            talk = null;
            Assert(ThreadWindow.OpenCount == 1, "Closing it leaves the first window");

            // The main window moves on to Trash; Delete in the email window still acts on the Inbox it was opened from.
            vm.NavigateCommand.Execute(vm.Smart.First(n => n.Kind == NavKind.Role && n.Role == FolderRole.Trash));
            var trash = engine.Store.GetFolders("A").Single(f => f.Role == FolderRole.Trash).Id;
            Assert(vm.IsTrashView && vm.ActionFolders("A").SequenceEqual(new[] { trash }), "The main window's own actions are on Trash now");
            var folders = first.ReaderModel.ActionFolders!("A", "window-2");
            Assert(folders.Contains(inbox) && !folders.Contains(trash),
                "The email window's actions stay on the list it was opened from");
            first.Left = 40;
            first.Top = 30;
            var size = (first.Width, first.Height);
            first.ReaderModel.DeleteCommand.Execute(null);
            PumpUntil(() => ThreadWindow.OpenCount == 0);
            Assert(engine.Store.GetThreadCopies("A", "window-2").All(m => m.FolderId != inbox) && engine.Store.GetThreadCopies("A", "window-1").Any(m => m.FolderId == inbox),
                "Delete in the email window took that email out of the Inbox, and only that one");
            Assert(!Application.Current.Windows.Cast<Window>().Contains(first), "The window closes when its email is deleted");
            var saved = engine.Config.EmailWindow;
            Assert(saved.HasSavedPlacement && saved.Left == 40 && saved.Top == 30 && saved.Width == size.Width && saved.Height == size.Height && !saved.Maximized,
                $"Its size and place are kept ({saved.Left},{saved.Top} {saved.Width}×{saved.Height})");

            // The next window opens there. It was opened from Trash, where this email is not: no position, no steps.
            third = ThreadWindow.Open(vm, new ThreadRow { AccountId = "A", ThreadKey = "window-3", Count = 1, Latest = engine.Store.GetThread("A", "window-3")[^1] }, activate: false, browser: false);
            Assert(third.Left == 40 && third.Top == 30 && third.Width == size.Width && third.Height == size.Height, "The next email window opens with that size and place");
            Assert(third.NavText.Text == "" && !third.Step(1) && !third.Step(-1) && third.ConversationKey == keys[2], "An email that is not in the list on screen has no previous or next");
            Assert(third.ReaderModel.ActionFolders!("A", "window-3").Contains(inbox), "Its actions find the folder the email is in");

            // The email is deleted somewhere else (the list, the phone): its window has nothing left to show and closes.
            PumpUntil(() => third.ReaderModel.Messages.Count == 1);
            foreach (var copy in engine.Store.GetThreadCopies("A", "window-3")) engine.Store.DeleteRow(copy.Id);
            third.ReaderModel.RefreshIfShowing(new ThreadRow { AccountId = "A", ThreadKey = "window-3" });
            PumpUntil(() => ThreadWindow.OpenCount == 0);
            third = null;
        }
        finally
        {
            second?.Close();
            third?.Close();
            talk?.Close();
            if (Application.Current.Windows.Cast<Window>().Contains(first)) first.Close();
        }
        vm.Reader.Clear();
        vm.NavigateCommand.Execute(inboxNav);
        Console.WriteLine("EMAIL_WINDOW_SMOKE_PASSED");
    }
}
