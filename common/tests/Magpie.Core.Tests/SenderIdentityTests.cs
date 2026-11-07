using System.Text.Json;
using Magpie.Core.Mail;
using Magpie.Core.Models;
using Magpie.Core.Settings;
using MimeKit;

namespace Magpie.Core.Tests;

public sealed class SenderIdentityTests
{
    private static Account Account() => new()
    {
        Id = "identity", Email = "primary@example.test", DisplayName = "Primary", Enabled = false,
        SenderAliases = new() { new() { Email = "projects@example.test", Name = "Projects Team" } },
    };

    private static Draft Draft() => new()
    {
        AccountId = "identity", SenderAddress = "projects@example.test", MessageId = "identity-draft@example.test",
        To = "recipient@example.test", Subject = "Project update", Html = "<p>Kept draft content</p>",
        RequestReadReceipt = true,
    };

    [Fact]
    public void Named_aliases_parse_and_roundtrip_without_changing_primary_identity()
    {
        var account = Account();
        var aliases = SenderIdentities.Parse("Projects Team <projects@example.test>\r\nBilling <billing@example.test>", account.Email);
        Assert.Equal(new[] { "projects@example.test", "billing@example.test" }, aliases.Select(a => a.Email));
        Assert.Equal("Projects Team", aliases[0].Name);
        var parsed = SenderIdentities.Parse(SenderIdentities.Format(aliases), account.Email);
        Assert.Equal(aliases.Select(a => (a.Email, a.Name)), parsed.Select(a => (a.Email, a.Name)));
        Assert.Equal("primary@example.test", SenderIdentities.Resolve(account, "").Address);
        Assert.Equal("Projects Team", SenderIdentities.Resolve(account, "PROJECTS@example.test").Name);
    }

    [Theory]
    [InlineData("PRIMARY@example.test")]
    [InlineData("alias@example.test\nALIAS@example.test")]
    [InlineData("unfinished")]
    [InlineData("first@example.test, second@example.test")]
    [InlineData("Alias\rBcc: other@example.test")]
    [InlineData("Alias\u0000 <alias@example.test>")]
    public void Invalid_duplicate_or_header_like_alias_entries_are_rejected(string input)
    {
        Assert.Throws<FormatException>(() => SenderIdentities.Parse(input, "primary@example.test"));
    }

    [Fact]
    public void Alias_count_and_display_name_have_explicit_bounds()
    {
        var entries = Enumerable.Range(0, 21).Select(n => $"alias{n}@example.test").ToArray();
        Assert.Equal(20, SenderIdentities.Parse(string.Join('\n', entries.Take(20)), "primary@example.test").Count);
        Assert.Throws<FormatException>(() => SenderIdentities.Parse(string.Join('\n', entries), "primary@example.test"));
        Assert.Throws<FormatException>(() => SenderIdentities.Parse(new string('n', 101) + " <alias@example.test>", "primary@example.test"));
    }

    [Fact]
    public void Settings_normalize_invalid_aliases_and_clone_without_shared_mutable_objects()
    {
        using var dir = new TempDir();
        var account = Account();
        account.SenderAliases.AddRange(new SenderAlias[]
        {
            new() { Email = "PROJECTS@example.test" }, new() { Email = account.Email },
            new() { Email = "bad" }, new() { Email = "valid@example.test", Name = "bad\nname" }, null!,
        });
        var settings = new AppSettings { Accounts = new() { account } };
        File.WriteAllText(dir.File("settings.json"), JsonSerializer.Serialize(settings, AppSettings.Json));
        using var engine = new MailEngine(new AppPaths(dir.Path), new FakeProtector());
        var restored = Assert.Single(engine.Config.Accounts);
        Assert.Equal("projects@example.test", Assert.Single(restored.SenderAliases).Email);
        var clone = restored.Clone();
        clone.SenderAliases[0].Name = "Changed clone";
        clone.SenderAliases.Add(new() { Email = "new@example.test" });
        Assert.Equal("Projects Team", Assert.Single(restored.SenderAliases).Name);
        Assert.Contains("projects@example.test", engine.MyAddresses);
        Assert.Contains("primary@example.test", engine.MyAddresses);
    }

    [Fact]
    public void Mime_uses_chosen_alias_and_read_receipt_address_with_original_authentication_account()
    {
        var account = Account();
        var draft = Draft();
        using var message = Composer.Build(draft, account);
        var sender = Assert.Single(message.From.Mailboxes);
        Assert.Equal("projects@example.test", sender.Address);
        Assert.Equal("Projects Team", sender.Name);
        Assert.Equal("projects@example.test", MailboxAddress.Parse(Assert.IsType<string>(message.Headers[HeaderId.DispositionNotificationTo])).Address);
        Assert.Equal("identity", draft.AccountId);
        Assert.Equal("primary@example.test", account.Email);
        Assert.Null(message.Sender);
    }

