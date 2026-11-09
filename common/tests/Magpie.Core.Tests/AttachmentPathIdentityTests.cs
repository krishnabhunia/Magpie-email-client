using Magpie.Core.Mail;
using Magpie.Core.Models;
using MimeKit;

namespace Magpie.Core.Tests;

public sealed class AttachmentPathIdentityTests
{
    private static byte[] Payload(MimeEntity entity)
    {
        var part = Assert.IsAssignableFrom<MimePart>(entity);
        Assert.NotNull(part.Content);
        using var output = new MemoryStream();
        part.Content.DecodeTo(output);
        return output.ToArray();
    }

    [Fact]
    public void Exact_repeated_paths_are_serialized_once_even_when_repeated_in_order()
    {
        using var dir = new TempDir();
        var file = dir.File("Report.bin");
        File.WriteAllBytes(file, new byte[] { 10, 20, 30 });
        var draft = new Draft
        {
            Html = "<p>Report</p>", AttachmentPaths = new() { file, file },
            AttachmentOrder = new() { new() { Path = file }, new() { Path = file } },
        };
        using var message = Composer.Build(draft, new Account { Email = "me@example.test" });
        Assert.Equal(new byte[] { 10, 20, 30 }, Payload(Assert.Single(message.Attachments)));
    }

    [Fact]
    public void Distinct_case_variant_paths_keep_bytes_names_and_order_through_drafts_history_and_queue()
    {
        using var dir = new TempDir();
        var upper = dir.File("Report.bin");
        var lower = dir.File("report.bin");
        var upperBytes = new byte[] { 10, 20, 30 };
        var lowerBytes = new byte[] { 40, 50, 60, 70 };
        File.WriteAllBytes(upper, upperBytes);
        File.WriteAllBytes(lower, lowerBytes);
        // Some runners use case-insensitive filesystems, where these two distinct files cannot exist.
        // The Linux fixture exercises the actual case-sensitive-volume regression without mocking file reads.
        if (!File.ReadAllBytes(upper).SequenceEqual(upperBytes)) return;
        using var engine = new MailEngine(new AppPaths(dir.Path), new FakeProtector());
        var account = new Account { Id = "case-files", Email = "me@example.test", Enabled = false };
        engine.Config.Accounts.Add(account);
        var draft = new Draft
        {
            AccountId = account.Id, MessageId = "case-files@example.test", To = "recipient@example.test", Html = "<p>Both reports</p>",
            AttachmentPaths = new() { upper, lower, upper },
            AttachmentOrder = new() { new() { Path = lower }, new() { Path = upper } },
        };
        draft.AttachmentNames[upper] = "first-report.bin";
        draft.AttachmentNames[lower] = "second-report.bin";
        var id = engine.SaveLocalDraft(draft, pendingUpload: true);
        using var saved = Composer.FromBytes(engine.Store.GetLocalDraft(id)!.Mime);
        void Check(MimeMessage message)
        {
            var attachments = message.Attachments.ToArray();
            Assert.Equal(new[] { "second-report.bin", "first-report.bin" }, attachments.Select(MimeAttachmentNames.Get));
            Assert.Equal(lowerBytes, Payload(attachments[0]));
            Assert.Equal(upperBytes, Payload(attachments[1]));
        }
        Check(saved);
        var revision = Assert.Single(engine.Store.GetDraftRevisions(account.Id, draft.MessageId));
        var restored = Assert.IsType<Draft>(engine.Store.OpenDraftRevision(account.Id, draft.MessageId, revision.Id));
        File.Delete(upper); File.Delete(lower);
        Check(Composer.Build(restored, account));
        var queuedId = engine.QueueSend(restored, DateTimeOffset.UtcNow.AddDays(1), null);
        using var queued = Composer.FromBytes(engine.Store.GetOutbox().Single(item => item.Id == queuedId).Mime);
        Check(queued);
    }

    [Fact]
    public void Order_entry_cannot_add_a_different_unselected_case_variant_file()
    {
        using var dir = new TempDir();
        var upper = dir.File("Report.bin");
        var lower = dir.File("report.bin");
        File.WriteAllBytes(upper, new byte[] { 1 });
        File.WriteAllBytes(lower, new byte[] { 2 });
        if (File.ReadAllBytes(upper)[0] != 1) return;
        var draft = new Draft
        {
            Html = "<p>Only the selected report</p>", AttachmentPaths = new() { lower },
            AttachmentOrder = new() { new() { Path = upper } },
        };
        using var message = Composer.Build(draft, new Account { Email = "me@example.test" });
        Assert.Equal(new byte[] { 2 }, Payload(Assert.Single(message.Attachments)));
    }
}
