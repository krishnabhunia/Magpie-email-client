using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace Magpie.Mac.Services;

/// <summary>Small sheets for messages and questions (Avalonia has no message box), and the file pickers.</summary>
public static class Dialogs
{
    /// <summary>A message with OK.</summary>
    public static Task Info(string title, string text, Window? owner = null) => Ask(title, text, new[] { "OK" }, owner);

    /// <summary>A message about something that went wrong.</summary>
    public static Task Error(string title, string text, Window? owner = null) => Ask(title, text, new[] { "OK" }, owner);

    /// <summary>Yes / No style question: true for <paramref name="yes"/>.</summary>
    public static async Task<bool> Confirm(string title, string text, string yes = "OK", string no = "Cancel", Window? owner = null) =>
        await Ask(title, text, new[] { no, yes }, owner) == yes;

    /// <summary>Shows <paramref name="buttons"/> (the last one is the default) and returns the one clicked ("" when closed).</summary>
    public static async Task<string> Ask(string title, string text, IReadOnlyList<string> buttons, Window? owner = null)
    {
        owner ??= ActiveWindow();
        var result = "";
        var dialog = new Window
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            CanMinimize = false,
            CanMaximize = false,
            WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            ShowInTaskbar = false,
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        for (var i = 0; i < buttons.Count; i++)
        {
            var label = buttons[i];
            var b = new Button { Content = label, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center, IsDefault = i == buttons.Count - 1, IsCancel = i == 0 && buttons.Count > 1 };
            if (i == buttons.Count - 1) b.Classes.Add("accent");
            b.Click += (_, _) => { result = label; dialog.Close(); };
            row.Children.Add(b);
        }
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = 15, TextWrapping = TextWrapping.Wrap },
                new SelectableTextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxHeight = 360 },
                new Border { Height = 6 },
                row,
            },
        };
        if (owner != null && owner.IsVisible) await dialog.ShowDialog(owner);
        else
        {
            var done = new TaskCompletionSource();
            dialog.Closed += (_, _) => done.TrySetResult();
            dialog.Show();
            await done.Task;
        }
        return result;
    }

    /// <summary>The window in front (a compose window, Settings…) or the main window.</summary>
    public static Window? ActiveWindow()
    {
        if (Application.Current?.ApplicationLifetime is not Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime life) return null;
        return life.Windows.FirstOrDefault(w => w.IsActive) ?? life.MainWindow;
    }

    /// <summary>Choose files to open (attachments, the Google client file).</summary>
    public static async Task<IReadOnlyList<string>> PickFiles(Window owner, string title, bool many, string? jsonOnly = null)
    {
        var options = new FilePickerOpenOptions { Title = title, AllowMultiple = many };
        if (jsonOnly != null) options.FileTypeFilter = new[] { new FilePickerFileType(jsonOnly) { Patterns = new[] { "*.json" } } };
        var files = await owner.StorageProvider.OpenFilePickerAsync(options);
        return files.Select(f => f.TryGetLocalPath()).Where(p => p != null).Select(p => p!).ToList();
    }
}
