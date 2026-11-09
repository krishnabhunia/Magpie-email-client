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
            Choice("12.0.0-beta002"), Choice("12.0.0-beta003"), Choice("12.0.0-beta004", compatible: false),
            Choice("12.0.0"), Choice("13.0.0", available: false),
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
                var showAll = (CheckBox)window.FindName("ShowAllBuilds");
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

                string RowText(int index) => (string)versions.Items[index].GetType().GetProperty("Label")!
                    .GetValue(versions.Items[index])!;

                Assert(versions.Items.Count == 2 && showAll.IsChecked != true,
                    "Normal update view has one newest compatible beta and one stable release");
                NoSelection("opening");
                versions.SelectedIndex = 0; // 12.0.0 stable.
                Assert(select.IsEnabled && select.Content.ToString()!.Contains("v12.0.0", StringComparison.Ordinal),
                    "The action names the exact stable target");
                Assert(summary.Text.Contains("preserves mail and drafts", StringComparison.Ordinal),
                    "The selected release summary is shown");
                Capture(window, Path.Combine(output, theme + "-version-manager-newer.png"));

                channel.SelectedIndex = 1;
                Assert(versions.Items.Count == 1, "Stable filter defaults to one compatible release");
                NoSelection("stable filter");
                channel.SelectedIndex = 2;
                Assert(versions.Items.Count == 1, "Beta filter defaults to only the latest compatible beta");
                NoSelection("beta filter");
                versions.SelectedIndex = 0;
                Assert(select.Content.ToString()!.Contains("v12.0.0-beta003", StringComparison.Ordinal),
                    "An incompatible newer beta never hides the newest compatible one");
                Assert(RowText(0).Contains("v12.0.0-beta003", StringComparison.Ordinal) &&
                    RowText(0).Contains("(LATEST)", StringComparison.Ordinal) &&
                    RowText(0).Contains("Exact tag: v12.0.0-beta003", StringComparison.Ordinal),
                    "Newest compatible beta preserves its permanent three-digit tag");
                Assert(RowText(0).Contains("30 Oct 2026", StringComparison.Ordinal),
                    "Release list shows the publication date with local clock time");

                showAll.IsChecked = true;
                Assert(versions.Items.Count == 3, "Show all retains every beta commit for exact-version selection");
                Assert(RowText(0).Contains("v12.0.0-beta004", StringComparison.Ordinal) &&
                    !RowText(0).Contains("(LATEST)", StringComparison.Ordinal),
                    "Blocked beta keeps exact identifier without a misleading latest marker");
                Assert(RowText(1).Contains("v12.0.0-beta003", StringComparison.Ordinal) &&
                    RowText(2).Contains("v12.0.0-beta002", StringComparison.Ordinal),
                    "Beta labels stay tied to immutable tags rather than shifting display ranks");
                NoSelection("show all beta builds");
                versions.SelectedIndex = 0; // 12.0.0-beta004 is incompatible.
                Assert(!select.IsEnabled && compatibility.Text.Contains("cannot safely", StringComparison.OrdinalIgnoreCase),
                    "Show all explains why the newest beta is blocked");
                channel.SelectedIndex = 0;
                Assert(versions.Items.Count == 5, "Show all includes missing assets and all stable and beta builds");
                Assert(RowText(1).Contains("(LATEST)", StringComparison.Ordinal),
                    "Latest compatible stable release is visibly marked");
                NoSelection("show all channels");
                versions.SelectedIndex = 0; // 13.0.0 application is missing.
                Assert(!select.IsEnabled && compatibility.Text.Contains("missing", StringComparison.OrdinalIgnoreCase),
                    "Show all retains missing applications with blocked actions");
                showAll.IsChecked = false;
                Assert(versions.Items.Count == 2, "Hiding historical builds restores latest-per-channel defaults");
                NoSelection("show all unchecked");

                direction.SelectedIndex = 1;
                Assert(!showAll.IsEnabled, "Rollback always displays the full history");
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
