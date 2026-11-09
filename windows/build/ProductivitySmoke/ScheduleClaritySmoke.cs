using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Magpie.App.ViewModels;
using Magpie.App.Views;
using Magpie.Core;
using Magpie.Core.Mail;
using Magpie.Core.Models;

namespace ProductivitySmoke;

internal static partial class Program
{
    private static void CheckScheduleClarity(string theme)
    {
        var compose = new ComposeWindow(new Draft { AccountId = "A", Html = "<p>Schedule fixture</p>" }, null);
        var vm = (ComposeViewModel)compose.DataContext;
        var dialog = new PickTimeDialog();
        try
        {
            var menu = (ContextMenu)RecipientInvoke(compose, "BuildSendLaterMenu", compose)!;
            Assert(menu.Items.OfType<MenuItem>().Any(item => item.Header is TextBlock block && block.Text == ScheduleTime.ExecutionHelp),
                theme + ": the preset menu explains PC, connectivity, overdue-send and paused-retry requirements before any choice");
            var presets = menu.Items.OfType<MenuItem>().Where(item => Equals(item.ToolTip, ScheduleTime.ExecutionHelp)).ToArray();
            Assert(presets.Length > 0 && presets.All(item => item.Header is TextBlock block && block.Text.Contains("Local time")
                && block.Text.Contains("UTC") && AutomationProperties.GetName(item) == block.Text),
                theme + ": every one-click send preset displays its resolved local zone and offset");
            var day = (DatePicker)dialog.FindName("Day");
            var time = (ComboBox)dialog.FindName("Time");
            var zone = (TextBlock)dialog.FindName("TimeZoneNote");
            day.SelectedDate = DateTime.Today.AddDays(2);
            time.SelectedItem = "08:00";
            Assert(zone.Text.Contains("Local time") && zone.Text.Contains("UTC")
                && AutomationProperties.GetName(zone) == zone.Text,
                theme + ": custom date/time selection displays an accessible resolved local zone and offset");
            var queued = new OutboxItem { SendAt = DateTimeOffset.Now.AddDays(2), Status = OutboxStatus.Queued };
            Assert(new ScheduledItem(queued, "me@example.test").When.Contains("UTC"),
                theme + ": the durable Scheduled row identifies the local time zone");
            queued.Status = OutboxStatus.Failed;
            queued.Attempts = OutboxItem.MaximumAutomaticAttempts;
            queued.LastError = "Server offline";
            var paused = new ScheduledItem(queued, "me@example.test").When;
            Assert(paused.Contains("Automatic retries paused") && paused.Contains("Send now") && !paused.Contains("will retry"),
                theme + ": exhausted automatic retries ask for an explicit retry and do not promise another automatic attempt");
            Console.WriteLine("SCHEDULE_CLARITY_SMOKE_PASSED theme=" + theme);
        }
        finally
        {
            typeof(ComposeWindow).GetField("_forceClose", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(compose, true);
            compose.Close();
            vm.Detach();
            dialog.Close();
        }
    }
}