    [Fact]
    public void Removed_or_unconfigured_sender_cannot_silently_become_primary()
    {
        var account = Account();
        var draft = Draft();
        account.SenderAliases.Clear();
        Assert.Throws<InvalidOperationException>(() => Composer.Build(draft, account));
        using var unrelated = new MimeMessage();
        unrelated.From.Add(MailboxAddress.Parse("unconfigured@example.test"));
        unrelated.Body = new TextPart("plain") { Text = "Draft content" };
        var reopened = Composer.FromMime(unrelated, account.Id, "");
        Assert.Equal("unconfigured@example.test", reopened.SenderAddress);
        Assert.Throws<InvalidOperationException>(() => Composer.Build(reopened, account));
    }

    [Fact]
    public void Alias_survives_saved_draft_revision_queue_and_undo_without_changing_account()
    {
        using var dir = new TempDir();
        using var engine = new MailEngine(new AppPaths(dir.Path), new FakeProtector());
        var account = Account();
        engine.Config.Accounts.Add(account);
        var draft = Draft();
        var localId = engine.SaveLocalDraft(draft, pendingUpload: true);
        var reopened = Assert.IsType<Draft>(engine.OpenLocalDraft(localId));
        Assert.Equal(draft.SenderAddress, reopened.SenderAddress);
        var revision = Assert.Single(engine.Store.GetDraftRevisions(account.Id, draft.MessageId));
        var restored = Assert.IsType<Draft>(engine.Store.OpenDraftRevision(account.Id, draft.MessageId, revision.Id));
        Assert.Equal(draft.SenderAddress, restored.SenderAddress);
        var outboxId = engine.QueueSend(reopened, DateTimeOffset.UtcNow.AddHours(1), null);
        var queued = engine.Store.GetOutbox().Single(i => i.Id == outboxId);
        Assert.Equal(account.Id, queued.AccountId);
        using var queuedMessage = Composer.FromBytes(queued.Mime);
        Assert.Equal(draft.SenderAddress, Assert.Single(queuedMessage.From.Mailboxes).Address);
        var recalled = Assert.IsType<Draft>(engine.Recall(outboxId));
        Assert.Equal(account.Id, recalled.AccountId);
        Assert.Equal(draft.SenderAddress, recalled.SenderAddress);
        account.SenderAliases.Clear();
        Assert.Throws<InvalidOperationException>(() => engine.QueueSend(recalled, DateTimeOffset.UtcNow, null));
    }

    [Fact]
    public async Task Removing_an_alias_after_scheduling_blocks_transport_until_the_user_reviews_it()
    {
        using var dir = new TempDir();
        var accepted = 0;
        using var engine = new MailEngine(new AppPaths(dir.Path), new FakeProtector(),
            sendMessage: (_, _, _) => { accepted++; return Task.CompletedTask; });
        var account = Account();
        engine.Config.Accounts.Add(account);
        var queuedId = engine.QueueSend(Draft(), DateTimeOffset.UtcNow.AddSeconds(-1), null);
        account.SenderAliases.Clear();
        await engine.ProcessOutboxAsync(CancellationToken.None);
        var failed = engine.Store.GetOutbox().Single(o => o.Id == queuedId);
        Assert.Equal(0, accepted);
        Assert.Equal(OutboxStatus.Failed, failed.Status);
        Assert.Contains("sender", failed.LastError, StringComparison.OrdinalIgnoreCase);
        var recalled = Assert.IsType<Draft>(engine.Recall(queuedId));
        Assert.Equal("projects@example.test", recalled.SenderAddress);
    }

    [Fact]
    public void Reply_all_excludes_primary_and_alias_addresses()
    {
        using var dir = new TempDir();
        using var engine = new MailEngine(new AppPaths(dir.Path), new FakeProtector());
        var account = Account();
        engine.Config.Accounts.Add(account);
        var row = Rows.Make(account.Id, 1, "thread", from: "colleague@example.test");
        row.To = "Projects Team <projects@example.test>, other@example.test";
        row.Cc = "primary@example.test, copy@example.test";
        var reply = Composer.Prepare(ComposeMode.ReplyAll, account, row, new MessageBody { Text = "Hello" }, engine.MyAddresses, null);
        Assert.DoesNotContain("projects@example.test", reply.To + reply.Cc, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("primary@example.test", reply.To + reply.Cc, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("colleague@example.test", reply.To);
        Assert.Contains("other@example.test", reply.To + reply.Cc);
        Assert.Contains("copy@example.test", reply.To + reply.Cc);
    }
}
