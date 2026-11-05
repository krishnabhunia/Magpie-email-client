using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Magpie.Core.Updates;

namespace Magpie.App.Services;

/// <summary>Lists exact releases; browsing and selecting a row never starts an installation.</summary>
public sealed class VersionHistoryWindow : Window
{
    private readonly AppVersion _current;
    private readonly CancellationTokenSource _lifetime = new();
    /// <summary>Updates | Version history &amp; roll back tabs (design VP1, Q90); a Selector like the dropdown it replaced.</summary>
    private readonly TabControl _direction = new() { Padding = new Thickness(0), Margin = new Thickness(0, 0, 0, 12) };
    private readonly ComboBox _channel = new() { Width = 170 };
    private readonly CheckBox _showAllBuilds = new()
    {
        Content = "Show all builds (including earlier betas)",
        Margin = new Thickness(0, 10, 0, 0),
        IsChecked = false,
    };
    private readonly ListBox _versions = new() { MinHeight = 140, Margin = new Thickness(0, 14, 0, 10) };
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBlock _compatibility = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly Button _select = new() { Content = "Choose a version", IsEnabled = false, MinWidth = 190 };
    private IReadOnlyList<VersionChoice> _choices;
    private readonly IReadOnlySet<string> _kept;

    public ReleaseInfo? Selection { get; private set; }
    /// <summary>The chosen row with its compatibility, for the confirm screen.</summary>
    public VersionChoice? SelectedChoice { get; private set; }
    /// <summary>Every release checked for this window, for the confirm screen's "skips the latest stable" note.</summary>
    public IReadOnlyList<VersionChoice> Choices => _choices;
    /// <summary>True when the chosen version's checked copy is in this PC's version backups.</summary>
    public bool SelectedOnThisPc => SelectedChoice is { } c && _kept.Contains(c.Release.Version.ToString());
    public bool IsRollback => _direction.SelectedIndex == 1;
    public bool IsBeta { get; private set; }

    public VersionHistoryWindow(UpdateClient client, AppVersion current, UpdateProfile profile, string installKind,
        IReadOnlySet<string>? kept = null, bool rollback = false)
        : this(current, [], showPin: true, kept, rollback)
    {
        Loaded += async (_, _) => await LoadAsync(client, profile, installKind);
    }

    /// <summary>The same chooser with an already checked catalogue; used by the native UI smoke tests.</summary>
    public VersionHistoryWindow(AppVersion current, IReadOnlyList<VersionChoice> choices, IReadOnlySet<string>? kept = null)
        : this(current, choices, showPin: false, kept, rollback: false) { }

    private VersionHistoryWindow(AppVersion current, IReadOnlyList<VersionChoice> choices, bool showPin, IReadOnlySet<string>? kept, bool rollback)
    {
        _current = current;
        _choices = choices;
        _kept = kept ?? new HashSet<string>();
        Title = "Magpie · Version manager";
        Width = 760; Height = 690; MinWidth = 620; MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "Brush.Window.Background");
        SetResourceReference(ForegroundProperty, "Brush.Text.Primary");
        NameScope.SetNameScope(this, new NameScope());
        RegisterName("DirectionFilter", _direction);
        RegisterName("ChannelFilter", _channel);
        RegisterName("ShowAllBuilds", _showAllBuilds);
        RegisterName("ReleaseList", _versions);
        RegisterName("ReleaseSummary", _summary);
        RegisterName("CompatibilityMessage", _compatibility);
        RegisterName("SelectVersionButton", _select);

