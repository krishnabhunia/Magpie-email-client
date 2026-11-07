using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
    private static (Border Studio, TabControl Tabs, TabItem Options, StackPanel Content) RealizeComposeOptions(ComposeWindow window)
    {
        var studio = (Border)window.FindName("StudioPanel");
        if (studio.Visibility != Visibility.Visible) Click((Button)window.FindName("StudioModeButton"));
        var tabs = ((DockPanel)studio.Child).Children.OfType<TabControl>().Single();
        var options = tabs.Items.OfType<TabItem>().Single(item => Equals(item.Header, "Options"));
        tabs.SelectedItem = options;
        // Unselected TabItem content has not entered the presenter yet. Realize the actual selected panel
        // before reading inherited DataContext/bindings, without loading the unrelated WebView editor.
        tabs.ApplyTemplate();
        studio.Measure(new Size(studio.Width, window.Height));
        studio.Arrange(new Rect(0, 0, studio.Width, window.Height));
        studio.UpdateLayout();
        Flush(window);
        return (studio, tabs, options, (StackPanel)((ScrollViewer)options.Content).Content);
    }

    private static string ComposeOptionState(CheckBox check, ComposeViewModel vm)
    {
        var binding = BindingOperations.GetBindingExpression(check, ToggleButton.IsCheckedProperty);
        return $"{check.Content}: checked={check.IsChecked?.ToString() ?? "null"}, enabled={check.IsEnabled}, "
            + $"context={check.DataContext?.GetType().Name ?? "null"}, contextMatches={ReferenceEquals(check.DataContext, vm)}, "
            + $"binding={binding?.Status.ToString() ?? "missing"}, sourceMatches={ReferenceEquals(binding?.DataItem, vm)}";
    }

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
            var options = RealizeComposeOptions(window);
            var check = (CheckBox)window.FindName("RequestDeliveryReceiptCheck");
            var readCheck = options.Content.Children.OfType<CheckBox>().Single(c => Equals(c.Content, "Request read receipt"));
            string State() => $" studio={options.Studio.Visibility}, tab={options.Tabs.SelectedIndex}, selectedOptions={options.Options.IsSelected}, "
                + $"vmDelivery={vm.RequestDeliveryReceipt}, vmRead={vm.RequestReadReceipt}, draftDelivery={draft.RequestDeliveryReceipt}, dirty={vm.Dirty}; "
                + ComposeOptionState(check, vm) + "; " + ComposeOptionState(readCheck, vm);
            Assert(options.Studio.Visibility == Visibility.Visible && options.Options.IsSelected
                && ReferenceEquals(check.DataContext, vm) && ReferenceEquals(readCheck.DataContext, vm)
                && BindingOperations.GetBindingExpression(check, ToggleButton.IsCheckedProperty)?.Status == BindingStatus.Active
                && check.IsChecked == true && vm.RequestDeliveryReceipt && !vm.RequestReadReceipt && readCheck.IsChecked == false,
                theme + ": delivery request is visible, bound and independent of the read-receipt control;" + State());
            Assert(AutomationProperties.GetName(check).Contains("sending server")
                && vm.DeliveryReceiptHelp.Contains("sends normally") && vm.DeliveryReceiptHelp.Contains("not a read receipt"),
                theme + ": delivery control has an accessible name and explains unsupported-server fallback");
            await vm.CaptureDraftRevisionAsync();
            var original = engine.Store.GetDraftRevisions(draft.AccountId, draft.MessageId).First();
            vm.Dirty = false;
            ((IToggleProvider)new CheckBoxAutomationPeer(check)).Toggle();
            Flush(window);
            Assert(check.IsChecked == false && !vm.RequestDeliveryReceipt && vm.Dirty && !vm.RequestReadReceipt && readCheck.IsChecked == false,
                theme + ": changing the checkbox edits only the requested delivery option;" + State());
            var restored = await vm.RestoreDraftRevisionAsync(original.Id);
            Flush(window);
            Assert(restored && vm.RequestDeliveryReceipt && draft.RequestDeliveryReceipt,
                theme + ": revision restoration returns the delivery option to both view model and draft;" + State());
            Assert(check.IsChecked == true && readCheck.IsChecked == false && !vm.RequestReadReceipt,
                theme + ": restored delivery intent returns to the actual native checkbox;" + State());
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
