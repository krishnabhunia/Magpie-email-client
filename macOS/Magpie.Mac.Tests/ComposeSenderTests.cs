using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Magpie.Core;
using Magpie.Core.Mail;
using Magpie.Core.Models;
using Magpie.Core.Security;
using Magpie.Mac.Services;
using Magpie.Mac.ViewModels;
using Magpie.Mac.Views;

namespace Magpie.Mac.Tests;

// The same test class keeps AppServices and Avalonia windows serial with the existing headless fixtures.
public partial class HeadlessTests
{
    [AvaloniaFact]
    public async Task Mac_reopens_the_actual_alias_and_preserves_it_through_save_schedule_and_undo()
    {
        using var fixture = new MacComposeFixture();
        var source = fixture.Draft();
        source.RequestDeliveryReceipt = true;
        var localId = fixture.Engine.SaveLocalDraft(source, pendingUpload: false);
        var draft = Assert.IsType<Draft>(fixture.Engine.OpenLocalDraft(localId));
        var (window, vm) = fixture.Open(draft);
        var picker = window.FindControl<ComboBox>("SenderIdentityPicker")!;
        Assert.False(vm.ShowFrom); // A single account must still show its non-primary From identity.
        Assert.True(picker.IsEffectivelyVisible);
        Assert.Equal("alias@example.com", vm.SelectedSender?.Address);
        Assert.Contains("Team alias", vm.SelectedSender!.Label);
        Assert.Same(vm.SelectedSender, picker.SelectedItem);
        Assert.False(vm.Dirty);

        picker.SelectedItem = vm.SenderChoices.First(s => s.Address == fixture.Account.Email);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(fixture.Account.Email, vm.SelectedSender?.Address);
        picker.SelectedItem = vm.SenderChoices.Single(s => s.Address == "alias@example.com");
        vm.Subject = "Mac edit saved";
        Assert.True(await vm.SaveDraftAsync());
        var reopened = Assert.IsType<Draft>(fixture.Engine.OpenLocalDraft(draft.LocalDraftId!.Value));
        Assert.Equal(fixture.Account.Id, reopened.AccountId);
        Assert.Equal("alias@example.com", reopened.SenderAddress);
        Assert.Equal("Mac edit saved", reopened.Subject);
        Assert.True(reopened.RequestDeliveryReceipt);

        await vm.SendAsync(DateTimeOffset.UtcNow.AddDays(2));
        var queued = Assert.Single(fixture.Engine.Store.GetOutbox());
        Assert.Equal(fixture.Account.Id, queued.AccountId);
        Assert.True(queued.RequestDeliveryReceipt);
        using var mime = Composer.FromBytes(queued.Mime);
        Assert.Equal("alias@example.com", Assert.Single(mime.From.Mailboxes).Address);
        Assert.Null(mime.Headers[DeliveryReceipts.DraftHeader]);
        var recalled = Assert.IsType<Draft>(fixture.Engine.Recall(queued.Id));
        Assert.Equal("alias@example.com", recalled.SenderAddress);
        Assert.Equal(fixture.Account.Id, recalled.AccountId);
        Assert.True(recalled.RequestDeliveryReceipt);
        Assert.Empty(fixture.Engine.Store.GetOutbox());
    }

