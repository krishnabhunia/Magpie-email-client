using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Magpie.App;
using Magpie.App.ViewModels;
using Magpie.Core;
using Magpie.Core.Mail;
using Magpie.Core.Models;
using Magpie.Core.Storage;

namespace ProductivitySmoke;

internal static partial class Program
{
    /// <summary>
    /// Design AL1 (option C): the production mail-row template shows the "Auto-deleted · date, time" label in this
    /// theme's amber, and the main window has the Trash filter row and the banner above the open email.
    /// </summary>
    private static void CheckAutoDeletedRow(MainWindow window)
    {
        Assert(window.FindName("TrashFilterBar") is WrapPanel { Children.Count: 3 } bar && bar.Children.OfType<RadioButton>().Count() == 3,
            "Main window has the three Trash choices: all, auto-deleted, deleted by you or elsewhere");
        // The three choices are wider than a narrow list pane: they wrap to a second line instead of running out of it.
        var narrow = new WrapPanel { Width = 284 };
        foreach (var words in new[] { "All · 1,250", "Auto-deleted · 1,000", "Deleted by you or elsewhere · 250" })
            narrow.Children.Add(new RadioButton { Style = (Style)window.FindResource("Button.Tab"), Padding = new Thickness(10, 6, 10, 6), Content = words });
        narrow.Measure(new Size(284, double.PositiveInfinity));
        narrow.Arrange(new Rect(narrow.DesiredSize));
        var widest = narrow.Children.OfType<RadioButton>().Max(b => b.DesiredSize.Width);
        Assert(widest > 0 && widest <= 284 && narrow.DesiredSize.Height > narrow.Children[0].DesiredSize.Height * 1.5,
            $"The Trash choices fit the narrowest list pane on more than one line (widest {widest:0} of 284)");
        Assert(window.FindName("AutoDeletedBanner") is Border && window.FindName("AutoDeletedRestore") is Button { Content: "Restore" },
            "Main window has the auto-deleted banner with Restore");

        var list = (ListBox)window.FindName("ThreadList");
        var template = list.ItemTemplate;
        var presenter = new ContentPresenter { ContentTemplate = template };
        var host = new Window
        {
            Content = presenter, DataContext = window.DataContext, Width = 650, Height = 180,
            ShowActivated = false, ShowInTaskbar = false, Left = -3000, Top = -3000,
        };
        var now = DateTimeOffset.Now;
        var at = now.AddHours(-3);
        var note = new TrashNote(at, "r1", "Rule: offers@shop.test after 30 days", 1);
        try
        {
            host.Show();
            foreach (var marked in new[] { false, true })
            {
                var item = new ThreadItem(new ThreadRow
                {
                    AccountId = "A", ThreadKey = "auto-deleted-smoke", Count = 1,
                    Latest = new MessageRow { Subject = "Weekend sale", FromAddress = "offers@shop.test", Flags = MessageFlags.Seen, Date = now, SortDate = now },
                }, "#14606E", false, now, "me@example.test")
                {
                    AutoDeletedLabel = marked ? AutoDeleted.RowLabel(at, now) : "",
                    AutoDeletedTip = marked ? AutoDeleted.Tip(note) : null,
                };
                presenter.Content = item;
                presenter.ApplyTemplate();
                host.UpdateLayout();
                host.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
                var label = template.FindName("AutoDeletedRowLabel", presenter) as Border
                    ?? throw new InvalidOperationException("The realized mail-row template has no named auto-deleted label.");
                label.GetBindingExpression(UIElement.VisibilityProperty)?.UpdateTarget();
                host.UpdateLayout();
                Assert(item.HasAutoDeleted == marked && item.HasTagLine == marked && label.Visibility == (marked ? Visibility.Visible : Visibility.Collapsed),
                    $"Mail-row auto-deleted label follows the row (marked={marked}, visibility={label.Visibility})");
                if (!marked) continue;
                var text = label.Child as TextBlock ?? throw new InvalidOperationException("The auto-deleted label has no text.");
                Assert(label.IsVisible && label.ActualWidth > 0, "Auto-deleted label is laid out on the row, not only switched on");
                Assert(text.Text == AutoDeleted.RowLabel(at, now) && text.Text.StartsWith("Auto-deleted · ", StringComparison.Ordinal),
                    "Auto-deleted label says the date and time (" + text.Text + ")");
                Assert(label.ToolTip as string == AutoDeleted.Tip(note) && ((string)label.ToolTip).StartsWith("Deleted by Magpie auto delete\n", StringComparison.Ordinal),
                    "Pointing at the label says who deleted it, the full date and time, and the rule");
                Assert(text.Foreground is SolidColorBrush fg && host.TryFindResource("Brush.Warning") is SolidColorBrush warning && fg.Color == warning.Color
                    && label.Background is SolidColorBrush bg && host.TryFindResource("Brush.Warning.Subtle") is SolidColorBrush subtle && bg.Color == subtle.Color,
                    "Auto-deleted label uses the current theme's amber pair");
            }
            Console.WriteLine("AUTO_DELETED_ROW_SMOKE_PASSED");
        }
        finally { presenter.Content = null; host.Close(); }
    }

