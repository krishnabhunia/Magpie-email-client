using System.Windows;
using System.Windows.Media;
using Magpie.App.Services;
using Magpie.Core;
using Magpie.Core.Mail;

namespace Magpie.App.Views;

/// <summary>One sender waiting at the door (design B7).</summary>
public sealed class GateRow
{
    public GateRow(GateSender s)
    {
        Address = s.Address;
        Name = string.IsNullOrWhiteSpace(s.Name) ? s.Address : s.Name.Trim().Trim('"');
        Subject = string.IsNullOrWhiteSpace(s.FirstSubject) ? "(no subject)" : s.FirstSubject;
        Meta = (s.Count == 1 ? "1 email" : $"{s.Count} emails") + " · last " + HtmlRenderer.FriendlyDate(s.Latest, DateTimeOffset.Now);
        Initials = HtmlRenderer.Initials(Name);
        AvatarBrush = Icons.Brush(Palette[Math.Abs(StringComparer.OrdinalIgnoreCase.GetHashCode(Address)) % Palette.Length]);
    }

    private static readonly string[] Palette = { "#2563EB", "#B91C1C", "#EA580C", "#6D28D9", "#0F766E", "#A21CAF", "#334155", "#0369A1", "#15803D", "#B45309" };

    public string Address { get; }
    public string Name { get; }
    public string Subject { get; }
    public string Meta { get; }
    public string Initials { get; }
    public Brush AvatarBrush { get; }
    public string AllowLabel => "Allow " + Address;
    public string BlockLabel => "Block " + Address;
}

/// <summary>Gatekeeper review (design B7): Allow or Block each new sender.</summary>
public partial class GatekeeperWindow : Window
{
    private static GatekeeperWindow? _open;
    private readonly MailEngine _e = AppServices.Engine;

    public static void Open()
    {
        if (_open != null) { _open.Activate(); return; }
        var w = new GatekeeperWindow();
        if (Application.Current.MainWindow is { IsVisible: true } owner) w.Owner = owner;
        else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _open = w;
        w.Closed += (_, _) => { _open = null; w._e.GateChanged -= w.OnGateChanged; };
        w.Show();
    }

    public GatekeeperWindow()
    {
        InitializeComponent();
        _e.GateChanged += OnGateChanged;
        Refresh();
    }

    private void OnGateChanged() => Ui.Post(Refresh);

    private void Refresh()
    {
        var rows = _e.GateSenders().Select(s => new GateRow(s)).ToList();
        Senders.ItemsSource = rows;
        Heading.Text = rows.Count switch
        {
            0 => "Nobody is waiting",
            1 => "1 new sender wants to reach you",
            _ => $"{rows.Count} new senders want to reach you",
        };
    }

    private void OnAllow(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: GateRow r }) Run(() => _e.AllowSender(r.Address));
    }

    private void OnBlock(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: GateRow r }) Run(() => _e.BlockSender(r.Address));
    }

    private void Run(Action a)
    {
        try { a(); }
        catch (Exception ex) { Log.Error("gatekeeper", ex); Ui.Error("Magpie", ex.Message); }
        Refresh();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
