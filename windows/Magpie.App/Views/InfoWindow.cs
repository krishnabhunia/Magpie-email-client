using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Magpie.App.Services;
using Magpie.Core;
using Magpie.Core.Info;
using Magpie.Core.Settings;
using Magpie.Core.Updates;
using Microsoft.Win32;

namespace Magpie.App.Views;

/// <summary>
/// "Magpie · Info" (design IN1, 5 Nov 2026): This build · What's implemented · What's new · Under the hood, with
/// Copy build info. Built in code like the version manager; every text and rule comes from Magpie.Core.Info.
/// Opened from the ⓘ next to the title-bar version, F1 in the main window, and Settings → About → Info.
/// </summary>
public sealed class InfoWindow : Window
{
    private static InfoWindow? _open;

    /// <summary>This copy's build: the app assembly's InformationalVersion and CI metadata.</summary>
    public static BuildInfo Build { get; } = BuildInfo.FromAssembly(typeof(InfoWindow).Assembly);

    private readonly BuildInfo _build;
    private readonly IReadOnlyList<FeatureRow> _features;
    private readonly TextBox _search = new() { MinWidth = 220, Margin = new Thickness(0, 0, 10, 0), VerticalContentAlignment = VerticalAlignment.Center };
    private readonly StackPanel _chips = new() { Orientation = Orientation.Horizontal };
    private readonly Grid _featureTable = new();
    private readonly TextBlock _featureCount = new() { Margin = new Thickness(0, 8, 0, 0) };
    private FeatureFilter _filter = FeatureFilter.All;

    /// <summary>Opens the Info window (or brings the open one forward), optionally on one tab (0–3).</summary>
    public static void Open(Window? owner, int tab = 0)
    {
        if (_open is { IsLoaded: true } w) { w.Tabs.SelectedIndex = tab; w.Activate(); return; }
        _open = new InfoWindow(Build, owner);
        _open.Tabs.SelectedIndex = tab;
        _open.Closed += (_, _) => _open = null;
        _open.Show();
    }

    public TabControl Tabs { get; } = new() { Margin = new Thickness(0, 14, 0, 0) };

