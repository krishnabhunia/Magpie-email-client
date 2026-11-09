using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Magpie.App.Services;
using Magpie.Core.Contacts;
using Magpie.Core.Models;

namespace Magpie.App.Views;

/// <summary>Local-first address-book picker, including frequently contacted people and saved Google contact groups.</summary>
public static class ComposeContactPicker
{
    private sealed record Choice(ContactEntry Entry)
    {
        public string Label => Entry.Name == Entry.Email ? Entry.Email : $"{Entry.Name}  ·  {Entry.Email}";
    }

    public static IReadOnlyList<ContactEntry> Pick(Window owner)
    {
        var engine = AppServices.Engine;
        var entries = ContactSuggest.Entries(engine.Contacts.All(), engine.Store.MailPeople());
        var groups = ContactSuggest.Groups(entries).ToDictionary(g => g.Group, g => g.Members, StringComparer.OrdinalIgnoreCase);
        var selected = new List<ContactEntry>();
        Style S(string name) => (Style)Application.Current.FindResource(name);

        var window = new Window
        {
            Title = "Choose recipients — Magpie", Width = 700, Height = 570,
            MinWidth = 560, MinHeight = 400, Owner = owner,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Style = S("Window.Dialog"),
        };
        var root = new Grid { Margin = new Thickness(20, 16, 20, 16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        heading.Children.Add(new TextBlock { Text = "Choose recipients", Style = S("Text.Serif"), FontSize = 19 });
        heading.Children.Add(new TextBlock { Text = "Select one or more people (Ctrl-click), or add a complete contact group.", Style = S("Text.Caption"), Margin = new Thickness(0, 5, 0, 0) });
        Grid.SetRow(heading, 0);
        root.Children.Add(heading);

        var options = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        options.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        options.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
        var search = new TextBox { Style = S("TextBox.Field"), Margin = new Thickness(0, 0, 9, 0), Tag = "Search people by name or address" };
        AutomationProperties.SetName(search, "Search contacts");
        options.Children.Add(search);
        var filter = new ComboBox { Margin = new Thickness(0, 0, 0, 0) };
        filter.Items.Add("All contacts");
        filter.Items.Add("Frequently contacted");
        foreach (var groupName in groups.Keys) filter.Items.Add("Group: " + groupName);
        filter.SelectedIndex = 0;
        AutomationProperties.SetName(filter, "Contact list or group");
        Grid.SetColumn(filter, 1);
        options.Children.Add(filter);
        Grid.SetRow(options, 1);
        root.Children.Add(options);

        var list = new ListBox { SelectionMode = SelectionMode.Extended, DisplayMemberPath = nameof(Choice.Label) };
        AutomationProperties.SetName(list, "Contacts to add to this email");
        Grid.SetRow(list, 2);
        root.Children.Add(list);

        var footer = new DockPanel { Margin = new Thickness(0, 13, 0, 0), LastChildFill = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var addGroup = new Button { Content = "Add entire group", Style = S("Button.Secondary"), Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", Style = S("Button.Secondary"), IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
        var add = new Button { Content = "Add selected", Style = S("Button.Primary"), IsDefault = true };
        buttons.Children.Add(addGroup);
        buttons.Children.Add(cancel);
        buttons.Children.Add(add);
        DockPanel.SetDock(buttons, Dock.Right);
        footer.Children.Add(buttons);
        var count = new TextBlock { Text = "", Style = S("Text.Caption"), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        footer.Children.Add(count);
        Grid.SetRow(footer, 3);
        root.Children.Add(footer);

        void Refresh()
        {
            var category = filter.SelectedItem as string ?? "All contacts";
            IEnumerable<ContactEntry> candidates = category == "Frequently contacted" ? ContactSuggest.Frequent(entries)
                : category.StartsWith("Group: ", StringComparison.Ordinal) && groups.TryGetValue(category[7..], out var members) ? members : entries;
            var term = search.Text.Trim();
            if (term.Length > 0)
                candidates = candidates.Where(person => person.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                    || person.Email.Contains(term, StringComparison.OrdinalIgnoreCase)
                    || (person.Saved?.Company.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false));
            var choices = candidates.Where(person => !string.IsNullOrWhiteSpace(person.Email)).Select(person => new Choice(person)).ToList();
            list.ItemsSource = choices;
            addGroup.IsEnabled = category.StartsWith("Group: ", StringComparison.Ordinal) && groups.ContainsKey(category[7..]);
            count.Text = $"{choices.Count} people";
        }
        search.TextChanged += (_, _) => Refresh();
        filter.SelectionChanged += (_, _) => Refresh();
        add.Click += (_, _) =>
        {
            selected = list.SelectedItems.OfType<Choice>().Select(choice => choice.Entry).ToList();
            if (selected.Count == 0) { count.Text = "Select a contact first."; return; }
            window.DialogResult = true;
        };
        addGroup.Click += (_, _) =>
        {
            var category = filter.SelectedItem as string ?? "";
            if (!category.StartsWith("Group: ", StringComparison.Ordinal) || !groups.TryGetValue(category[7..], out var members)) return;
            selected = members.Where(person => !string.IsNullOrWhiteSpace(person.Email)).ToList();
            if (selected.Count > 25 && MessageBox.Show(window,
                    $"Add all {selected.Count} people in this group to the current recipient field?",
                    "Large contact group", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            if (selected.Count > 0) window.DialogResult = true;
        };
        Refresh();
        window.Content = root;
        window.Loaded += (_, _) => search.Focus();
        return window.ShowDialog() == true ? selected : Array.Empty<ContactEntry>();
    }
}
