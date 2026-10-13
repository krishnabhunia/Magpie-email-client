using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Magpie.Core.Models;

namespace Magpie.App.Views;

public sealed record WorkspaceCommand(string Title, string Details, string Shortcut, string Aliases,
    Action Run, Func<bool>? Available = null, string DisabledReason = "")
{
    public bool Enabled => Available?.Invoke() ?? true;
}

/// <summary>A modal chooser: typing never reaches mail shortcuts; execution happens after closing.</summary>
public sealed class CommandPaletteWindow : Window
{
    private readonly TextBox _query = new() { Margin = new Thickness(0, 0, 0, 12) };
    private readonly ListBox _list = new();
    private readonly TextBlock _hint = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 8) };
    private readonly Button _run = new() { Content = "Run command", MinWidth = 120, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly IReadOnlyList<WorkspaceCommand> _commands;
    public WorkspaceCommand? Picked { get; private set; }

    public CommandPaletteWindow(Window owner, IReadOnlyList<WorkspaceCommand> commands)
    {
        Owner = owner;
        Title = "Magpie commands";
        Width = 600; Height = 560; MinWidth = 420; MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _commands = commands;
        SetResourceReference(BackgroundProperty, "Brush.Window.Background");
        SetResourceReference(ForegroundProperty, "Brush.Text.Primary");
        var panel = new DockPanel { Margin = new Thickness(20) };
        var heading = new TextBlock { Text = "Commands", FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(heading, Dock.Top); panel.Children.Add(heading);
        _query.SetResourceReference(StyleProperty, "TextBox.Search");
        _query.ToolTip = "Type an action, folder, saved view or shortcut";
        System.Windows.Automation.AutomationProperties.SetName(_query, "Find a command");
        DockPanel.SetDock(_query, Dock.Top); panel.Children.Add(_query);
        var footer = new StackPanel();
        footer.Children.Add(_hint);
        _run.SetResourceReference(StyleProperty, "Button.Primary");
        footer.Children.Add(_run);
        DockPanel.SetDock(footer, Dock.Bottom); panel.Children.Add(footer);
        _list.SetResourceReference(StyleProperty, "ListBox.Flat");
        panel.Children.Add(_list);
        Content = panel;
        _query.TextChanged += (_, _) => Refresh();
        _list.SelectionChanged += (_, _) => UpdateHint();
        _list.MouseDoubleClick += (_, e) => { if (ItemsControl.ContainerFromElement(_list, e.OriginalSource as DependencyObject) != null) Pick(); };
        _run.Click += (_, _) => Pick();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Close(); e.Handled = true; }
            else if (e.Key == Key.Enter) { Pick(); e.Handled = true; }
            else if (e.Key is Key.Down or Key.Up && Keyboard.Modifiers == ModifierKeys.None)
            {
                if (_list.Items.Count > 0)
                {
                    _list.SelectedIndex = Math.Clamp(_list.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, _list.Items.Count - 1);
                    _list.ScrollIntoView(_list.SelectedItem);
                }
                e.Handled = true;
            }
        };
        Loaded += (_, _) => _query.Focus();
        Refresh();
    }

    private void Refresh()
    {
        _list.Items.Clear();
        foreach (var command in _commands.Where(c => WorkspaceCommandSearch.Matches(_query.Text, c.Title, c.Details, c.Aliases)))
        {
            var content = new StackPanel { Margin = new Thickness(4, 6, 4, 6) };
            content.Children.Add(new TextBlock { Text = command.Title + (command.Shortcut.Length == 0 ? "" : "    " + command.Shortcut),
                FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            var detail = new TextBlock { Text = command.Enabled ? command.Details : command.DisabledReason,
                FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
            detail.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text.Secondary");
            content.Children.Add(detail);
            _list.Items.Add(new ListBoxItem { Content = content, Tag = command, Opacity = command.Enabled ? 1 : 0.55 });
        }
        if (_list.Items.Count > 0) _list.SelectedIndex = 0;
        UpdateHint();
    }

    private void UpdateHint()
    {
        var command = (_list.SelectedItem as ListBoxItem)?.Tag as WorkspaceCommand;
        _run.IsEnabled = command?.Enabled == true;
        _hint.Text = command == null ? "No matching command. Try inbox, search, remind or draft."
            : command.Enabled ? command.Details + "  ·  ↑↓ choose · Enter run · Esc close" : command.DisabledReason;
    }

    private void Pick()
    {
        if ((_list.SelectedItem as ListBoxItem)?.Tag is not WorkspaceCommand { Enabled: true } command) { UpdateHint(); return; }
        Picked = command;
        DialogResult = true;
    }
}
