using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Magpie.Core;
using Magpie.Core.Mail;
using Magpie.Core.Models;

namespace Magpie.Mac.ViewModels;

public enum SidebarKind { AllInboxes, Account, Folder }

/// <summary>One row of the sidebar: "All inboxes", an account's heading, or one of its folders.</summary>
public sealed partial class SidebarItem : ObservableObject
{
    public SidebarKind Kind { get; init; }
    public string Label { get; init; } = "";
    public string AccountId { get; init; } = "";
    public long FolderId { get; init; }
    public FolderRole Role { get; init; }
    public int Depth { get; init; }
    public string AccountColor { get; init; } = "#14606E";
    public IBrush AccountBrush => Brush(AccountColor);

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(UnreadText), nameof(HasUnread))] private int _unread;

    public bool IsHeader => Kind == SidebarKind.Account;
    public bool IsSelectable => Kind != SidebarKind.Account;
    public string UnreadText => Unread > 0 ? FolderCounts.Format(Unread) : "";
    public bool HasUnread => Unread > 0;
    public Thickness Indent => new(IsHeader ? 4 : 10 + Depth * 14, IsHeader ? 10 : 0, 0, 0);
    /// <summary>Stable key, so the selection survives a rebuild of the sidebar.</summary>
    public string Key => Kind switch
    {
        SidebarKind.AllInboxes => "all",
        SidebarKind.Account => "acc:" + AccountId,
        _ => "f:" + FolderId,
    };

    public static IBrush Brush(string colour) => Color.TryParse(colour, out var c) ? new SolidColorBrush(c) : Brushes.Teal;

    /// <summary>A small symbol per kind of folder (plain text, so it needs no icon set).</summary>
    public string Glyph => Kind == SidebarKind.AllInboxes ? "◎" : Role switch
    {
        FolderRole.Inbox => "▣",
        FolderRole.Sent => "➤",
        FolderRole.Drafts => "✎",
        FolderRole.Trash => "⌫",
        FolderRole.Junk => "⊘",
        FolderRole.Archive => "▤",
        FolderRole.Flagged or FolderRole.Important => "★",
        FolderRole.All => "≡",
        _ => "▢",
    };
}

/// <summary>One conversation in the message list.</summary>
public sealed class ThreadItem
{
    public ThreadRow Row { get; }
    public string Sender { get; }
    public string Subject { get; }
    public string Preview { get; }
    public string DateText { get; }
    public string DateTip { get; }
    public bool IsUnread => Row.UnreadCount > 0;
    public bool IsPinned => Row.Flagged;
    public bool HasAttachments => Row.HasAttachments;
    public string CountText => Row.Count > 1 ? Row.Count.ToString() : "";
    public string Key => Row.AccountId + "|" + Row.ThreadKey;
    public string AccountColor { get; }
    public IBrush AccountBrush => SidebarItem.Brush(AccountColor);
    public bool ShowAccountDot { get; }
    /// <summary>A draft kept on this Mac (saved while offline): opens in a compose window, not the reading pane.</summary>
    public long? LocalDraftId { get; init; }
    public bool LocalPending { get; init; }
    public bool IsLocalDraft => LocalDraftId != null;
    public string LocalBadge => LocalDraftId == null ? "" : LocalPending ? "On this Mac · uploads when online" : "On this Mac";

    public ThreadItem(ThreadRow row, string myEmail, string accountColor, bool showAccountDot, DateTimeOffset now)
    {
        Row = row;
        var m = row.Latest;
        var mine = m.FromAddress.Equals(myEmail, StringComparison.OrdinalIgnoreCase);
        Sender = row.Count > 1 && !string.IsNullOrEmpty(row.Participants) ? row.Participants : mine ? "To: " + FirstRecipient(m.To) : m.Sender;
        Subject = string.IsNullOrWhiteSpace(m.Subject) ? "(no subject)" : m.Subject;
        Preview = m.Preview;
        DateText = HtmlRenderer.FriendlyDate(m.Date, now);
        DateTip = m.Date.LocalDateTime.ToString("dddd d MMMM yyyy, HH:mm");
        AccountColor = accountColor;
        ShowAccountDot = showAccountDot;
    }

    private static string FirstRecipient(string to)
    {
        var first = Composer.ParseAddresses(to).Mailboxes.FirstOrDefault();
        return first == null ? to : string.IsNullOrWhiteSpace(first.Name) ? first.Address : first.Name;
    }
}

/// <summary>"Sending “Hello”… Undo" at the bottom of the main window (and other short notes).</summary>
public sealed partial class ToastItem : ObservableObject
{
    public long OutboxId { get; init; }
    public DateTimeOffset Until { get; init; }
    public string Subject { get; init; } = "";
    public bool CanUndo { get; init; } = true;
    [ObservableProperty] private string _text = "";

    public void Refresh(DateTimeOffset now)
    {
        if (!CanUndo) return;
        var left = Math.Max(0, (int)Math.Ceiling((Until - now).TotalSeconds));
        Text = $"Sending “{Subject}” · {left} s";
    }
}
