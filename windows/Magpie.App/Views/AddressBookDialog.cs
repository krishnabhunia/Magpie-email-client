using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Magpie.App.Services;
using Magpie.Core.Contacts;
using Magpie.Core.Models;

namespace Magpie.App.Views;

/// <summary>
/// Design CW1 (A4): the address-book picker — All / Frequent / Groups from the Contacts page, tick people (a group ticks its
/// members), send them to To, Cc or Bcc. Returns what was chosen per row.
/// </summary>
internal sealed class AddressBookDialog
{
    public sealed record Result(List<string> To, List<string> Cc, List<string> Bcc);

    private readonly Window _win;
    private readonly List<ContactEntry> _all;
    private readonly List<(string Group, List<ContactEntry> Members)> _groups;
    private readonly StackPanel _list = new();
    private readonly TextBox _search = new();
    private readonly TextBlock _count = new();
    private readonly TextBlock _summary = new();
    private readonly Dictionary<string, ContactEntry> _ticked = new();
    private readonly List<string> _to = new(), _cc = new(), _bcc = new();
    private string _tab = "all";

    public static Result? Show(Window owner)
    {
        var e = AppServices.Engine;
        var entries = ContactSuggest.Entries(e.Store.GetSavedContacts(), e.Store.MailPeople());
        var d = new AddressBookDialog(owner, entries);
        return d._win.ShowDialog() == true ? new Result(d._to, d._cc, d._bcc) : null;
    }

    private AddressBookDialog(Window owner, List<ContactEntry> entries)
    {
        _all = entries;
        _groups = ContactSuggest.Groups(entries);
        _win = new Window
        {
            Owner = owner, Title = "Address book", Width = 720, Height = 560, MinWidth = 560, MinHeight = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Style = (Style)Application.Current.FindResource("Window.Dialog"),
        };
        var root = new Grid { Margin = new Thickness(18) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });

