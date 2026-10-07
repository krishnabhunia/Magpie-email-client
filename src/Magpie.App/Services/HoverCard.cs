using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace Magpie.App.Services;

/// <summary>
/// Design DD1 (H2, R2): a small card that opens when the mouse rests on an element (after the hover delay in
/// Settings → Appearance) and stays while the mouse is on the element or the card. One card is shared by every element
/// attached to it; <c>build</c> returns null when that element has nothing to show.
/// </summary>
public sealed class HoverCard
{
    private readonly Func<FrameworkElement, UIElement?> _build;
    private readonly Popup _popup = new() { StaysOpen = true, AllowsTransparency = true, PopupAnimation = PopupAnimation.Fade, Placement = PlacementMode.Bottom, VerticalOffset = 2 };
    private readonly DispatcherTimer _openTimer = new();
    private readonly DispatcherTimer _closeTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private FrameworkElement? _pending;
    private FrameworkElement? _target;

    public HoverCard(Func<FrameworkElement, UIElement?> build)
    {
        _build = build;
        _openTimer.Tick += (_, _) => { _openTimer.Stop(); if (_pending is { IsMouseOver: true } p) Show(p); };
        _closeTimer.Tick += (_, _) => { _closeTimer.Stop(); if (!_popup.IsMouseOver && _target?.IsMouseOver != true) Close(); };
        _popup.MouseEnter += (_, _) => _closeTimer.Stop();
        _popup.MouseLeave += (_, _) => _closeTimer.Start();
        _popup.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            var back = _target;
            Close();
            if (back is { Focusable: true }) back.Focus();
        };
    }

    public bool IsOpen => _popup.IsOpen;

    /// <summary>Opens the card for <paramref name="target"/> after the hover delay (each element is attached once).</summary>
    public void Attach(FrameworkElement target)
    {
        if (ReferenceEquals(target.GetValue(AttachedProperty), this)) return;
        target.SetValue(AttachedProperty, this);
        target.MouseEnter += (_, _) =>
        {
            _closeTimer.Stop();
            if (_popup.IsOpen && ReferenceEquals(_target, target)) return;
            _pending = target;
            _openTimer.Interval = TimeSpan.FromMilliseconds(AppServices.Engine.Config.Appearance.FolderHover.DelayMs);
            _openTimer.Start();
        };
        target.MouseLeave += (_, _) =>
        {
            if (ReferenceEquals(_pending, target)) _openTimer.Stop();
            if (_popup.IsOpen && ReferenceEquals(_target, target)) _closeTimer.Start();
        };
        target.Unloaded += (_, _) => { if (ReferenceEquals(_target, target)) Close(); };
    }

    private static readonly DependencyProperty AttachedProperty =
        DependencyProperty.RegisterAttached("HoverCardAttached", typeof(HoverCard), typeof(HoverCard));

    /// <summary>Opens at once (a click on the element, or the keyboard: <paramref name="focus"/> moves into the card so
    /// Tab and Esc work there).</summary>
    public void Show(FrameworkElement target, bool focus = false)
    {
        _openTimer.Stop();
        if (_build(target) is not { } content) { Close(); return; }
        var card = new Border
        {
            Child = content, MaxWidth = 340, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 10, 14, 12),
            Margin = new Thickness(8), Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.18, Color = Colors.Black },
        };
        card.SetResourceReference(Border.BackgroundProperty, "Brush.Card.Background");
        card.SetResourceReference(Border.BorderBrushProperty, "Brush.Card.Border");
        _target = target;
        _popup.PlacementTarget = target;
        _popup.Child = card;
        _popup.IsOpen = true;
        if (focus && FirstButton(content) is { } first)
            card.Dispatcher.BeginInvoke(() => first.Focus(), DispatcherPriority.Input);
    }

    private static Button? FirstButton(object node)
    {
        if (node is Button b) return b;
        if (node is not DependencyObject d) return null;
        foreach (var child in LogicalTreeHelper.GetChildren(d))
            if (FirstButton(child) is { } found) return found;
        return null;
    }

    public void Close()
    {
        _openTimer.Stop();
        _closeTimer.Stop();
        _popup.IsOpen = false;
        _target = null;
    }
}
