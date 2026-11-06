using Magpie.Core.Mail;
using Magpie.Core.Models;
using Magpie.Core.Storage;
using MailKit;
using MimeKit;
using MFlags = Magpie.Core.Models.MessageFlags;
using KitFlags = MailKit.MessageFlags;

namespace Magpie.Core.Tests;

public class MailPriorityTests
{
    [Theory]
    [InlineData("Importance", "high", "High")]
    [InlineData("importance", " HIGH ", "High")]
    [InlineData("Importance", "normal", "Normal")]
    [InlineData("Importance", "low", "Low")]
    [InlineData("Importance", "not-high", "Normal")]
    [InlineData("X-Priority", "1", "High")]
    [InlineData("X-Priority", "1 (Highest)", "High")]
    [InlineData("X-Priority", "2 (High)", "High")]
    [InlineData("X-Priority", "3 (Normal)", "Normal")]
    [InlineData("X-Priority", "4 (Low)", "Low")]
    [InlineData("X-Priority", "5 (Lowest)", "Low")]
    [InlineData("X-Priority", "10", "Normal")]
    [InlineData("X-Priority", "1urgent", "Normal")]
    [InlineData("X-Priority", "1 (broken", "Normal")]
    [InlineData("X-Priority", "1 (High) trailing", "Normal")]
    [InlineData("X-Priority", "0", "Normal")]
    [InlineData("X-Priority", "6", "Normal")]
    [InlineData("X-MSMail-Priority", "High", "High")]
    [InlineData("X-MSMail-Priority", "LOW", "Low")]
    [InlineData("X-MSMail-Priority", "Normal", "Normal")]
    public void Parses_only_recognized_priority_values(string header, string value, string expected)
    {
        var headers = new HeaderList { { header, value } };
        Assert.Equal(expected, MailPriorityHeaders.Read(headers));
        var flags = MailPriorityHeaders.Flags(headers);
        Assert.True(flags.HasFlag(MFlags.PriorityKnown));
        Assert.Equal(expected == "High", flags.HasFlag(MFlags.HighPriority));
        Assert.False(flags.HasFlag(MFlags.Flagged));
    }

    [Fact]
    public void Explicit_importance_wins_over_conflicting_legacy_headers()
    {
        var headers = new HeaderList { { "Importance", "normal" }, { "X-Priority", "1" }, { "X-MSMail-Priority", "High" } };
        Assert.Equal("Normal", MailPriorityHeaders.Read(headers));
        headers["Importance"] = "low";
        Assert.Equal("Low", MailPriorityHeaders.Read(headers));
        headers["Importance"] = "unrecognized";
        Assert.Equal("High", MailPriorityHeaders.Read(headers));
        headers["X-Priority"] = "3";
        Assert.Equal("Normal", MailPriorityHeaders.Read(headers));
    }

    [Fact]
    public void Empty_checked_headers_differ_from_not_fetched_headers()
    {
        Assert.Equal("Normal", MailPriorityHeaders.Read(null));
        Assert.Equal(MFlags.None, MailPriorityHeaders.Flags(null));
        Assert.Equal(MFlags.PriorityKnown, MailPriorityHeaders.Flags(new HeaderList()));
    }

    [Theory]
    [InlineData("High", "1 (Highest)", "high", "High")]
    [InlineData("Low", "5 (Lowest)", "low", "Low")]
    [InlineData("Normal", null, null, null)]
    public void Compose_uses_interoperable_priority_headers_without_changing_the_subject(string priority,
        string? numbered, string? importance, string? microsoft)
    {
        using var message = Composer.Build(new Draft { Html = "<p>Body</p>", Subject = "Unmodified subject", Priority = priority },
            new Account { Id = "A", Email = "me@example.test" });
        Assert.Equal(numbered, message.Headers["X-Priority"]);
        Assert.Equal(importance, message.Headers["Importance"]);
        Assert.Equal(microsoft, message.Headers["X-MSMail-Priority"]);
        Assert.Equal("Unmodified subject", message.Subject);
        Assert.Equal(priority, Composer.FromMime(message, "A", "thread").Priority);
    }

