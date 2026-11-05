using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Magpie.Core;
using Magpie.Core.Info;
using Magpie.Core.Updates;
using Magpie.Mac.Services;

namespace Magpie.Mac.Views;

/// <summary>
/// "Magpie · Info" for the Mac (design IN1, 5 Nov 2026): the same four tabs as Windows — This build · What's
/// implemented · What's new · Under the hood — and Copy build info. Built in code; texts and rules come from
/// Magpie.Core.Info. Opened from the ⓘ next to the version, F1 / ⌘I, Magpie → Magpie Info…, and Settings → About.
/// </summary>
public sealed class InfoWindow : Window
{
    private static InfoWindow? _open;

    /// <summary>This copy's build: the app assembly's InformationalVersion and CI metadata.</summary>
    public static BuildInfo Build { get; } = BuildInfo.FromAssembly(typeof(InfoWindow).Assembly);

    private readonly BuildInfo _build;
    private readonly IReadOnlyList<FeatureRow> _features;
    private readonly TextBox _search = new() { Name = "FeatureSearch", Width = 240, PlaceholderText = "Search what Magpie can do", Margin = new Thickness(0, 0, 10, 6) };
    private readonly StackPanel _chips = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private readonly Grid _featureTable = new() { Name = "FeatureTable" };
    private readonly TextBlock _featureCount = new() { Margin = new Thickness(0, 8, 0, 0) };
    private FeatureFilter _filter = FeatureFilter.All;

    public TabControl Tabs { get; } = new() { Name = "InfoTabs", Margin = new Thickness(0, 12, 0, 0) };
    public TextBlock SubtitleText { get; } = new() { Name = "InfoSubtitle", Margin = new Thickness(0, 4, 0, 0) };
    public TextBlock? Banner { get; private set; }
    public Grid BuildTable { get; private set; } = new();
    public Grid FeatureTable => _featureTable;
    public TextBox FeatureSearch => _search;

    /// <summary>Opens the Info window (or brings the open one forward).</summary>
    public static void Open(int tab = 0)
    {
        if (_open == null)
        {
            _open = new InfoWindow(Build);
            _open.Closed += (_, _) => _open = null;
            if (AppServices.MainWindow is { IsVisible: true } main) _open.Show(main);
            else _open.Show();
        }
        _open.Tabs.SelectedIndex = tab;
        _open.Activate();
    }

