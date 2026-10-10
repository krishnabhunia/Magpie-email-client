using Avalonia.Controls;
using Avalonia.Input;
using Magpie.Core.Mail;

namespace Magpie.Mac.Services;

/// <summary>
/// The Mac menu bar. The Magpie (application) menu is set on the App (About, Settings… ⌘,, Check for Updates…;
/// Avalonia adds Services, Hide, Hide Others, Show All and Quit ⌘Q after them). Every window gets the same
/// File · Edit · View · Message · Window menus, so the ⌘ keys work wherever the keyboard is.
/// </summary>
public static class MacMenus
{
    private static NativeMenuItem Item(string header, Action click, Key? key = null, KeyModifiers mods = KeyModifiers.Meta)
    {
        var item = new NativeMenuItem(header);
        if (key is { } k) item.Gesture = new KeyGesture(k, mods);
        item.Click += (_, _) => click();
        return item;
    }

    private static NativeMenuItem Sub(string header, params NativeMenuItemBase[] items)
    {
        var menu = new NativeMenu();
        foreach (var i in items) menu.Add(i);
        return new NativeMenuItem(header) { Menu = menu };
    }

    private static NativeMenuItemSeparator Sep() => new();

    private static ViewModels.MainViewModel? Main => AppServices.Main;

    /// <summary>The items of the Magpie menu (before Avalonia's Services · Hide · Quit).</summary>
    public static NativeMenu AppMenu()
    {
        var m = new NativeMenu();
        m.Add(Item("About Magpie", () => Views.SettingsWindow.Open("About")));
        m.Add(Sep());
        m.Add(Item("Settings…", () => Views.SettingsWindow.Open(), Key.OemComma));
        m.Add(Item("Check for Updates…", () =>
        {
            Views.SettingsWindow.Open("Updates");
            _ = AppServices.Updates?.CheckNowAsync();
        }));
        return m;
    }

    /// <summary>File · Edit · View · Message · Window for <paramref name="window"/>.</summary>
    public static NativeMenu ForWindow(Window window)
    {
        var bar = new NativeMenu();
        bar.Add(Sub("File",
            Item("New Message", () => Views.ComposeWindow.Open(ComposeMode.New, null), Key.N),
            Sep(),
            Item("Add Account…", () => Views.AddAccountWindow.Open(null)),
            Sep(),
            Item("Close Window", window.Close, Key.W)));
        bar.Add(Sub("Edit",
            Item("Undo", () => EditCommands.Run("undo"), Key.Z),
            Item("Redo", () => EditCommands.Run("redo"), Key.Z, KeyModifiers.Meta | KeyModifiers.Shift),
            Sep(),
            Item("Cut", () => EditCommands.Run("cut"), Key.X),
            Item("Copy", () => EditCommands.Run("copy"), Key.C),
            Item("Paste", () => EditCommands.Run("paste"), Key.V),
            Item("Select All", () => EditCommands.Run("selectAll"), Key.A),
            Sep(),
            Item("Find", () => ShowMain()?.FocusSearch(), Key.F)));
        bar.Add(Sub("View",
            Item("All Inboxes", () => { if (ShowMain() != null && Main != null) Main.Current = Main.Sidebar.FirstOrDefault(); }, Key.D1),
            Item("Get New Mail", () => AppServices.Engine.SyncNow(), Key.N, KeyModifiers.Meta | KeyModifiers.Shift),
            Sep(),
            Item("Show Pictures in This Conversation", () => Main?.Reader.LoadImagesCommand.Execute(null))));
        bar.Add(Sub("Message",
            Item("Reply", () => Main?.Reader.ReplyCommand.Execute(null), Key.R),
            Item("Reply All", () => Main?.Reader.ReplyAllCommand.Execute(null), Key.R, KeyModifiers.Meta | KeyModifiers.Shift),
            Item("Forward", () => Main?.Reader.ForwardCommand.Execute(null), Key.F, KeyModifiers.Meta | KeyModifiers.Shift),
            Sep(),
            Item("Archive", () => { if (!Typing()) Main?.Reader.ArchiveCommand.Execute(null); }, Key.A, KeyModifiers.Meta | KeyModifiers.Control),
            // ⌘⌫ (as Finder's Move to Trash), so ⌫ alone still deletes text in the search and address fields.
            Item("Delete", () => { if (!Typing()) Main?.Reader.DeleteCommand.Execute(null); }, Key.Back),
            Sep(),
            Item("Mark as Read / Unread", () => Main?.Reader.ToggleReadCommand.Execute(null), Key.U, KeyModifiers.Meta | KeyModifiers.Shift),
            Item("Pin / Unpin", () => Main?.Reader.TogglePinCommand.Execute(null), Key.L, KeyModifiers.Meta | KeyModifiers.Shift)));
        bar.Add(Sub("Window",
            Item("Minimize", () => window.WindowState = WindowState.Minimized, Key.M),
            Item("Zoom", () => window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized),
            Sep(),
            Item("Magpie", () => ShowMain(), Key.D0)));
        return bar;
    }

    /// <summary>The keyboard is in a text field or the compose editor: ⌘⌫ there must not delete a conversation.</summary>
    private static bool Typing()
    {
        var w = Dialogs.ActiveWindow();
        return w is not MainWindow || w.FocusManager?.GetFocusedElement() is TextBox || (WebSurface.Focused != null && WebSurface.Focused != (w as MainWindow)?.ReaderSurface);
    }

    /// <summary>Brings the main window back (it hides rather than closes, the Mac way).</summary>
    public static MainWindow? ShowMain()
    {
        if (AppServices.MainWindow is not MainWindow w) return null;
        if (!w.IsVisible) w.Show();
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Activate();
        return w;
    }
}
