using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Magpie.App.ViewModels;
using Magpie.App.Views;
using Magpie.Core;
using Magpie.Core.Mail;
using Magpie.Core.Models;

namespace ProductivitySmoke;

internal static partial class Program
{
    /// <summary>Real WPF recipient controls with fixture-only contacts; no editor launch, mail send or clipboard write.</summary>
    private static void CheckComposeRecipients(MailEngine engine, string theme)
    {
        var windows = new List<ComposeWindow>();
        ComposeWindow Create(Draft draft)
        {
            var created = new ComposeWindow(draft, null);
            windows.Add(created);
            Flush(created);
            RecipientInvoke(created, "RefreshAllRecipientChips");
            return created;
        }
        try
        {
            engine.Store.SaveLocalContact(new SavedContact
            {
                AccountId = "A", Name = "Jane Example", Emails = new() { "primary@example.test", "jane@example.test" },
                Company = "Example Company", Title = "Engineer", Groups = new() { "QA fixtures" },
            });
            engine.Store.TouchContacts(new[] { ("jane@example.test", "Jane Example") }, DateTimeOffset.UtcNow, sentTo: true);
            const string original = "\"Doe, Jane\" <jane@example.test>; incomplete address, second@example.test";
            var window = Create(new Draft { AccountId = "A", To = original, MessageId = "recipient-smoke@example.test", Html = "<p>Fixture</p>" });
            var vm = (ComposeViewModel)window.DataContext;
            var to = (TextBox)window.FindName("ToBox");
            var panel = (WrapPanel)window.FindName("ToChipPanel");
            var ccRow = (StackPanel)window.FindName("CcRow");
            var bccRow = (StackPanel)window.FindName("BccRow");
            Assert(!vm.ShowCcRow && !vm.ShowBccRow && ccRow.Visibility == Visibility.Collapsed && bccRow.Visibility == Visibility.Collapsed,
                theme + ": empty Cc and Bcc start independently collapsed");
            Click((Button)window.FindName("ShowCcLink"));
            Flush(window);
            Assert(vm.ShowCcRow && !vm.ShowBccRow && ccRow.Visibility == Visibility.Visible && bccRow.Visibility == Visibility.Collapsed,
                theme + ": Cc link opens only Cc");
            vm.Cc = "unfinished Cc";
            Flush(window);
            Assert(!vm.SetRecipientRowVisible("cc", false) && vm.ShowCcRow && vm.Cc == "unfinished Cc"
                && !((Button)window.FindName("HideCcLink")).IsEnabled,
                theme + ": non-empty invalid Cc cannot be hidden or cleared by Hide");
            vm.Cc = "";
            Flush(window);
            Click((Button)window.FindName("HideCcLink"));
            Flush(window);
            Assert(!vm.ShowCcRow && !vm.ShowBccRow, theme + ": an empty Cc can be closed without opening Bcc");
            Click((Button)window.FindName("ShowBccLink"));
            Flush(window);
            Assert(!vm.ShowCcRow && vm.ShowBccRow, theme + ": Bcc link opens only Bcc");
            vm.Bcc = "private@example.test";
            Flush(window);
            Assert(!vm.SetRecipientRowVisible("bcc", false) && vm.Bcc == "private@example.test" && vm.ShowBccRow,
                theme + ": hiding Bcc cannot hide or destroy a private recipient");
            vm.Bcc = "";
            vm.SetRecipientRowVisible("bcc", false);
            Flush(window);

            var chips = panel.Children.OfType<Border>().ToArray();
            Assert(chips.Length == 3 && vm.To == original && to.Text == original,
                theme + ": quoted commas and mixed valid/invalid recipient tokens remain intact");
            var invalid = chips.Single(chip => AutomationProperties.GetName(chip).StartsWith("Invalid recipient", StringComparison.Ordinal));
            Assert(((SolidColorBrush)invalid.BorderBrush).Color == ((SolidColorBrush)Application.Current.FindResource("Brush.Danger")).Color
                && ((SolidColorBrush)invalid.Background).Color == ((SolidColorBrush)Application.Current.FindResource("Brush.Danger.Subtle")).Color,
                theme + ": invalid chips use the current theme's danger border and background");
            var invalidHover = (StackPanel)RecipientInvoke(window, "BuildRecipientHover", invalid)!;
            Assert(invalidHover.Children.OfType<TextBlock>().Any(line => line.Text == "incomplete address")
                && invalidHover.Children.OfType<WrapPanel>().Single().Children.OfType<Button>().Count() == 2,
                theme + ": invalid hover preserves raw text and offers edit/remove, not unsafe address actions");

            var details = (StackPanel)RecipientInvoke(window, "BuildRecipientHover", chips[0])!;
            var lines = string.Join("\n", details.Children.OfType<TextBlock>().Select(line => line.Text));
            Assert(lines.Contains("jane@example.test") && lines.Contains("Saved contact · Jane Example")
                && lines.Contains("Engineer · Example Company") && lines.Contains("QA fixtures") && lines.Contains("Local contact frequency:"),
                theme + ": contact hover uses saved contact details for an exact secondary-email match");
            Assert(details.Children.OfType<WrapPanel>().Single().Children.OfType<Button>()
                .Where(button => button.Content?.ToString()?.StartsWith("Move to ", StringComparison.Ordinal) == true).All(button => !button.IsEnabled),
                theme + ": move actions do not discard existing invalid text in any recipient row");

            Click(((StackPanel)invalid.Child).Children.OfType<Button>().First());
            Assert(to.Visibility == Visibility.Visible && to.SelectedText == "incomplete address" && vm.To == original,
                theme + ": activating an invalid chip opens its exact raw token for correction");
            RecipientInvoke(window, "RefreshAllRecipientChips");
            chips = panel.Children.OfType<Border>().ToArray();
            var staleRemove = ((StackPanel)chips[2].Child).Children.OfType<Button>().Last();
            Click(((StackPanel)chips[0].Child).Children.OfType<Button>().Last());
            Flush(window);
            Assert(vm.To == "incomplete address, second@example.test" && Composer.InvalidAddresses(vm.To).Contains("incomplete address"),
                theme + ": removing a valid chip preserves neighbouring invalid input exactly");
            var afterRemoval = vm.To;
            Click(staleRemove);
            Assert(vm.To == afterRemoval, theme + ": a stale chip cannot remove a different token after a field changes");

            vm.To = "\"Doe, Jane\" <jane@example.test>, second@example.test";
            vm.Cc = "copy@example.test";
            vm.Bcc = "private@example.test";
            Flush(window);
            RecipientInvoke(window, "RefreshAllRecipientChips");
            var moving = panel.Children.OfType<Border>().First().Tag;
            var other = Create(new Draft { AccountId = "A", To = vm.To, Cc = "existing@example.test", MessageId = "other-recipient-smoke@example.test" });
            var otherVm = (ComposeViewModel)other.DataContext;
            Assert(otherVm.ShowCcRow && !otherVm.ShowBccRow, theme + ": reopening a Cc-only draft does not reveal an empty Bcc row");
            otherVm.Cc = "";
            Assert(otherVm.ShowCcRow && otherVm.SetRecipientRowVisible("cc", false) && !otherVm.ShowCcRow,
                theme + ": clearing the last Cc address keeps its editor open until explicitly hidden");
            Assert(RecipientInvoke(other, "MoveRecipientChip", moving, "bcc") is false && otherVm.Bcc == "",
                theme + ": chips from a different compose window cannot move recipients");
            Assert(RecipientInvoke(window, "MoveRecipientChip", moving, "bcc") is true,
                theme + ": the same recipient action used by drag-and-drop moves a valid chip");
            Assert(!Composer.ParseAddresses(vm.To).Mailboxes.Any(person => person.Address == "jane@example.test")
                && Composer.ParseAddresses(vm.Bcc).Mailboxes.Any(person => person.Address == "jane@example.test" && person.Name == "Doe, Jane")
                && Composer.ParseAddresses(vm.Bcc).Mailboxes.Any(person => person.Address == "private@example.test")
                && vm.Cc == "copy@example.test" && vm.ShowBccRow,
                theme + ": moving preserves the name, other fields and existing private recipients");
        }
        finally
        {
            foreach (var window in windows)
            {
                typeof(ComposeWindow).GetField("_forceClose", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
                window.Close();
                ((ComposeViewModel)window.DataContext).Detach();
            }
        }
    }

    private static void Flush(ComposeWindow window) => window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static object? RecipientInvoke(ComposeWindow window, string method, params object[] args) =>
        typeof(ComposeWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args);
}
