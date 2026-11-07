using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Magpie.Core.Mail;
using Magpie.Core.Models;
using Magpie.Core.Storage;
using Microsoft.Data.Sqlite;
using MimeKit;

namespace Magpie.Core.Tests;

public class ComposeBodySafetyTests
{
    private const string Rich = "<p>My writing</p><div class='magpie-signature'>My signature</div><div class='magpie-quote'>Entire quoted message</div>";
    private const string Flattened = "<p>My writing</p><p>My signature</p><p>Entire quoted message</p>";
    private static Account Sender() => new() { Id = "A", Email = "sender@example.test", Enabled = false };
    private static Draft Draft(bool lost = false, bool plain = false) => new()
    {
        AccountId = "A", To = "recipient@example.test", Subject = "Boundary state", MessageId = "boundary@example.test",
        Html = Flattened, BodySectionsLost = lost, IsPlainText = plain,
    };
    private static MailEngine Engine(TempDir dir)
    {
        var engine = new MailEngine(new AppPaths(dir.Path), new FakeProtector());
        if (engine.AccountById("A") == null) engine.Config.Accounts.Add(Sender());
        return engine;
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Draft_roundtrip_preserves_explicit_state_while_normal_MIME_is_clean(bool lost, bool plain)
    {
        var draft = Draft(lost, plain);
        using var saved = Composer.BuildForDraft(draft, Sender());
        using var wire = Composer.FromBytes(Composer.ToBytes(saved));
        var reopened = Composer.FromMime(wire, "A", "");
        Assert.Equal(lost || plain, reopened.BodySectionsLost);
        Assert.Equal(plain, reopened.IsPlainText);
        Assert.Equal(lost || plain ? ComposeBodySafety.LostSections : ComposeBodySafety.KnownSections, wire.Headers[ComposeBodySafety.DraftHeader]);
        using var outgoing = Composer.Build(reopened, Sender());
        Assert.Null(outgoing.Headers[ComposeBodySafety.DraftHeader]);
        Assert.Contains("My signature", outgoing.TextBody);
        Assert.Contains("Entire quoted message", outgoing.TextBody);
    }

    [Fact]
    public void Exported_or_legacy_unmarked_MIME_is_conservative_but_existing_rich_sections_are_supported()
    {
        var draft = Draft();
        using var unmarked = Composer.Build(draft, Sender());
        Assert.True(Composer.FromMime(unmarked, "A", "").BodySectionsLost);
        draft.Html = Rich;
        using var marked = Composer.Build(draft, Sender());
        Assert.False(Composer.FromMime(marked, "A", "").BodySectionsLost);
        marked.Headers.Add(ComposeBodySafety.DraftHeader, ComposeBodySafety.LostSections);
        Assert.True(Composer.FromMime(marked, "A", "").BodySectionsLost); // A later signature cannot repair a flattened quote.
    }

    [Theory]
    [InlineData("v2; sections=known")]
    [InlineData("v1; sections=invalid")]
    [InlineData("")]
    public void Invalid_or_duplicate_draft_metadata_never_reenables_body_replacement(string value)
    {
        var draft = Draft(); draft.Html = Rich;
        using var message = Composer.Build(draft, Sender());
        message.Headers.Add(ComposeBodySafety.DraftHeader, value);
        Assert.True(Composer.FromMime(message, "A", "").BodySectionsLost);
        message.Headers.Add(ComposeBodySafety.DraftHeader, ComposeBodySafety.KnownSections);
        Assert.True(Composer.FromMime(message, "A", "").BodySectionsLost);
    }

    [Fact]
    public void Plain_MIME_cannot_claim_that_its_sections_are_known()
    {
        using var message = Composer.Build(Draft(plain: true), Sender());
        message.Headers.Add(ComposeBodySafety.DraftHeader, ComposeBodySafety.KnownSections);
        Assert.True(Composer.FromMime(message, "A", "").BodySectionsLost);
    }

    [Theory]
    [InlineData("<p>My writing</p><section class='magpie-quote'>Entire quoted message</section>")]
    [InlineData("<article><p>My writing</p><section class=magpie-quote><p>Entire quoted message</p></section><p>Following tail</p></article>")]
    [InlineData("<p>My writing <span class=magpie-signature>My signature</span></p><section class=magpie-quote>Entire quoted message</section>")]
    public void Fallback_tail_capture_matches_DOM_markers_including_nested_and_unquoted_classes(string html)
    {
        var tail = ComposeBodySafety.CapturePlainProtectedTail(html);
        Assert.DoesNotContain("My writing", tail);
        Assert.Contains("Entire quoted message", tail);
        if (html.Contains("My signature")) Assert.Contains("My signature", tail);
        if (html.Contains("Following tail")) Assert.Contains("Following tail", tail);
        Assert.EndsWith(tail, MimeText.HtmlToText(html));
    }

    [Fact]
    public void Local_save_and_history_keep_loss_after_returning_to_rich_and_can_restore_a_known_revision()
    {
        using var dir = new TempDir();
        using var engine = Engine(dir);
        var draft = Draft(); draft.Html = Rich;
        var local = engine.SaveLocalDraft(draft, false);
        var known = Assert.Single(engine.Store.GetDraftRevisions("A", draft.MessageId));
        draft.Html = Flattened; draft.IsPlainText = true; draft.BodySectionsLost = true;
        engine.SaveLocalDraft(draft, false);
        draft.IsPlainText = false;
        engine.SaveLocalDraft(draft, false);
        Assert.True(engine.OpenLocalDraft(local)!.BodySectionsLost);
        var latest = engine.Store.GetDraftRevisions("A", draft.MessageId)[0];
        var restored = engine.Store.OpenDraftRevision("A", draft.MessageId, latest.Id)!;
        Assert.False(restored.IsPlainText);
        Assert.True(restored.BodySectionsLost);
        Assert.Equal(Flattened, restored.Html);
        var original = engine.Store.OpenDraftRevision("A", draft.MessageId, known.Id)!;
        Assert.False(original.BodySectionsLost);
        Assert.Equal(Rich, original.Html);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Scheduling_restart_reschedule_and_undo_preserve_both_states_without_wire_metadata(bool lost)
    {
        using var dir = new TempDir(); long id; byte[] original;
        using (var engine = Engine(dir))
        {
            engine.Settings.Save(notify: false);
            id = engine.QueueSend(Draft(lost), DateTimeOffset.UtcNow.AddDays(1), null);
            var queued = Assert.Single(engine.Outbox()); original = queued.Mime;
            Assert.Equal(lost, queued.BodySectionsLost);
            using var wire = Composer.FromBytes(queued.Mime);
            Assert.Null(wire.Headers[ComposeBodySafety.DraftHeader]);
            engine.Store.RescheduleOutbox(id, DateTimeOffset.UtcNow.AddDays(2));
        }
        using var restarted = Engine(dir);
        Assert.Equal(lost, Assert.Single(restarted.OutboxSummary()).BodySectionsLost);
        Assert.Equal(original, Assert.Single(restarted.Outbox()).Mime);
        var recalled = restarted.Recall(id)!;
        Assert.Equal(lost, recalled.BodySectionsLost);
        Assert.Contains("Entire quoted message", recalled.Html);
    }

    [Fact]
    public void Existing_schema_queue_gets_nullable_column_without_rewriting_legacy_MIME()
    {
        using var dir = new TempDir();
        var store = new MailStore(dir.File("mail.db"));
        using var message = Composer.Build(Draft(), Sender());
        var bytes = Composer.ToBytes(message);
        var id = store.AddOutbox(new OutboxItem { AccountId = "A", Mime = bytes, SendAt = DateTimeOffset.UtcNow.AddDays(1) });
        SqliteConnection.ClearAllPools();
        using (var c = new SqliteConnection("Data Source=" + dir.File("mail.db")))
        {
            c.Open(); using var cmd = c.CreateCommand();
            cmd.CommandText = "ALTER TABLE outbox DROP COLUMN body_sections_lost"; cmd.ExecuteNonQuery();
        }
        using var engine = Engine(dir);
        var queued = Assert.Single(engine.Outbox());
        Assert.Null(queued.BodySectionsLost);
        Assert.Equal(bytes, queued.Mime);
        Assert.True(engine.Recall(id)!.BodySectionsLost);
    }

    [Fact]
    public void Legacy_revision_without_state_does_not_erase_conservative_MIME_inference()
    {
        using var dir = new TempDir(); var store = new MailStore(dir.File("mail.db"));
        var draft = Draft(); using var message = Composer.Build(draft, Sender());
        var id = store.SaveDraftRevision(draft, Composer.ToBytes(message));
        using var c = new SqliteConnection("Data Source=" + dir.File("mail.db")); c.Open();
        byte[] snapshot, mime;
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT snapshot,mime FROM draft_revisions WHERE id=$id"; cmd.Parameters.AddWithValue("$id", id);
            using var reader = cmd.ExecuteReader(); Assert.True(reader.Read());
            var node = JsonNode.Parse((byte[])reader[0])!.AsObject(); node.Remove("BodySectionsLost");
            snapshot = Encoding.UTF8.GetBytes(node.ToJsonString()); mime = (byte[])reader[1];
        }
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var bytes in new[] { snapshot, mime })
        {
            var length = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
            hash.AppendData(length); hash.AppendData(bytes);
        }
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "UPDATE draft_revisions SET snapshot=$s,payload_digest=$d WHERE id=$id";
            cmd.Parameters.AddWithValue("$s", snapshot); cmd.Parameters.AddWithValue("$d", Convert.ToHexString(hash.GetHashAndReset()));
            cmd.Parameters.AddWithValue("$id", id); cmd.ExecuteNonQuery();
        }
        Assert.True(store.OpenDraftRevision("A", draft.MessageId, id)!.BodySectionsLost);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Send_strips_only_outer_draft_metadata_and_keeps_attached_message_unchanged(bool receipt)
    {
        using var dir = new TempDir();
        using var inner = Composer.BuildForDraft(Draft(lost: true), Sender());
        var attached = new MessagePart { Message = inner, ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) };
        var innerBefore = Composer.ToBytes(inner); var sent = 0;
        Task Send(Account _, MimeMessage message, CancellationToken __)
        {
            Assert.Null(message.Headers[ComposeBodySafety.DraftHeader]);
            Assert.Equal(innerBefore, Composer.ToBytes(Assert.IsType<MimeMessage>(Assert.IsType<MessagePart>(Assert.Single(message.Attachments)).Message)));
            sent++; return Task.CompletedTask;
        }
        using var engine = new MailEngine(new AppPaths(dir.Path), new FakeProtector(), sendMessage: Send,
            sendWithDeliveryReceipt: async (a, m, ct) => { await Send(a, m, ct); return new(DeliveryReceiptState.Unavailable, "Accepted"); });
        engine.Config.Accounts.Add(Sender());
        var draft = Draft(lost: true); draft.RequestDeliveryReceipt = receipt; draft.CarriedParts.Add(attached);
        // Model a legacy queue containing draft-only headers; normal QueueSend already produces clean MIME.
        using var marked = Composer.BuildForDraft(draft, Sender());
        Assert.NotNull(marked.Headers[ComposeBodySafety.DraftHeader]);
        engine.Store.AddOutbox(new OutboxItem
        {
            AccountId = "A", Mime = Composer.ToBytes(marked), SendAt = DateTimeOffset.UtcNow.AddSeconds(-1),
            MessageId = draft.MessageId, Subject = draft.Subject, RequestDeliveryReceipt = receipt, BodySectionsLost = true,
        });
        await engine.ProcessOutboxAsync(CancellationToken.None);
        Assert.Equal(1, sent);
        Assert.Equal(OutboxStatus.Sent, Assert.Single(engine.Store.GetOutbox(includeDone: true)).Status);
    }
}
