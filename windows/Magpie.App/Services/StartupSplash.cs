using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using Magpie.Core;
using Path = System.Windows.Shapes.Path;
using Magpie.Core.Settings;

namespace Magpie.App.Services;

public enum SplashChoice { Close, GoBack }

/// <summary>
/// Design SP1-B: the start-up screen. A dark brand strip on the left, and on the right the four things Magpie does before
/// its window opens, ticked off with their times, plus the account chips. It runs on its own UI thread, so it keeps
/// moving while the main thread opens the data folder and database, and it never touches the app's resources (they
/// belong to the main thread and don't exist yet when it appears). Every call is safe when no screen is showing, and
/// a problem in the screen itself is logged and ignored: start-up must never depend on it.
/// </summary>
public sealed class StartupSplash
{
    public static StartupSplash? Current { get; private set; }

    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(3);

    private readonly object _gate = new();
    private readonly SplashPrefs _prefs;
    private readonly string _version;
    private readonly bool _afterUpdate, _restarting, _dark;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly TimeSpan?[] _times = new TimeSpan?[SplashPlan.StepCount];
    private readonly ManualResetEventSlim _ready = new();
    private TimeSpan _stepStarted;
    private int _step;
    private int _accounts;
    private int _suspended;
    private bool _finished, _closeNow;
    private volatile bool _closed;
    private Thread? _thread;
    private Dispatcher? _ui;
    private SplashWindow? _win;

    private StartupSplash(SplashPrefs prefs, string version, bool afterUpdate, bool restarting)
    {
        _prefs = prefs;
        _version = version;
        _afterUpdate = afterUpdate;
        _restarting = restarting;
        _accounts = prefs.Accounts.Count;
        _dark = ThemeManager.Resolve(prefs.Theme, prefs.Theme == ThemeMode.MatchWindows && ThemeManager.WindowsUsesDark());
    }

    /// <summary>Shows the screen at once (called from Program.Main, before the app object exists).</summary>
    public static void Start(SplashPrefs prefs, string version, bool afterUpdate, bool restarting)
    {
        try
        {
            var s = new StartupSplash(prefs, version, afterUpdate, restarting);
            var t = new Thread(s.Run) { IsBackground = true, Name = "Magpie start-up screen" };
            t.SetApartmentState(ApartmentState.STA);
            s._thread = t;
            t.Start();
            Current = s;
        }
        catch (Exception ex) { Log.Warn("start-up screen could not start: " + ex.Message); }
    }

    private void Run()
    {
        try
        {
            _ui = Dispatcher.CurrentDispatcher;
            // Anything that goes wrong in the screen closes it; it is never allowed to reach start-up.
            _ui.UnhandledException += (_, e) =>
            {
                Log.Warn("start-up screen failed: " + e.Exception.Message);
                e.Handled = true;
                _closed = true;
                try { _win?.Close(); } catch { }
                try { _ui.InvokeShutdown(); } catch { }
            };
            bool closeNow, finished, hidden;
            lock (_gate) { closeNow = _closeNow; finished = _finished; hidden = _suspended > 0; }
            if (!closeNow)
            {
                _win = new SplashWindow(this, _dark);
                _win.Place(_prefs);
                Render();
                if (!hidden) _win.Show();
                // Finish may have come before this thread had its dispatcher.
                if (finished) _win.CloseAfter(SplashPlan.HoldBeforeClose(_clock.Elapsed), fade: true);
            }
            _ready.Set();
            if (closeNow) return;
            Dispatcher.Run();
        }
        catch (Exception ex) { Log.Warn("start-up screen failed: " + ex.Message); }
        finally
        {
            // Whatever happened, every later call returns at once instead of waiting on a dead thread.
            _closed = true;
            _ready.Set();
            try { Dispatcher.CurrentDispatcher.InvokeShutdown(); } catch { }
        }
    }

    // ── Called by the main thread ──

