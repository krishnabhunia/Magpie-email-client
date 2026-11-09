using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Magpie.App.ViewModels;
using Magpie.App.Views;
using Magpie.Core.Mail;

namespace ProductivitySmoke;

internal static partial class Program
{
    /// <summary>Exercise real close handlers without loading WebView2, opening dialogs or touching the clipboard.</summary>
    private static async Task CheckComposeBusyCloseAsync(string theme)
    {
        var window = new ComposeWindow(new Draft
        {
            AccountId = "A", MessageId = "busy-close-" + theme.ToLowerInvariant() + "@example.test",
            To = "other@example.test", Subject = "Clean pending operation", Html = "<p>Body</p>",
        }, null);
        var vm = (ComposeViewModel)window.DataContext;
        var closed = false;
        window.Closed += (_, _) => closed = true;
        try
        {
            foreach (var operation in new[] { "attachment", "send", "restore" })
            {
                vm.Dirty = false;
                vm.AttachmentOperationBusy = operation == "attachment";
                vm.Sending = operation == "send";
                vm.RevisionBusy = operation == "restore";
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
                Assert(window.HasUnsavedContent, theme + ": Windows session ending must wait for a clean draft's active " + operation);
                if (vm.Sending)
                    Assert(!((TextBox)window.FindName("ToBox")).IsEnabled,
                        theme + ": recipient editing is disabled while send preparation holds its snapshot");

                window.Close();
                Assert(!closed && Application.Current.Windows.Cast<Window>().Contains(window) && !vm.Dirty,
                    theme + ": the native close handler cancels closing during " + operation + " even when Dirty is false");
                Assert(!await window.CloseForExitAsync() && !closed && vm.Status.Contains("operation to finish", StringComparison.Ordinal),
                    theme + ": application exit cannot discard an in-flight " + operation);
                vm.AttachmentOperationBusy = vm.Sending = vm.RevisionBusy = false;
            }
            Assert(!window.HasUnsavedContent, theme + ": a clean idle draft no longer blocks session ending");
            Assert(await window.CloseForExitAsync() && closed && !Application.Current.Windows.Cast<Window>().Contains(window),
                theme + ": the same window closes normally after all operations finish");
            Console.WriteLine("COMPOSE_BUSY_CLOSE_SMOKE_PASSED theme=" + theme);
        }
        finally
        {
            if (!closed)
            {
                vm.AttachmentOperationBusy = vm.Sending = vm.RevisionBusy = false;
                vm.Dirty = false;
                await window.CloseForExitAsync();
            }
        }
    }
}
