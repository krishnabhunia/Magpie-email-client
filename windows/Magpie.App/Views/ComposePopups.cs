using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Magpie.App.Services;

namespace Magpie.App.Views;

/// <summary>Design CW1: the small popups of the compose window — colour palette (B2), table grid (B8), emoji picker (B10),
/// the shortcut sheet (E4). All built in code, styled with the app's brushes.</summary>
internal static class ComposePopups
{
    /// <summary>The 40 colours of design CW1 screen 3 (greys, bright, light, Magpie's own).</summary>
    public static readonly string[] Palette =
    {
        "#000000", "#434343", "#666666", "#999999", "#B7B7B7", "#CCCCCC", "#D9D9D9", "#EFEFEF", "#F3F3F3", "#FFFFFF",
        "#980000", "#B3261E", "#FF9900", "#FFFF00", "#00FF00", "#00FFFF", "#4A86E8", "#0000FF", "#9900FF", "#FF00FF",
        "#E6B8AF", "#F4CCCC", "#FCE5CD", "#FFF2CC", "#D9EAD3", "#D0E0E3", "#C9DAF8", "#CFE2F3", "#D9D2E9", "#EAD1DC",
        "#14606E", "#4B3F86", "#256745", "#87541D", "#A4313D", "#1A3E8C", "#0F766E", "#8B4513", "#6A1B9A", "#1B5E20",
    };

