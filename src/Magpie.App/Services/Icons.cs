using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ShapePath = System.Windows.Shapes.Path;

namespace Magpie.App.Services;

/// <summary>
/// The icon set (designs C1, C4): one line icon and one colour per action or folder, the same everywhere.
/// Paths are 24×24 stroke drawings; colours have a light and a dark variant (the dark theme uses the second pair).
/// </summary>
public static class Icons
{
    public sealed record Spec(string Name, string Path, string Fg, string Bg, string DarkFg, string DarkBg);

    private static readonly Dictionary<string, Spec> All = new(StringComparer.OrdinalIgnoreCase)
    {
        ["inbox"] = new("Inbox", "M3 13h5l1.5 3h5L16 13h5 M5 5h14l2 8v6H3v-6z", "#1D4ED8", "#DBEAFE", "#BFDBFE", "#1E3A8A"),
        ["pin"] = new("Pin", "M9 3h6l-1 6 4 4H6l4-4z M12 13v8", "#B45309", "#FEF3C7", "#FDE68A", "#78350F"),
        ["setaside"] = new("Set aside", "M12 3l9 5-9 5-9-5z M3 13l9 5 9-5", "#4338CA", "#E0E7FF", "#C7D2FE", "#312E81"),
        ["snooze"] = new("Snooze", "M12 7v5l3 2 M12 21a9 9 0 1 0 0-18 9 9 0 0 0 0 18z", "#6D28D9", "#EDE9FE", "#DDD6FE", "#4C1D95"),
        ["followup"] = new("Follow up", "M5 21V4 M5 4h11l-2 4 2 4H5", "#C2410C", "#FFEDD5", "#FED7AA", "#7C2D12"),
        ["scheduled"] = new("Scheduled", "M4 6h16v14H4z M4 10h16 M8 3v4 M16 3v4", "#0F766E", "#CCFBF1", "#99F6E4", "#134E4A"),
        ["sent"] = new("Sent", "M4 12l16-8-6 16-3-7z M11 13l9-9", "#15803D", "#DCFCE7", "#BBF7D0", "#14532D"),
        ["drafts"] = new("Drafts", "M4 20h4L19 9l-4-4L4 16z M13 7l4 4", "#334155", "#E2E8F0", "#E2E8F0", "#334155"),
        ["archive"] = new("Archive", "M3 4h18v4H3z M5 8v12h14V8 M10 12h4", "#0E7490", "#CFFAFE", "#A5F3FC", "#164E63"),
        ["allmail"] = new("All Mail", "M4 6h16v12H4z M4 7l8 6 8-6", "#0369A1", "#E0F2FE", "#BAE6FD", "#0C4A6E"),
        ["spam"] = new("Spam", "M12 21a9 9 0 1 0 0-18 9 9 0 0 0 0 18z M6 6l12 12", "#A21CAF", "#FAE8FF", "#F5D0FE", "#701A75"),
        ["delete"] = new("Delete", "M4 7h16 M9 7V4h6v3 M6 7l1 13h10l1-13", "#B91C1C", "#FEE2E2", "#FECACA", "#7F1D1D"),
        ["remind"] = new("Remind", "M6 16V11a6 6 0 0 1 12 0v5l2 2H4z M10 21h4", "#C2410C", "#FFEDD5", "#FED7AA", "#7C2D12"),
        ["tag"] = new("Tag", "M3 12V3h9l9 9-9 9z M8 8h.01", "#BE185D", "#FCE7F3", "#FBCFE8", "#831843"),
        ["move"] = new("Move", "M3 6h6l2 2h10v11H3z M10 14h6 M14 12l2 2-2 2", "#334155", "#E2E8F0", "#E2E8F0", "#334155"),
        ["folder"] = new("Folder", "M3 6h6l2 2h10v11H3z", "#C2410C", "#FFEDD5", "#FED7AA", "#7C2D12"),
        ["star"] = new("Starred", "M12 3l2.8 5.8 6.2.9-4.5 4.4 1 6.2L12 17.4 6.5 20.3l1-6.2L3 9.7l6.2-.9z", "#B45309", "#FEF3C7", "#FDE68A", "#78350F"),
        ["important"] = new("Important", "M4 5h11l5 7-5 7H4l4-7z", "#B91C1C", "#FEE2E2", "#FECACA", "#7F1D1D"),
        ["reply"] = new("Reply", "M10 9L5 13l5 4 M5 13h9a5 5 0 0 1 5 5", "#0F6E7E", "#CCFBF1", "#99F6E4", "#134E4A"),
        ["replyall"] = new("Reply all", "M12 9l-4 4 4 4 M7 9l-4 4 4 4 M8 13h7a5 5 0 0 1 5 5", "#0F6E7E", "#CCFBF1", "#99F6E4", "#134E4A"),
        ["forward"] = new("Forward", "M14 9l5 4-5 4 M19 13h-9a5 5 0 0 0-5 5", "#0369A1", "#E0F2FE", "#BAE6FD", "#0C4A6E"),
        ["unread"] = new("Mark unread", "M4 6h16v12H4z M4 7l8 6 8-6 M19 4h.01", "#1D4ED8", "#DBEAFE", "#BFDBFE", "#1E3A8A"),
        ["summarise"] = new("Summarise", "M12 3l1.8 5.2L19 10l-5.2 1.8L12 17l-1.8-5.2L5 10l5.2-1.8z", "#6D28D9", "#EDE9FE", "#DDD6FE", "#4C1D95"),
        ["update"] = new("Updates", "M12 4v11 M7 10l5 5 5-5 M5 20h14", "#15803D", "#DCFCE7", "#BBF7D0", "#14532D"),
        ["sync"] = new("Sync", "M20 11a8 8 0 1 0-2 6 M20 5v6h-6", "#1D4ED8", "#DBEAFE", "#BFDBFE", "#1E3A8A"),
        ["received"] = new("Received", "M12 4v11 M7 10l5 5 5-5 M5 20h14", "#15803D", "#DCFCE7", "#BBF7D0", "#14532D"),
        ["sending"] = new("Sending", "M4 12l16-8-6 16-3-7z", "#C2410C", "#FFEDD5", "#FED7AA", "#7C2D12"),
        ["check"] = new("Done", "M5 12l5 5 9-10", "#15803D", "#DCFCE7", "#BBF7D0", "#14532D"),
        ["warn"] = new("Problem", "M12 3l10 18H2z M12 10v4 M12 17h.01", "#B91C1C", "#FEE2E2", "#FECACA", "#7F1D1D"),
        ["offline"] = new("Offline", "M7 18h10a4 4 0 0 0 0-8 6 6 0 0 0-11.5 1.5A3.5 3.5 0 0 0 7 18z M4 4l16 16", "#B45309", "#FEF3C7", "#FDE68A", "#78350F"),
        ["clock"] = new("Waiting", "M12 7v5l3 2 M12 21a9 9 0 1 0 0-18 9 9 0 0 0 0 18z", "#B45309", "#FEF3C7", "#FDE68A", "#78350F"),
        ["general"] = new("General", "M12 15a3 3 0 1 0 0-6 3 3 0 0 0 0 6z M19 12h2 M3 12h2 M12 3v2 M12 19v2 M5.6 5.6l1.4 1.4 M17 17l1.4 1.4 M5.6 18.4L7 17 M17 7l1.4-1.4", "#334155", "#E2E8F0", "#E2E8F0", "#334155"),
        ["accounts"] = new("Accounts", "M9 11a4 4 0 1 0 0-8 4 4 0 0 0 0 8z M2 21a7 7 0 0 1 14 0 M16 3.5a4 4 0 0 1 0 7 M18 14a6 6 0 0 1 4 7", "#1D4ED8", "#DBEAFE", "#BFDBFE", "#1E3A8A"),
        ["rules"] = new("Rules", "M4 5h16l-6 7v6l-4 2v-8z", "#0E7490", "#CFFAFE", "#A5F3FC", "#164E63"),
        ["toolbar"] = new("Toolbar & buttons", "M4 6h16 M4 12h16 M4 18h10", "#0F766E", "#CCFBF1", "#99F6E4", "#134E4A"),
        ["notifications"] = new("Notifications", "M6 16V11a6 6 0 0 1 12 0v5l2 2H4z M10 21h4", "#C2410C", "#FFEDD5", "#FED7AA", "#7C2D12"),
        ["templates"] = new("Templates", "M4 20h4L19 9l-4-4L4 16z", "#B45309", "#FEF3C7", "#FDE68A", "#78350F"),
        ["keys"] = new("Keyboard shortcuts", "M3 7h18v10H3z M7 11h.01 M11 11h.01 M15 11h.01 M8 14h8", "#0369A1", "#E0F2FE", "#BAE6FD", "#0C4A6E"),
        ["about"] = new("About", "M12 21a9 9 0 1 0 0-18 9 9 0 0 0 0 18z M12 11v6 M12 7h.01", "#334155", "#E2E8F0", "#E2E8F0", "#334155"),
        ["compose"] = new("New message", "M4 20h4L19 9l-4-4L4 16z M13 7l4 4", "#FFFFFF", "#0F6E7E", "#FFFFFF", "#0F6E7E"),
        ["more"] = new("More", "M5 12h.01 M12 12h.01 M19 12h.01", "#334155", "#E2E8F0", "#E2E8F0", "#334155"),
        ["search"] = new("Search", "M11 18a7 7 0 1 0 0-14 7 7 0 0 0 0 14z M20 20l-4-4", "#334155", "#E2E8F0", "#E2E8F0", "#334155"),
        ["unsubscribe"] = new("Unsubscribe", "M4 6h16v12H4z M4 7l8 6 8-6 M3 3l18 18", "#B91C1C", "#FEE2E2", "#FECACA", "#7F1D1D"),
    };