    public InfoWindow(BuildInfo build)
    {
        _build = build;
        _features = FeatureList.Parse(InfoContent.Features);
        Title = $"{BuildInfo.App} · Info";
        Width = 820; Height = 640; MinWidth = 620; MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Bind(BackgroundProperty, this.GetResourceObservable("Magpie.Pane"));
        MacMenus.Attach(this);

        var root = new DockPanel { Margin = new Thickness(22, 18, 22, 18) };
        Content = root;

        var actions = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 14, 0, 0) };
        var copy = new Button { Name = "CopyBuildInfo", Content = "Copy build info", Classes = { "tool" } };
        var copied = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), Classes = { "muted" } };
        copy.Click += async (_, _) =>
        {
            try
            {
                if (Clipboard is { } clipboard) { await clipboard.SetTextAsync(CopyText()); copied.Text = "Copied"; }
            }
            catch (Exception ex) { copied.Text = "Couldn't copy: " + ex.Message; }
        };
        var close = new Button { Content = "Close", IsCancel = true, Classes = { "tool" } };
        close.Click += (_, _) => Close();
        DockPanel.SetDock(copy, Dock.Left); DockPanel.SetDock(copied, Dock.Left); DockPanel.SetDock(close, Dock.Right);
        actions.Children.Add(copy); actions.Children.Add(copied); actions.Children.Add(close);
        DockPanel.SetDock(actions, Dock.Bottom); root.Children.Add(actions);

        var header = new StackPanel();
        header.Children.Add(new TextBlock { Text = Title, Classes = { "heading" } });
        SubtitleText.Text = build.Subtitle("Mac");
        SubtitleText.Classes.Add("muted");
        header.Children.Add(SubtitleText);
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);

        Tabs.Items.Add(new TabItem { Header = "This build", Content = Scroll(ThisBuild()) });
        Tabs.Items.Add(new TabItem { Header = "What's implemented", Content = Implemented() });
        Tabs.Items.Add(new TabItem { Header = "What's new", Content = Scroll(WhatsNew()) });
        Tabs.Items.Add(new TabItem { Header = "Under the hood", Content = Scroll(UnderTheHoodTab()) });
        root.Children.Add(Tabs);

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Meta) { Tabs.SelectedIndex = 1; _search.Focus(); e.Handled = true; }
            else if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        };
    }

    private Control ThisBuild()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 12, 0, 0), Spacing = 12 };
        if (_build.Banner is { } banner)
        {
            Banner = new TextBlock { Name = "BetaBanner", Text = banner, TextWrapping = TextWrapping.Wrap };
            var box = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 9), BorderThickness = new Thickness(1), Child = Banner };
            box.Bind(Border.BackgroundProperty, box.GetResourceObservable("Magpie.AccentSoft"));
            box.Bind(Border.BorderBrushProperty, box.GetResourceObservable("Magpie.Accent"));
            panel.Children.Add(box);
        }
        BuildTable = new Grid { Name = "BuildTable" };
        Fill(BuildTable, ["SrNo.", "Item", "This copy"], _build.Rows(SystemInfo.InstalledAs(UpdateInstallKinds.MacApp))
            .Select((r, i) => new[] { new CellData((i + 1).ToString()), new CellData(r.Item), new CellData(r.Value, r.Link) }).ToList(), wide: 2);
        panel.Children.Add(BuildTable);
        return panel;
    }

    private Control Implemented()
    {
        var dock = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        _search.TextChanged += (_, _) => RefreshFeatures();
        bar.Children.Add(_search);
        foreach (var (filter, label) in new[]
                 {
                     (FeatureFilter.All, "All areas"), (FeatureFilter.NewInThisVersion, FeatureList.NewInLabel(_build.Core)),
                     (FeatureFilter.WindowsOnly, "Windows only"), (FeatureFilter.AlsoOnMac, "Also on Mac"),
                 })
        {
            var chip = new ToggleButton { Content = label, Tag = filter, IsChecked = filter == _filter, Padding = new Thickness(10, 3), CornerRadius = new CornerRadius(12) };
            chip.Click += (_, _) => { _filter = (FeatureFilter)chip.Tag!; RefreshFeatures(); };
            _chips.Children.Add(chip);
        }
        bar.Children.Add(_chips);
        DockPanel.SetDock(bar, Dock.Top); dock.Children.Add(bar);
        _featureCount.Classes.Add("muted");
        DockPanel.SetDock(_featureCount, Dock.Bottom); dock.Children.Add(_featureCount);
        dock.Children.Add(Scroll(_featureTable));
        RefreshFeatures();
        return dock;
    }

    private void RefreshFeatures()
    {
        foreach (var chip in _chips.Children.OfType<ToggleButton>()) chip.IsChecked = (FeatureFilter)chip.Tag! == _filter;
        var rows = FeatureList.Filter(_features, _search.Text, _filter, _build.Core);
        Fill(_featureTable, ["SrNo.", "Area", "What it does", "Since", "Windows", "Mac"],
            rows.Select(r => new[] { new CellData(r.SrNo.ToString()), new CellData(r.Area), new CellData(r.What), new CellData(r.Since), new CellData(r.Windows), new CellData(r.Mac) }).ToList(),
            wide: 2);
        _featureCount.Text = rows.Count == _features.Count ? $"{rows.Count} features" : $"{rows.Count} of {_features.Count} features";
    }

    private Control WhatsNew()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 12, 0, 0), Spacing = 3 };
        var section = ChangelogNotes.Section(InfoContent.Changelog, _build.Core);
        panel.Children.Add(new TextBlock { Text = ChangelogNotes.Heading(_build, section), FontWeight = FontWeight.SemiBold, FontSize = 15, Margin = new Thickness(0, 0, 0, 6) });
        if (section is null || section.IsEmpty)
            panel.Children.Add(new TextBlock { Text = "No changelog lines were built into this copy.", TextWrapping = TextWrapping.Wrap });
        else
            foreach (var group in section.Groups.Where(g => g.Lines.Count > 0))
            {
                panel.Children.Add(new TextBlock { Text = group.Kind, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 2) });
                foreach (var line in group.Lines)
                {
                    var nested = line.StartsWith("· ", StringComparison.Ordinal);
                    panel.Children.Add(new TextBlock { Text = nested ? "◦ " + line[2..] : "• " + line, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(nested ? 30 : 12, 1, 0, 1) });
                }
            }
        panel.Children.Add(Link("All releases on GitHub", BuildInfo.ReleasesUrl));
        return panel;
    }

    private Control UnderTheHoodTab()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 12, 0, 0), Spacing = 12 };
        var data = AppServices.Engine?.Paths.Root ?? "";
        var include = AppServices.Engine?.Config.Updates.IncludePrerelease ?? false;
        var rows = UnderTheHood.Rows(true, "Avalonia " + Ver(typeof(Avalonia.Application)) + " · CommunityToolkit.Mvvm " + Ver(typeof(CommunityToolkit.Mvvm.ComponentModel.ObservableObject)),
            data, include, "Follows the Mac's light or dark look (" + (App.IsDark ? "dark" : "light") + " now) · Fluent theme with Magpie's colours", null);
        var table = new Grid { Name = "PartsTable" };
        Fill(table, ["SrNo.", "Part", "This copy uses"], rows.Select((r, i) => new[] { new CellData((i + 1).ToString()), new CellData(r.Item), new CellData(r.Value, r.Link) }).ToList(), wide: 2);
        panel.Children.Add(table);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var openData = new Button { Content = "Open data folder", Classes = { "tool" }, IsEnabled = data.Length > 0 };
        openData.Click += (_, _) => Shell.OpenLocal(data);
        var openLog = new Button { Content = "Open log file", Classes = { "tool" }, IsEnabled = Log.FilePath is { } p && File.Exists(p) };
        openLog.Click += (_, _) => { if (Log.FilePath is { } path) Shell.Reveal(path); };
        buttons.Children.Add(openData); buttons.Children.Add(openLog);
        panel.Children.Add(buttons);
        return panel;
    }

    /// <summary>The two "Copy build info" lines: e.g. "macOS 15.1 arm64 · app (Magpie.app) · .NET 10".</summary>
    public static string CopyText() => Build.CopyText(SystemInfo.Line("macOS " + Environment.OSVersion.Version.ToString(2),
        SystemInfo.Arch(System.Runtime.InteropServices.RuntimeInformation.OSArchitecture),
        SystemInfo.InstalledAs(UpdateInstallKinds.MacApp), Environment.Version));

    private static string Ver(Type t) =>
        AppVersion.TryParse(t.Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion)?.ToString()
        ?? t.Assembly.GetName().Version?.ToString(3) ?? "";

    private sealed record CellData(string Text, string? Link = null);

    private static ScrollViewer Scroll(Control content) => new()
    {
        Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
    };

    private static Button Link(string text, string url)
    {
        var b = new Button { Content = text, Classes = { "link" }, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
        b.Click += (_, _) => Shell.OpenWeb(url);
        return b;
    }

    /// <summary>A header row plus one row per entry; column <paramref name="wide"/> takes the remaining width.</summary>
    private static void Fill(Grid grid, string[] header, IReadOnlyList<CellData[]> rows, int wide)
    {
        grid.Children.Clear(); grid.RowDefinitions.Clear(); grid.ColumnDefinitions.Clear();
        for (int c = 0; c < header.Length; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = c == wide ? new GridLength(1, GridUnitType.Star) : GridLength.Auto, MinWidth = c == 0 ? 44 : 0 });
        void Add(int row, int col, Control element) { Grid.SetRow(element, row); Grid.SetColumn(element, col); grid.Children.Add(element); }
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        var head = new Border { BorderThickness = new Thickness(0, 0, 0, 1) };
        head.Bind(Border.BorderBrushProperty, head.GetResourceObservable("Magpie.Line"));
        head.Bind(Border.BackgroundProperty, head.GetResourceObservable("Magpie.Sidebar"));
        Grid.SetColumnSpan(head, header.Length); Add(0, 0, head);
        for (int c = 0; c < header.Length; c++)
            Add(0, c, new TextBlock { Text = header[c], FontWeight = FontWeight.SemiBold, Margin = new Thickness(8, 6, 12, 6) });
        for (int r = 0; r < rows.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var line = new Border { BorderThickness = new Thickness(0, 0, 0, 1) };
            line.Bind(Border.BorderBrushProperty, line.GetResourceObservable("Magpie.Line"));
            Grid.SetColumnSpan(line, header.Length); Add(r + 1, 0, line);
            for (int c = 0; c < header.Length; c++)
            {
                var data = rows[r][c];
                Control cell;
                if (data.Link is { } link)
                {
                    var b = new Button { Content = data.Text, Classes = { "link" }, Padding = new Thickness(4, 2), Margin = new Thickness(4, 2, 12, 2) };
                    ToolTip.SetTip(b, link);
                    b.Click += (_, _) => Shell.OpenWeb(link);
                    cell = b;
                }
                else
                {
                    var t = new TextBlock { Text = data.Text, TextWrapping = c == wide ? TextWrapping.Wrap : TextWrapping.NoWrap, Margin = new Thickness(8, 5, 12, 5) };
                    if (c == 0) t.Classes.Add("muted");
                    cell = t;
                }
                Add(r + 1, c, cell);
            }
        }
    }
}
