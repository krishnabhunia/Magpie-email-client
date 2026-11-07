using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Magpie.App;
using Magpie.App.ViewModels;
using Magpie.Core.Models;

namespace ProductivitySmoke;

internal static partial class Program
{
    private static void CheckPriorityIndicator(MainWindow window)
    {
        var list = (ListBox)window.FindName("ThreadList");
        var template = list.ItemTemplate;
        // LoadContent alone has no template host; WPF defers template bindings in that disconnected tree.
        // Realize the production template in its own small host without showing MainWindow or starting its WebView.
        var presenter = new ContentPresenter { ContentTemplate = template };
        var host = new Window
        {
            Content = presenter, DataContext = window.DataContext, Width = 650, Height = 180,
            ShowActivated = false, ShowInTaskbar = false, Left = -3000, Top = -3000,
        };
        var now = DateTimeOffset.Now;
        TextBlock? badge = null;
        try
        {
            host.Show();
            foreach (var (flags, aggregate, expected) in new[]
            {
                (MessageFlags.None, false, false),
                (MessageFlags.Flagged | MessageFlags.PriorityKnown, false, false),
                (MessageFlags.HighPriority | MessageFlags.PriorityKnown, false, true),
                (MessageFlags.Seen | MessageFlags.PriorityKnown, true, true),
            })
            {
                var item = new ThreadItem(new ThreadRow
                {
                    AccountId = "A", ThreadKey = "priority-smoke", Count = 2, HighPriority = aggregate,
                    Flagged = flags.HasFlag(MessageFlags.Flagged),
                    Latest = new MessageRow { Subject = "Subject is unchanged", FromAddress = "sender@example.test", Flags = flags, Date = now, SortDate = now },
                }, "#14606E", false, now, "me@example.test");
                presenter.Content = item;
                presenter.ApplyTemplate();
                host.UpdateLayout();
                host.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
                badge = template.FindName("HighPriorityBadge", presenter) as TextBlock
                    ?? throw new InvalidOperationException("The realized mail-row template has no named high-priority badge.");
                var binding = badge.GetBindingExpression(UIElement.VisibilityProperty);
                binding?.UpdateTarget();
                Assert(item.IsHighPriority == expected && badge.Visibility == (expected ? Visibility.Visible : Visibility.Collapsed),
                    $"Mail-list priority badge follows sender metadata ({flags}, conversation={aggregate}, expected={expected}, "
                    + $"vm={item.IsHighPriority}, visibility={badge.Visibility}, context={badge.DataContext?.GetType().Name ?? "null"}, binding={binding?.Status.ToString() ?? "missing"})");
                Assert(item.Subject == "Subject is unchanged", "Priority marker does not rewrite the actual message subject");
            }
            Assert(badge!.Text == "!" && AutomationProperties.GetName(badge) == "High priority message",
                "High-priority marker has a non-colour-only symbol and an accessible name");
            Assert(badge.ToolTip?.ToString()?.Contains("by its sender", StringComparison.Ordinal) == true,
                "Priority tooltip distinguishes sender-declared importance from the user's pinned state");
            Assert(badge.Foreground is SolidColorBrush brush && host.TryFindResource("Brush.Danger") is SolidColorBrush danger
                && brush.Color == danger.Color, "High-priority marker uses the current theme's visible danger colour");
            Console.WriteLine("PRIORITY_INDICATOR_SMOKE_PASSED");
        }
        finally { presenter.Content = null; host.Close(); }
    }
}