    /// <summary>The running step is done; the next one starts. <paramref name="accounts"/> updates the last step's line.</summary>
    public void Next(int accounts = -1)
    {
        lock (_gate)
        {
            if (_finished) return;
            if (accounts >= 0) _accounts = accounts;
            if (_step < SplashPlan.StepCount)
            {
                var now = _clock.Elapsed;
                _times[_step] = now - _stepStarted;
                _stepStarted = now;
                _step++;
            }
        }
        Post(Render);
    }

    /// <summary>The main window has drawn: every step ticks, the screen stays up to 0.8 s in all, then fades. Returns the log line.</summary>
    public string Finish()
    {
        string line;
        lock (_gate)
        {
            if (!_finished)
            {
                var now = _clock.Elapsed;
                while (_step < SplashPlan.StepCount)
                {
                    _times[_step] = now - _stepStarted;
                    _stepStarted = now;
                    _step++;
                }
                _finished = true;
            }
            line = SplashPlan.LogLine(_times, _clock.Elapsed);
        }
        if (Current == this) Current = null;
        Post(() => { Render(); _win?.CloseAfter(SplashPlan.HoldBeforeClose(_clock.Elapsed), fade: true); });
        return line;
    }

    /// <summary>Closes at once (Magpie is closing, or the window starts hidden).</summary>
    public void Close()
    {
        lock (_gate) { _finished = true; _closeNow = true; }
        if (Current == this) Current = null;
        Post(() => _win?.CloseAfter(TimeSpan.Zero, fade: false));
    }

    /// <summary>
    /// Hides the screen while a start-up dialog asks something; shows it again when the last one is disposed. Never
    /// waits for the screen's thread: the count is also read when the window first appears.
    /// </summary>
    public IDisposable Suspend()
    {
        lock (_gate) _suspended++;
        Post(() => { if (Suspended) _win?.Hide(); });
        return new Resume(this);
    }

    private bool Suspended { get { lock (_gate) return _suspended > 0; } }

    private sealed class Resume(StartupSplash owner) : IDisposable
    {
        private bool _done;
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            lock (owner._gate) owner._suspended = Math.Max(0, owner._suspended - 1);
            owner.Post(() => { if (!owner.Suspended) owner._win?.ShowAgain(); });
        }
    }

    /// <summary>
    /// Start-up failed: the running step turns red and the screen shows <paramref name="message"/> with Open log folder,
    /// Close and (after an update) Go back to the previous version. Waits for the user's choice; null when the screen
    /// can't show it (the caller then shows its own message box, and the screen is gone).
    /// </summary>
    public SplashChoice? Fail(string message, bool canGoBack)
    {
        lock (_gate) { _finished = true; _suspended = 0; }
        if (Current == this) Current = null;
        if (!_ready.Wait(CallTimeout) || _closed || _ui == null || _win == null)
        {
            lock (_gate) _closeNow = true;
            Post(() => _win?.CloseAfter(TimeSpan.Zero, fade: false));
            return null;
        }
        var answer = new TaskCompletionSource<SplashChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ui = _ui;
        EventHandler stopped = (_, _) => answer.TrySetResult(SplashChoice.Close);
        try
        {
            ui.ShutdownStarted += stopped;
            var shown = false;
            ui.Invoke(() =>
            {
                if (_win == null || _closed) return;
                _win.ShowError(_step, message, canGoBack, c => answer.TrySetResult(c));
                shown = true;
            }, DispatcherPriority.Send, CancellationToken.None, CallTimeout);
            if (!shown) { Post(() => _win?.CloseAfter(TimeSpan.Zero, fade: false)); return null; }
            // Never wait on a screen that has gone: the thread ending or the window closing answers Close.
            while (!answer.Task.Wait(250))
                if (_closed || _thread is { IsAlive: false }) answer.TrySetResult(SplashChoice.Close);
            var choice = answer.Task.Result;
            Post(() => _win?.CloseAfter(TimeSpan.Zero, fade: false));
            return choice;
        }
        catch (Exception ex)
        {
            Log.Warn("start-up screen could not show the error: " + ex.Message);
            Post(() => _win?.CloseAfter(TimeSpan.Zero, fade: false));
            return null;
        }
        finally
        {
            try { ui.ShutdownStarted -= stopped; } catch { }
        }
    }

    /// <summary>Shows a start-up failure on the screen. False when there is no screen (show the usual message box).</summary>
    public static bool TryReportFailure(string message, Exception error, bool afterUpdate)
    {
        var s = Current;
        if (s == null) return false;
        // The choice is waited for on this thread; a main window already up would look frozen beside it.
        try { if (Application.Current?.MainWindow is { IsVisible: true } main) main.Hide(); } catch { }
        var choice = s.Fail(message, afterUpdate && UpdateService.CanOfferRollback());
        if (choice == null) return false;
        // OfferRollback explains its own failures; only a snapshot that vanished meanwhile needs a word here.
        if (choice == SplashChoice.GoBack && !UpdateService.OfferRollback(error, confirmed: true) && !UpdateService.CanOfferRollback())
            MessageBox.Show(message + "\n\nDetails are in " + Log.FilePath, "Magpie", MessageBoxButton.OK, MessageBoxImage.Error);
        return true;
    }

    // ── Splash thread ──

    internal void MarkClosed() => _closed = true;

    private void Post(Action a)
    {
        try { if (!_closed) _ui?.BeginInvoke(a); }
        catch { }
    }

    internal string Heading => _restarting ? "Restarting Magpie" : _afterUpdate ? "Getting the new version ready" : "Getting your mail ready";
    internal string VersionText => "Version " + _version + (_afterUpdate ? " · just updated" : "");
    internal IReadOnlyList<SplashAccount> Chips => _prefs.ShowAccounts ? _prefs.Accounts : Array.Empty<SplashAccount>();

    private void Render()
    {
        if (_win == null) return;
        int step, accounts;
        TimeSpan?[] times;
        lock (_gate) { step = _step; accounts = _accounts; times = (TimeSpan?[])_times.Clone(); }
        _win.ShowSteps(step, accounts, times);
    }

    internal bool IsFinished { get { lock (_gate) return _finished; } }
}

