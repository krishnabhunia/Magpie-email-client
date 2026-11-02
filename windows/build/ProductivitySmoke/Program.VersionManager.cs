using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Magpie.App.Services;
using Magpie.Core.Updates;

namespace ProductivitySmoke;

internal static partial class Program
{
    private static void CheckVersionManager(Window owner, string output, string theme)
    {
        static VersionChoice Choice(string version, bool available = true, bool compatible = true)
        {
            var parsed = AppVersion.TryParse(version)!;
            var prefix = $"https://github.com/{UpdateClient.Repo}/releases/download/v{version}/";
            var release = new ReleaseInfo
            {
                Version = parsed,
                Tag = "v" + version,
                Notes = "Release notes for " + version + ": preserves mail and drafts.",
                Published = new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero),
                ExeUrl = available ? prefix + "Magpie.exe" : "",
                ShaUrl = available ? prefix + "Magpie.exe.sha256" : "",
            };
            return new VersionChoice(release, parsed.IsPrerelease,
                new CompatibilityResult(available && compatible, !available ? "The application or its checksum is missing."
                    : !compatible ? "This release cannot safely read the current mail database."
                    : "Compatible with your mail, settings and installation."));
        }

        // Supply the catalogue directly: this is the real WPF chooser with no HTTP or installation side effects.
        VersionChoice[] choices =
        [
            Choice("12.0.0-beta.2"), Choice("12.0.0"), Choice("13.0.0", available: false),
            Choice("10.0.0"), Choice("11.0.0-beta.4"), Choice("9.0.0", compatible: false), Choice("11.0.0"),
        ];
        var window = new VersionHistoryWindow(AppVersion.TryParse("11.0.0")!, choices) { Owner = owner };
        Exception? failure = null;
        window.Dispatcher.BeginInvoke(() =>
        {
            try
            {
                var direction = (ComboBox)window.FindName("DirectionFilter");
                var channel = (ComboBox)window.FindName("ChannelFilter");
                var versions = (ListBox)window.FindName("ReleaseList");
                var summary = (TextBlock)window.FindName("ReleaseSummary");
                var compatibility = (TextBlock)window.FindName("CompatibilityMessage");
                var select = (Button)window.FindName("SelectVersionButton");

                void NoSelection(string scenario)
                {
                    Assert(versions.SelectedIndex == -1 && !select.IsEnabled,
                        "Version chooser never preselects a target: " + scenario);
                    Assert(window.Selection == null, "Browsing a version does not accept or install it: " + scenario);
                }

                Assert(versions.Items.Count == 3, "All newer stable/beta releases are shown, including missing assets");
                NoSelection("opening");
                versions.SelectedIndex = 0; // 13.0.0: newest, but its file is missing.
                Assert(!select.IsEnabled && compatibility.Text.Contains("missing", StringComparison.OrdinalIgnoreCase),
                    "A missing application remains visible with a blocked action and explanation");
                versions.SelectedIndex = 1; // 12.0.0 stable.
                Assert(select.IsEnabled && select.Content.ToString()!.Contains("v12.0.0", StringComparison.Ordinal),
                    "The action names the exact stable target");
                Assert(summary.Text.Contains("preserves mail and drafts", StringComparison.Ordinal),
                    "The selected release summary is shown");
                Capture(window, Path.Combine(output, theme + "-version-manager-newer.png"));

                channel.SelectedIndex = 1;
                Assert(versions.Items.Count == 2, "Stable filter retains every newer stable release");
                NoSelection("stable filter");
                channel.SelectedIndex = 2;
                Assert(versions.Items.Count == 1, "Beta filter retains the exact beta release");
                NoSelection("beta filter");
                versions.SelectedIndex = 0;
                Assert(select.Content.ToString()!.Contains("v12.0.0-beta.2", StringComparison.Ordinal),
                    "The beta suffix is retained in the action label");

                channel.SelectedIndex = 0;
                direction.SelectedIndex = 1;
                Assert(versions.Items.Count == 3, "Rollback shows older stable and beta versions");
                NoSelection("rollback filter");
                versions.SelectedIndex = 2; // 9.0.0 cannot read the current schema.
                Assert(!select.IsEnabled && compatibility.Text.Contains("cannot safely", StringComparison.OrdinalIgnoreCase),
                    "An incompatible downgrade is blocked before confirmation");
                channel.SelectedIndex = 2;
                Assert(versions.Items.Count == 1, "Rollback can isolate an older beta");
                versions.SelectedIndex = 0;
                Assert(select.IsEnabled && select.Content.ToString()!.Contains("v11.0.0-beta.4", StringComparison.Ordinal),
                    "Older beta is selectable with its exact version");
                Assert(window.Selection == null, "Selecting an older beta alone performs no transition");
                channel.SelectedIndex = 1;
                NoSelection("older stable filter");
                versions.SelectedIndex = 0; // 10.0.0 stable.
                Assert(select.IsEnabled && select.Content.ToString()!.Contains("v10.0.0", StringComparison.Ordinal),
                    "The rollback action names the exact stable target");
                Capture(window, Path.Combine(output, theme + "-version-manager-rollback.png"));
                select.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            catch (Exception ex) { failure = ex; window.Close(); }
        }, DispatcherPriority.ApplicationIdle);
        var accepted = window.ShowDialog();
        if (failure != null) throw failure;
        Assert(accepted == true && window.Selection?.Version.ToString() == "10.0.0" && window.IsRollback && !window.IsBeta,
            "Only the explicit action returns the exact selected rollback target to the caller");
    }
}