    public static bool Has(string key) => All.ContainsKey(key);
    public static Spec Get(string key) => All.TryGetValue(key, out var s) ? s : All["folder"];

    /// <summary>Raised when the "Colourful icons" setting or the theme changes, so shown icons repaint.</summary>
    public static event Action? Changed;

    private static bool _colourful = true;
    private static bool _dark;
    public static bool Colourful { get => _colourful; set { if (_colourful == value) return; _colourful = value; Changed?.Invoke(); } }
    public static bool Dark { get => _dark; set { if (_dark == value) return; _dark = value; Changed?.Invoke(); } }

    private static readonly Dictionary<string, Geometry> Geometries = new();
    private static readonly Dictionary<string, SolidColorBrush> Brushes = new();

    public static Geometry GeometryOf(string key)
    {
        lock (Geometries)
        {
            if (Geometries.TryGetValue(key, out var g)) return g;
            g = Geometry.Parse(Get(key).Path);
            g.Freeze();
            Geometries[key] = g;
            return g;
        }
    }

    public static SolidColorBrush Brush(string hex)
    {
        lock (Brushes)
        {
            if (Brushes.TryGetValue(hex, out var b)) return b;
            b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            Brushes[hex] = b;
            return b;
        }
    }

    public static SolidColorBrush Fg(string key, bool? colourful = null) => Brush((colourful ?? Colourful) ? (Dark ? Get(key).DarkFg : Get(key).Fg) : (Dark ? "#E6EBF2" : "#3B4453"));
    public static SolidColorBrush Bg(string key, bool? colourful = null) => Brush((colourful ?? Colourful) ? (Dark ? Get(key).DarkBg : Get(key).Bg) : (Dark ? "#2A333F" : "#EEF0F4"));
    /// <summary>The colour of a folder's unread number: the icon's colour (design C2).</summary>
    public static SolidColorBrush Accent(string key) => Brush(Colourful ? (Dark ? Get(key).DarkFg : Get(key).Fg) : (Dark ? "#E6EBF2" : "#1A1A1A"));