    /// <summary>
    /// Design AL1 (option C) in the native view models: Trash labels what auto delete moved there, the filter splits
    /// the list, the banner of the open conversation says when / rule / where it was, and Restore ends it.
    /// </summary>
    private static void CheckAutoDeletedTrash(MainViewModel vm, MailEngine engine, long inbox)
    {
        var trash = engine.Store.UpsertFolder(new MailFolder { AccountId = "A", Path = "Trash", Name = "Trash", Role = FolderRole.Trash });
        var now = DateTimeOffset.Now;
        var at = now.AddHours(-3);
        var rule = new AutoDeleteRule { Pattern = "offers@shop.test", Amount = 30 };
        engine.Store.SaveAutoDeleteRule(rule);
        MessageRow Mail(long folder, int uid, string id, string thread, string from, DateTimeOffset date) => new()
        {
            AccountId = "A", FolderId = folder, Uid = uid, MessageId = id, ThreadKey = thread, FromAddress = from, To = "me@example.test",
            Subject = thread, Date = date, SortDate = date, Flags = MessageFlags.Seen,
        };
        var arrived = now.AddDays(-31);
        var offers = engine.Store.UpsertFolder(new MailFolder { AccountId = "A", Path = "Offers", Name = "Offers" });
        // What Magpie wrote when the emails left the Inbox: two by auto delete (one conversation of two emails, the
        // later one under a rule that is gone since), one by hand.
        engine.Store.RecordTrashed(new[] { Mail(inbox, 0, "gone-1", "Weekend sale", "offers@shop.test", arrived) }, at, _ => AutoDeleted.RuleSnapshot(rule));
        engine.Store.RecordTrashed(new[] { Mail(inbox, 0, "gone-2", "Price drop", "offers@shop.test", arrived) }, at.AddHours(-20), _ => AutoDeleted.RuleSnapshot(rule));
        engine.Store.RecordTrashed(new[] { Mail(inbox, 0, "gone-3", "Price drop", "offers@shop.test", arrived) }, at.AddHours(1), _ => AutoDeleted.RuleSnapshot(null));
        engine.Store.RecordTrashed(new[] { Mail(inbox, 0, "hand-1", "Old chat", "friend@other.test", arrived) }, at);
        // What the next check lists in Trash, plus one that was deleted in another app.
        var rows = engine.Store.InsertMessages(new[]
        {
            Mail(trash, 951, "gone-1", "Weekend sale", "offers@shop.test", arrived),
            Mail(trash, 952, "gone-2", "Price drop", "offers@shop.test", arrived),
            Mail(trash, 953, "gone-3", "Price drop", "offers@shop.test", arrived.AddHours(1)),
            Mail(trash, 954, "hand-1", "Old chat", "friend@other.test", arrived),
            Mail(trash, 955, "phone-1", "Deleted on the phone", "friend@other.test", arrived),
            // A second copy of an auto-deleted email under a label: the conversation shows that copy, not the Trash one.
            Mail(offers, 956, "gone-1", "Weekend sale", "offers@shop.test", arrived),
        });
        foreach (var row in rows) engine.Store.SaveBody(row.Id, new MessageBody { Html = "<p>Cached locally</p>", Text = "Cached locally", ImagesComplete = true });

        var trashNav = vm.Smart.First(n => n.Kind == NavKind.Role && n.Role == FolderRole.Trash);
        vm.SearchText = "";
        vm.NavigateCommand.Execute(trashNav);
        Assert(vm.IsTrashView && vm.Threads.Count == 4, "Trash lists its four conversations");
        Assert(vm.ShowTrashFilter && vm.TrashFilterAll && !vm.TrashFilterAuto && !vm.TrashFilterOther, "Trash offers its filter, on All, once auto delete put something there");
        Assert(vm.TrashAllText == "All · 4" && vm.TrashAutoText == "Auto-deleted · 2" && vm.TrashOtherText == "Deleted by you or elsewhere · 2",
            $"The three choices carry their counts ({vm.TrashAllText} | {vm.TrashAutoText} | {vm.TrashOtherText})");
        ThreadItem Row(string thread) => vm.Threads.Single(t => t.Row.ThreadKey == thread);
        Assert(Row("Weekend sale").AutoDeletedLabel == AutoDeleted.RowLabel(at, now) && Row("Weekend sale").HasAutoDeleted,
            "An auto-deleted conversation carries the label with the date and time (" + Row("Weekend sale").AutoDeletedLabel + ")");
        Assert(Row("Price drop").AutoDeletedLabel == AutoDeleted.RowLabel(at.AddHours(1), now)
            && Row("Price drop").AutoDeletedTip?.EndsWith(AutoDeleted.ManyLine(2), StringComparison.Ordinal) == true,
            "A conversation with two auto-deleted emails shows the later one and says there are two");
        Assert(!Row("Old chat").HasAutoDeleted && !Row("Deleted on the phone").HasAutoDeleted && Row("Old chat").AutoDeletedTip == null,
            "Emails deleted by hand or in another app carry no label");

        vm.SetTrashFilterCommand.Execute("auto");
        Assert(vm.TrashFilterAuto && vm.Threads.Count == 2 && vm.Threads.All(t => t.HasAutoDeleted), "Auto-deleted lists only what auto delete moved to Trash");
        vm.SetTrashFilterCommand.Execute("other");
        Assert(vm.TrashFilterOther && vm.Threads.Count == 2 && vm.Threads.All(t => !t.HasAutoDeleted), "Deleted by you or elsewhere lists the rest");
        Assert(vm.TrashAllText == "All · 4", "The counts stay those of the whole of Trash under a filter");
        vm.NavigateCommand.Execute(vm.Smart.First(n => n.Kind == NavKind.Inbox));
        Assert(!vm.ShowTrashFilter && vm.TrashFilterAll && vm.Threads.All(t => !t.HasAutoDeleted), "Leaving Trash drops the filter; other lists have no such label");
        vm.NavigateCommand.Execute(trashNav);
        Assert(vm.TrashFilterAll && vm.Threads.Count == 4, "Coming back to Trash shows all of it");

        // The banner above the open conversation.
        ReaderPage? page = null;
        void Ready(ReaderPage value) => page = value;
        vm.Reader.PageReady += Ready;
        try
        {
            vm.Reader.Neighbours = Array.Empty<ThreadRow>();
            vm.Reader.Show(Row("Weekend sale").Row, vm);
            PumpUntil(() => page?.Key == "A\nWeekend sale");
            Assert(vm.Reader.Messages.Count == 1 && vm.Reader.Messages[0].FolderId == offers,
                "The open conversation shows the email's copy under its label, not the one in Trash");
            Assert(vm.Reader.ShowAutoDeleted && vm.Reader.AutoDeletedTitle == "Deleted by Magpie auto delete" && vm.Reader.AutoDeletedWhen == AutoDeleted.When(at),
                "The open auto-deleted conversation has its banner with the full date and time (" + vm.Reader.AutoDeletedWhen + ")");
            Assert(vm.Reader.AutoDeletedDetails == "Rule: offers@shop.test after 30 days · Arrived " + arrived.ToLocalTime().ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture) + " · Was in Inbox",
                "The banner names the rule, the arrival date and the folder it was in (" + vm.Reader.AutoDeletedDetails + ")");
            Assert(vm.Reader.AutoDeletedMany == "" && vm.Reader.CanEditAutoDeletedRule && vm.Reader.AutoDeletedRuleId == rule.Id
                && vm.Reader.AutoDeletedRestoreTip == "Puts it back in Inbox and keeps it from auto delete",
                "One auto-deleted email: no count line; Edit rule and Restore are offered");
            Assert(page!.Html.Contains("class=\"dd weeks gone\"", StringComparison.Ordinal) && page.Html.Contains("🗑 " + AutoDeleted.MessageNote(at, now), StringComparison.Ordinal),
                "The email itself carries its auto-deleted note next to the date");

            vm.Reader.Show(Row("Price drop").Row, vm);
            PumpUntil(() => page?.Key == "A\nPrice drop");
            Assert(vm.Reader.ShowAutoDeleted && vm.Reader.AutoDeletedWhen == AutoDeleted.When(at.AddHours(1)) && vm.Reader.AutoDeletedMany == AutoDeleted.ManyLine(2),
                "Two auto-deleted emails: the banner shows the later one and says so");
            Assert(vm.Reader.AutoDeletedDetails.StartsWith("Its rule was no longer there · ", StringComparison.Ordinal) && !vm.Reader.CanEditAutoDeletedRule,
                "A rule that was gone when it ran is said so, without Edit rule");
            Assert(page!.Html.Split("class=\"dd weeks gone\"").Length - 1 == 2, "Each of the two emails carries its own note");

            vm.Reader.Show(Row("Old chat").Row, vm);
            PumpUntil(() => page?.Key == "A\nOld chat");
            Assert(!vm.Reader.ShowAutoDeleted && vm.Reader.AutoDeletedWhen == "" && !page!.Html.Contains("class=\"dd weeks gone\"", StringComparison.Ordinal),
                "A conversation deleted by hand has no banner and no note");
        }
        finally { vm.Reader.PageReady -= Ready; }

        // Restore: the conversations leave Trash, are kept from their rule, and the filter goes with the last of them.
        vm.SetTrashFilterCommand.Execute("auto");
        engine.Restore("A", "Weekend sale");
        vm.ReloadList();
        Assert(vm.TrashFilterAuto && vm.Threads.Count == 1 && vm.TrashAutoText == "Auto-deleted · 1" && vm.TrashAllText == "All · 3",
            "Restoring one auto-deleted conversation leaves the other under the filter");
        Assert(engine.Store.IsKeptRestored("A", "gone-1") && !engine.Store.IsKeptRestored("A", "hand-1"), "The restored email is kept from auto delete from then on");
        engine.Restore("A", "Price drop");
        vm.ReloadList();
        Assert(!vm.ShowTrashFilter && vm.TrashFilterAll && vm.Threads.Count == 2 && vm.Threads.All(t => !t.HasAutoDeleted),
            "With nothing auto-deleted left, the filter row goes and Trash shows what remains");
        vm.Reader.Clear();
        vm.NavigateCommand.Execute(vm.Smart.First(n => n.Kind == NavKind.Inbox));
        Console.WriteLine("AUTO_DELETED_TRASH_SMOKE_PASSED");
    }
}
