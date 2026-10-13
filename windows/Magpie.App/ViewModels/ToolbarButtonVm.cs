using Magpie.App.Services;
using Magpie.Core.Settings;

namespace Magpie.App.ViewModels;

/// <summary>One reading-pane toolbar button (designs C1, C3): icon + name, per the user's style.</summary>
public sealed class ToolbarButtonVm
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string IconKey { get; init; } = "";
    public string Shortcut { get; init; } = "";
    public string ToolTip { get; init; } = "";
    public bool ShowIcon { get; init; } = true;
    public bool ShowText { get; init; } = true;
    /// <summary>Delete has a ▾ for auto-delete (design AD1).</summary>
    public bool HasDropdown => Id == "delete";

    private static readonly Dictionary<string, (string Name, string Icon, string Key, string Tip)> Defs = new()
    {
        ["archive"] = ("Archive", "archive", "E", "Archive"),
        ["delete"] = ("Delete", "delete", "Del", "Move to Trash"),
        ["snooze"] = ("Snooze", "snooze", "S", "Hide until later"),
        ["setaside"] = ("Set aside", "setaside", "L", "Out of the Inbox without a date, until you want it"),
        ["remind"] = ("Remind", "remind", "", "Bring this back if nobody replies"),
        ["tag"] = ("Tag", "tag", "", "Add or remove tags"),
        ["pin"] = ("Pin", "pin", "P", "Pin or unpin"),
        ["move"] = ("Move", "move", "", "Move to another folder"),
        ["unread"] = ("Mark unread", "unread", "U", "Mark as unread"),
        ["replyall"] = ("Reply all", "replyall", "A", "Reply to everyone"),
        ["forward"] = ("Forward", "forward", "F", "Forward"),
        // Row hover actions only (design H3)
        ["read"] = ("Read / unread", "unread", "", "Mark as read or unread"),
        ["spam"] = ("Spam", "spam", "", "Move to Spam"),
    };

    public static string NameOf(string id) => Defs.TryGetValue(id, out var d) ? d.Name : id;
    public static string IconOf(string id) => Defs.TryGetValue(id, out var d) ? d.Icon : "folder";
    public static string KeyOf(string id) => Defs.TryGetValue(id, out var d) ? d.Key : "";

    public static ToolbarButtonVm For(string id, ButtonStyle style)
    {
        var d = Defs.TryGetValue(id, out var x) ? x : (Name: id, Icon: "folder", Key: "", Tip: id);
        return new ToolbarButtonVm
        {
            Id = id, Name = d.Name, IconKey = d.Icon, Shortcut = d.Key,
            ToolTip = d.Tip + (d.Key.Length > 0 ? $" ({d.Key})" : ""),
            ShowIcon = style != ButtonStyle.NameOnly,
            ShowText = style != ButtonStyle.IconOnly,
        };
    }
}
