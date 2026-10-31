using System.Windows;
using System.Windows.Controls;
using Magpie.Core.Updates;

namespace Magpie.App.Services;

/// <summary>Read-only version history until the user explicitly chooses a release.</summary>
public sealed class VersionHistoryWindow : Window
{
    private readonly UpdateClient _client;
    private readonly AppVersion _current;
    private readonly ComboBox _direction = new() { Width = 170, Margin = new Thickness(0, 0, 12, 0) };
    private readonly ComboBox _channel = new() { Width = 170 };
    private readonly ListBox _versions = new() { MinHeight = 220, Margin = new Thickness(0, 14, 0, 10) };
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _select = new() { Content = "Continue with selected version", IsEnabled = false, MinWidth = 210 };
    private IReadOnlyList<(ReleaseInfo Info, bool Prerelease)> _releases = [];

    public ReleaseInfo? Selection { get; private set; }
    public bool IsRollback => _direction.SelectedIndex == 1;
    public bool IsBeta { get; private set; }

    public VersionHistoryWindow(UpdateClient client, AppVersion current)
    {
        _client = client;
        _current = current;
        Title = "Magpie · Version manager";
        Width = 610;
        Height = 490;
        MinWidth = 540;
        MinHeight = 410;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(20) };
        Content = root;

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var cancel = new Button { Content = "Cancel", MinWidth = 90, Margin = new Thickness(10, 0, 0, 0) };
        cancel.Click += (_, _) => Close();
        _select.Click += (_, _) =>
        {
            if (_versions.SelectedItem is not VersionRow row) return;
            Selection = row.Release;
            IsBeta = row.Beta;
            DialogResult = true;
        };
        actions.Children.Add(_select);
        actions.Children.Add(cancel);
        DockPanel.SetDock(actions, Dock.Bottom);
        root.Children.Add(actions);

        var header = new StackPanel();
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        header.Children.Add(new TextBlock { Text = "Choose the version you want", FontWeight = FontWeights.SemiBold, FontSize = 20 });
        header.Children.Add(new TextBlock
        {
            Text = $"Running Magpie {_current}. Nothing downloads or installs until you confirm a version.",
            Margin = new Thickness(0, 5, 0, 12),
            TextWrapping = TextWrapping.Wrap
        });
        _direction.Items.Add("Update (newer)");
        _direction.Items.Add("Roll back (older)");
        _direction.SelectedIndex = 0;
        _channel.Items.Add("Stable and beta");
        _channel.Items.Add("Stable only");
        _channel.Items.Add("Beta only");
        _channel.SelectedIndex = 0;
        _direction.SelectionChanged += (_, _) => Refresh();
        _channel.SelectionChanged += (_, _) => Refresh();
        var filters = new StackPanel { Orientation = Orientation.Horizontal };
        filters.Children.Add(_direction);
        filters.Children.Add(_channel);
        header.Children.Add(filters);

        var center = new DockPanel();
        root.Children.Add(center);
        DockPanel.SetDock(_message, Dock.Bottom);
        center.Children.Add(_message);
        _versions.DisplayMemberPath = nameof(VersionRow.Label);
        _versions.SelectionChanged += (_, _) =>
        {
            _select.IsEnabled = _versions.SelectedItem is VersionRow;
            if (_versions.SelectedItem is VersionRow)
                _message.Text = "Review the release and confirm before installing. Beta and rollback versions carry additional compatibility risks.";
        };
        center.Children.Add(_versions);

        Loaded += async (_, _) =>
        {
            _message.Text = "Loading available releases from GitHub…";
            try
            {
                _releases = await _client.ListVersionsAsync(_current, CancellationToken.None);
                Refresh();
            }
            catch (Exception ex) { _message.Text = "Release history unavailable: " + ex.Message; }
        };
    }

    private void Refresh()
    {
        if (_direction.SelectedIndex < 0 || _channel.SelectedIndex < 0) return;
        var selected = UpdateClient.SelectVersions(_releases, _current,
            older: _direction.SelectedIndex == 1, includePrerelease: true);
        if (_channel.SelectedIndex == 1) selected = selected.Where(r => !r.Prerelease).ToList();
        else if (_channel.SelectedIndex == 2) selected = selected.Where(r => r.Prerelease).ToList();
        _versions.ItemsSource = selected.Select(r => new VersionRow(r.Info, r.Prerelease,
            $"v{r.Info.Version}  ·  {(r.Prerelease ? "Beta" : "Stable")}" +
            (r.Info.Published is { } d ? $"  ·  {d.LocalDateTime:d}" : ""))).ToList();
        _select.IsEnabled = false;
        _message.Text = selected.Count == 0
            ? "No SHA-256-backed version matches this filter."
            : $"{selected.Count} selectable version(s). Choose a specific release to continue.";
    }

    private sealed record VersionRow(ReleaseInfo Release, bool Beta, string Label);
}
