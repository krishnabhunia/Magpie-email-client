using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using Magpie.App;
using Magpie.App.ViewModels;
using Magpie.App.Views;
using Magpie.Core;
using Magpie.Core.Mail;
using Magpie.Core.Models;
using MimeKit;

namespace ProductivitySmoke;

internal static partial class Program
{
    private static async Task CheckDeliveryReceiptOptionsAsync(MailEngine originalEngine, string theme)
    {
        var http = new RejectHttp();
        using var engine = new MailEngine(new AppPaths(Path.Combine(originalEngine.Paths.Root, "delivery-options-" + theme)),
            new TestProtector(), http, sendMessage: (_, _, _) => Task.CompletedTask);
        AppServices.Engine = engine;
        ComposeWindow? window = null;
        ComposeViewModel? vm = null;
        var closed = false;
        try
        {
            engine.Config.Accounts.Add(new Account { Id = "delivery", Email = "me@example.test", Enabled = false });
            engine.Config.Ai.Enabled = false;
            const string html = "<p>A receipt request is not delivery confirmation.</p>";
            var draft = new Draft
            {
                AccountId = "delivery", MessageId = "receipt-ui-" + theme + "@example.test", Subject = "Delivery options",
                To = "recipient@example.test", Html = html, RequestDeliveryReceipt = true,
            };
            window = new ComposeWindow(draft, null);
            window.Closed += (_, _) => closed = true;
            vm = (ComposeViewModel)window.DataContext;
            vm.GetHtml = () => Task.FromResult(html);
            vm.GetNewText = () => Task.FromResult("A receipt request is not delivery confirmation.");
            vm.RestoreRevisionEditor = (_, _) => Task.CompletedTask;
            var check = (CheckBox)window.FindName("RequestDeliveryReceiptCheck");
            BindingOperations.GetBindingExpression(check, System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty)?.UpdateTarget();
            Assert(check.IsChecked == true && vm.RequestDeliveryReceipt && !vm.RequestReadReceipt,
                theme + ": delivery request is visible, bound and independent of the read-receipt control");
            Assert(AutomationProperties.GetName(check).Contains("sending server")
                && vm.DeliveryReceiptHelp.Contains("sends normally") && vm.DeliveryReceiptHelp.Contains("not a read receipt"),
                theme + ": delivery control has an accessible name and explains unsupported-server fallback");
            await vm.CaptureDraftRevisionAsync();
            var original = engine.Store.GetDraftRevisions(draft.AccountId, draft.MessageId).First();
            vm.Dirty = false;
            check.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, false);
            BindingOperations.GetBindingExpression(check, System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty)?.UpdateSource();
            Assert(!vm.RequestDeliveryReceipt && vm.Dirty && !vm.RequestReadReceipt,
                theme + ": changing the checkbox edits only the requested delivery option");
            Assert(await vm.RestoreDraftRevisionAsync(original.Id) && vm.RequestDeliveryReceipt && draft.RequestDeliveryReceipt,
                theme + ": revision restoration returns the delivery option to both view model and draft");
            BindingOperations.GetBindingExpression(check, System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty)?.UpdateTarget();
            Assert(check.IsChecked == true, theme + ": restored delivery intent returns to the actual native checkbox");
            await vm.SendAsync(DateTimeOffset.Now.AddDays(1), null);
            var queued = engine.Store.GetOutbox().Single();
            using var mime = Composer.FromBytes(queued.Mime);
            Assert(queued.RequestDeliveryReceipt && queued.DeliveryReceipt?.State == DeliveryReceiptState.Pending
                && mime.Headers[DeliveryReceipts.DraftHeader] == null,
                theme + ": scheduled Send persists delivery intent separately from transmitted MIME");
            var reopened = engine.Recall(queued.Id);
            Assert(reopened?.RequestDeliveryReceipt == true && engine.Store.GetOutbox().Count == 0 && http.Requests == 0,
                theme + ": Undo restores delivery intent without contacting a mail or HTTP service");
            Console.WriteLine("DELIVERY_RECEIPT_OPTIONS_SMOKE_PASSED theme=" + theme);
        }
        finally
        {
            if (vm != null) { vm.Dirty = false; vm.Detach(); }
            if (!closed) window?.Close();
            AppServices.Engine = originalEngine;
        }
    }
}