        var root = new DockPanel { Margin = new Thickness(22) };
        Content = root;
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90, Margin = new Thickness(10, 0, 0, 0) };
        cancel.SetResourceReference(StyleProperty, "Button.Secondary");
        _select.SetResourceReference(StyleProperty, "Button.Primary");
        cancel.Click += (_, _) => Close();
        _select.Click += (_, _) =>
        {
            if (_versions.SelectedItem is not VersionRow row || !row.Choice.Compatibility.CanInstall) return;
            Selection = row.Choice.Release;
            SelectedChoice = row.Choice;
            IsBeta = row.Choice.Prerelease;
            DialogResult = true;
        };
        actions.Children.Add(_select); actions.Children.Add(cancel);
        DockPanel.SetDock(actions, Dock.Bottom); root.Children.Add(actions);

        var header = new StackPanel();
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        header.Children.Add(new TextBlock { Text = "Choose the version you want", FontWeight = FontWeights.SemiBold, FontSize = 21 });
        header.Children.Add(new TextBlock
        {
            Text = $"Running Magpie v{_current}. Choose a release, review its details, then confirm the exact version.",
            Margin = new Thickness(0, 6, 0, 12), TextWrapping = TextWrapping.Wrap,
        });
        if (showPin && UpdateService.PinnedVersion is { Length: > 0 } pinned)
        {
            var pinLabel = new TextBlock { Text = $"Pinned to v{pinned}. Installation always needs your confirmation.",
                Margin = new Thickness(0, 0, 0, 5), TextWrapping = TextWrapping.Wrap };
            var clear = new Button { Content = "Clear version pin", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 10) };
            clear.SetResourceReference(StyleProperty, "Button.Secondary");
            clear.Click += (_, _) =>
            {
                try { UpdateService.ClearVersionPin(); pinLabel.Text = "Version pin cleared. Automatic checks can be resumed in Settings."; clear.IsEnabled = false; }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "Could not clear version pin"); }
            };
            header.Children.Add(pinLabel); header.Children.Add(clear);
        }
        _direction.Items.Add(new TabItem { Header = "Updates" });
        _direction.Items.Add(new TabItem { Header = "Version history & roll back" });
        _direction.SelectedIndex = rollback ? 1 : 0;
        _channel.Items.Add("Stable and beta"); _channel.Items.Add("Stable only"); _channel.Items.Add("Beta only"); _channel.SelectedIndex = 0;
        _direction.SelectionChanged += (_, _) => Refresh(); _channel.SelectionChanged += (_, _) => Refresh();
        _showAllBuilds.Checked += (_, _) => Refresh(); _showAllBuilds.Unchecked += (_, _) => Refresh();
        header.Children.Add(_direction);
        var filters = new StackPanel { Orientation = Orientation.Horizontal };
        filters.Children.Add(_channel); header.Children.Add(filters);
        header.Children.Add(_showAllBuilds);

        var details = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        details.Children.Add(_message); details.Children.Add(_compatibility);
        details.Children.Add(new ScrollViewer { Content = _summary, MaxHeight = 150, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        DockPanel.SetDock(details, Dock.Bottom); root.Children.Add(details);
        _versions.DisplayMemberPath = nameof(VersionRow.Label);
        _versions.SelectionChanged += (_, _) => ShowSelection(); root.Children.Add(_versions);
        Closed += (_, _) => _lifetime.Cancel();
        Refresh();
    }

    private async Task LoadAsync(UpdateClient client, UpdateProfile profile, string installKind)
    {
        _message.Text = "Loading releases and checking compatibility…";
        try
        {
            var releases = await client.ListVersionsAsync(_current, _lifetime.Token);
            using var gate = new SemaphoreSlim(4);
            var pending = releases.Select(async release =>
            {
                await gate.WaitAsync(_lifetime.Token);
                try
                {
                    var manifest = release.Info.AssetAvailable ? await client.LoadCompatibilityAsync(release.Info, _lifetime.Token) : null;
                    return new VersionChoice(release.Info, release.Prerelease,
                        UpdateCompatibility.Evaluate(release.Info, manifest, profile, installKind));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    return new VersionChoice(release.Info, release.Prerelease,
                        new CompatibilityResult(false, "Compatibility could not be verified: " + ex.Message));
                }
                finally { gate.Release(); }
            });
            _choices = await Task.WhenAll(pending);
            if (!_lifetime.IsCancellationRequested) Refresh();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _message.Text = "Release history unavailable: " + ex.Message; }
    }

    private void Refresh()
    {
        if (_direction.SelectedIndex < 0 || _channel.SelectedIndex < 0) return;
        // Update: only the newest installable release for this platform in each selected channel.
        // The full retained catalogue is still accessible for exact-version selection and rollback.
        _showAllBuilds.IsEnabled = !IsRollback;
        var matching = _choices.Where(c => IsRollback ? c.Release.Version.CompareTo(_current) < 0 : c.Release.Version.CompareTo(_current) > 0)
            .Where(c => _channel.SelectedIndex == 0 || (_channel.SelectedIndex == 1 ? !c.Prerelease : c.Prerelease))
            .OrderByDescending(c => c.Release.Version).GroupBy(c => c.Release.Version.ToString()).Select(g => g.First()).ToList();
        var visible = !IsRollback && _showAllBuilds.IsChecked != true
            ? matching.Where(c => c.Release.AssetAvailable && c.Compatibility.CanInstall)
                .GroupBy(c => c.Prerelease).Select(g => g.First()).OrderByDescending(c => c.Release.Version)
            : matching.AsEnumerable();
        // Design BT1 A: every row shows its real version (v15.0.0-beta007); old long test tags show as they are.
        var installable = _choices.Where(c => c.Release.AssetAvailable && c.Compatibility.CanInstall)
            .OrderByDescending(c => c.Release.Version)
            .GroupBy(c => c.Release.Version.ToString()).Select(g => g.First()).ToList();
        var newestBeta = installable.FirstOrDefault(c => c.Prerelease)?.Release.Tag;
        var newestStable = installable.FirstOrDefault(c => !c.Prerelease)?.Release.Tag;
        var rows = visible.Select(c => new VersionRow(c,
            c.Release.Tag == (c.Prerelease ? newestBeta : newestStable),
            _kept.Contains(c.Release.Version.ToString()))).ToList();
        _versions.ItemsSource = rows; _versions.SelectedIndex = -1;
        Selection = null; SelectedChoice = null; _select.IsEnabled = false; _select.Content = "Choose a version";
        _summary.Text = ""; _compatibility.Text = "";
        _message.Text = rows.Count == 0
            ? (!IsRollback && _showAllBuilds.IsChecked != true
                ? "No compatible updates in this channel. Select Show all builds to inspect release history."
                : "No releases match this filter.")
            : $"{rows.Count} release(s), {rows.Count(c => c.Choice.Compatibility.CanInstall)} compatible. " +
                (!IsRollback && _showAllBuilds.IsChecked != true ? "Latest available per channel. " : "") +
                "Select a row to review it.";
    }

    private void ShowSelection()
    {
        if (_versions.SelectedItem is not VersionRow row) { _select.IsEnabled = false; return; }
        var choice = row.Choice;
        _select.IsEnabled = choice.Compatibility.CanInstall;
        _select.Content = IsRollback ? $"Roll back to v{choice.Release.Version}" : $"Install v{choice.Release.Version}";
        _compatibility.Text = choice.Compatibility.Reason;
        _summary.Text = string.IsNullOrWhiteSpace(choice.Release.Notes) ? "No release notes were published."
            : choice.Release.Notes.Replace("\r", "").Replace("**", "");
    }

    private sealed record VersionRow(VersionChoice Choice, bool Latest, bool OnThisPc = false)
    {
        public string Label
        {
            get
            {
                var newest = Latest ? "  (LATEST)" : "";
                var published = Choice.Release.Published is { } date
                    ? $"  ·  {date.ToLocalTime():dd MMM yyyy, h:mm tt} (local)" : "";
                var availability = (Choice.Release.AssetAvailable
                    ? "Application and checksum available" : "Application or checksum missing")
                    + (OnThisPc ? "  ·  On this PC" : "");
                var name = Choice.Release.Tag is { Length: > 0 } tag ? tag : "v" + Choice.Release.Version;
                return $"{name}{newest}{published}\n{availability}  ·  {(Choice.Compatibility.CanInstall ? "Compatible" : "Blocked")}";
            }
        }
    }
}
