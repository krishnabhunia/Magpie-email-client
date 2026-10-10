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
    // The shortcuts of the menus being built (see ForWindow / AppMenu), so a ⌘ key pressed inside a web page can run
    // the same command (see KeyForwardScript and TryRunKey).
    private static List<(KeyGesture Gesture, Action Run)>? _collect;
    private static readonly List<(KeyGesture Gesture, Action Run)> AppShortcuts = new();
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Window, List<(KeyGesture Gesture, Action Run)>> WindowShortcuts = new();

    private static NativeMenuItem Item(string header, Action click, Key? key = null, KeyModifiers mods = KeyModifiers.Meta)
    {
        var item = new NativeMenuItem(header);
        if (key is { } k)
        {
            item.Gesture = new KeyGesture(k, mods);
            _collect?.Add((item.Gesture, click));
        }
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
        AppShortcuts.Clear();
        _collect = AppShortcuts;
        try { return BuildAppMenu(); } finally { _collect = null; }
    }

    private static NativeMenu BuildAppMenu()
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

    /// <summary>Gives <paramref name="window"/> its menu bar, once. Avalonia binds the native menu to this first
    /// NativeMenu instance: setting another one later (even null and back) throws "The menu being updated does not
    /// match" and ends the app — found on a real Mac. Known glitch (Mac, after the update restart): only the Magpie
    /// menu shows until another window has been active once.</summary>
    public static void Attach(Window window) => NativeMenu.SetMenu(window, ForWindow(window));

    /// <summary>File · Edit · View · Message · Window for <paramref name="window"/>.</summary>
    public static NativeMenu ForWindow(Window window)
    {
        var list = new List<(KeyGesture Gesture, Action Run)>();
        _collect = list;
        try { return BuildForWindow(window); }
        finally { _collect = null; WindowShortcuts.AddOrUpdate(window, list); }
    }

    /// <summary>
    /// Put into Magpie's own pages (compose editor, reading pane): while a web page has the keyboard, macOS hands ⌘ keys
    /// to the page instead of the menu bar — found on a real Mac, where ⌘W did nothing in the compose editor. The page
    /// sends them here instead ({t:"menukey"}), except the keys the page itself uses (copy, paste, cut, select all,
    /// undo / redo, bold, italic, underline, link) and ⌘↩.
    /// </summary>
    public const string KeyForwardScript = """
<script>
(function(){
  var own = {c:1, v:1, x:1, a:1, z:1, b:1, i:1, u:1, k:1};
  document.addEventListener('keydown', function(e){
    if (!e.metaKey) return;
    var k = (e.key || '').toLowerCase();
    if (k.length !== 1) return;
    if (own[k] && !e.ctrlKey && !e.altKey && !(e.shiftKey && k !== 'z')) return;
    e.preventDefault();
    try { window.chrome.webview.postMessage(JSON.stringify({t:'menukey', k:k, s:e.shiftKey, c:e.ctrlKey, a:e.altKey})); } catch(_) {}
  }, true);
})();
</script>
""";

    /// <summary>Adds <see cref="KeyForwardScript"/> to a page.</summary>
    public static string WithKeyForwarding(string html)
    {
        var i = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        return i < 0 ? html + KeyForwardScript : html.Insert(i, KeyForwardScript);
    }

    /// <summary>Runs the menu command for a ⌘ key a page sent (see <see cref="KeyForwardScript"/>); true when one ran.</summary>
    public static bool TryRunKey(Window window, string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (!r.TryGetProperty("t", out var t) || t.GetString() != "menukey") return false;
            var k = r.TryGetProperty("k", out var kv) ? kv.GetString() ?? "" : "";
            var mods = KeyModifiers.Meta;
            if (r.TryGetProperty("s", out var sv) && sv.GetBoolean()) mods |= KeyModifiers.Shift;
            if (r.TryGetProperty("c", out var cv) && cv.GetBoolean()) mods |= KeyModifiers.Control;
            if (r.TryGetProperty("a", out var av) && av.GetBoolean()) mods |= KeyModifiers.Alt;
            if (KeyFor(k) is not { } key) return true;   // a menukey message, but nothing to run
            var run = Find(WindowShortcuts.TryGetValue(window, out var list) ? list : null, key, mods) ?? Find(AppShortcuts, key, mods);
            if (run != null) Avalonia.Threading.Dispatcher.UIThread.Post(run);
            return true;
        }
        catch (Exception ex) { Magpie.Core.Log.Warn("menu key from page: " + ex.Message); return false; }
    }

    private static Action? Find(List<(KeyGesture Gesture, Action Run)>? list, Key key, KeyModifiers mods)
    {
        if (list == null) return null;
        foreach (var (g, run) in list)
            if (g.Key == key && g.KeyModifiers == mods) return run;
        return null;
    }

    internal static Key? KeyFor(string k)
    {
        if (k.Length != 1) return null;
        var c = char.ToUpperInvariant(k[0]);
        if (c is >= 'A' and <= 'Z') return Enum.Parse<Key>(c.ToString());
        if (c is >= '0' and <= '9') return Enum.Parse<Key>("D" + c);
        return c switch { ',' => Key.OemComma, '.' => Key.OemPeriod, _ => null };
    }

    private static NativeMenu BuildForWindow(Window window)
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
