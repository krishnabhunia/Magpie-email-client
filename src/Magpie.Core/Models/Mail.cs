namespace Magpie.Core.Models;

public enum FolderRole { Other = 0, Inbox = 1, Sent = 2, Drafts = 3, Trash = 4, Junk = 5, Archive = 6, All = 7, Flagged = 8, Important = 9 }

[Flags]
public enum MessageFlags { None = 0, Seen = 1, Flagged = 2, Answered = 4, Draft = 8, Deleted = 16 }

/// <summary>Spark-style smart inbox buckets.</summary>
public enum Category { People = 0, Notifications = 1, Newsletters = 2 }

public sealed class MailFolder
{
    public long Id { get; set; }
    public string AccountId { get; set; } = "";
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public FolderRole Role { get; set; }
    public char Delimiter { get; set; } = '/';
    public long UidValidity { get; set; }
    public long UidNext { get; set; }
    public long HighestModSeq { get; set; }
    public int Unread { get; set; }
    public int Total { get; set; }
    public long LastSync { get; set; }
    /// <summary>False for folders we list but do not mirror (e.g. Gmail "All Mail").</summary>
    public bool Synced { get; set; } = true;

    public int Depth => Delimiter == '\0' ? 0 : Path.Count(c => c == Delimiter);
}

