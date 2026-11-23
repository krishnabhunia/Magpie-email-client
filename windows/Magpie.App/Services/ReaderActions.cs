using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Magpie.App.ViewModels;
using Magpie.App.Views;
using Magpie.Core;
using Magpie.Core.Mail;
using Magpie.Core.Models;

namespace Magpie.App.Services;

/// <summary>
/// Design OW1: whatever shows a conversation with its toolbar and banners — the reading pane of the main window, or
/// an email's own window. <see cref="Views.ReaderHeadView"/> draws the same head for both and asks its host to act.
/// </summary>
public interface IReaderHost
{
    Window Window { get; }
    MainViewModel Main { get; }
    ThreadViewModel Reader { get; }
    /// <summary>True in an email's own window: no "New window" button there, and its subject card has no such line.</summary>
    bool OwnWindow { get; }
    /// <summary>Runs a toolbar button by its id (<see cref="Core.Settings.Appearance.ToolbarIds"/>).</summary>
    void RunAction(string id, FrameworkElement anchor);
    /// <summary>Delete forever… (design DX1-A): asks once, then every copy goes.</summary>
    void DeleteForever();
    /// <summary>Restore on the "Deleted by Magpie auto delete" banner (design AL1).</summary>
    void RestoreFromTrash();
}

/// <summary>The menus and actions of a reader's toolbar, shared by the main window and an email's own window (design OW1).</summary>
public static class ReaderActions
{
    public static void ShowMenu(FrameworkElement anchor, ContextMenu menu)
    {
        menu.PlacementTarget = anchor;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    public static MenuItem IconItem(string header, string icon, Action onClick, string? gesture = null)
    {
        var mi = ReaderMenus.Item(header, onClick, gesture);
        mi.Icon = new IconChip { Icon = icon, Size = 18 };
        return mi;
    }

    /// <summary>What a toolbar button does to the open conversation. "window" (design OW1) opens it in its own window.</summary>
    public static void Run(IReaderHost host, string id, FrameworkElement anchor)
    {
        var r = host.Reader;
        if (!r.HasThread) return;
        switch (id)
        {
            case "archive": r.ArchiveCommand.Execute(null); break;
            case "delete": r.DeleteCommand.Execute(null); break;
            case "snooze": ShowMenu(anchor, ReaderMenus.Snooze(r, host.Window)); break;
            case "remind": ShowMenu(anchor, ReaderMenus.Remind(r, host.Window)); break;
            case "tag": ShowMenu(anchor, ReaderMenus.Tag(r)); break;
            case "pin": r.TogglePinCommand.Execute(null); break;
            case "setaside": r.ToggleSetAsideCommand.Execute(null); break;
            case "move": ShowMenu(anchor, MoveMenu(r)); break;
            case "unread": r.MarkUnreadCommand.Execute(null); break;
            case "replyall": r.ReplyAllCommand.Execute(null); break;
            case "forward": r.ForwardCommand.Execute(null); break;
            case "window": if (!host.OwnWindow && r.CurrentThreadRow is { } row) ThreadWindow.Open(host.Main, row); break;
        }
    }

    public static ContextMenu MoveMenu(ThreadViewModel r)
    {
        var menu = new ContextMenu();
        foreach (var f in AppServices.Engine.Folders(r.AccountId).Where(f => f.Role is not (FolderRole.All or FolderRole.Flagged or FolderRole.Important)))
        {
            var folder = f;
            var mi = ReaderMenus.Item(new string(' ', Math.Min(f.Depth, 4) * 2) + f.Name, () => r.MoveTo(folder));
            mi.Icon = new IconChip { Icon = Icons.ForRole(f.Role), Size = 18 };
            menu.Items.Add(mi);
        }
        return menu;
    }

    /// <summary>
    /// The Delete ▾ menu (design DX1): Delete now · Delete forever…, then for the sender and for the whole domain the
    /// "already here" and "now and later" picks that open the one dialog pre-filled, and the two quick rule picks.
    /// </summary>
    public static ContextMenu DeleteMenu(IReaderHost host, bool withDeleteNow)
    {
        var r = host.Reader;
        var menu = new ContextMenu();
        if (withDeleteNow)
        {
            menu.Items.Add(IconItem("Delete now", "delete", () => host.RunAction("delete", host.Window), "Del"));
            menu.Items.Add(IconItem("Delete forever…", "delete", host.DeleteForever, "Shift+Del"));
        }
        var sender = r.SenderAddress?.Trim().ToLowerInvariant();
        if (sender is { Length: > 0 } && sender.Contains('@'))
        {
            if (withDeleteNow) menu.Items.Add(new Separator());
            var domain = AutoDelete.DomainPattern(sender);
            AddDeleteFromItems(host, menu, sender, $"EMAILS FROM {sender.ToUpperInvariant()}", single: true);
            AddDeleteFromItems(host, menu, domain, $"EMAILS FROM ANYONE AT {domain.ToUpperInvariant()}", single: false);
            menu.Items.Add(new Separator());
        }
        else if (!withDeleteNow) menu.Items.Add(new MenuItem { Header = "Works on emails from other people", IsEnabled = false });
        if (r.HasDeleteTimer) menu.Items.Add(ReaderMenus.Item("Keep this one (no auto-delete)", () => r.KeepFromAutoDeleteCommand.Execute(null)));
        menu.Items.Add(ReaderMenus.Item("Manage auto-delete rules", () => SettingsWindow.Open("Rules:AutoDelete")));
        return menu;
    }

    private static MenuItem SectionHeader(string text) => new() { Header = text, IsEnabled = false, FontSize = 11, FontWeight = FontWeights.SemiBold };

    /// <summary>One section of the menu: "Delete all N already here…", "Keep only the last week, now and later…"; for a
    /// single address also "Delete after a set time… (rule only)" and "OTP delete — 24 h after arrival" (acts at once).</summary>
    private static void AddDeleteFromItems(IReaderHost host, ContextMenu menu, string pattern, string header, bool single)
    {
        var e = AppServices.Engine;
        var n = e.PastFrom(pattern).Count;
        menu.Items.Add(SectionHeader(header));
        var all = IconItem($"Delete all {n:N0} already here…", "delete", () => OpenDeleteFrom(host.Window, pattern, past: true, future: false, keepNothing: true));
        all.IsEnabled = n > 0;
        menu.Items.Add(all);
        menu.Items.Add(IconItem("Keep only the last week, now and later…", "delete", () => OpenDeleteFrom(host.Window, pattern, past: true, future: true)));
        if (!single) return;
        menu.Items.Add(IconItem("Delete after a set time…", "clock", () => OpenDeleteFrom(host.Window, pattern, past: false, future: true), "(rule only)"));
        menu.Items.Add(IconItem("OTP delete — 24 h after arrival", "clock", () => host.Main.RunDeleteFrom(new AutoDelete.DeleteFromRequest
        {
            Pattern = pattern, Future = true, Otp = true, StartOnExisting = e.Config.AutoDeleteIncludePast,
        })));
    }

    /// <summary>Opens the "Delete emails from…" dialog pre-filled (design DX1-B1); the dialog runs the delete itself.</summary>
    public static void OpenDeleteFrom(Window owner, string pattern, bool past, bool future, bool keepNothing = false) =>
        AutoDeleteDialog.Show(owner, new AutoDeleteRule { Pattern = pattern }, editing: false, past: past, future: future, keepNothing: keepNothing);

    /// <summary>Opens an auto-delete rule for changing; says <paramref name="gone"/> when the rule no longer exists.</summary>
    public static void EditRule(Window owner, string ruleId, string gone)
    {
        var rule = AppServices.Engine.AutoDeleteRules().FirstOrDefault(x => x.Id == ruleId);
        if (rule != null) AutoDeleteDialog.Show(owner, rule, true);
        else Ui.Error("Auto-delete", gone);
    }

    public const string RuleGoneKeepsDate = "That rule was removed; this email keeps the date it was given. Use Keep this one to stop it.";
    public const string RuleGoneAfterDelete = "That rule was removed since it deleted this email.";

    /// <summary>A toolbar button as a menu line (··· More, and the list's right-click menu).</summary>
    public static MenuItem ActionItem(IReaderHost host, ToolbarButtonVm b, FrameworkElement anchor)
    {
        var r = host.Reader;
        var name = b.Id == "pin" && r.IsPinned ? "Unpin" : b.Id == "setaside" && r.IsSetAside ? "Back to Inbox" : b.Id == "unread" ? "Mark as unread"
            : b.Id == "window" ? EmailWindow.OpenWords : b.Name;
        if (b.Id == "move")
        {
            var move = new MenuItem { Header = "Move to", Icon = new IconChip { Icon = "move", Size = 18 } };
            foreach (var item in MoveMenu(r).Items.OfType<MenuItem>().ToList())
            {
                ((ContextMenu)item.Parent).Items.Remove(item);
                move.Items.Add(item);
            }
            return move;
        }
        return IconItem(name + (b.Id is "snooze" or "remind" or "tag" ? "…" : ""), b.IconKey, () => host.RunAction(b.Id, anchor), b.Shortcut.Length > 0 ? b.Shortcut : null);
    }

    /// <summary>··· More: the buttons hidden from the toolbar, then the less common actions.</summary>
    public static ContextMenu MoreMenu(IReaderHost host, FrameworkElement anchor)
    {
        var r = host.Reader;
        var menu = new ContextMenu();
        foreach (var b in host.Main.HiddenButtons.Where(b => !(host.OwnWindow && b.Id == "window"))) menu.Items.Add(ActionItem(host, b, anchor));
        if (menu.Items.Count > 0) menu.Items.Add(new Separator());

        if (r.SenderAddress is { } sender0)
        {
            var cat = new MenuItem { Header = "Smart inbox: always put " + sender0 + " in" };
            cat.Items.Add(ReaderMenus.Item("People", () => r.SetSenderCategory(Category.People)));
            cat.Items.Add(ReaderMenus.Item("Notifications", () => r.SetSenderCategory(Category.Notifications)));
            cat.Items.Add(ReaderMenus.Item("Newsletters", () => r.SetSenderCategory(Category.Newsletters)));
            menu.Items.Add(cat);
        }
        if (r.CanUnsubscribe) menu.Items.Add(IconItem("Unsubscribe…", "unsubscribe", () => r.UnsubscribeCommand.Execute(null)));
        menu.Items.Add(IconItem("Customise toolbar…", "toolbar", () => SettingsWindow.Open("Toolbar")));
        return menu;
    }

    /// <summary>
    /// The card of an auto-delete date (design DD1: the chip next to the subject, and a row of the list): the date,
    /// the rule, Keep this one · Change rule, and Delete now where the caller gives it.
    /// </summary>
    public static UIElement DeleteCard(Window owner, Action close, string title, string rule, string ruleId, Action keep, Action? deleteNow)
    {
        var panel = new StackPanel();
        var head = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        head.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Danger");
        var sub = new TextBlock { Text = rule, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 10) };
        sub.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text.Secondary");
        panel.Children.Add(head);
        panel.Children.Add(sub);
        var buttons = new WrapPanel();
        Button Make(string text, string style, Action run, string tip)
        {
            var b = new Button { Content = text, FontSize = 12, MinHeight = 26, Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 0, 6, 4), ToolTip = tip };
            b.SetResourceReference(FrameworkElement.StyleProperty, style);
            b.Click += (_, _) => { close(); run(); };
            buttons.Children.Add(b);
            return b;
        }
        Make("Keep this one", "Button.Primary", keep, "Take the timer off this email (pinning it keeps it too)");
        Make("Change rule", "Button.Secondary", () => EditRule(owner, ruleId, RuleGoneKeepsDate), "Change the time or the sender of this rule");
        if (deleteNow != null)
            Make("Delete now", "Button.Secondary", deleteNow, "Move it to Trash now").SetResourceReference(Control.ForegroundProperty, "Brush.Danger");
        panel.Children.Add(buttons);
        return panel;
    }
}
