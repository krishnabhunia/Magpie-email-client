using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Magpie.Core.Mail;

namespace Magpie.App.Services;

/// <summary>
/// Design HM1: the card that opens when the mouse rests on the subject (S1–S9), or on right-click / the Menu key.
/// It stays while the mouse is on the subject or the card; moving away or Esc closes it.
/// </summary>
public sealed class SubjectCard
{
    public sealed record Content(string Title, string Sub, IReadOnlyList<HoverOption> Items);

    private readonly FrameworkElement _target;
    private readonly Func<Content?> _build;
    private readonly Action<string, FrameworkElement> _run;
    private readonly Popup _popup;
    private readonly DispatcherTimer _openTimer = new();
    private readonly DispatcherTimer _closeTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private TextBlock? _note;

    public SubjectCard(FrameworkElement target, Func<Content?> build, Action<string, FrameworkElement> run)
    {
        _target = target;
        _build = build;
        _run = run;
        _popup = new Popup
        {
            PlacementTarget = target, Placement = PlacementMode.Bottom, StaysOpen = true, AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade, VerticalOffset = 2,
        };
        _openTimer.Tick += (_, _) => { _openTimer.Stop(); Show(keyboard: false); };
        _closeTimer.Tick += (_, _) => { _closeTimer.Stop(); if (!_popup.IsMouseOver && !_target.IsMouseOver) Close(); };

        target.MouseEnter += (_, _) =>
        {
            _closeTimer.Stop();
            if (_popup.IsOpen) return;
            _openTimer.Interval = TimeSpan.FromMilliseconds(AppServices.Engine.Config.Appearance.FolderHover.DelayMs);
            _openTimer.Start();
        };
        target.MouseLeave += (_, _) => { _openTimer.Stop(); if (_popup.IsOpen) _closeTimer.Start(); };
        target.MouseRightButtonUp += (_, e) => { e.Handled = true; Show(keyboard: false); };
        target.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Apps || (e.Key == Key.F10 && Keyboard.Modifiers == ModifierKeys.Shift) || e.Key == Key.Enter)
            {
                e.Handled = true;
                Show(keyboard: true);
            }
        };
        _popup.MouseEnter += (_, _) => _closeTimer.Stop();
        _popup.MouseLeave += (_, _) => _closeTimer.Start();
        _popup.KeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); _target.Focus(); } };
        target.Unloaded += (_, _) => Close();
        // A popup stays on top of other apps: close it when Magpie's window loses focus or moves.
        target.Loaded += (_, _) =>
        {
            if (Window.GetWindow(target) is not { } w || _hooked) return;
            _hooked = true;
            w.Deactivated += (_, _) => { if (!_popup.IsKeyboardFocusWithin) Close(); };
            w.LocationChanged += (_, _) => Close();
        };
    }

    private bool _hooked;

    public void Close()
    {
        _openTimer.Stop();
        _closeTimer.Stop();
        _popup.IsOpen = false;
    }

    /// <summary>A short line at the bottom of the card saying what happened ("Copied the subject").</summary>
    public void Note(string text)
    {
        if (_note == null) return;
        _note.Text = text;
        _note.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Show(bool keyboard)
    {
        if (_build() is not { } content) return;
        var panel = new StackPanel();
        var head = new StackPanel { Margin = new Thickness(14, 8, 14, 10) };
        var title = new TextBlock { Text = content.Title, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        var sub = new TextBlock { Text = content.Sub, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) };
        sub.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text.Secondary");
        head.Children.Add(title);
        head.Children.Add(sub);
        var headBorder = new Border { Child = head, BorderThickness = new Thickness(0, 0, 0, 1), Margin = new Thickness(0, 0, 0, 4) };
        headBorder.SetResourceReference(Border.BorderBrushProperty, "Brush.Divider");
        panel.Children.Add(headBorder);

        Button? first = null;
        foreach (var item in content.Items)
        {
            var b = new Button
            {
                Content = item.Label, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(14, 6, 14, 6),
                BorderThickness = new Thickness(0), MinHeight = 0,
            };
            b.SetResourceReference(FrameworkElement.StyleProperty, "Button.Ghost");
            b.HorizontalAlignment = HorizontalAlignment.Stretch;
            var id = item.Id;
            b.Click += (_, _) => _run(id, _target);
            AutomationSetName(b, item.Label);
            panel.Children.Add(b);
            first ??= b;
        }
        _note = new TextBlock { FontSize = 12, Margin = new Thickness(14, 6, 14, 2), Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
        _note.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Accent.Text");
        panel.Children.Add(_note);

        var card = new Border
        {
            Child = panel, Width = 300, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(0, 6, 0, 6),
            Margin = new Thickness(8), Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.18, Color = Colors.Black },
        };
        card.SetResourceReference(Border.BackgroundProperty, "Brush.Card.Background");
        card.SetResourceReference(Border.BorderBrushProperty, "Brush.Card.Border");
        KeyboardNavigation.SetDirectionalNavigation(panel, KeyboardNavigationMode.Cycle);
        _popup.Child = card;
        _popup.IsOpen = true;
        if (keyboard && first != null) Dispatcher.CurrentDispatcher.BeginInvoke(() => first.Focus(), DispatcherPriority.Input);
    }

    private static void AutomationSetName(DependencyObject d, string name) => System.Windows.Automation.AutomationProperties.SetName(d, name);
}