    private static Border Frame(UIElement child)
    {
        var b = new Border { Child = child, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(8) };
        b.SetResourceReference(Border.BackgroundProperty, "Brush.Card.Background");
        b.SetResourceReference(Border.BorderBrushProperty, "Brush.Card.Border");
        b.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.2 };
        return b;
    }

    private static Popup Make(UIElement target, UIElement child) =>
        new() { PlacementTarget = target, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true, VerticalOffset = 4, Child = Frame(child) };

    private static TextBlock Caps(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 10.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 2, 2, 6) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text.Tertiary");
        return t;
    }

    /// <summary>B2: a palette; <paramref name="pick"/> gets "#RRGGBB", or "" for "Automatic".</summary>
    public static Popup Colours(UIElement target, string title, string current, Action<string> pick)
    {
        var panel = new StackPanel { Width = 230 };
        panel.Children.Add(Caps(title.ToUpperInvariant()));
        var grid = new UniformGrid { Columns = 10 };
        Popup? popup = null;
        foreach (var hex in Palette)
        {
            var b = new Button { Width = 20, Height = 20, Margin = new Thickness(1.5), Padding = new Thickness(0), BorderThickness = new Thickness(1), ToolTip = hex,
                Background = Brush(hex), BorderBrush = hex.Equals(current, StringComparison.OrdinalIgnoreCase) ? Brushes.Black : Brush("#20000000") };
            b.Template = SwatchTemplate();
            b.Click += (_, _) => { popup!.IsOpen = false; pick(hex); };
            grid.Children.Add(b);
        }
        panel.Children.Add(grid);
        var auto = new Button { Content = "Automatic (no colour)", Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, FontSize = 12, Padding = new Thickness(0) };
        auto.SetResourceReference(FrameworkElement.StyleProperty, "Button.Link");
        auto.Click += (_, _) => { popup!.IsOpen = false; pick(""); };
        panel.Children.Add(auto);
        var row = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        var go = new Button { Content = "Use", FontSize = 12, Padding = new Thickness(10, 2, 10, 2), MinHeight = 26, Margin = new Thickness(6, 0, 0, 0), FontWeight = FontWeights.Normal };
        go.SetResourceReference(FrameworkElement.StyleProperty, "Button.Secondary");
        DockPanel.SetDock(go, Dock.Right);
        var custom = new TextBox { Tag = "#RRGGBB", FontSize = 12, MinHeight = 26 };
        custom.SetResourceReference(FrameworkElement.StyleProperty, "TextBox.Field");
        void UseCustom()
        {
            var t = custom.Text.Trim();
            if (!t.StartsWith('#')) t = "#" + t;
            if (System.Text.RegularExpressions.Regex.IsMatch(t, "^#[0-9A-Fa-f]{6}$")) { popup!.IsOpen = false; pick(t.ToUpperInvariant()); }
        }
        go.Click += (_, _) => UseCustom();
        custom.KeyDown += (_, e) => { if (e.Key == Key.Enter) UseCustom(); };
        row.Children.Add(go);
        row.Children.Add(custom);
        panel.Children.Add(row);
        popup = Make(target, panel);
        return popup;
    }

    private static ControlTemplate SwatchTemplate()
    {
        var t = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        t.VisualTree = border;
        return t;
    }

    public static Brush Brush(string hex)
    {
        var b = (Brush)new BrushConverter().ConvertFromString(hex)!;
        b.Freeze();
        return b;
    }

    /// <summary>B8: hover the grid, click = insert rows × cols (up to 8 × 6).</summary>
    public static Popup TableGrid(UIElement target, Action<int, int> insert)
    {
        const int cols = 8, rows = 6;
        var panel = new StackPanel();
        var label = Caps("INSERT TABLE");
        panel.Children.Add(label);
        var grid = new UniformGrid { Columns = cols, Rows = rows };
        var cells = new Border[rows, cols];
        Popup? popup = null;
        void Paint(int r, int c)
        {
            for (var i = 0; i < rows; i++)
                for (var j = 0; j < cols; j++)
                {
                    var on = i <= r && j <= c;
                    cells[i, j].SetResourceReference(Border.BackgroundProperty, on ? "Brush.Accent.Subtle" : "Brush.Row.Alt");
                    cells[i, j].SetResourceReference(Border.BorderBrushProperty, on ? "Brush.Accent" : "Brush.Control.Border");
                }
            label.Text = r < 0 ? "INSERT TABLE" : $"INSERT TABLE · {c + 1} × {r + 1}";
        }
        for (var i = 0; i < rows; i++)
            for (var j = 0; j < cols; j++)
            {
                var cell = new Border { Width = 20, Height = 20, Margin = new Thickness(1.5), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(2), Cursor = Cursors.Hand };
                var (r, c) = (i, j);
                cell.MouseEnter += (_, _) => Paint(r, c);
                cell.MouseLeftButtonUp += (_, _) => { popup!.IsOpen = false; insert(r + 1, c + 1); };
                cells[i, j] = cell;
                grid.Children.Add(cell);
            }
        grid.MouseLeave += (_, _) => Paint(-1, -1);
        Paint(-1, -1);
        panel.Children.Add(grid);
        popup = Make(target, panel);
        return popup;
    }

    private static readonly (string Name, string Icon, string[] Items)[] EmojiGroups =
    {
        ("Smileys", "😀", new[] { "😀","😃","😄","😁","😆","😅","🤣","😂","🙂","😉","😊","😇","🥰","😍","😘","😋","😎","🤔","🤗","🤩","😐","😏","😌","😔","😢","😭","😤","😡","🥳","🤯","😴","🙄","😬","🤝","🙏","👏","💪","🤞","✌️","👌" }),
        ("Gestures", "👍", new[] { "👍","👎","👋","🙌","🫡","🤙","👉","👈","👆","👇","☝️","✋","🤚","🖐️","🫶","❤️","🧡","💛","💚","💙","💜","🖤","💯","🔥","✨","⭐","🎉","🎊","🎁","🏆" }),
        ("Work", "💡", new[] { "💡","📎","📌","📝","📅","📆","🗓️","📊","📈","📉","📧","📨","📬","🗂️","📁","💼","🖥️","💻","⌨️","🖨️","📱","☎️","🔔","🔕","⏰","⌛","✅","☑️","✔️","❌","❗","❓","⚠️","🚀","🛠️","🔧","🔑","🔒" }),
        ("Food", "☕", new[] { "☕","🍵","🧋","🥤","🍺","🍷","🥂","🍕","🍔","🍟","🌮","🍝","🍜","🍣","🍱","🥗","🍎","🍌","🍇","🍓","🍰","🎂","🍩","🍪","🍫" }),
        ("Travel", "🚗", new[] { "🚗","🚕","🚌","🚆","✈️","🚀","🛳️","🚲","🏍️","🏠","🏢","🏥","🏖️","🏔️","🌍","🗺️","🧭","🧳","⛽","🅿️" }),
        ("Nature", "🐦", new[] { "🐦","🐱","🐶","🐭","🐰","🦊","🐻","🐼","🦁","🐯","🐸","🐧","🦋","🌸","🌼","🌻","🌳","🌵","🌙","☀️","⛅","🌧️","❄️","🌈" }),
        ("Sport", "⚽", new[] { "⚽","🏀","🏏","🏈","🎾","🏐","🏓","🏸","🥊","🎯","🎮","🎲","🎵","🎶","🎸","🎬","📷","🎨" }),
        ("Symbols", "♥", new[] { "♥","♦","♣","♠","©","®","™","°","±","×","÷","→","←","↑","↓","↔","⇒","∞","≈","≠","≤","≥","€","£","₹","$","¥","•","…","–","—","✓","✗" }),
    };

    /// <summary>B10: searchable picker with a Recent row; <paramref name="recent"/> newest first.</summary>
    public static Popup Emoji(UIElement target, IReadOnlyList<string> recent, Action<string> pick)
    {
        var panel = new StackPanel { Width = 320 };
        var search = new TextBox { Tag = "Search emoji", FontSize = 12.5, MinHeight = 28 };
        search.SetResourceReference(FrameworkElement.StyleProperty, "TextBox.Field");
        panel.Children.Add(search);
        var cats = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 4) };
        panel.Children.Add(cats);
        var scroll = new ScrollViewer { MaxHeight = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var body = new StackPanel();
        scroll.Content = body;
        panel.Children.Add(scroll);
        Popup? popup = null;
        var names = new Dictionary<string, string>();
        foreach (var g in EmojiGroups) foreach (var e in g.Items) names[e] = g.Name.ToLowerInvariant();
        WrapPanel Grid(IEnumerable<string> items)
        {
            var w = new WrapPanel();
            foreach (var e in items)
            {
                var b = new Button { Content = e, FontSize = 17, Width = 32, Height = 30, MinHeight = 30, Padding = new Thickness(0), Margin = new Thickness(1), FontFamily = new FontFamily("Segoe UI Emoji, Segoe UI Symbol, Segoe UI") };
                b.SetResourceReference(FrameworkElement.StyleProperty, "Button.Icon");
                b.Click += (_, _) => { popup!.IsOpen = false; pick(e); };
                w.Children.Add(b);
            }
            return w;
        }
        void Fill(string query, string? group)
        {
            body.Children.Clear();
            var q = query.Trim().ToLowerInvariant();
            if (q.Length > 0)
            {
                var hits = EmojiGroups.Where(g => g.Name.Contains(q, StringComparison.OrdinalIgnoreCase)).SelectMany(g => g.Items)
                    .Concat(EmojiGroups.SelectMany(g => g.Items).Where(e => Alias(e).Contains(q))).Distinct().ToList();
                body.Children.Add(Caps(hits.Count == 0 ? "NOTHING MATCHES" : "MATCHES"));
                body.Children.Add(Grid(hits));
                return;
            }
            if (group == null)
            {
                if (recent.Count > 0) { body.Children.Add(Caps("RECENT")); body.Children.Add(Grid(recent)); }
                foreach (var g in EmojiGroups) { body.Children.Add(Caps(g.Name.ToUpperInvariant())); body.Children.Add(Grid(g.Items)); }
            }
            else
            {
                var g = EmojiGroups.First(x => x.Name == group);
                body.Children.Add(Caps(g.Name.ToUpperInvariant()));
                body.Children.Add(Grid(g.Items));
            }
        }
        void Cat(string text, string? group)
        {
            var b = new Button { Content = text, FontSize = 14, Width = 30, Height = 26, MinHeight = 26, Padding = new Thickness(0), Margin = new Thickness(0, 0, 2, 0), ToolTip = group ?? "Recent", FontFamily = new FontFamily("Segoe UI Emoji, Segoe UI Symbol, Segoe UI") };
            b.SetResourceReference(FrameworkElement.StyleProperty, "Button.Icon");
            b.Click += (_, _) => { search.Text = ""; Fill("", group); scroll.ScrollToTop(); };
            cats.Children.Add(b);
        }
        Cat("🕘", null);
        foreach (var g in EmojiGroups) Cat(g.Icon, g.Name);
        search.TextChanged += (_, _) => Fill(search.Text, null);
        Fill("", null);
        popup = Make(target, panel);
        popup.Opened += (_, _) => { search.Focus(); };
        return popup;
    }

    private static readonly Dictionary<string, string> Aliases = new()
    {
        ["👍"] = "thumbs up yes ok good like", ["🙏"] = "thanks please pray", ["😊"] = "smile happy", ["✅"] = "done check tick", ["🎉"] = "party congrats celebrate",
        ["📎"] = "attach clip", ["☕"] = "coffee tea", ["🙂"] = "smile", ["👋"] = "wave hello bye", ["❤️"] = "heart love", ["🔥"] = "fire hot", ["😂"] = "laugh lol",
        ["🤔"] = "think hmm", ["😢"] = "sad cry", ["🚀"] = "rocket launch ship", ["💡"] = "idea light", ["⚠️"] = "warning", ["❌"] = "no cross wrong", ["📅"] = "calendar date",
        ["💪"] = "strong flex", ["👏"] = "clap", ["🥳"] = "party", ["😎"] = "cool sunglasses", ["😴"] = "sleep tired", ["⭐"] = "star", ["✨"] = "sparkle", ["🏆"] = "trophy win",
    };
    private static string Alias(string e) => Aliases.TryGetValue(e, out var a) ? a : "";

    /// <summary>E4: the shortcut sheet.</summary>
    public static void Shortcuts(Window owner)
    {
        var w = new Window
        {
            Owner = owner, Title = "Keyboard shortcuts", Width = 720, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Style = (Style)Application.Current.FindResource("Window.Dialog"),
        };
        var root = new StackPanel { Margin = new Thickness(24) };
        var heading = new TextBlock { Text = "Keyboard shortcuts in a message", Style = (Style)Application.Current.FindResource("Text.Serif"), FontSize = 20, Margin = new Thickness(0, 0, 0, 14) };
        root.Children.Add(heading);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(Column("MESSAGE", new[]
        {
            ("Send", "Ctrl+Enter"), ("Send & Archive (replies)", "Ctrl+Alt+Enter"), ("Save draft", "Ctrl+S"), ("Attach files", "Ctrl+Shift+O"),
            ("Open Cc · Bcc", "Ctrl+Shift+C · Ctrl+Shift+B"), ("Assistant panel", "Ctrl+Shift+A"), ("Find · Replace", "Ctrl+F · Ctrl+H"),
            ("This sheet", "Ctrl+/"), ("Close", "Esc"),
        }, 0));
        grid.Children.Add(Column("TEXT", new[]
        {
            ("Bold · Italic · Underline", "Ctrl+B · Ctrl+I · Ctrl+U"), ("Strikethrough", "Ctrl+Shift+X"), ("Bulleted · Numbered list", "Ctrl+Shift+8 · Ctrl+Shift+7"),
            ("Indent · Outdent", "Ctrl+] · Ctrl+["), ("Align left · centre · right · justify", "Ctrl+Shift+L · E · R · J"), ("Link · Remove link", "Ctrl+K · Ctrl+Shift+K"),
            ("Normal · Heading 1–3 · Code", "Ctrl+Alt+0 · 1 · 2 · 3 · C"), ("Superscript · Subscript", "Ctrl+. · Ctrl+,"), ("Clear formatting", "Ctrl+\\"),
            ("Paste as plain text", "Ctrl+Shift+V"), ("Undo · Redo", "Ctrl+Z · Ctrl+Y"),
        }, 2));
        root.Children.Add(grid);
        var close = new Button { Content = "Close", IsCancel = true, IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0), FontWeight = FontWeights.Normal };
        close.SetResourceReference(FrameworkElement.StyleProperty, "Button.Secondary");
        root.Children.Add(close);
        w.Content = root;
        Typography.SetRole(w, "dialogBody");
        Typography.SetRole(heading, "dialogTitle");
        w.ShowDialog();
    }

    private static StackPanel Column(string title, (string What, string Keys)[] rows, int column)
    {
        var p = new StackPanel();
        Grid.SetColumn(p, column);
        p.Children.Add(Caps(title));
        foreach (var (what, keys) in rows)
        {
            var d = new DockPanel { Margin = new Thickness(0, 0, 0, 7) };
            var k = new TextBlock { Text = keys, FontFamily = new FontFamily("Consolas"), FontSize = 12, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            k.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text.Secondary");
            DockPanel.SetDock(k, Dock.Right);
            d.Children.Add(k);
            d.Children.Add(new TextBlock { Text = what, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center });
            p.Children.Add(d);
        }
        return p;
    }
}