/// <summary>The start-up screen's window, built in code (no XAML, no app resources) for its own thread.</summary>
internal sealed class SplashWindow : Window
{
    private const double CardWidth = 560, StripWidth = 200, Margin_ = 18;

    private readonly StartupSplash _owner;
    private readonly Palette _p;
    private readonly FontFamily _ui, _display, _mono;
    private readonly StackPanel _steps = new();
    private readonly TextBlock _heading = new();
    private readonly Grid _bar = new() { Height = 3, Margin = new Thickness(0, 4, 0, 0) };
    private readonly StackPanel _slow = new() { Visibility = Visibility.Collapsed };
    private readonly StackPanel _error = new() { Visibility = Visibility.Collapsed };
    private readonly WrapPanel _chips = new();
    private readonly DispatcherTimer _slowTimer;
    private bool _closing;

    private sealed record Palette(Color Bg, Color Text, Color Muted, Color Line, Color Accent, Color Soft, Color Danger, Color OnAccent, Color Strip, Color Cream, Color StripMuted);

    private static readonly Palette Light = new(C("#F7F6F2"), C("#14181C"), C("#5A6068"), C("#DCD8CF"), C("#14606E"), C("#DFE9E9"), C("#B3261E"), C("#FFFFFF"), C("#14181C"), C("#F7F6F2"), C("#B9B6AE"));
    private static readonly Palette Dark = new(C("#1E2328"), C("#E8E6E1"), C("#9AA1A8"), C("#2C3238"), C("#4FA6B5"), C("#1B2A2E"), C("#E0645A"), C("#08191C"), C("#0E1114"), C("#F1EFE9"), C("#9AA1A8"));

