using System.Windows;
using System.Windows.Controls;
using Magpie.App.Services;
using Magpie.Core.Models;
using Magpie.Core.Storage;

namespace Magpie.App.Views;

/// <summary>Create/edit a persisted local inbox split or saved search. No email moves and no server calls.</summary>
public sealed class InboxViewDialog : Window
{
    private sealed record Scope(string? Id, string Label);
    private readonly TextBox _name = new();
    private readonly TextBox _query = new();
    private readonly ComboBox _account = new() { DisplayMemberPath = "Label" };
    private readonly CheckBox _inbox = new() { Content = "Inbox folders only", IsChecked = true };
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap };
    public Action<string, string, string?, bool>? Save { get; init; }

    public InboxViewDialog(Window owner, string query = "", InboxView? view = null)
    {
        Owner = owner; Title = view == null ? "Save a mail view" : "Edit mail view";
        Width = 560; SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "Brush.Window.Background");
        SetResourceReference(ForegroundProperty, "Brush.Text.Primary");
        var panel = new StackPanel { Margin = new Thickness(22) };
        void Field(string label, Control control)
        {
            panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 8, 0, 4) });
            if (control is TextBox) control.SetResourceReference(StyleProperty, "TextBox.Base");
            panel.Children.Add(control);
        }
        _name.MaxLength = 80; _name.Text = view?.Name ?? "";
        _query.MaxLength = 2048; _query.Text = view?.Query ?? query;
        Field("View name", _name);
        Field("Search query", _query);
        panel.Children.Add(new TextBlock { Text = "Examples: domain:example.com · from:anita · to:team · subject:invoice · label:Work · has:attachment",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6) });
        var scopes = new[] { new Scope(null, "All accounts") }.Concat(AppServices.Engine.Accounts.Select(a => new Scope(a.Id, a.Email))).ToList();
        if (view?.AccountId is { } missing && scopes.All(a => a.Id != missing))
            scopes.Add(new Scope(missing, "Removed account (empty until restored)"));
        _account.ItemsSource = scopes;
        _account.SelectedItem = scopes.First(a => a.Id == view?.AccountId);
        Field("Account", _account);
        _inbox.Margin = new Thickness(0, 12, 0, 8); _inbox.IsChecked = view?.InboxOnly ?? true;
        panel.Children.Add(_inbox);
        panel.Children.Add(new TextBlock { Text = "Saved on this PC. Uncheck Inbox folders only for a saved search across mail folders. Body search covers email already downloaded.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
        _error.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Danger"); panel.Children.Add(_error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
        cancel.SetResourceReference(StyleProperty, "Button.Secondary");
        var save = new Button { Content = "Save view", IsDefault = true };
        save.SetResourceReference(StyleProperty, "Button.Primary");
        save.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_name.Text) || SearchQuery.Parse(_query.Text).IsEmpty) { _error.Text = "Enter a view name and a search query."; return; }
            try { Save?.Invoke(_name.Text, _query.Text, (_account.SelectedItem as Scope)?.Id, _inbox.IsChecked == true); DialogResult = true; }
            catch (Exception ex) { _error.Text = ex.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) ? "A view already has this name. Choose another." : ex.Message; }
        };
        buttons.Children.Add(cancel); buttons.Children.Add(save); panel.Children.Add(buttons);
        Content = panel;
        Loaded += (_, _) => _name.Focus();
    }
}