    /// <summary>Plain text colours used next to icons (counts, hover card), in the current theme.</summary>
    public static SolidColorBrush Neutral => Brush(Dark ? "#C9CFD6" : "#3B4453");
    public static SolidColorBrush Dim => Brush(Dark ? "#6F7780" : "#9AA2B1");
    public static SolidColorBrush Ink => Brush(Dark ? "#E8E6E1" : "#23293A");

    /// <summary>
    /// A user colour (tag, account) made readable on the dark theme: the known palette gets its dark-theme pair
    /// (design B1: reds / ambers slightly desaturated), anything else is lightened towards white.
    /// </summary>
    public static string ForTheme(string hex)
    {
        if (!Dark) return hex;
        var key = hex.Trim().ToUpperInvariant();
        switch (key)
        {
            case "#B3261E": return "#E0645A";
            case "#B45309": case "#8A5300": return "#E0A04A";
            case "#14606E": return "#6CC3CF";
            case "#4B3F86": return "#A99DEB";
            case "#1B6B2E": return "#5CC27A";
            case "#2F5BEA": return "#7FA0F5";
            case "#5A6068": return "#AEB5BC";
        }
        try
        {
            var c = (Color)ColorConverter.ConvertFromString(hex);
            byte Up(byte v) => (byte)(v + (255 - v) * 0.45);
            return $"#{Up(c.R):X2}{Up(c.G):X2}{Up(c.B):X2}";
        }
        catch { return hex; }
    }