        var left = new DockPanel();
        _search.SetResourceReference(FrameworkElement.StyleProperty, "TextBox.Field");
        _search.Tag = "Search name, email, company";
        _search.TextChanged += (_, _) => Fill();
        DockPanel.SetDock(_search, Dock.Top);
        left.Children.Add(_search);
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 6) };
        DockPanel.SetDock(tabs, Dock.Top);
        RadioButton Tab(string id, string text, bool on)
        {
            var r = new RadioButton { Content = text, GroupName = "AbTabs", IsChecked = on, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(12, 4, 12, 4), FontSize = 12.5 };
            r.SetResourceReference(FrameworkElement.StyleProperty, "Button.Chip");
            r.Checked += (_, _) => { _tab = id; Fill(); };
            return r;
        }
        tabs.Children.Add(Tab("all", $"All {entries.Count:N0}", true));
        tabs.Children.Add(Tab("frequent", "Frequent", false));
        tabs.Children.Add(Tab("groups", "Groups", false));
        left.Children.Add(tabs);
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _list };
        var frame = new Border { Child = scroll, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(4) };
        frame.SetResourceReference(Border.BorderBrushProperty, "Brush.Card.Border");
        frame.SetResourceReference(Border.BackgroundProperty, "Brush.Card.Background");
        left.Children.Add(frame);
        root.Children.Add(left);

        var right = new StackPanel { Margin = new Thickness(14, 0, 0, 0) };
        Grid.SetColumn(right, 1);
        _count.FontSize = 12; _count.Margin = new Thickness(0, 6, 0, 10);
        _count.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text.Secondary");
        right.Children.Add(_count);
        Button Send(string text, List<string> target)
        {
            var b = new Button { Content = text, Margin = new Thickness(0, 0, 0, 8), FontWeight = FontWeights.Normal, HorizontalAlignment = HorizontalAlignment.Stretch };
            b.SetResourceReference(FrameworkElement.StyleProperty, "Button.Secondary");
            b.Click += (_, _) => MoveTicked(target);
            return b;
        }
        right.Children.Add(Send("To →", _to));
        right.Children.Add(Send("Cc →", _cc));
        right.Children.Add(Send("Bcc →", _bcc));
        _summary.FontSize = 11.5; _summary.TextWrapping = TextWrapping.Wrap; _summary.Margin = new Thickness(0, 6, 0, 0);
        _summary.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text.Secondary");
        right.Children.Add(_summary);
        var done = new Button { Content = "Done", IsDefault = true, Margin = new Thickness(0, 18, 0, 0), HorizontalAlignment = HorizontalAlignment.Stretch };
        done.SetResourceReference(FrameworkElement.StyleProperty, "Button.Primary");
        done.Click += (_, _) => { if (_ticked.Count > 0) MoveTicked(_to); _win.DialogResult = true; };
        right.Children.Add(done);
        var cancel = new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(0, 8, 0, 0), FontWeight = FontWeights.Normal, HorizontalAlignment = HorizontalAlignment.Stretch };
        cancel.SetResourceReference(FrameworkElement.StyleProperty, "Button.Ghost");
        right.Children.Add(cancel);
        root.Children.Add(right);

        _win.Content = root;
        Typography.SetRole(_win, "dialogBody");
        _win.Loaded += (_, _) => _search.Focus();
        Fill();
        UpdateCount();
    }

    private void Fill()
    {
        _list.Children.Clear();
        var q = _search.Text.Trim();
        var shown = q.Length > 0 ? ContactSuggest.Entries(_all.Where(e => e.Saved != null).Select(e => e.Saved!).ToList(), Array.Empty<(string, string, int, DateTimeOffset, bool)>(), q)
                                       .Concat(_all.Where(e => e.Saved == null && (e.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || e.Email.Contains(q, StringComparison.OrdinalIgnoreCase)))).ToList()
                 : _all;
        if (_tab == "groups" && q.Length == 0)
        {
            foreach (var (group, members) in _groups)
            {
                var row = Row(group + $" ({members.Count})", "adds " + members.Count + " people", null, members);
                _list.Children.Add(row);
            }
            if (_groups.Count == 0) _list.Children.Add(Caption("No contact groups. Groups come from your Google contacts."));
            return;
        }
        if (_tab == "frequent") shown = ContactSuggest.Frequent(shown);
        string? section = null;
        foreach (var e in shown)
        {
            if (_tab == "all" && e.Section != section) { section = e.Section; _list.Children.Add(Caption(section)); }
            _list.Children.Add(Row(e.Name, e.Email + (e.IsRecent ? "  · Recent" : ""), e, null));
        }
        if (shown.Count == 0) _list.Children.Add(Caption(q.Length > 0 ? "Nobody matches." : "No contacts yet — Google contacts and the people you write to appear here."));
    }

    private static TextBlock Caption(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 10.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(8, 8, 8, 2) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text.Tertiary");
        return t;
    }

    private Border Row(string name, string detail, ContactEntry? one, List<ContactEntry>? many)
    {
        var members = one != null ? new List<ContactEntry> { one } : many ?? new();
        var cb = new CheckBox { VerticalAlignment = VerticalAlignment.Center, IsChecked = members.Count > 0 && members.All(m => _ticked.ContainsKey(m.Key)) };
        cb.Checked += (_, _) => { foreach (var m in members) _ticked[m.Key] = m; UpdateCount(); };
        cb.Unchecked += (_, _) => { foreach (var m in members) _ticked.Remove(m.Key); UpdateCount(); };
        var text = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = name, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis });
        var d = new TextBlock { Text = detail, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis };
        d.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text.Secondary");
        text.Children.Add(d);
        var dock = new DockPanel { LastChildFill = true };
        dock.Children.Add(cb);
        dock.Children.Add(text);
        var row = new Border { Child = dock, Padding = new Thickness(8, 5, 8, 5), CornerRadius = new CornerRadius(4), Cursor = Cursors.Hand };
        row.MouseLeftButtonUp += (_, e) => { if (e.OriginalSource is not CheckBox) cb.IsChecked = cb.IsChecked != true; };
        row.MouseEnter += (_, _) => row.SetResourceReference(Border.BackgroundProperty, "Brush.Row.Hover");
        row.MouseLeave += (_, _) => row.Background = null;
        return row;
    }

    private void MoveTicked(List<string> target)
    {
        foreach (var m in _ticked.Values)
        {
            var entry = m.Name.Length > 0 && m.Name != m.Email ? (m.Name.Contains(',') ? "\"" + m.Name + "\"" : m.Name) + " <" + m.Email + ">" : m.Email;
            foreach (var list in new[] { _to, _cc, _bcc }) list.RemoveAll(x => x.Equals(entry, StringComparison.OrdinalIgnoreCase));
            target.Add(entry);
        }
        _ticked.Clear();
        Fill();
        UpdateCount();
    }

    private void UpdateCount()
    {
        _count.Text = _ticked.Count == 0 ? "Tick people, then choose a row" : $"{_ticked.Count} selected";
        _summary.Text = $"To: {Names(_to)}\nCc: {Names(_cc)}\nBcc: {Names(_bcc)}";
        static string Names(List<string> l) => l.Count == 0 ? "—" : l.Count <= 2 ? string.Join(", ", l.Select(Short)) : Short(l[0]) + " + " + (l.Count - 1);
        static string Short(string entry) => Core.Mail.Composer.NameAndAddress(entry) is var (n, a) && n.Length > 0 ? n : a;
    }
}