    [Theory]
    [InlineData("X-Priority", "2 (High)", "High")]
    [InlineData("X-MSMail-Priority", "Low", "Low")]
    public void Editing_legacy_draft_keeps_its_priority(string header, string value, string priority)
    {
        using var message = new MimeMessage { Subject = "Draft", Body = new TextPart("plain") { Text = "Body" } };
        message.Headers.Add(header, value);
        Assert.Equal(priority, Composer.FromMime(message, "A", "thread").Priority);
    }

    [Fact]
    public void Imap_flag_merge_keeps_header_metadata_and_does_not_pin_priority_mail()
    {
        var metadata = MFlags.HighPriority | MFlags.PriorityKnown;
        var merged = AccountSync.Merge(metadata | MFlags.Flagged | MFlags.Draft, KitFlags.Seen | KitFlags.Answered);
        Assert.Equal(metadata | MFlags.Seen | MFlags.Answered, merged);
        Assert.True(new MessageRow { Flags = merged }.IsHighPriority);
        Assert.False(new MessageRow { Flags = merged }.IsFlagged);
        Assert.False(new MessageRow { Flags = MFlags.Flagged }.IsHighPriority);
    }

    [Fact]
    public void Priority_refresh_preserves_every_existing_local_flag_and_is_idempotent()
    {
        using var dir = new TempDir();
        var (store, inbox, _) = Rows.NewStore(dir);
        var local = MFlags.Seen | MFlags.Flagged | MFlags.Answered | MFlags.Draft | MFlags.Deleted;
        var row = Assert.Single(store.InsertMessages(new[] { Rows.Make("A", inbox, "priority", flags: local) }));
        Assert.Equal(1, store.UpdateMessagePriorities(inbox, new[] { (row.Uid, true) }));
        Assert.Equal(local | MFlags.PriorityKnown | MFlags.HighPriority, store.GetMessage(row.Id)!.Flags);
        Assert.Equal(0, store.UpdateMessagePriorities(inbox, new[] { (row.Uid, true) }));
        Assert.Equal(1, store.UpdateMessagePriorities(inbox, new[] { (row.Uid, false) }));
        Assert.Equal(local | MFlags.PriorityKnown, store.GetMessage(row.Id)!.Flags);
    }

    [Fact]
    public void Stale_flag_updates_cannot_erase_or_resurrect_priority_metadata()
    {
        using var dir = new TempDir();
        var (store, inbox, sent) = Rows.NewStore(dir);
        var row = Assert.Single(store.InsertMessages(new[] { Rows.Make("A", inbox, "priority") }));
        var other = Rows.Make("A", sent, "other");
        other.Uid = row.Uid;
        store.InsertMessages(new[] { other });
        store.UpdateMessagePriorities(inbox, new[] { (row.Uid, true) });
        store.SetLocalFlags(row.Id, MFlags.Seen); // A stale UI row was created before the priority headers arrived.
        Assert.Equal(MFlags.Seen | MFlags.PriorityKnown | MFlags.HighPriority, store.GetMessage(row.Id)!.Flags);
        store.UpdateFlags(inbox, new[] { (row.Uid, MFlags.Answered | MFlags.Deleted) });
        Assert.Equal(MFlags.Answered | MFlags.Deleted | MFlags.PriorityKnown | MFlags.HighPriority, store.GetMessage(row.Id)!.Flags);
        Assert.Equal(MFlags.None, store.GetMessage(other.Id)!.Flags);
        store.UpdateMessagePriorities(inbox, new[] { (row.Uid, false) });
        store.UpdateFlags(inbox, new[] { (row.Uid, MFlags.Seen | MFlags.PriorityKnown | MFlags.HighPriority) });
        Assert.Equal(MFlags.Seen | MFlags.PriorityKnown, store.GetMessage(row.Id)!.Flags);
    }

