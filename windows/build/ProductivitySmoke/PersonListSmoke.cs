using Magpie.App.ViewModels;
using Magpie.Core;
using Magpie.Core.Models;
using Magpie.Core.Storage;

namespace ProductivitySmoke;

internal static partial class Program
{
    /// <summary>
    /// Design PE1 (19.0.0): the "emails with one address" list in the native view model — one row per email, the four
    /// filters, Conversations, Bcc, and the ways out of it (Back, typing a search, picking a folder).
    /// </summary>
    private static void CheckPersonList(MainViewModel vm, MailEngine engine, long inbox)
    {
        const string rajesh = "rajesh@buildwell.test";
        const string audit = "audit@buildwell.test";
        var sent = engine.Store.UpsertFolder(new MailFolder { AccountId = "A", Path = "Sent", Name = "Sent", Role = FolderRole.Sent });
        var now = DateTimeOffset.Now;
        engine.Store.InsertMessages(new[]
        {
            new MessageRow { AccountId = "A", FolderId = inbox, Uid = 901, MessageId = "person-1", ThreadKey = "person-talk", FromName = "Rajesh Mehta",
                FromAddress = rajesh, To = "me@example.test", Subject = "Site visit", Date = now.AddHours(-3), SortDate = now.AddHours(-3), Flags = MessageFlags.Seen },
            new MessageRow { AccountId = "A", FolderId = sent, Uid = 902, MessageId = "person-2", ThreadKey = "person-talk", FromAddress = "me@example.test",
                To = $"Rajesh Mehta <{rajesh}>, Anita Rao <anita@buildwell.test>", Bcc = audit, Subject = "Re: Site visit", Date = now.AddHours(-2), SortDate = now.AddHours(-2), Flags = MessageFlags.Seen },
            new MessageRow { AccountId = "A", FolderId = sent, Uid = 903, MessageId = "person-3", ThreadKey = "person-other", FromAddress = "me@example.test",
                To = "Anita Rao <anita@buildwell.test>", Cc = rajesh, Subject = "Snag list", Date = now.AddHours(-1), SortDate = now.AddHours(-1), Flags = MessageFlags.Seen },
        });

        vm.SearchText = "";
        vm.ShowPerson(rajesh, "Rajesh Mehta", PersonKind.All);
        Assert(vm.IsPersonView && vm.Current?.Kind == NavKind.Inbox && vm.SearchText == "with:" + rajesh, "Person list opens on the Inbox view with the address in the search box");
        Assert(vm.PersonTitle == "Emails with Rajesh Mehta" && vm.PersonFilters.Count == 4 && vm.PersonFilters[0].IsSelected, "Person list has its heading and four filters");
        Assert(vm.Threads.Count == 3 && vm.Threads.All(t => t.IsPersonRow && !t.CanTick), "Each email is one row without a tick box");
        Assert(vm.Threads.Select(t => t.SelectKey).Distinct().Count() == 3 && vm.Threads.Count(t => t.Key == "A|person-talk") == 2,
            "Two emails of one conversation are two rows that can be told apart");
        Assert(vm.Threads[0].Sender == "To Anita Rao · cc Rajesh Mehta" && vm.Threads[1].Sender == "To Rajesh Mehta +2" && vm.Threads[2].Sender == "From Rajesh Mehta",
            "Rows say who the email went from and to, newest first");
        Assert(vm.Threads[2].TagChips.Any(c => c.Name == "Inbox") && vm.Threads[0].TagChips.Any(c => c.Name == "Sent"), "Rows carry their folder");
        Assert(!vm.ShowListTools && !vm.RowActionsAlways && !vm.RowActionsOnHover && vm.PersonFootText.StartsWith("3 emails on this PC."), "Each email rows have no bulk tools; the count is under the list");

        vm.SetPersonKindCommand.Execute(vm.PersonFilters.First(f => f.Kind == PersonKind.YouSent));
        Assert(vm.Threads.Count == 2 && vm.PersonFilters.Single(f => f.IsSelected).Kind == PersonKind.YouSent, "You sent lists only the emails you sent to the address");
        vm.SetPersonKindCommand.Execute(vm.PersonFilters.First(f => f.Kind == PersonKind.From));
        Assert(vm.Threads.Count == 1 && vm.Threads[0].Row.Latest.MessageId == "person-1", "From lists only the emails the address sent");

        vm.ShowPerson(audit, "", PersonKind.YouSent);
        Assert(vm.Threads.Count == 1 && vm.Threads[0].TagChips.Any(c => c.Name == "Bcc") && vm.Threads[0].Sender.EndsWith("· bcc " + audit),
            "An email with the address only in Bcc is found and marked");

        vm.ShowPerson(rajesh, "Rajesh Mehta", PersonKind.All);
        vm.SetPersonRowsCommand.Execute("conversations");
        Assert(vm.PersonConversations && vm.Threads.Count == 2 && vm.Threads.All(t => !t.IsPersonRow && t.CanTick) && vm.ShowListTools, "Conversations lists each conversation once, with tick boxes");
        vm.SetPersonRowsCommand.Execute("each");
        Assert(vm.Threads.Count == 3, "Each email comes back");

        vm.SearchText = "site";
        Assert(!vm.IsPersonView && vm.ShowListTools, "Typing in the search box turns the list into an ordinary search");
        vm.ShowPerson(rajesh, "Rajesh Mehta", PersonKind.All);
        vm.PersonBackCommand.Execute(null);
        Assert(!vm.IsPersonView && vm.SearchText == "site" && vm.Current?.Kind == NavKind.Inbox, "Back returns to the search that was open");
        vm.ShowPerson(rajesh, "Rajesh Mehta", PersonKind.All);
        vm.NavigateCommand.Execute(vm.Smart.First(n => n.Kind == NavKind.Inbox));
        Assert(!vm.IsPersonView && vm.SearchText == "", "Picking a folder ends the person list");
    }
}