    public SplashWindow(StartupSplash owner, bool dark)
    {
        _owner = owner;
        _p = dark ? Dark : Light;
        _ui = Font("IBM Plex Sans", "Segoe UI");
        _display = Font("IBM Plex Serif", "Georgia");
        _mono = new FontFamily("Cascadia Mono, Consolas");

        Title = "Magpie";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        FontFamily = _ui;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        // Stop implicit-style lookups at this window: the app's resources belong to the main thread (and its theme swap).
        Style = new Style(typeof(SplashWindow));
        foreach (var t in new[] { typeof(Border), typeof(Grid), typeof(StackPanel), typeof(DockPanel), typeof(WrapPanel), typeof(TextBlock),
                     typeof(Button), typeof(ContentPresenter), typeof(Viewbox), typeof(Canvas), typeof(Ellipse), typeof(Path), typeof(Rectangle) })
            Resources.Add(t, new Style(t));
        Content = Build();
        Opacity = Animate ? 0 : 1;
        Loaded += (_, _) => { if (Animate) BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140))); };
        KeyDown += (_, e) => { if (e.Key == Key.Escape && _owner.IsFinished) CloseAfter(TimeSpan.Zero, fade: false); };
        Closed += (_, _) => { _slowTimer?.Stop(); _owner.MarkClosed(); Dispatcher.InvokeShutdown(); };

        _slowTimer = new DispatcherTimer { Interval = SplashPlan.SlowAfter };
        _slowTimer.Tick += (_, _) => { _slowTimer.Stop(); if (!_owner.IsFinished) _slow.Visibility = Visibility.Visible; };
        _slowTimer.Start();
    }

    private static bool Animate => SystemParameters.ClientAreaAnimation;

    private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);
    private static SolidColorBrush B(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    private static FontFamily Font(string family, string fallback)
    {
        try
        {
            _ = System.IO.Packaging.PackUriHelper.UriSchemePack;   // registers pack:// before the app object exists
            return new FontFamily(new Uri("pack://application:,,,/"), "./Assets/Fonts/#" + family + ", " + fallback);
        }
        catch { return new FontFamily(fallback); }
    }

    /// <summary>Centred where the main window will open (its saved place, if that's on a screen), else on the main screen.</summary>
    public void Place(SplashPrefs prefs)
    {
        if (Content is FrameworkElement root) root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = (Content as FrameworkElement)?.DesiredSize ?? new Size(CardWidth + 2 * Margin_, 340);
        var vx = SystemParameters.VirtualScreenLeft; var vy = SystemParameters.VirtualScreenTop;
        var vw = SystemParameters.VirtualScreenWidth; var vh = SystemParameters.VirtualScreenHeight;
        double cx = 0, cy = 0;
        var saved = !double.IsNaN(prefs.WindowLeft) && !double.IsNaN(prefs.WindowTop) && prefs.WindowWidth > 100 && prefs.WindowHeight > 100;
        if (saved)
        {
            cx = prefs.WindowLeft + prefs.WindowWidth / 2;
            cy = prefs.WindowTop + prefs.WindowHeight / 2;
            if (cx < vx || cy < vy || cx > vx + vw || cy > vy + vh) saved = false;
        }
        if (!saved)
        {
            var wa = SystemParameters.WorkArea;
            cx = wa.Left + wa.Width / 2;
            cy = wa.Top + wa.Height / 2;
        }
        Left = Math.Round(cx - size.Width / 2);
        Top = Math.Round(cy - size.Height / 2);
    }

    private UIElement Build()
    {
        var card = new Border
        {
            Width = CardWidth,
            CornerRadius = new CornerRadius(10),
            Background = B(_p.Bg),
            BorderBrush = B(_p.Line),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(Margin_),
            Effect = new DropShadowEffect { BlurRadius = 26, ShadowDepth = 6, Direction = 270, Opacity = 0.28, Color = Colors.Black },
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(StripWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        card.Child = grid;

        // Brand strip
        var strip = new Border { Background = B(_p.Strip), CornerRadius = new CornerRadius(9, 0, 0, 9), Padding = new Thickness(22, 22, 20, 22) };
        var stripPanel = new DockPanel { LastChildFill = false };
        var mark = Mark(52);
        DockPanel.SetDock(mark, Dock.Top);
        stripPanel.Children.Add(mark);
        var names = new StackPanel { Margin = new Thickness(0, 40, 0, 0) };
        DockPanel.SetDock(names, Dock.Bottom);
        names.Children.Add(new TextBlock { Text = "Magpie", FontFamily = _display, FontWeight = FontWeights.SemiBold, FontSize = 28, Foreground = B(_p.Cream) });
        names.Children.Add(new TextBlock { Text = _owner.VersionText, FontFamily = _mono, FontSize = 11.5, Foreground = B(_p.StripMuted), Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap });
        stripPanel.Children.Add(names);
        strip.Child = stripPanel;
        grid.Children.Add(strip);

        // Steps side
        var right = new StackPanel { Margin = new Thickness(22, 20, 22, 18) };
        Grid.SetColumn(right, 1);
        _heading.Text = _owner.Heading;
        _heading.FontSize = 14;
        _heading.FontWeight = FontWeights.SemiBold;
        _heading.Foreground = B(_p.Text);
        _heading.Margin = new Thickness(0, 0, 0, 12);
        right.Children.Add(_heading);
        right.Children.Add(_steps);

        var chips = _owner.Chips;
        if (chips.Count > 0)
        {
            _chips.Margin = new Thickness(0, 12, 0, 0);
            foreach (var a in chips.Take(4)) _chips.Children.Add(Chip(a.Email, a.Color));
            if (chips.Count > 4) _chips.Children.Add(Chip($"+{chips.Count - 4} more", null));
            right.Children.Add(_chips);
        }

        _bar.Margin = new Thickness(0, 14, 0, 0);
        _bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0, GridUnitType.Star) });
        _bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var track = new Border { Background = B(_p.Soft), CornerRadius = new CornerRadius(2) };
        Grid.SetColumnSpan(track, 2);
        _bar.Children.Add(track);
        _bar.Children.Add(new Border { Background = B(_p.Accent), CornerRadius = new CornerRadius(2) });
        right.Children.Add(_bar);

        _slow.Margin = new Thickness(0, 12, 0, 0);
        _slow.Orientation = Orientation.Horizontal;
        _slow.Children.Add(new TextBlock { Text = "This is taking longer than usual.", FontSize = 12, Foreground = B(_p.Muted), VerticalAlignment = VerticalAlignment.Center });
        var slowLog = Link("Open log folder", OpenLogFolder);
        slowLog.Margin = new Thickness(10, 0, 0, 0);
        _slow.Children.Add(slowLog);
        right.Children.Add(_slow);

        _error.Margin = new Thickness(0, 12, 0, 0);
        right.Children.Add(_error);
        grid.Children.Add(right);
        return card;
    }

    private FrameworkElement Mark(double size)
    {
        var c = new Canvas { Width = 64, Height = 64 };
        var tile = new Rectangle { Width = 59, Height = 59, RadiusX = 14, RadiusY = 14, Fill = B(C("#14181C")), Stroke = B(Color.FromArgb(0x29, 0xFF, 0xFF, 0xFF)), StrokeThickness = 1 };
        Canvas.SetLeft(tile, 2.5); Canvas.SetTop(tile, 2.5);
        c.Children.Add(tile);
        var env = new Rectangle { Width = 38, Height = 27, RadiusX = 3.5, RadiusY = 3.5, Fill = B(C("#F7F6F2")) };
        Canvas.SetLeft(env, 11); Canvas.SetTop(env, 20);
        c.Children.Add(env);
        c.Children.Add(new Path { Data = Geometry.Parse("M11.6,21.2 L30,35.5 L48.4,21.2 Z"), Fill = B(C("#14606E")) });
        c.Children.Add(new Path { Data = Geometry.Parse("M40,22 L55,12.5 L57.3,16.2 L42.6,25.4 Z"), Fill = B(C("#F7F6F2")) });
        c.Children.Add(new Path { Data = Geometry.Parse("M42.6,25.4 L57.3,16.2 L58.4,18 L43.9,27.2 Z"), Fill = B(C("#4B3F86")) });
        return new Viewbox { Width = size, Height = size, Child = c, HorizontalAlignment = HorizontalAlignment.Left };
    }

    private Border Chip(string text, string? colour)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        if (colour != null)
        {
            Color dot;
            try { dot = C(colour); } catch { dot = _p.Accent; }
            row.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = B(dot), Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
        }
        row.Children.Add(new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeights.Medium, Foreground = B(_p.Muted), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 250 });
        return new Border
        {
            Child = row, BorderBrush = B(_p.Line), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 6, 6),
        };
    }

    // ── Steps ──

    public void ShowSteps(int step, int accounts, IReadOnlyList<TimeSpan?> times)
    {
        if (_closing || _error.Visibility == Visibility.Visible) return;
        _steps.Children.Clear();
        for (var i = 0; i < SplashPlan.StepCount; i++)
        {
            var (now, done) = SplashPlan.Step(i, accounts);
            var state = i < step ? StepState.Done : i == step ? StepState.Running : StepState.Waiting;
            _steps.Children.Add(StepRow(state, state == StepState.Done ? done : now, state == StepState.Done && times[i] is { } t ? SplashPlan.Duration(t) : state == StepState.Running ? "…" : ""));
        }
        var fraction = Math.Clamp(step / (double)SplashPlan.StepCount, 0, 1);
        _bar.ColumnDefinitions[0].Width = new GridLength(fraction, GridUnitType.Star);
        _bar.ColumnDefinitions[1].Width = new GridLength(1 - fraction, GridUnitType.Star);
        if (step >= SplashPlan.StepCount) _slow.Visibility = Visibility.Collapsed;
    }

    private enum StepState { Waiting, Running, Done, Failed }

    private FrameworkElement StepRow(StepState state, string text, string time)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 9) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(StepIcon(state));
        var label = new TextBlock
        {
            Text = text, FontSize = 13, Margin = new Thickness(10, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
            Foreground = B(state switch { StepState.Waiting => _p.Muted, StepState.Failed => _p.Danger, _ => _p.Text }),
            FontWeight = state is StepState.Running or StepState.Failed ? FontWeights.SemiBold : FontWeights.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(label, 1);
        g.Children.Add(label);
        var ms = new TextBlock { Text = time, FontFamily = _mono, FontSize = 11, Foreground = B(_p.Muted), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(ms, 2);
        g.Children.Add(ms);
        return g;
    }

    private FrameworkElement StepIcon(StepState state)
    {
        var g = new Grid { Width = 18, Height = 18, VerticalAlignment = VerticalAlignment.Center };
        switch (state)
        {
            case StepState.Done:
                g.Children.Add(new Ellipse { Fill = B(_p.Accent) });
                g.Children.Add(new Path { Data = Geometry.Parse("M5,9.4 L8,12.2 L13,6.4"), Stroke = B(_p.Bg), StrokeThickness = 2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round });
                break;
            case StepState.Failed:
                g.Children.Add(new Ellipse { Fill = B(_p.Danger) });
                g.Children.Add(new Path { Data = Geometry.Parse("M6,6 L12,12 M12,6 L6,12"), Stroke = B(_p.Bg), StrokeThickness = 2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
                break;
            case StepState.Running:
                var arc = new Path
                {
                    Data = Geometry.Parse("M9,1.75 A7.25,7.25 0 1 1 1.75,9"), Stroke = B(_p.Accent), StrokeThickness = 1.75,
                    StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                    RenderTransformOrigin = new Point(0.5, 0.5), Width = 18, Height = 18, Stretch = Stretch.None,
                };
                var spin = new RotateTransform();
                arc.RenderTransform = spin;
                if (Animate) spin.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(800)) { RepeatBehavior = RepeatBehavior.Forever });
                g.Children.Add(arc);
                break;
            default:
                g.Children.Add(new Ellipse { Stroke = B(_p.Line), StrokeThickness = 1.5 });
                break;
        }
        return g;
    }

    // ── Error ──

    public void ShowError(int step, string message, bool canGoBack, Action<SplashChoice> choose)
    {
        _slowTimer.Stop();
        _slow.Visibility = Visibility.Collapsed;
        _chips.Visibility = Visibility.Collapsed;
        _bar.Visibility = Visibility.Collapsed;
        _heading.Text = "Magpie couldn't start";
        _heading.Foreground = B(_p.Danger);
        var failed = Math.Clamp(step, 0, SplashPlan.StepCount - 1);
        if (failed < _steps.Children.Count)
        {
            var (now, _) = SplashPlan.Step(failed, 0);
            var text = (_steps.Children[failed] as Grid)?.Children.OfType<TextBlock>().FirstOrDefault()?.Text ?? now;
            _steps.Children[failed] = StepRow(StepState.Failed, text, "");
        }
        _error.Children.Clear();
        _error.Children.Add(new TextBlock
        {
            Text = message, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 120,
            FontSize = 12.5, Foreground = B(_p.Text), Margin = new Thickness(0, 2, 0, 12),
        });
        var buttons = new WrapPanel();
        if (canGoBack) buttons.Children.Add(Button("Go back to the previous version", primary: true, () => choose(SplashChoice.GoBack)));
        buttons.Children.Add(Button("Open log folder", primary: false, OpenLogFolder));
        buttons.Children.Add(Button("Close", primary: !canGoBack, () => choose(SplashChoice.Close)));
        _error.Children.Add(buttons);
        _error.Visibility = Visibility.Visible;
        Closed += (_, _) => choose(SplashChoice.Close);   // Alt+F4 must not leave start-up waiting
        if (!IsVisible) Show();
        Activate();
    }

    private Button Button(string text, bool primary, Action click)
    {
        var face = new FrameworkElementFactory(typeof(Border));
        face.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        face.SetValue(Border.PaddingProperty, new Thickness(12, 5, 12, 6));
        face.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding(nameof(Background)) { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        face.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding(nameof(BorderBrush)) { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        face.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        face.AppendChild(content);
        var b = new Button
        {
            Content = text, FontSize = 12.5, Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 8, 6),
            Template = new ControlTemplate(typeof(Button)) { VisualTree = face },
            Background = B(primary ? _p.Accent : _p.Bg), BorderBrush = B(primary ? _p.Accent : _p.Line),
            Foreground = B(primary ? _p.OnAccent : _p.Text), FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal,
            FocusVisualStyle = null,
        };
        b.MouseEnter += (_, _) => b.Opacity = 0.88;
        b.MouseLeave += (_, _) => b.Opacity = 1;
        b.Click += (_, _) => click();
        return b;
    }

    private TextBlock Link(string text, Action click)
    {
        var t = new TextBlock { Text = text, FontSize = 12, Foreground = B(_p.Accent), Cursor = Cursors.Hand, TextDecorations = TextDecorations.Underline, VerticalAlignment = VerticalAlignment.Center };
        t.MouseLeftButtonUp += (_, _) => click();
        return t;
    }

    private static void OpenLogFolder()
    {
        try
        {
            var log = Log.FilePath;
            if (log != null && File.Exists(log)) Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + log + "\"") { UseShellExecute = true });
            else Process.Start(new ProcessStartInfo("explorer.exe", "\"" + AppPaths.Default().Logs + "\"") { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Warn("open log folder: " + ex.Message); }
    }

    // ── Showing and closing ──

    public void ShowAgain()
    {
        if (_closing) return;
        Show();
        if (!_owner.IsFinished && _slow.Visibility != Visibility.Visible) { _slowTimer.Stop(); _slowTimer.Start(); }
    }

    public new void Hide()
    {
        _slowTimer.Stop();
        base.Hide();
    }

    public void CloseAfter(TimeSpan hold, bool fade)
    {
        if (_closing) return;
        _closing = true;
        _slowTimer.Stop();
        void Go()
        {
            if (!fade || !Animate || !IsVisible) { Close(); return; }
            var a = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(160));
            a.Completed += (_, _) => Close();
            BeginAnimation(OpacityProperty, a);
        }
        if (hold <= TimeSpan.Zero) { Go(); return; }
        var t = new DispatcherTimer { Interval = hold };
        t.Tick += (_, _) => { t.Stop(); Go(); };
        t.Start();
    }
}