/// <summary>One message row as stored locally (headers + state, no body).</summary>
public sealed class MessageRow
{
    public long Id { get; set; }
    public string AccountId { get; set; } = "";
    public long FolderId { get; set; }
    public long Uid { get; set; }
    public string MessageId { get; set; } = "";
    public string InReplyTo { get; set; } = "";
    public string References { get; set; } = "";
    public string ThreadKey { get; set; } = "";
    public string FromName { get; set; } = "";
    public string FromAddress { get; set; } = "";
    public string To { get; set; } = "";
    public string Cc { get; set; } = "";
    public string ReplyTo { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Preview { get; set; } = "";
    public DateTimeOffset Date { get; set; }
    public DateTimeOffset SortDate { get; set; }
    public MessageFlags Flags { get; set; }
    public bool HasAttachments { get; set; }
    public long Size { get; set; }
    public Category Category { get; set; }
    public string ListUnsubscribe { get; set; } = "";
    public DateTimeOffset? SnoozeUntil { get; set; }
    public string Tags { get; set; } = "";
    public bool BodyCached { get; set; }

    public bool IsSeen => Flags.HasFlag(MessageFlags.Seen);
    public bool IsFlagged => Flags.HasFlag(MessageFlags.Flagged);
    /// <summary>
    /// Set aside (design B7) is stored as a snooze that never wakes (31 Dec 9999): the conversation leaves the Inbox and
    /// every Inbox count exactly like a snoozed one, without a date. Snoozed views leave it out; the Set aside view lists it.
    /// </summary>
    public static readonly DateTimeOffset SetAsideMark = new(9999, 12, 31, 0, 0, 0, TimeSpan.Zero);
    /// <summary>Gatekeeper (design B7): mail from a new sender waits at the door the same way, a day earlier.
    /// Every real snooze is before <see cref="GateMark"/>.</summary>
    public static readonly DateTimeOffset GateMark = new(9999, 12, 30, 0, 0, 0, TimeSpan.Zero);
    public bool IsSetAside => SnoozeUntil == SetAsideMark;
    public bool IsAtGate => SnoozeUntil == GateMark;
    public string Sender => string.IsNullOrWhiteSpace(FromName) ? FromAddress : FromName;
}

/// <summary>A conversation as shown in the message list: newest message + aggregate state.</summary>
public sealed class ThreadRow
{
    public string ThreadKey { get; set; } = "";
    public string AccountId { get; set; } = "";
    public MessageRow Latest { get; set; } = new();
    public int Count { get; set; }
    public int UnreadCount { get; set; }
    public bool Flagged { get; set; }
    public bool HasAttachments { get; set; }
    public string Participants { get; set; } = "";
    public DateTimeOffset? SnoozeUntil { get; set; }
    /// <summary>Earliest auto-delete timer on the conversation (design AD3) and the rule that set it.</summary>
    public DateTimeOffset? DeleteAt { get; set; }
    public string DeleteRule { get; set; } = "";
    public bool IsSetAside => SnoozeUntil == MessageRow.SetAsideMark;
    public bool IsSnoozed(DateTimeOffset now) => SnoozeUntil is { } s && s > now && s < MessageRow.GateMark;
}

public sealed class AttachmentInfo
{
    public int Index { get; set; }
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long Size { get; set; }
    public string ContentId { get; set; } = "";
    public bool Inline { get; set; }
}

public sealed class MessageBody
{
    public string Html { get; set; } = "";
    public string Text { get; set; } = "";
    /// <summary>The message's iCalendar part (text/calendar), if it carries an invite (design B3).</summary>
    public string Calendar { get; set; } = "";
    public List<AttachmentInfo> Attachments { get; set; } = new();
}

public enum OutboxStatus { Queued = 0, Sending = 1, Failed = 2, Sent = 3, Cancelled = 4 }

/// <summary>A message being written, kept on this PC (design F1): autosaved while composing, and held
/// until it reaches the server's Drafts folder (<see cref="PendingUpload"/>) when saving there failed.</summary>
public sealed class LocalDraft
{
    public long Id { get; set; }
    public string AccountId { get; set; } = "";
    public byte[] Mime { get; set; } = Array.Empty<byte>();
    public string Subject { get; set; } = "";
    public string ToText { get; set; } = "";
    public string Preview { get; set; } = "";
    public string ThreadKey { get; set; } = "";
    public long? SourceDraftRow { get; set; }
    public bool PendingUpload { get; set; }
    public DateTimeOffset Updated { get; set; }
    /// <summary>The draft's Message-ID, kept the same across saves.</summary>
    public string MessageId { get; set; } = "";
}

public sealed class OutboxItem
{
    public long Id { get; set; }
    public string AccountId { get; set; } = "";
    public byte[] Mime { get; set; } = Array.Empty<byte>();
    public DateTimeOffset SendAt { get; set; }
    public OutboxStatus Status { get; set; }
    public int Attempts { get; set; }
    public string LastError { get; set; } = "";
    public string Subject { get; set; } = "";
    public string ToText { get; set; } = "";
    public string MessageId { get; set; } = "";
    public string ThreadKey { get; set; } = "";
    /// <summary>"Remind me if nobody replies by" — set when scheduling.</summary>
    public DateTimeOffset? RemindAt { get; set; }
    public DateTimeOffset Created { get; set; }
}

public enum ReminderState { Waiting = 0, Due = 1, Done = 2 }

/// <summary>Follow-up reminder: surface the thread again if nobody replied after our message.</summary>
public sealed class Reminder
{
    public long Id { get; set; }
    public string AccountId { get; set; } = "";
    public string ThreadKey { get; set; } = "";
    public string Subject { get; set; } = "";
    /// <summary>Only replies dated after this count (usually our sent time).</summary>
    public DateTimeOffset After { get; set; }
    public DateTimeOffset Due { get; set; }
    public ReminderState State { get; set; }
    /// <summary>True: remind unconditionally ("Remind me" on a received thread). False: only if no reply arrived.</summary>
    public bool Always { get; set; }
}

public sealed class Contact
{
    public string Address { get; set; } = "";
    public string Name { get; set; } = "";
    public int Count { get; set; }
    public DateTimeOffset Last { get; set; }
    public string Display => string.IsNullOrWhiteSpace(Name) ? Address : $"{Name} <{Address}>";
}

public enum PendingOpKind { SetSeen, ClearSeen, SetFlagged, ClearFlagged, Move, Delete }

/// <summary>Server change recorded while applying it locally first (offline-first).</summary>
public sealed class PendingOp
{
    public long Id { get; set; }
    public string AccountId { get; set; } = "";
    public long FolderId { get; set; }
    public long Uid { get; set; }
    public PendingOpKind Kind { get; set; }
    /// <summary>Target folder id for Move.</summary>
    public long Arg { get; set; }
    public int Attempts { get; set; }
}