    [Fact]
    public void Thread_badge_includes_older_high_priority_messages_but_pinned_view_does_not()
    {
        using var dir = new TempDir();
        var (store, inbox, _) = Rows.NewStore(dir);
        var now = DateTimeOffset.Now;
        store.InsertMessages(new[]
        {
            Rows.Make("A", inbox, "high", date: now.AddMinutes(-2), flags: MFlags.Seen | MFlags.HighPriority | MFlags.PriorityKnown),
            Rows.Make("A", inbox, "high", date: now.AddMinutes(-1), flags: MFlags.Seen | MFlags.PriorityKnown),
            Rows.Make("A", inbox, "pin", date: now, flags: MFlags.Flagged | MFlags.PriorityKnown),
        });
        var threads = store.ListThreads(new ListQuery { FolderIds = new[] { inbox } }, now);
        var high = Assert.Single(threads, t => t.ThreadKey == "high");
        Assert.True(high.HighPriority);
        Assert.False(high.Latest.IsHighPriority);
        Assert.False(high.Flagged);
        Assert.Equal(2, high.Count);
        Assert.Equal(0, high.UnreadCount);
        Assert.False(threads.Single(t => t.ThreadKey == "pin").HighPriority);
        Assert.Equal("pin", Assert.Single(store.ListThreads(new ListQuery { FolderIds = new[] { inbox }, FlaggedOnly = true }, now)).ThreadKey);
    }

    [Fact]
    public void Priority_aggregation_keeps_account_scope_and_deduplicates_folder_copies()
    {
        using var dir = new TempDir();
        var (store, inbox, sent) = Rows.NewStore(dir);
        var otherInbox = store.UpsertFolder(new Magpie.Core.Models.MailFolder { AccountId = "B", Name = "Inbox", Path = "INBOX", Role = FolderRole.Inbox });
        var flags = MFlags.HighPriority | MFlags.PriorityKnown;
        store.InsertMessages(new[]
        {
            Rows.Make("A", inbox, "same-thread", messageId: "same@example.test", flags: flags),
            Rows.Make("A", sent, "same-thread", messageId: "same@example.test", flags: flags),
            Rows.Make("B", otherInbox, "same-thread", messageId: "same@example.test", flags: MFlags.PriorityKnown),
        });
        var rows = store.ListThreads(new ListQuery { FolderIds = new[] { inbox, sent, otherInbox } }, DateTimeOffset.Now);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(1, r.Count));
        Assert.True(rows.Single(r => r.AccountId == "A").HighPriority);
        Assert.False(rows.Single(r => r.AccountId == "B").HighPriority);
    }

    [Fact]
    public void Existing_mail_priority_backfill_is_bounded_newest_first_and_excludes_known_or_removed_mail()
    {
        var map = Enumerable.Range(1, 260).ToDictionary(i => (long)i,
            i => (id: (long)i, flags: i > 200 ? MFlags.PriorityKnown : MFlags.Seen | MFlags.Flagged));
        var server = Enumerable.Range(1, 220).Select(i => new UniqueId(1, (uint)i)).ToArray();
        var excluded = new HashSet<long> { 198, 199, 200 };
        var candidates = AccountSync.PriorityBackfillCandidates(map, server, int.MaxValue, excluded);
        Assert.Equal(AccountSync.PriorityBackfillPerRound, candidates.Count);
        Assert.Equal(197u, candidates[0].Id);
        Assert.Equal(candidates.Select(u => u.Id).OrderByDescending(u => u), candidates.Select(u => u.Id));
        Assert.All(candidates, uid => Assert.DoesNotContain((long)uid.Id, excluded));
        Assert.Equal(3, AccountSync.PriorityBackfillCandidates(map, server, 3, excluded).Count);
        Assert.Empty(AccountSync.PriorityBackfillCandidates(map, server, 0, excluded));
        Assert.Empty(AccountSync.PriorityBackfillCandidates(map, server, -1, excluded));
        Assert.Empty(AccountSync.PriorityBackfillCandidates(map, Array.Empty<UniqueId>(), 100, excluded));
    }

    [Fact]
    public void Priority_backfill_requests_only_the_three_headers_not_message_bodies()
    {
        var request = AccountSync.PriorityHeadersRequest();
        var bodyFields = MessageSummaryItems.Body | MessageSummaryItems.BodyStructure | MessageSummaryItems.PreviewText;
        Assert.Equal(MessageSummaryItems.None, request.Items & bodyFields);
        Assert.True(request.Items.HasFlag(MessageSummaryItems.UniqueId));
        Assert.NotNull(request.Headers);
        Assert.Equal(MailPriorityHeaders.Names.OrderBy(s => s, StringComparer.OrdinalIgnoreCase),
            request.Headers!.OrderBy(s => s, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
    }
}