    [AvaloniaFact]
    public async Task Mac_explicit_account_switch_uses_the_new_primary_and_keeps_unexposed_draft_options()
    {
        using var fixture = new MacComposeFixture(secondAccount: true);
        var original = fixture.Draft();
        original.RequestReadReceipt = true;
        original.RequestDeliveryReceipt = true;
        original.Security = new() { Format = MailSecurityFormat.OpenPgp, Sign = true, Encrypt = true };
        original.ReplyTo = "reply@example.net";
        original.Priority = "High";
        original.IsPlainText = true;
        original.InReplyTo = "<source@example.net>";
        original.References = "<source@example.net>";
        // Exercise the same saved MIME path as an opened server draft, without connecting an account.
        using var saved = Composer.BuildForDraft(original, fixture.Account);
        var draft = Composer.FromMime(saved, fixture.Account.Id, "");
        var originalNames = draft.AttachmentNames;
        var originalOrder = draft.AttachmentOrder;
        var (window, vm) = fixture.Open(draft);
        window.FindControl<ComboBox>("FromAccountPicker")!.SelectedItem = fixture.Second;
        Dispatcher.UIThread.RunJobs();
        Assert.Same(fixture.Second, vm.From);
        Assert.Equal(fixture.Second!.Email, vm.SelectedSender?.Address);
        Assert.DoesNotContain(vm.SenderChoices, s => s.Address == "alias@example.com");
        Assert.True(vm.Dirty);
        Assert.True(await vm.SaveDraftAsync());
        Assert.Equal(fixture.Second.Id, draft.AccountId);
        Assert.Equal(fixture.Second.Email, draft.SenderAddress);
        Assert.True(draft.RequestReadReceipt);
        Assert.True(draft.RequestDeliveryReceipt);
        Assert.Equal(MailSecurityFormat.OpenPgp, draft.Security.Format);
        Assert.True(draft.Security.Sign && draft.Security.Encrypt);
        Assert.Equal("reply@example.net", draft.ReplyTo);
        Assert.Equal("High", draft.Priority);
        Assert.True(draft.IsPlainText);
        Assert.Equal("<source@example.net>", draft.InReplyTo);
        Assert.Same(originalNames, draft.AttachmentNames);
        Assert.Same(originalOrder, draft.AttachmentOrder);
        var reopened = Assert.IsType<Draft>(fixture.Engine.OpenLocalDraft(draft.LocalDraftId!.Value));
        Assert.Equal(fixture.Second.Email, reopened.SenderAddress);
        Assert.True(reopened.RequestReadReceipt);
        Assert.True(reopened.RequestDeliveryReceipt);
        Assert.Equal(MailSecurityFormat.OpenPgp, reopened.Security.Format);
        Assert.True(reopened.Security.Sign && reopened.Security.Encrypt);
        Assert.Equal("reply@example.net", reopened.ReplyTo);
        Assert.Equal("High", reopened.Priority);
        Assert.True(reopened.IsPlainText);
        using var outgoing = Composer.Build(reopened, fixture.Second);
        Assert.Contains(fixture.Second.Email, outgoing.Headers["Disposition-Notification-To"]);
    }

    [AvaloniaFact]
    public async Task Mac_removed_alias_stays_visible_and_blocks_save_and_send_until_explicitly_changed()
    {
        using var fixture = new MacComposeFixture();
        var draft = fixture.Draft();
        var (window, vm) = fixture.Open(draft);
        vm.Dirty = false;
        var selected = vm.SelectedSender;
        fixture.Engine.Settings.Save();
        Assert.Same(selected, vm.SelectedSender);
        Assert.False(vm.Dirty);
        fixture.Account.SenderAliases.Clear();
        fixture.Engine.Settings.Save();
        Assert.False(vm.Dirty);
        Assert.Equal("alias@example.com", vm.SelectedSender?.Address);
        Assert.False(vm.SelectedSender!.Available);
        Assert.Contains("unavailable", vm.SelectedSender.Label);
        Assert.True(window.FindControl<ComboBox>("SenderIdentityPicker")!.IsEffectivelyVisible);
        Assert.False(await vm.SaveDraftAsync());
        await vm.SendAsync(DateTimeOffset.UtcNow.AddDays(1));
        Assert.Contains("configured sender", vm.Status);
        Assert.Empty(fixture.Engine.LocalDrafts());
        Assert.Empty(fixture.Engine.Store.GetOutbox());
        Assert.Equal("alias@example.com", draft.SenderAddress);

        fixture.Account.SenderAliases.Add(new SenderAlias { Email = "alias@example.com", Name = "Restored alias" });
        fixture.Engine.Settings.Save();
        Assert.True(vm.SelectedSender!.Available);
        Assert.Equal("alias@example.com", vm.SelectedSender.Address);
        Assert.False(vm.Dirty);
        fixture.Account.SenderAliases.Clear();
        fixture.Engine.Settings.Save();
        window.FindControl<ComboBox>("SenderIdentityPicker")!.SelectedItem = vm.SenderChoices.Single(s => s.Available);
        Assert.True(vm.Dirty);
        Assert.True(await vm.SaveDraftAsync());
        Assert.Equal(fixture.Account.Email, draft.SenderAddress);
    }

