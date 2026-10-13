using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Magpie.App.Services;
using Magpie.App.ViewModels;
using Magpie.Core;
using Magpie.Core.Models;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Magpie.App.Views;

/// <summary>Design HM1 (S3): a conversation in its own window, to keep it open beside the list.</summary>
public sealed class ThreadWindow : Window
{
    private readonly MainViewModel _main;
    private readonly ThreadRow _row;
    private readonly ThreadViewModel _reader = new();
    private readonly WebView2 _web = new();
    private readonly ReaderHover _hover;
    private bool _ready;
    private readonly ReaderPresenter _presenter = new();

    public static void Open(MainViewModel main, ThreadRow row)
    {
        var w = new ThreadWindow(main, row);
        w.Show();
        w.Activate();
    }

    private ThreadWindow(MainViewModel main, ThreadRow row)
    {
        _main = main;
        _row = row;
        Style = (Style)Application.Current.FindResource("Window.Dialog");
        ResizeMode = ResizeMode.CanResize;
        Width = 860;
        Height = 760;
        MinWidth = 480;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        DataContext = _reader;
        SetBinding(TitleProperty, new Binding(nameof(ThreadViewModel.Subject)) { StringFormat = "{0} — Magpie" });

        var root = new DockPanel();
        var head = new StackPanel { Margin = new Thickness(24, 14, 24, 8) };
        var subject = new TextBlock { FontSize = 19, TextWrapping = TextWrapping.Wrap, Focusable = true };
        subject.SetResourceReference(StyleProperty, "Text.Serif");
        subject.SetBinding(TextBlock.TextProperty, new Binding(nameof(ThreadViewModel.Subject)));
        var meta = new TextBlock { FontSize = 12, Margin = new Thickness(0, 3, 0, 8) };
        meta.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text.Secondary");
        meta.SetBinding(TextBlock.TextProperty, new Binding(nameof(ThreadViewModel.Meta)));
        var buttons = new WrapPanel();
        buttons.Children.Add(MakeButton("Reply", "Button.Primary", () => _reader.ReplyCommand.Execute(null)));
        buttons.Children.Add(MakeButton("Reply all", "Button.Secondary", () => _reader.ReplyAllCommand.Execute(null)));
        buttons.Children.Add(MakeButton("Forward", "Button.Secondary", () => _reader.ForwardCommand.Execute(null)));
        buttons.Children.Add(MakeButton("Archive", "Button.Secondary", () => _reader.ArchiveCommand.Execute(null)));
        buttons.Children.Add(MakeButton("Delete", "Button.Secondary", () => _reader.DeleteCommand.Execute(null)));
        head.Children.Add(subject);
        head.Children.Add(meta);
        head.Children.Add(buttons);
        var headBorder = new Border { Child = head, BorderThickness = new Thickness(0, 0, 0, 1) };
        headBorder.SetResourceReference(Border.BorderBrushProperty, "Brush.Divider");
        DockPanel.SetDock(headBorder, Dock.Top);
        root.Children.Add(headBorder);
        _web.DefaultBackgroundColor = ThemeManager.WebBackground;
        root.Children.Add(_web);
        Content = root;

        _hover = new ReaderHover(this, main, _reader, js => _ready ? _web.CoreWebView2.ExecuteScriptAsync(js) : Task.CompletedTask);
        SubjectCard? card = null;
        card = new SubjectCard(subject, () => _hover.SubjectContent(ownWindow: true), (id, anchor) => _hover.RunSubject(id, anchor, card!));

        _reader.PageReady += page => Ui.Post(() => _presenter.Show(page));
        _reader.Loading += _presenter.Loading;
        _reader.ThreadRemoved += () => Ui.Post(Close);
        Action redraw = () => Ui.Post(() => { _web.DefaultBackgroundColor = ThemeManager.WebBackground; _reader.Redraw(); });
        ThemeManager.Changed += redraw;
        Closed += (_, _) => { ThemeManager.Changed -= redraw; _reader.Clear(); };
        Loaded += async (_, _) => await InitAsync();
    }

    private Button MakeButton(string text, string style, Action click)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 0, 8, 4), MinHeight = 30, Padding = new Thickness(14, 4, 14, 4) };
        b.SetResourceReference(StyleProperty, style);
        b.Click += (_, _) => click();
        return b;
    }

    private async Task InitAsync()
    {
        try
        {
            if (!await WebHost.InitAsync(_web, scripts: true))
            {
                Ui.Error("Magpie", "The reading pane needs Microsoft Edge WebView2.", this);
                Close();
                return;
            }
            var core = _web.CoreWebView2;
            core.NavigationStarting += (_, e) =>
            {
                if (WebHost.IsOwnPage(e.Uri)) return;
                e.Cancel = true;
                _reader.OnLink(e.Uri);
            };
            core.NewWindowRequested += (_, e) => { e.Handled = true; _reader.OnLink(e.Uri); };
            core.WebMessageReceived += OnWebMessage;
            core.ContextMenuRequested += (_, e) =>
            {
                foreach (var item in e.MenuItems.ToList())
                    if (item.Name is "back" or "forward" or "reload" or "saveAs" or "print" or "inspectElement" or "other")
                        e.MenuItems.Remove(item);
            };
            _ready = true;
            _presenter.Attach(core);
            _reader.Show(_row, _main);
        }
        catch (Exception ex)
        {
            Log.Error("conversation window", ex);
            Close();
        }
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.TryGetWebMessageAsString());
            _hover.OnMessage(doc.RootElement);
        }
        catch (Exception ex) { Log.Warn("web message: " + ex.Message); }
    }
}
