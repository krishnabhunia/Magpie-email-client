using System.Windows;
using System.Windows.Controls;
using Magpie.App.ViewModels;
using Magpie.App.Views;
using Magpie.Core;

namespace Magpie.App.Services;

/// <summary>The Snooze, Remind me and Tag menus of a reader — the toolbar's and the hover cards' (design HM1).</summary>
public static class ReaderMenus
{
    public static MenuItem Item(string header, Action onClick, string? gesture = null, bool isChecked = false, bool enabled = true)
    {
        var mi = new MenuItem { Header = header, IsChecked = isChecked, IsEnabled = enabled };
        if (gesture != null) mi.InputGestureText = gesture;
        mi.Click += (_, _) => onClick();
        return mi;
    }

    public static ContextMenu Snooze(ThreadViewModel r, Window owner)
    {
        var menu = new ContextMenu();
        foreach (var p in TimePresets.For(DateTime.Now))
            menu.Items.Add(Item($"{p.Label}  ·  {TimePresets.Describe(p.When, DateTime.Now)}", () => r.Snooze(p.When)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Pick date & time…", () =>
        {
            var when = PickTimeDialog.Ask(owner, "Snooze until", "The conversation leaves your inbox and comes back at this time.", DateTime.Now.AddDays(1).Date.AddHours(8));
            if (when != null) r.Snooze(when.Value);
        }));
        if (r.IsSnoozed) menu.Items.Add(Item("Unsnooze now", () => r.UnsnoozeCommand.Execute(null)));
        return menu;
    }

    /// <summary>Remind me: "if nobody replies" when the newest email is yours (S4), otherwise "bring this back".</summary>
    public static ContextMenu Remind(ThreadViewModel r, Window owner)
    {
        var ifNoReply = r.LatestIsMine;
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = ifNoReply ? "Remind me if nobody replies by…" : "Bring this back to the top on…", IsEnabled = false });
        foreach (var p in TimePresets.For(DateTime.Now))
            menu.Items.Add(Item($"{p.Label}  ·  {TimePresets.Describe(p.When, DateTime.Now)}", () => r.RemindMe(p.When, ifNoReply)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Pick date & time…", () =>
        {
            var when = PickTimeDialog.Ask(owner, "Remind me", ifNoReply ? "If nobody has replied by then, the conversation comes back to the top of your inbox." : "The conversation comes back to the top of your inbox at this time.", DateTime.Now.AddDays(2).Date.AddHours(9));
            if (when != null) r.RemindMe(when.Value, ifNoReply);
        }));
        return menu;
    }

    public static ContextMenu Tag(ThreadViewModel r)
    {
        var current = r.CurrentTags;
        var menu = new ContextMenu();
        foreach (var t in AppServices.Engine.Config.Tags)
            menu.Items.Add(Item(t.Name, () => r.ToggleTag(t.Name), isChecked: current.Contains(t.Name)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Manage tags…", () => SettingsWindow.Open("General")));
        return menu;
    }
}
