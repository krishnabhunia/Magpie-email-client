using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Magpie.App.Services;
using Magpie.Core.Settings;

namespace ProductivitySmoke;

internal static partial class Program
{
    // SP2: exercise the real early-STA splash, including both themes, even though the main WPF Application
    // and its resource dictionaries do not exist when the splash first appears in production.
    private static void CheckSplashWindow(string output)
    {
        var windowField = typeof(StartupSplash).GetField("_win", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Splash window field not found");
        foreach (var theme in new[] { ThemeMode.Light, ThemeMode.Dark })
        {
            StartupSplash.Start(new SplashPrefs
            {
                Theme = theme,
                Accounts = new[] { new SplashAccount("offline@example.test", "#5B8DEF") },
            }, "14.0.0", afterUpdate: false, restarting: false);
            var splash = StartupSplash.Current;
            Assert(splash != null, "Early startup splash controller was created");

            var appeared = SpinWait.SpinUntil(() => windowField.GetValue(splash) is Window, TimeSpan.FromSeconds(5));
            Assert(appeared, theme + " splash was created on its own STA");
            var window = (Window)windowField.GetValue(splash)!;
            window.Dispatcher.Invoke(() =>
            {
                Assert(window.IsVisible, theme + " splash is visible while loading");
                var card = (Border)window.Content;
                Assert(card.Width >= 700, "Signature splash uses readable desktop dimensions");
                var labels = Descendants(card).OfType<TextBlock>().Select(t => t.Text).ToList();
                Assert(labels.Contains("magpie."), "Signature wordmark is visible");
                Assert(labels.Any(t => t.Contains("Your email, beautifully organized.")), "Approved tagline is visible");
                Assert(labels.Any(t => t.Contains("Checking the last update")), "The active startup step is visible");
                Capture(window, Path.Combine(output, theme + "-splash.png"));
            });
            for (var step = 0; step < SplashPlan.StepCount; step++) splash!.Next(1);
            var log = splash!.Finish();
            Assert(log.StartsWith("start-up ", StringComparison.Ordinal), "Real startup milestones create timing log");
            Thread.Sleep(1000); // Allow the minimum-visible fade to finish before the next fixture.
        }
    }
}