    [AvaloniaFact]
    public async Task Mac_sender_change_during_editor_read_does_not_queue_a_mixed_identity()
    {
        using var fixture = new MacComposeFixture(secondAccount: true);
        var draft = fixture.Draft();
        var (_, vm) = fixture.Open(draft);
        var body = new TaskCompletionSource<string>();
        vm.GetHtml = () => body.Task;
        var sending = vm.SendAsync(DateTimeOffset.UtcNow.AddDays(1));
        Assert.True(vm.Sending);
        vm.From = fixture.Second;
        body.SetResult("<p>Edited while reading</p>");
        await sending;
        Assert.Contains("sender changed", vm.Status);
        Assert.Empty(fixture.Engine.Store.GetOutbox());
        Assert.Equal(fixture.Account.Id, draft.AccountId);
        Assert.Equal("alias@example.com", draft.SenderAddress);
        Assert.False(vm.Sending);
        await vm.SendAsync(DateTimeOffset.UtcNow.AddDays(1));
        var queued = Assert.Single(fixture.Engine.Store.GetOutbox());
        Assert.Equal(fixture.Second!.Id, queued.AccountId);
        using var mime = Composer.FromBytes(queued.Mime);
        Assert.Equal(fixture.Second.Email, Assert.Single(mime.From.Mailboxes).Address);
    }

    private sealed class MacComposeFixture : IDisposable
    {
        private readonly MailEngine? _previous = AppServices.Engine;
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "magpie-mac-senders-" + Guid.NewGuid().ToString("N"));
        private readonly List<ComposeWindow> _windows = new();
        public MailEngine Engine { get; }
        public Account Account { get; } = new()
        {
            Id = "mac-primary", Email = "primary@example.com", DisplayName = "Primary", Enabled = false,
            SenderAliases = new() { new SenderAlias { Email = "alias@example.com", Name = "Team alias" } },
        };
        public Account? Second { get; }

        public MacComposeFixture(bool secondAccount = false)
        {
            Engine = new MailEngine(new AppPaths(_directory), new MacComposeProtector());
            Engine.Config.Accounts.Add(Account);
            if (secondAccount)
            {
                Second = new Account { Id = "mac-second", Email = "second@example.net", Enabled = false };
                Engine.Config.Accounts.Add(Second);
            }
            Engine.Settings.Save();
            AppServices.Engine = Engine;
        }

        public Draft Draft() => new()
        {
            AccountId = Account.Id, SenderAddress = "alias@example.com", To = "recipient@example.org",
            Subject = "Sender compatibility fixture", Html = "<p>Offline test message</p>",
        };

        public (ComposeWindow Window, ComposeViewModel Model) Open(Draft draft)
        {
            var window = new ComposeWindow(draft);
            _windows.Add(window);
            window.Show();
            var vm = Assert.IsType<ComposeViewModel>(window.DataContext);
            vm.GetHtml = () => Task.FromResult(draft.Html);
            return (window, vm);
        }

        public void Dispose()
        {
            foreach (var window in _windows)
            {
                ((ComposeViewModel)window.DataContext!).Dirty = false;
                window.Close();
            }
            AppServices.Engine = _previous!;
            Engine.Dispose();
            try { Directory.Delete(_directory, recursive: true); }
            catch (IOException) { }
        }
    }

    private sealed class MacComposeProtector : ISecretProtector
    {
        public byte[] Protect(byte[] plain) => plain.ToArray();
        public byte[] Unprotect(byte[] cipher) => cipher.ToArray();
    }
}
