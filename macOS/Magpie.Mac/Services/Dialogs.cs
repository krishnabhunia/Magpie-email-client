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

    /// <summary>A question about something that can't be undone: Cancel is the default (Return cancels).</summary>
    public static async Task<bool> ConfirmDanger(string title, string text, string yes, Window? owner = null) =>
        await Ask(title, text, new[] { "Cancel", yes }, owner, defaultButton: 0) == yes;

    /// <summary>Shows <paramref name="buttons"/> and returns the one clicked ("" when closed). The default button
    /// (Return) is the last one unless <paramref name="defaultButton"/> says otherwise.</summary>
    public static async Task<string> Ask(string title, string text, IReadOnlyList<string> buttons, Window? owner = null, int? defaultButton = null)
    {
        var def = defaultButton ?? buttons.Count - 1;
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
            var b = new Button { Content = label, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center, IsDefault = i == def, IsCancel = i == 0 && buttons.Count > 1 };
            if (i == def) b.Classes.Add("accent");
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

    /// <summary>Asks for one line of text; null when cancelled.</summary>
    public static async Task<string?> Prompt(string title, string label, string initial, Window? owner = null)
    {
        owner ??= ActiveWindow();
        string? result = null;
        var box = new TextBox { Text = initial, MinWidth = 340 };
        var dialog = new Window
        {
            Title = title, Width = 420, SizeToContent = SizeToContent.Height, CanResize = false, CanMinimize = false, CanMaximize = false,
            WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen, ShowInTaskbar = false,
        };
        var ok = new Button { Content = "OK", MinWidth = 80, IsDefault = true, HorizontalContentAlignment = HorizontalAlignment.Center };
        ok.Classes.Add("accent");
        var cancel = new Button { Content = "Cancel", MinWidth = 80, IsCancel = true, HorizontalContentAlignment = HorizontalAlignment.Center };
        ok.Click += (_, _) => { result = box.Text ?? ""; dialog.Close(); };
        cancel.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20), Spacing = 10,
            Children =
            {
                new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = 15 },
                new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap },
                box,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, ok } },
            },
        };
        dialog.Opened += (_, _) => { box.Focus(); box.SelectAll(); };
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