    /// <summary>For folders on the server: which icon a folder role uses.</summary>
    public static string ForRole(Magpie.Core.Models.FolderRole role) => role switch
    {
        Magpie.Core.Models.FolderRole.Inbox => "inbox",
        Magpie.Core.Models.FolderRole.Sent => "sent",
        Magpie.Core.Models.FolderRole.Drafts => "drafts",
        Magpie.Core.Models.FolderRole.Trash => "delete",
        Magpie.Core.Models.FolderRole.Junk => "spam",
        Magpie.Core.Models.FolderRole.Archive => "archive",
        Magpie.Core.Models.FolderRole.All => "allmail",
        Magpie.Core.Models.FolderRole.Flagged => "star",
        Magpie.Core.Models.FolderRole.Important => "important",
        _ => "folder",
    };
}

/// <summary>
/// A coloured rounded tile with a line icon (design C1): <c>&lt;svc:IconChip Icon="archive" Size="24" /&gt;</c>.
/// <see cref="Plain"/> draws only the icon, in its colour, without the tile.
/// </summary>
public sealed class IconChip : Border
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(nameof(Icon), typeof(string), typeof(IconChip),
        new PropertyMetadata("folder", (d, _) => ((IconChip)d).Rebuild()));
    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(nameof(Size), typeof(double), typeof(IconChip),
        new PropertyMetadata(24.0, (d, _) => ((IconChip)d).Rebuild()));
    public static readonly DependencyProperty PlainProperty = DependencyProperty.Register(nameof(Plain), typeof(bool), typeof(IconChip),
        new PropertyMetadata(false, (d, _) => ((IconChip)d).Rebuild()));
    /// <summary>Overrides the icon colour (e.g. white on a filled button).</summary>
    /// <summary>Draw in grey whatever the "Colourful icons" setting (Settings preview of that switch).</summary>
    public static readonly DependencyProperty MonoProperty = DependencyProperty.Register(nameof(Mono), typeof(bool), typeof(IconChip),
        new PropertyMetadata(false, (d, _) => ((IconChip)d).Rebuild()));
    public bool Mono { get => (bool)GetValue(MonoProperty); set => SetValue(MonoProperty, value); }

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(IconChip),
        new PropertyMetadata(null, (d, _) => ((IconChip)d).Rebuild()));

    public string Icon { get => (string)GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }
    public bool Plain { get => (bool)GetValue(PlainProperty); set => SetValue(PlainProperty, value); }
    public Brush? Stroke { get => (Brush?)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }

    private readonly ShapePath _path = new()
    {
        StrokeThickness = 2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
    };

    public IconChip()
    {
        VerticalAlignment = VerticalAlignment.Center;
        SnapsToDevicePixels = true;
        var canvas = new Canvas { Width = 24, Height = 24 };
        canvas.Children.Add(_path);
        Child = new Viewbox { Child = canvas, Stretch = Stretch.Uniform };
        Focusable = false;
        IsHitTestVisible = false;
        Loaded += (_, _) => { Icons.Changed += Rebuild; Rebuild(); };
        Unloaded += (_, _) => Icons.Changed -= Rebuild;
        Rebuild();
    }

    private void Rebuild()
    {
        var key = string.IsNullOrEmpty(Icon) ? "folder" : Icon;
        _path.Data = Icons.GeometryOf(key);
        bool? colourful = Mono ? false : null;
        _path.Stroke = Stroke ?? Icons.Fg(key, colourful);
        Width = Height = Size;
        var inner = Plain ? Size : Math.Round(Size * 0.62);
        ((Viewbox)Child).Width = inner;
        ((Viewbox)Child).Height = inner;
        Background = Plain ? System.Windows.Media.Brushes.Transparent : Icons.Bg(key, colourful);
        CornerRadius = new CornerRadius(Plain ? 0 : Math.Round(Size / 3.6));
        _path.StrokeThickness = Plain ? 2 : 2.2;
    }
}
