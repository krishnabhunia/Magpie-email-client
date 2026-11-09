using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Magpie.App;
using Magpie.App.Views;
using Magpie.Core.Info;

namespace ProductivitySmoke;

internal static partial class Program
{
    /// <summary>Design IN1: the real Info window opens with its four tabs in this theme, for a test build and a local one.</summary>
    private static void CheckInfoWindow(Window owner, string output, string theme)
    {
        var beta = BuildInfo.From("15.0.0-beta007", key => key switch
        {
            "SourcePr" => "35", "SourceCommit" => "4e68c2a0b1c2d3e4f5061728394a5b6c7d8e9f00",
            "BuildTimeUtc" => "2026-11-04T14:25:00Z", "ReleaseTag" => "v15.0.0-beta007", _ => null,
        });
        var window = new InfoWindow(beta, owner) { ShowActivated = false, Left = -4000, Top = -4000 };
        try
        {
            window.Show();
            window.UpdateLayout();
            Assert(window.Title == "Magpie · Info", "Info window title");
            Assert(((TextBlock)window.FindName("InfoSubtitle")).Text == "Version 15.0.0-beta007 · Beta · Windows", "Info subtitle names version, channel and platform");
            var tabs = (TabControl)window.FindName("InfoTabs");
            Assert(tabs.Items.Cast<TabItem>().Select(t => (string)t.Header).SequenceEqual(new[] { "This build", "What's implemented", "What's new", "Under the hood" }),
                "Info window has the four tabs of the design");
            Assert(((TextBlock)window.FindName("BetaBanner")).Text.Contains("beta 007 of Magpie 15.0.0", StringComparison.Ordinal), "Test build banner names its beta number");
            Assert(Descendants((Grid)window.FindName("BuildTable")).OfType<TextBlock>().Any(t => t.Text == "PR #35" || t.Inlines.OfType<System.Windows.Documents.Hyperlink>().Any()),
                "This build lists the PR it was made from");
            Assert(window.FindName("CopyBuildInfo") is Button, "Copy build info button exists");
            Capture(window, Path.Combine(output, theme + "-info-this-build.png"));
            tabs.SelectedIndex = 1; window.UpdateLayout();
            var search = (TextBox)window.FindName("FeatureSearch");
            var table = (Grid)window.FindName("FeatureTable");
            var all = table.RowDefinitions.Count;
            Assert(all > 20, "What's implemented lists docs/FEATURES.md");
            search.Text = "Info window"; window.UpdateLayout();
            Assert(table.RowDefinitions.Count == 2, "Searching narrows the feature table (header + 1 row)");
            search.Text = ""; window.UpdateLayout();
            Assert(table.RowDefinitions.Count == all, "Clearing the search shows every feature again");
            Capture(window, Path.Combine(output, theme + "-info-implemented.png"));
            tabs.SelectedIndex = 2; window.UpdateLayout();
            Assert(window.FindName("FullReleaseNotes") is Button, "What's new offers the full release notes (the former What's New button)");
            Capture(window, Path.Combine(output, theme + "-info-whats-new.png"));
            tabs.SelectedIndex = 3; window.UpdateLayout();
            Assert(window.FindName("PartsTable") is Grid parts && parts.RowDefinitions.Count >= 10, "Under the hood lists the parts");
            Capture(window, Path.Combine(output, theme + "-info-under-the-hood.png"));
        }
        finally { window.Close(); }

        var local = new InfoWindow(BuildInfo.From("15.0.0", _ => null), owner) { ShowActivated = false, Left = -4000, Top = -4000 };
        try
        {
            local.Show();
            Assert(local.FindName("BetaBanner") == null, "A stable build has no test-build banner");
            Assert(Descendants((Grid)local.FindName("BuildTable")).OfType<TextBlock>().Any(t => t.Text == BuildInfo.LocalBuild),
                "A local build says Local build where CI details are missing");
        }
        finally { local.Close(); }
        Assert(InfoWindow.CopyText().Split('\n').Length == 2, "Copy build info is two lines");
    }

    private static void CheckInfoEntryPoints(MainWindow window)
    {
        var info = window.FindName("InfoButton") as Button;
        Assert(info != null && System.Windows.Automation.AutomationProperties.GetName(info) == "Info" && (string)info.ToolTip == "Info (F1)",
            "The title bar has an Info button named Info with the F1 tooltip");
        Assert(window.FindName("BetaChip") is Border, "The title bar has the Beta chip");
        Assert(!LogicalDescendants(window).OfType<Button>().Any(b => System.Windows.Automation.AutomationProperties.GetName(b) == "What's New"),
            "Only one ⓘ in the title bar: the separate What's New button is gone");
    }

    private static IEnumerable<object> LogicalDescendants(DependencyObject node)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(node))
        {
            yield return child;
            if (child is DependencyObject d)
                foreach (var next in LogicalDescendants(d)) yield return next;
        }
    }
}
