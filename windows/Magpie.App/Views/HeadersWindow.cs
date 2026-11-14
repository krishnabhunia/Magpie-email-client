using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Magpie.App.Services;
using Magpie.Core;
using Magpie.Core.Mail;
using Magpie.Core.Models;
using Microsoft.Win32;

namespace Magpie.App.Views;

/// <summary>
/// "Email headers" (design HD1, option A): every header line of one email as a table or as plain text, a find box,
/// the mail server's SPF / DKIM / DMARC results and how the email travelled, Copy all headers and Save as a text file.
/// Built in code; every word and rule comes from <see cref="HeaderView"/>. Opened by the Headers button of the
/// Details card in the reading pane.
/// </summary>
public sealed class HeadersWindow : Window
{
    private readonly MessageRow _row;
    private readonly Func<CancellationToken, Task<List<HeaderLine>?>>? _load;
    private readonly CancellationTokenSource _closing = new();
    private List<HeaderLine> _lines = new();
    private bool _loaded;

    private readonly WrapPanel _chips = new() { Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed };
    private readonly TextBox _find = new() { MinWidth = 220, VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "Shows only the header lines that hold this text" };
    private readonly RadioButton _asTable = new() { Content = "Table", IsChecked = true, GroupName = "HeaderLook", ToolTip = "One row per header line" };
    private readonly RadioButton _asText = new() { Content = "Plain text", GroupName = "HeaderLook", ToolTip = "The header lines as text, as they are copied and saved" };
    private readonly Button _copy = new() { Content = "Copy all headers", MinWidth = 130, IsEnabled = false };
    private readonly Button _save = new() { Content = "Save as a text file…", MinWidth = 150, IsEnabled = false };
    private readonly Button _retry = new() { Content = "Try again", MinWidth = 100, Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Collapsed };
    private readonly StackPanel _rows = new();
    private readonly ScrollViewer _table;
    private readonly TextBox _plain = new()
    {
        IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, BorderThickness = new Thickness(0),
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = Mono, FontSize = 12.5, Padding = new Thickness(10, 8, 10, 8),
        Visibility = Visibility.Collapsed,
    };
    private readonly StackPanel _status = new() { Margin = new Thickness(2, 18, 0, 0) };
    private readonly TextBlock _statusText = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _count = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _said = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };

    private static FontFamily Mono { get; } = new("Cascadia Mono, Consolas, Courier New");

    /// <summary>Opens the window for one email and reads its header lines (this PC's copy, else the mail server).</summary>
    public static HeadersWindow Open(Window? owner, MailEngine engine, MessageRow row)
    {
        var window = new HeadersWindow(row, ct => engine.HeadersAsync(row, ct), owner);
        window.Show();
        return window;
    }

    /// <summary>The window with its lines already known (the UI smoke test).</summary>
    public HeadersWindow(MessageRow row, IReadOnlyList<HeaderLine> lines, Window? owner = null) : this(row, (Func<CancellationToken, Task<List<HeaderLine>?>>?)null, owner) =>
        ShowLines(lines.ToList());

    private HeadersWindow(MessageRow row, Func<CancellationToken, Task<List<HeaderLine>?>>? load, Window? owner)
    {
        _row = row;
        _load = load;
        if (owner is { IsLoaded: true }) Owner = owner;
        Title = HeaderView.Title;
        Width = 900; Height = 680; MinWidth = 560; MinHeight = 380;
        WindowStartupLocation = Owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
        SetResourceReference(BackgroundProperty, "Brush.Window.Background");
        SetResourceReference(ForegroundProperty, "Brush.Text.Primary");
        NameScope.SetNameScope(this, new NameScope());

        var root = new DockPanel { Margin = new Thickness(22, 18, 22, 18) };
        Content = root;

        // ── bottom: how many lines, what was just done, Close ──
        var actions = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 14, 0, 0) };
        _count.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text.Secondary");
        _said.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Accent.Text");
        var close = new Button { Content = "Close", MinWidth = 90, IsCancel = true };
        close.SetResourceReference(StyleProperty, "Button.Primary");
        close.Click += (_, _) => Close();
        DockPanel.SetDock(_count, Dock.Left); DockPanel.SetDock(_said, Dock.Left); DockPanel.SetDock(close, Dock.Right);
        actions.Children.Add(_count); actions.Children.Add(_said); actions.Children.Add(close);
        DockPanel.SetDock(actions, Dock.Bottom);
        root.Children.Add(actions);

        // ── top: which email, the server's checks, the tools ──
        var top = new StackPanel();
        var subject = new TextBlock { Text = HeaderView.Subject(row), FontSize = 16, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        var sub = new TextBlock { Text = HeaderView.SubTitle(row), Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap };
        sub.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text.Secondary");
        top.Children.Add(subject); top.Children.Add(sub); top.Children.Add(_chips);

        var tools = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 12, 0, 10) };
        _save.SetResourceReference(StyleProperty, "Button.Secondary");
        _copy.SetResourceReference(StyleProperty, "Button.Secondary");
        _save.Margin = new Thickness(8, 0, 0, 0); _copy.Margin = new Thickness(8, 0, 0, 0);
        _save.ToolTip = "Saves every header line of this email as a text file";
        _copy.ToolTip = "Copies every header line of this email, one per line";
        var look = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 0) };
        _asTable.SetResourceReference(StyleProperty, "Button.Tab");
        _asText.SetResourceReference(StyleProperty, "Button.Tab");
        look.Children.Add(_asTable); look.Children.Add(_asText);
        DockPanel.SetDock(_save, Dock.Right); DockPanel.SetDock(_copy, Dock.Right); DockPanel.SetDock(look, Dock.Right);
        tools.Children.Add(_save); tools.Children.Add(_copy); tools.Children.Add(look);
        _find.SetResourceReference(StyleProperty, "TextBox.Search");
        System.Windows.Automation.AutomationProperties.SetName(_find, "Find in headers");
        tools.Children.Add(_find);
        top.Children.Add(tools);
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);

        // ── middle: the lines (table or plain text), or what is going on ──
        _table = new ScrollViewer { Content = _rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Visibility = Visibility.Collapsed };
        _plain.SetResourceReference(BackgroundProperty, "Brush.Row.Alt");
        _plain.SetResourceReference(ForegroundProperty, "Brush.Text.Primary");
        _retry.SetResourceReference(StyleProperty, "Button.Secondary");
        _status.Children.Add(_statusText); _status.Children.Add(_retry);
        var frame = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), ClipToBounds = true };
        frame.SetResourceReference(Border.BorderBrushProperty, "Brush.Divider");
        var body = new Grid();
        body.Children.Add(_table); body.Children.Add(_plain); body.Children.Add(_status);
        frame.Child = body;
        root.Children.Add(frame);

        RegisterName("HeaderFind", _find);
        RegisterName("HeaderRows", _rows);
        RegisterName("HeaderPlain", _plain);
        RegisterName("HeaderChips", _chips);
        RegisterName("HeaderCount", _count);
        RegisterName("HeaderStatus", _statusText);
        RegisterName("HeaderAsTable", _asTable);
        RegisterName("HeaderAsText", _asText);
        RegisterName("HeaderCopy", _copy);
        RegisterName("HeaderSave", _save);

        _find.TextChanged += (_, _) => Render();
        _asTable.Checked += (_, _) => Render();
        _asText.Checked += (_, _) => Render();
        _copy.Click += (_, _) => CopyAll();
        _save.Click += (_, _) => SaveAll();
        _retry.Click += (_, _) => _ = LoadAsync();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { _find.Focus(); _find.SelectAll(); e.Handled = true; }
        };
        Closed += (_, _) => { try { _closing.Cancel(); } catch { } };
        if (_load != null) Loaded += (_, _) => _ = LoadAsync();
    }

    /// <summary>The header lines the window holds (empty until they arrived).</summary>
    public IReadOnlyList<HeaderLine> Lines => _lines;

    /// <summary>Exactly what Copy all headers and Save write: "Name: value" per line, Windows line ends.</summary>
    public string AllText => AddressCopy.ForClipboard(HeaderView.PlainText(_lines));

    private async Task LoadAsync()
    {
        if (_load == null) return;
        Say(HeaderView.Loading, retry: false);
        try
        {
            var lines = await _load(_closing.Token);
            if (_closing.IsCancellationRequested) return;
            if (lines == null) Say(HeaderView.Gone, retry: false);
            else ShowLines(lines);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warn("headers: " + ex.Message);
            if (!_closing.IsCancellationRequested) Say(HeaderView.Failed, retry: true);
        }
    }

    private void Say(string text, bool retry)
    {
        _loaded = false;
        _statusText.Text = text;
        _retry.Visibility = retry ? Visibility.Visible : Visibility.Collapsed;
        _status.Visibility = Visibility.Visible;
        _table.Visibility = Visibility.Collapsed;
        _plain.Visibility = Visibility.Collapsed;
        _copy.IsEnabled = _save.IsEnabled = false;
        _count.Text = "";
    }

    private void ShowLines(List<HeaderLine> lines)
    {
        _lines = lines;
        _loaded = true;
        _copy.IsEnabled = _save.IsEnabled = lines.Count > 0;
        BuildChips();
        Render();
    }

    private void BuildChips()
    {
        _chips.Children.Clear();
        var checks = HeaderView.Checks(_lines);
        var hops = HeaderView.HopsLine(_lines);
        if (checks.Count > 0)
        {
            var label = new TextBlock { Text = HeaderView.ChecksLabel, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 6) };
            label.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text.Secondary");
            _chips.Children.Add(label);
            foreach (var check in checks) _chips.Children.Add(Chip(check.Label, check.Passed ? "Brush.Success" : check.Result == "fail" ? "Brush.Danger" : null));
        }
        if (hops.Length > 0) _chips.Children.Add(Chip(hops, null));
        _chips.Visibility = _chips.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>A rounded label; the words carry the meaning ("passed" / "failed"), the colour only repeats it.</summary>
    private static Border Chip(string text, string? brush)
    {
        var words = new TextBlock { Text = text, FontSize = 12.5, FontWeight = FontWeights.Medium };
        if (brush != null) words.SetResourceReference(TextBlock.ForegroundProperty, brush);
        var chip = new Border { Child = words, CornerRadius = new CornerRadius(11), BorderThickness = new Thickness(1), Padding = new Thickness(10, 2, 10, 3), Margin = new Thickness(0, 0, 8, 6) };
        chip.SetResourceReference(Border.BorderBrushProperty, "Brush.Divider");
        return chip;
    }

    private void Render()
    {
        if (!_loaded) return;   // still getting them, or it failed: the status line stays
        var shown = HeaderView.Filter(_lines, _find.Text);
        _count.Text = HeaderView.CountLine(shown.Count, _lines.Count);
        var text = _asText.IsChecked == true;
        if (shown.Count == 0)
        {
            _statusText.Text = _lines.Count == 0 ? "This email has no header lines." : $"No header line holds \"{_find.Text.Trim()}\".";
            _retry.Visibility = Visibility.Collapsed;
            _status.Visibility = Visibility.Visible;
            _table.Visibility = _plain.Visibility = Visibility.Collapsed;
            _rows.Children.Clear();
            _plain.Text = "";
            return;
        }
        _status.Visibility = Visibility.Collapsed;
        _table.Visibility = text ? Visibility.Collapsed : Visibility.Visible;
        _plain.Visibility = text ? Visibility.Visible : Visibility.Collapsed;
        if (text) { _plain.Text = AddressCopy.ForClipboard(HeaderView.PlainText(shown)); return; }
        _rows.Children.Clear();
        for (int i = 0; i < shown.Count; i++) _rows.Children.Add(Row(shown[i], i));
    }

    /// <summary>One line of the table: the name, and the value as text that can be selected and copied.</summary>
    private static Border Row(HeaderLine line, int index)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var name = new TextBlock { Text = line.Name, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 12, 0) };
        var value = new TextBox
        {
            Text = line.Value, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Background = Brushes.Transparent,
            FontFamily = Mono, FontSize = 12.5, Padding = new Thickness(0), IsTabStop = false,
        };
        value.SetResourceReference(ForegroundProperty, "Brush.Text.Primary");
        System.Windows.Automation.AutomationProperties.SetName(value, line.Name);
        Grid.SetColumn(value, 1);
        grid.Children.Add(name); grid.Children.Add(value);
        var band = new Border { Child = grid, Padding = new Thickness(12, 6, 12, 6) };
        if (index % 2 == 0) band.SetResourceReference(Border.BackgroundProperty, "Brush.Row.Alt");
        return band;
    }

    private void CopyAll()
    {
        if (_lines.Count == 0) return;
        try { Clipboard.SetText(AllText); _said.Text = HeaderView.Copied; }
        catch (Exception ex) { Log.Warn("copy headers: " + ex.Message); _said.Text = AddressCopy.Failed; }
    }

    private void SaveAll()
    {
        if (_lines.Count == 0) return;
        try
        {
            var dialog = new SaveFileDialog { FileName = HeaderView.FileName(_row), Filter = "Text file (*.txt)|*.txt", DefaultExt = ".txt", AddExtension = true, OverwritePrompt = true };
            if (dialog.ShowDialog(this) != true) return;
            File.WriteAllText(dialog.FileName, AllText + "\r\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            _said.Text = "Saved as " + Path.GetFileName(dialog.FileName);
        }
        catch (Exception ex) { Log.Warn("save headers: " + ex.Message); _said.Text = "Could not save the file. Try another folder."; }
    }
}
