using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace Magpie.App.Services;

/// <summary>Progress remains reachable for older versions, whose title-bar update button is intentionally hidden.</summary>
public sealed class UpdateProgressWindow : Window
{
    private bool _finished;

    public UpdateProgressWindow(UpdateService service)
    {
        Title = "Magpie · Changing version";
        Width = 540; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "Brush.Window.Background");
        SetResourceReference(ForegroundProperty, "Brush.Text.Primary");
        DataContext = service;
        var panel = new StackPanel { Margin = new Thickness(24) };
        var title = new TextBlock { FontWeight = FontWeights.SemiBold, FontSize = 18, TextWrapping = TextWrapping.Wrap };
        title.SetBinding(TextBlock.TextProperty, new Binding(nameof(UpdateService.Title)));
        var detail = new TextBlock { Margin = new Thickness(0, 12, 0, 16), TextWrapping = TextWrapping.Wrap };
        detail.SetBinding(TextBlock.TextProperty, new Binding(nameof(UpdateService.Detail)));
        var progress = new ProgressBar { Maximum = 1, Height = 8, Margin = new Thickness(0, 0, 0, 16) };
        progress.SetBinding(ProgressBar.ValueProperty, new Binding(nameof(UpdateService.Progress)));
        progress.SetBinding(ProgressBar.IsIndeterminateProperty, new Binding(nameof(UpdateService.IsPreparing)));
        var cancel = new Button { Content = "Cancel", HorizontalAlignment = HorizontalAlignment.Right };
        cancel.SetResourceReference(StyleProperty, "Button.Secondary");
        cancel.SetBinding(Button.CommandProperty, new Binding(nameof(UpdateService.CancelDownloadCommand)));
        cancel.SetBinding(IsEnabledProperty, new Binding(nameof(UpdateService.CanCancel)));
        panel.Children.Add(title); panel.Children.Add(detail); panel.Children.Add(progress); panel.Children.Add(cancel);
        Content = panel;
        Closing += (_, e) =>
        {
            if (_finished) return;
            e.Cancel = true;
            if (service.CanCancel) service.CancelDownloadCommand.Execute(null);
        };
    }

    public void Finish() { _finished = true; Close(); }
}