    /// <summary>The window for a given build (the UI smoke test passes a beta).</summary>
    public InfoWindow(BuildInfo build, Window? owner = null)
    {
        _build = build;
        _features = FeatureList.Parse(InfoContent.Features);
        if (owner is { IsLoaded: true }) Owner = owner;
        Title = $"{BuildInfo.App} · Info";
        Width = 820; Height = 640; MinWidth = 620; MinHeight = 460;
        WindowStartupLocation = Owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
        SetResourceReference(BackgroundProperty, "Brush.Window.Background");
        SetResourceReference(ForegroundProperty, "Brush.Text.Primary");
        NameScope.SetNameScope(this, new NameScope());
        RegisterName("InfoTabs", Tabs);
        RegisterName("FeatureSearch", _search);
        RegisterName("FeatureTable", _featureTable);

        var root = new DockPanel { Margin = new Thickness(22, 18, 22, 18) };
        Content = root;

        var actions = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 14, 0, 0) };
        var copy = new Button { Content = "Copy build info", MinWidth = 140, ToolTip = "Copies the version, PR, commit, build time and this PC's details" };
        copy.SetResourceReference(StyleProperty, "Button.Secondary");
        var copied = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        copied.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text.Secondary");
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(CopyText()); copied.Text = "Copied"; }
            catch (Exception ex) { copied.Text = "Couldn't copy: " + ex.Message; }
        };
        RegisterName("CopyBuildInfo", copy);
        var close = new Button { Content = "Close", MinWidth = 90, IsCancel = true };
        close.SetResourceReference(StyleProperty, "Button.Primary");
        close.Click += (_, _) => Close();
        DockPanel.SetDock(copy, Dock.Left); DockPanel.SetDock(copied, Dock.Left); DockPanel.SetDock(close, Dock.Right);
        actions.Children.Add(copy); actions.Children.Add(copied); actions.Children.Add(close);
        DockPanel.SetDock(actions, Dock.Bottom); root.Children.Add(actions);

        var header = new StackPanel();
        header.Children.Add(new TextBlock { Text = Title, FontWeight = FontWeights.SemiBold, FontSize = 21 });
        var subtitle = new TextBlock { Text = build.Subtitle("Windows"), Margin = new Thickness(0, 4, 0, 0) };
        subtitle.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text.Secondary");
        RegisterName("InfoSubtitle", subtitle);
        header.Children.Add(subtitle);
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);

        Tabs.Items.Add(new TabItem { Header = "This build", Content = Scroll(ThisBuild()) });
        Tabs.Items.Add(new TabItem { Header = "What's implemented", Content = Implemented() });
        Tabs.Items.Add(new TabItem { Header = "What's new", Content = Scroll(WhatsNew()) });
        Tabs.Items.Add(new TabItem { Header = "Under the hood", Content = Scroll(UnderTheHoodTab()) });
        root.Children.Add(Tabs);

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { Tabs.SelectedIndex = 1; _search.Focus(); e.Handled = true; }
        };
    }

    // ───────────────────────── This build ─────────────────────────

    private UIElement ThisBuild()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        if (_build.Banner is { } banner)
        {
            var box = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 9, 12, 9), Margin = new Thickness(0, 0, 0, 14), BorderThickness = new Thickness(1) };
            box.SetResourceReference(Border.BackgroundProperty, "Brush.Warning.Subtle");
            box.SetResourceReference(Border.BorderBrushProperty, "Brush.Warning");
            var text = new TextBlock { Text = banner, TextWrapping = TextWrapping.Wrap };
            RegisterName("BetaBanner", text);
            box.Child = text;
            panel.Children.Add(box);
        }
        panel.Children.Add(Table(new[] { "SrNo.", "Item", "This copy" }, _build.Rows(SystemInfo.InstalledAs(InstallKind()))
            .Select((r, i) => new[] { Cell((i + 1).ToString()), Cell(r.Item), Cell(r.Value, r.Link) }).ToList(), "BuildTable"));
        return panel;
    }

    // ───────────────────────── What's implemented ─────────────────────────

    private UIElement Implemented()
    {
        var dock = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
        var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        _search.SetResourceReference(StyleProperty, "TextBox.Search");
        _search.ToolTip = "Search what Magpie can do (Ctrl+F)";
        System.Windows.Automation.AutomationProperties.SetName(_search, "Search features");
        _search.TextChanged += (_, _) => RefreshFeatures();
        bar.Children.Add(_search);
        foreach (var (filter, label) in new[]
                 {
                     (FeatureFilter.All, "All areas"), (FeatureFilter.NewInThisVersion, FeatureList.NewInLabel(_build.Core)),
                     (FeatureFilter.WindowsOnly, "Windows only"), (FeatureFilter.AlsoOnMac, "Also on Mac"),
                 })
        {
            var chip = new ToggleButton { Content = label, Margin = new Thickness(0, 0, 6, 0), IsChecked = filter == _filter, Tag = filter };
            chip.SetResourceReference(StyleProperty, "Button.Chip");
            chip.Click += (_, _) => { _filter = (FeatureFilter)chip.Tag; RefreshFeatures(); };
            _chips.Children.Add(chip);
        }
        bar.Children.Add(_chips);
        DockPanel.SetDock(bar, Dock.Top); dock.Children.Add(bar);
        _featureCount.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text.Secondary");
        DockPanel.SetDock(_featureCount, Dock.Bottom); dock.Children.Add(_featureCount);
        dock.Children.Add(Scroll(_featureTable));
        RefreshFeatures();
        return dock;
    }

    private void RefreshFeatures()
    {
        foreach (ToggleButton chip in _chips.Children) chip.IsChecked = (FeatureFilter)chip.Tag == _filter;
        var rows = FeatureList.Filter(_features, _search.Text, _filter, _build.Core);
        Fill(_featureTable, new[] { "SrNo.", "Area", "What it does", "Since", "Windows", "Mac" },
            rows.Select(r => new[] { Cell(r.SrNo.ToString()), Cell(r.Area), Cell(r.What), Cell(r.Since), Cell(r.Windows), Cell(r.Mac) }).ToList(),
            wide: 2);
        _featureCount.Text = rows.Count == _features.Count ? $"{rows.Count} features" : $"{rows.Count} of {_features.Count} features";
    }

    // ───────────────────────── What's new ─────────────────────────

    private UIElement WhatsNew()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        var section = ChangelogNotes.Section(InfoContent.Changelog, _build.Core);
        panel.Children.Add(new TextBlock { Text = ChangelogNotes.Heading(_build, section), FontWeight = FontWeights.SemiBold, FontSize = 15, Margin = new Thickness(0, 0, 0, 8) });
        if (section is null || section.IsEmpty)
            panel.Children.Add(new TextBlock { Text = "No changelog lines were built into this copy.", TextWrapping = TextWrapping.Wrap });
        else
            foreach (var group in section.Groups.Where(g => g.Lines.Count > 0))
            {
                panel.Children.Add(new TextBlock { Text = group.Kind, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 4) });
                foreach (var line in group.Lines)
                {
                    var nested = line.StartsWith("· ", StringComparison.Ordinal);
                    panel.Children.Add(new TextBlock
                    {
                        Text = (nested ? "◦ " + line[2..] : "• " + line), TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(nested ? 30 : 12, 2, 0, 2),
                    });
                }
            }
        // One ⓘ (Krishna, 9 Nov 2026): the title bar's former What's New button lives here — the installed release's
        // full implementation details, read from its GitHub release notes.
        var full = new Button { Name = "FullReleaseNotes", Content = "Full release notes", HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 14, 0, 0), Padding = new Thickness(0, 2, 0, 2),
            ToolTip = "What's New — full implementation details for the installed release" };
        full.SetResourceReference(StyleProperty, "Button.Link");
        System.Windows.Automation.AutomationProperties.SetName(full, "Full release notes");
        full.Click += async (_, _) => await Magpie.App.Services.ReleaseNotesWindow.ShowInstalledAsync(this);
        RegisterName(full.Name, full);
        panel.Children.Add(full);
        var all = LinkButton("All releases on GitHub", BuildInfo.ReleasesUrl);
        all.Margin = new Thickness(0, 6, 0, 0);
        panel.Children.Add(all);
        return panel;
    }

    // ───────────────────────── Under the hood ─────────────────────────

    private UIElement UnderTheHoodTab()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        var config = AppServices.Engine?.Config;
        var mode = config?.Appearance.Theme ?? ThemeMode.MatchWindows;
        var theme = (mode switch { ThemeMode.Light => "Light", ThemeMode.Dark => "Dark", _ => "Match Windows" })
            + $" ({(ThemeManager.IsDark ? "dark" : "light")} now) · Magpie's own styles";
        var data = AppServices.Engine?.Paths.Root ?? AppPaths.Default().Root;
        var rows = UnderTheHood.Rows(false, "WPF · CommunityToolkit.Mvvm " + Ver(typeof(CommunityToolkit.Mvvm.ComponentModel.ObservableObject)),
            data, config?.Updates.IncludePrerelease ?? false, theme, "IBM Plex Sans and IBM Plex Serif (built in)");
        panel.Children.Add(Table(new[] { "SrNo.", "Part", "This copy uses" },
            rows.Select((r, i) => new[] { Cell((i + 1).ToString()), Cell(r.Item), Cell(r.Value, r.Link) }).ToList(), "PartsTable"));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        var openData = new Button { Content = "Open data folder", Margin = new Thickness(0, 0, 8, 0) };
        openData.SetResourceReference(StyleProperty, "Button.Secondary");
        openData.Click += (_, _) => Ui.OpenExternal(data);
        var openLog = new Button { Content = "Open log file", IsEnabled = Log.FilePath is { } p && File.Exists(p) };
        openLog.SetResourceReference(StyleProperty, "Button.Secondary");
        openLog.Click += (_, _) => { if (Log.FilePath is { } path) Ui.OpenExternal(path); };
        buttons.Children.Add(openData); buttons.Children.Add(openLog);
        panel.Children.Add(buttons);
        return panel;
    }

    // ───────────────────────── Copy build info ─────────────────────────

    /// <summary>The two "Copy build info" lines; also appended to the About Me feedback email.</summary>
    public static string CopyText() => CopyText(Build);

    private static string CopyText(BuildInfo build) => build.CopyText(SystemInfo.Line(WindowsName(),
        SystemInfo.Arch(System.Runtime.InteropServices.RuntimeInformation.OSArchitecture),
        SystemInfo.InstalledAs(InstallKind()), Environment.Version));

    private static string InstallKind()
    {
        try { return UpdateService.InstallKind; } catch { return ""; }
    }

    private static string WindowsName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            return SystemInfo.WindowsName(key?.GetValue("ProductName") as string, key?.GetValue("DisplayVersion") as string, Environment.OSVersion.Version.Build);
        }
        catch { return SystemInfo.WindowsName(null, null, Environment.OSVersion.Version.Build); }
    }

    private static string Ver(Type t) =>
        AppVersion.TryParse(t.Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion)?.ToString()
        ?? t.Assembly.GetName().Version?.ToString(3) ?? "";

    // ───────────────────────── table helpers ─────────────────────────

    private sealed record CellData(string Text, string? Link = null);
    private static CellData Cell(string text, string? link = null) => new(text, link);

    private static ScrollViewer Scroll(UIElement content) => new()
    {
        Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
    };

    private Border Table(string[] header, IReadOnlyList<CellData[]> rows, string name)
    {
        var grid = new Grid();
        Fill(grid, header, rows, wide: header.Length - 1);
        RegisterName(name, grid);
        return new Border { Child = grid };
    }

    /// <summary>A header row plus one row per entry; column <paramref name="wide"/> takes the remaining width.</summary>
    private static void Fill(Grid grid, string[] header, IReadOnlyList<CellData[]> rows, int wide)
    {
        grid.Children.Clear(); grid.RowDefinitions.Clear(); grid.ColumnDefinitions.Clear();
        for (int c = 0; c < header.Length; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = c == wide ? new GridLength(1, GridUnitType.Star) : GridLength.Auto, MinWidth = c == 0 ? 44 : 0 });
        void Add(int row, int col, UIElement element) { Grid.SetRow(element, row); Grid.SetColumn(element, col); grid.Children.Add(element); }
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var head = new Border { BorderThickness = new Thickness(0, 0, 0, 1) };
        head.SetResourceReference(Border.BackgroundProperty, "Brush.Header.Background");
        head.SetResourceReference(Border.BorderBrushProperty, "Brush.Divider");
        Grid.SetColumnSpan(head, header.Length); Add(0, 0, head);
        for (int c = 0; c < header.Length; c++)
            Add(0, c, new TextBlock { Text = header[c], FontWeight = FontWeights.SemiBold, Margin = new Thickness(8, 6, 12, 6) });
        for (int r = 0; r < rows.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            if (r % 2 == 1)
            {
                var band = new Border();
                band.SetResourceReference(Border.BackgroundProperty, "Brush.Row.Alt");
                Grid.SetColumnSpan(band, header.Length); Add(r + 1, 0, band);
            }
            for (int c = 0; c < header.Length; c++)
            {
                var data = rows[r][c];
                var text = new TextBlock { TextWrapping = c == wide ? TextWrapping.Wrap : TextWrapping.NoWrap, Margin = new Thickness(8, 5, 12, 5) };
                if (data.Link is { } link)
                {
                    var hyperlink = new Hyperlink(new Run(data.Text)) { ToolTip = link };
                    hyperlink.SetResourceReference(TextElement.ForegroundProperty, "Brush.Accent.Text");
                    hyperlink.Click += (_, _) => Ui.OpenExternal(link);
                    text.Inlines.Add(hyperlink);
                }
                else text.Text = data.Text;
                if (c == 0) text.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text.Secondary");
                Add(r + 1, c, text);
            }
        }
    }

    private static Button LinkButton(string text, string url)
    {
        var b = new Button { Content = text, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 14, 0, 0), Padding = new Thickness(0, 2, 0, 2) };
        b.SetResourceReference(StyleProperty, "Button.Link");
        b.Click += (_, _) => Ui.OpenExternal(url);
        return b;
    }
}
