using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Magpie.Core.Mail;

namespace Magpie.App.Controls;

/// <summary>
/// Design CW1 (A1, A3, A6): an address field whose finished addresses become chips (initials, name, ✕), with the typed
/// remainder in a plain text box at the end. <see cref="Text"/> is the whole field as one comma-separated string, so the
/// view model, drafts and the AI code keep working with strings. Chips can be dragged to another RecipientBox.
/// </summary>
public sealed class RecipientBox : Border
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(RecipientBox),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, e) => ((RecipientBox)d).OnTextChanged((string?)e.NewValue ?? "")));
    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.Register(nameof(Placeholder), typeof(string), typeof(RecipientBox), new PropertyMetadata("", (d, _) => ((RecipientBox)d).UpdatePlaceholder()));
    /// <summary>Only one address allowed (the Reply-To row).</summary>
    public static readonly DependencyProperty SingleProperty = DependencyProperty.Register(nameof(Single), typeof(bool), typeof(RecipientBox), new PropertyMetadata(false));

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public string Placeholder { get => (string)GetValue(PlaceholderProperty); set => SetValue(PlaceholderProperty, value); }
    public bool Single { get => (bool)GetValue(SingleProperty); set => SetValue(SingleProperty, value); }

    /// <summary>The plain text box at the end of the field (the compose window wires its contact suggestions to it).</summary>
    public TextBox Input { get; }
    /// <summary>The finished addresses, in order ("Name &lt;a@b&gt;" or "a@b").</summary>
    public IReadOnlyList<string> Entries => _chips.Select(c => c.Entry).ToList();
    /// <summary>Something inside the field has keyboard focus.</summary>
    public bool HasFocusWithin => IsKeyboardFocusWithin;

    /// <summary>"Move to Cc" / "Move to Bcc" from the hover card: (entry, "cc" | "bcc"). The box already removed the chip.</summary>
    public event Action<string, string>? MoveRequested;
    public event Action<string>? OpenContactRequested;
    /// <summary>Set by the window: one line about the address for the hover card ("Saved contact · you wrote to her 14 times"), or null.</summary>
    public Func<string, string?>? Describe { get; set; }
    /// <summary>Text about to be shown in a chip pasted or typed with several addresses: the window may want to know.</summary>
    public event Action? Changed;

    private const string DragFormat = "magpie/recipient";
    private readonly WrapPanel _panel = new();
    private readonly List<Chip> _chips = new();
    private readonly TextBlock _placeholder;
    private bool _updating;
    private Chip? _hovered;
    private readonly DispatcherTimer _hoverTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _leaveTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private Popup? _card;

    public RecipientBox()
    {
        SetResourceReference(BorderBrushProperty, "Brush.Control.Border");
        SetResourceReference(BackgroundProperty, "Brush.Control.Background");
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(6);
        Padding = new Thickness(5, 2, 5, 2);
        MinHeight = 32;
        AllowDrop = true;
        Input = new TextBox { BorderThickness = new Thickness(0), Background = Brushes.Transparent, MinWidth = 140, Padding = new Thickness(3, 4, 3, 4), VerticalAlignment = VerticalAlignment.Center };
        Input.SetResourceReference(TextBox.ForegroundProperty, "Brush.Text.Primary");
        Input.TextChanged += (_, _) => { if (!_updating) { CommitSeparated(); PushText(); } };
        Input.PreviewKeyDown += OnInputKey;
        // Leaving the field commits what was typed — unless the focus went to the suggestion list (the pick replaces the typed text).
        Input.LostKeyboardFocus += (_, _) => { if (!_updating && !SuggestionsOpen) CommitInput(); };
        _placeholder = new TextBlock { IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
        _placeholder.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text.Tertiary");
        var grid = new Grid();
        grid.Children.Add(_panel);
        grid.Children.Add(_placeholder);
        _panel.Children.Add(Input);
        Child = grid;
        MouseLeftButtonDown += (_, e) => { if (e.OriginalSource == this || e.OriginalSource == grid || e.OriginalSource == _panel) { Input.Focus(); Input.CaretIndex = Input.Text.Length; } };
        IsKeyboardFocusWithinChanged += (_, _) => UpdateFocusLook();
        DragOver += OnDragOver;
        Drop += OnDrop;
        _hoverTimer.Tick += (_, _) => { _hoverTimer.Stop(); if (_hovered != null) ShowCard(_hovered); };
        _leaveTimer.Tick += (_, _) => { _leaveTimer.Stop(); if (_card is { IsOpen: true } && !_card.IsMouseOver && (_hovered == null || !_hovered.Root.IsMouseOver)) HideCard(); };
        UpdatePlaceholder();
    }

    // ───────────────────────── text ↔ chips ─────────────────────────

    private void OnTextChanged(string text)
    {
        if (_updating) return;
        _updating = true;
        try
        {
            foreach (var c in _chips) _panel.Children.Remove(c.Root);
            _chips.Clear();
            foreach (var piece in Composer.SplitAddresses(text)) AddChipCore(piece);
            Input.Text = "";
            UpdatePlaceholder();
        }
        finally { _updating = false; }
    }

    private void PushText()
    {
        var parts = _chips.Select(c => c.Entry).ToList();
        var typed = Input.Text.Trim();
        if (typed.Length > 0) parts.Add(typed);
        var text = string.Join(", ", parts);
        if (text == Text) return;
        _updating = true;
        try { SetCurrentValue(TextProperty, text); }
        finally { _updating = false; }
        UpdatePlaceholder();
        Changed?.Invoke();
    }

    /// <summary>Pieces before the last separator become chips (typing "," or pasting a list).</summary>
    private void CommitSeparated()
    {
        var t = Input.Text;
        var last = LastSeparator(t);
        if (last < 0) return;
        var head = t[..(last + 1)];
        var tail = t[(last + 1)..];
        _updating = true;
        try
        {
            foreach (var piece in Composer.SplitAddresses(head)) AddChipCore(piece);
            Input.Text = tail.TrimStart();
            Input.CaretIndex = Input.Text.Length;
        }
        finally { _updating = false; }
    }

    private static int LastSeparator(string t)
    {
        var quoted = false;
        var escaped = false;
        var last = -1;
        for (var i = 0; i < t.Length; i++)
        {
            // Same rules as Composer.AddressPieces: a \" inside a quoted name is not a quote, and separators inside quotes don't split.
            if (t[i] == '"' && !escaped) quoted = !quoted;
            else if (!quoted && t[i] is ',' or ';' or '\n') last = i;
            escaped = t[i] == '\\' && !escaped;
        }
        return last;
    }

    /// <summary>What is typed becomes a chip (Enter, Tab, leaving the field).</summary>
    public void CommitInput()
    {
        var typed = Input.Text.Trim().TrimEnd(',', ';').Trim();
        if (typed.Length == 0) { if (Input.Text.Length > 0) { _updating = true; Input.Text = ""; _updating = false; PushText(); } return; }
        _updating = true;
        try { AddChipCore(typed); Input.Text = ""; }
        finally { _updating = false; }
        PushText();
    }

    /// <summary>Adds a finished address (from the suggestion list or the address book). Duplicates are skipped (A6).</summary>
    public void AddChip(string entry)
    {
        _updating = true;
        try { AddChipCore(entry); Input.Text = ""; }
        finally { _updating = false; }
        PushText();
    }

    private void AddChipCore(string piece)
    {
        var (_, address) = Composer.NameAndAddress(piece);
        if (_chips.Any(c => c.Address.Equals(address, StringComparison.OrdinalIgnoreCase))) return;
        if (Single && _chips.Count > 0) { var old = _chips[0]; _chips.Remove(old); _panel.Children.Remove(old.Root); }
        var chip = new Chip(this, piece);
        _chips.Add(chip);
        _panel.Children.Insert(_panel.Children.Count - 1, chip.Root);
        UpdatePlaceholder();
    }

    private void RemoveChip(Chip c, bool push = true)
    {
        if (c == _hovered) HideCard();
        _chips.Remove(c);
        _panel.Children.Remove(c.Root);
        if (push) PushText();
        UpdatePlaceholder();
    }

    /// <summary>Removes the address (e.g. after it was moved to another row).</summary>
    public void Remove(string entry)
    {
        var (_, address) = Composer.NameAndAddress(entry);
        var c = _chips.FirstOrDefault(x => x.Address.Equals(address, StringComparison.OrdinalIgnoreCase));
        if (c != null) RemoveChip(c);
    }

    private void OnInputKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Back && Input.Text.Length == 0 && _chips.Count > 0)
        {
            // The last chip becomes text again, ready to edit.
            var last = _chips[^1];
            var entry = last.Entry;
            RemoveChip(last, push: false);
            _updating = true;
            try { Input.Text = entry; Input.CaretIndex = entry.Length; }
            finally { _updating = false; }
            PushText();
            e.Handled = true;
        }
        else if (e.Key is Key.Enter or Key.Tab && Input.Text.Trim().Length > 0 && !SuggestionsOpen)
        {
            CommitInput();
            if (e.Key == Key.Enter) e.Handled = true;
        }
    }

    /// <summary>Set by the window while its suggestion list is open (Enter / Tab then pick a suggestion, not a chip).</summary>
    public bool SuggestionsOpen { get; set; }

    private void EditChip(Chip c)
    {
        var entry = c.Entry;
        RemoveChip(c, push: false);
        _updating = true;
        try { Input.Text = entry; }
        finally { _updating = false; }
        Input.Focus();
        Input.CaretIndex = entry.Length;
        PushText();
    }

    private void UpdatePlaceholder()
    {
        _placeholder.Text = Placeholder;
        _placeholder.Visibility = _chips.Count == 0 && Input.Text.Length == 0 && Placeholder.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateFocusLook() => SetResourceReference(BorderBrushProperty, IsKeyboardFocusWithin ? "Brush.Accent" : "Brush.Control.Border");

    // ───────────────────────── drag between rows (A3) ─────────────────────────

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DragFormat) ? DragDropEffects.Move : DragDropEffects.None;
        if (e.Effects == DragDropEffects.Move) SetResourceReference(BorderBrushProperty, "Brush.Accent");
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        UpdateFocusLook();
        if (e.Data.GetData(DragFormat) is not string entry) return;
        if (e.Data.GetData(typeof(RecipientBox)) is RecipientBox source && source != this) source.Remove(entry);
        AddChip(entry);
        e.Handled = true;
    }

    protected override void OnDragLeave(DragEventArgs e) { base.OnDragLeave(e); UpdateFocusLook(); }

    // ───────────────────────── hover card (A1) ─────────────────────────

    private void ShowCard(Chip c)
    {
        HideCard();
        var panel = new StackPanel { Margin = new Thickness(12, 10, 12, 10), MinWidth = 220, MaxWidth = 320 };
        var name = new TextBlock { Text = c.Name.Length > 0 ? c.Name : c.Address, FontWeight = FontWeights.SemiBold, FontSize = 13, TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(name);
        if (c.Name.Length > 0)
        {
            var addr = new TextBlock { Text = c.Address, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
            addr.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text.Secondary");
            panel.Children.Add(addr);
        }
        var line = c.Valid ? Describe?.Invoke(c.Address) : "Doesn't look like an email address — click it to fix";
        if (!string.IsNullOrWhiteSpace(line))
        {
            var info = new TextBlock { Text = line, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
            info.SetResourceReference(TextBlock.ForegroundProperty, c.Valid ? "Brush.Text.Secondary" : "Brush.Danger");
            panel.Children.Add(info);
        }
        var acts = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        void Act(string text, Action a)
        {
            var b = new Button { Content = text, Margin = new Thickness(0, 0, 10, 2), Padding = new Thickness(0), FontSize = 12 };
            b.SetResourceReference(StyleProperty, "Button.Link");
            b.Click += (_, _) => { HideCard(); a(); };
            acts.Children.Add(b);
        }
        if (!Single)
        {
            Act("Move to Cc", () => { var entry = c.Entry; RemoveChip(c); MoveRequested?.Invoke(entry, "cc"); });
            Act("Move to Bcc", () => { var entry = c.Entry; RemoveChip(c); MoveRequested?.Invoke(entry, "bcc"); });
        }
        if (c.Valid) Act("Open contact", () => OpenContactRequested?.Invoke(c.Address));
        Act("Copy address", () => { try { Clipboard.SetText(c.Address); } catch { } });
        Act("Remove", () => RemoveChip(c));
        panel.Children.Add(acts);
        var border = new Border { Child = panel, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8) };
        border.SetResourceReference(BackgroundProperty, "Brush.Card.Background");
        border.SetResourceReference(BorderBrushProperty, "Brush.Card.Border");
        border.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Opacity = 0.18 };
        _card = new Popup { Child = border, PlacementTarget = c.Root, Placement = PlacementMode.Bottom, StaysOpen = true, AllowsTransparency = true, VerticalOffset = 4, IsOpen = true };
        border.MouseLeave += (_, _) => { _leaveTimer.Stop(); _leaveTimer.Start(); };
    }

    private void HideCard()
    {
        if (_card != null) _card.IsOpen = false;
        _card = null;
    }

    private void ChipEnter(Chip c) { _hovered = c; _leaveTimer.Stop(); _hoverTimer.Stop(); _hoverTimer.Start(); }
    private void ChipLeave(Chip c) { _hoverTimer.Stop(); _leaveTimer.Stop(); _leaveTimer.Start(); }

    // ───────────────────────── one chip ─────────────────────────

    private sealed class Chip
    {
        public Border Root { get; }
        public string Entry { get; }
        public string Name { get; }
        public string Address { get; }
        public bool Valid { get; }
        private Point _down;
        private bool _dragging;

        public Chip(RecipientBox box, string entry)
        {
            Entry = entry;
            (Name, Address) = Composer.NameAndAddress(entry);
            Valid = Composer.IsValidAddress(entry);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var avatar = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(10), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0),
                Background = Valid ? ColourFor(Address) : null };
            if (!Valid) avatar.SetResourceReference(BackgroundProperty, "Brush.Danger");
            avatar.Child = new TextBlock { Text = Valid ? Initials(Name, Address) : "!", FontSize = 9.5, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(avatar);
            var label = new TextBlock { Text = Name.Length > 0 ? Name : Address, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 260, TextTrimming = TextTrimming.CharacterEllipsis };
            label.SetResourceReference(TextBlock.ForegroundProperty, Valid ? "Brush.Text.Primary" : "Brush.Danger");
            row.Children.Add(label);
            var close = new Button { Content = "", FontSize = 8, Width = 18, Height = 18, MinHeight = 18, Padding = new Thickness(0), Margin = new Thickness(3, 0, 0, 0), ToolTip = "Remove" };
            close.SetResourceReference(StyleProperty, "Button.Icon");
            close.Click += (_, e) => { e.Handled = true; box.RemoveChip(this); };
            row.Children.Add(close);
            Root = new Border { Child = row, CornerRadius = new CornerRadius(999), Padding = new Thickness(3, 2, 4, 2), Margin = new Thickness(0, 2, 6, 2), BorderThickness = new Thickness(1), Cursor = Cursors.Arrow,
                ToolTip = Valid ? (Name.Length > 0 ? Address : null) : "Doesn't look like an email address" };
            Root.SetResourceReference(BackgroundProperty, Valid ? "Brush.Row.Alt" : "Brush.Danger.Subtle");
            Root.SetResourceReference(BorderBrushProperty, Valid ? "Brush.Row.Alt" : "Brush.Danger");
            Root.MouseEnter += (_, _) => box.ChipEnter(this);
            Root.MouseLeave += (_, _) => box.ChipLeave(this);
            Root.MouseLeftButtonDown += (_, e) => { _down = e.GetPosition(null); _dragging = false; };
            Root.MouseMove += (_, e) =>
            {
                if (e.LeftButton != MouseButtonState.Pressed || _dragging || box.Single) return;
                var p = e.GetPosition(null);
                if (Math.Abs(p.X - _down.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(p.Y - _down.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                _dragging = true;
                box.HideCard();
                var data = new DataObject(DragFormat, Entry);
                data.SetData(typeof(RecipientBox), box);
                Root.Opacity = 0.5;
                try { DragDrop.DoDragDrop(Root, data, DragDropEffects.Move); }
                finally { Root.Opacity = 1; _dragging = false; }
            };
            Root.MouseLeftButtonUp += (_, e) => { if (!_dragging && e.OriginalSource is not Button) box.EditChip(this); };
        }

        private static string Initials(string name, string address)
        {
            var src = name.Length > 0 ? name : address.Split('@')[0];
            var parts = src.Split(new[] { ' ', '.', '_', '-' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return "?";
            var s = parts.Length >= 2 ? $"{parts[0][0]}{parts[1][0]}" : parts[0][..Math.Min(2, parts[0].Length)];
            return s.ToUpperInvariant();
        }

        private static readonly string[] Palette = { "#14606E", "#4B3F86", "#256745", "#87541D", "#A4313D", "#1A3E8C", "#0F766E", "#6A1B9A", "#8B4513", "#1B5E20" };
        private static Brush ColourFor(string address)
        {
            var h = 0;
            foreach (var ch in address.ToLowerInvariant()) h = unchecked(h * 31 + ch);
            var brush = (Brush)new BrushConverter().ConvertFromString(Palette[Math.Abs(h) % Palette.Length])!;
            brush.Freeze();
            return brush;
        }
    }
}
