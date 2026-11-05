using System.Windows;
using System.Windows.Controls;
using Magpie.Core.Updates;

namespace Magpie.App.Services;

/// <summary>Read-only published implementation details for one exact Magpie release.</summary>
public sealed class ReleaseNotesWindow : Window
{
    public ReleaseNotesWindow(ReleaseInfo release, string installed)
    {
        Title = $"Magpie · What's New · {release.Tag}";
        Width = 780; Height = 660; MinWidth = 560; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "Brush.Window.Background");
        SetResourceReference(ForegroundProperty, "Brush.Text.Primary");
        var root = new DockPanel { Margin = new Thickness(24) };
        Content = root;
        var close = new Button { Content = "Close", Width = 95, IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        close.SetResourceReference(StyleProperty, "Button.Secondary");
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Bottom); root.Children.Add(close);
        var header = new StackPanel();
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        header.Children.Add(new TextBlock { Text = $"What's New · {release.Tag}", FontSize = 22, FontWeight = FontWeights.SemiBold });
        header.Children.Add(new TextBlock { Text = $"Windows · {(release.Version.Pre.Length > 0 ? "Beta" : "Stable")} · Installed: v{installed}", Margin = new Thickness(0, 5, 0, 14) });
        var stack = new StackPanel();
        if (string.IsNullOrWhiteSpace(release.Notes))
            stack.Children.Add(new TextBlock { Text = "No implementation details were supplied for this version.", TextWrapping = TextWrapping.Wrap });
        else
            foreach (var line in release.Notes.Replace("\r", "").Split('\n'))
            {
                var raw = line.Trim();
                if (raw.Length == 0 || raw.StartsWith("<!--")) continue;
                var isHeading = raw.StartsWith('#');
                var text = raw.TrimStart('#', ' ', '-', '*').Replace("**", "");
                stack.Children.Add(new TextBlock { Text = (raw.StartsWith("- ") ? "• " : "") + text,
                    Margin = new Thickness(isHeading ? 0 : 12, isHeading ? 13 : 5, 0, 3),
                    FontSize = isHeading ? 17 : 13.5, FontWeight = isHeading ? FontWeights.SemiBold : FontWeights.Normal,
                    TextWrapping = TextWrapping.Wrap });
            }
        root.Children.Add(new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
    }

    public static async Task ShowInstalledAsync(Window owner)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(35) };
            var current = UpdateService.Current;
            var releases = await new UpdateClient(http).ListVersionsAsync(current, CancellationToken.None);
            var currentRelease = releases.FirstOrDefault(r => r.Info.Version.Equals(current)).Info;
            if (currentRelease is null)
            {
                MessageBox.Show(owner, $"Published implementation notes were not found for installed v{current}. No changes were inferred.", "Magpie · What's New");
                return;
            }
            new ReleaseNotesWindow(currentRelease, current.ToString()) { Owner = owner }.ShowDialog();
        }
        catch (Exception ex) { MessageBox.Show(owner, "Could not load release details: " + ex.Message, "Magpie · What's New"); }
    }
}
