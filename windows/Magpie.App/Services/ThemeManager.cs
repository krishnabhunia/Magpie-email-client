using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Magpie.Core;
using Magpie.Core.Settings;
using Microsoft.Win32;

namespace Magpie.App.Services;

/// <summary>
/// Light / dark look (design B1). Swaps Themes/Light.xaml for Themes/Dark.xaml at run time (every brush is a
/// DynamicResource, so open windows repaint), switches icons to their dark colour pair, gives native title bars
/// the dark frame, and follows the Windows "app mode" when the setting is Match Windows.
/// </summary>
public static class ThemeManager
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private static ThemeMode _mode = ThemeMode.MatchWindows;
    private static bool _started;

    /// <summary>True while the dark theme is showing.</summary>
    public static bool IsDark { get; private set; }

    /// <summary>Raised (on the UI thread) after the theme changed, so pages drawn in HTML can be drawn again.</summary>
    public static event Action? Changed;

    /// <summary>The reading pane / editor background while a page loads (WebView2 DefaultBackgroundColor).</summary>
    public static System.Drawing.Color WebBackground => IsDark ? System.Drawing.Color.FromArgb(255, 0x18, 0x1C, 0x20) : System.Drawing.Color.White;

    /// <summary>Call once at start-up, before the first window is shown.</summary>
    public static void Start(ThemeMode mode)
    {
        if (_started) return;
        _started = true;
        _mode = mode;
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) => SetTitleBar((Window)s)));
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        Apply();
    }

    public static void Stop() => SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;

    /// <summary>The setting changed (Settings → Appearance → Theme).</summary>
    public static void SetMode(ThemeMode mode)
    {
        _mode = mode;
        Apply();
    }

    /// <summary>Windows "Choose your app mode": AppsUseLightTheme = 0 means dark.</summary>
    public static bool WindowsUsesDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    public static bool Resolve(ThemeMode mode, bool windowsDark) => mode == ThemeMode.Dark || (mode == ThemeMode.MatchWindows && windowsDark);

    private static void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (_mode != ThemeMode.MatchWindows) return;
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
            Ui.Post(Apply);
    }

    private static void Apply()
    {
        var dark = Resolve(_mode, WindowsUsesDark());
        var app = Application.Current;
        if (app == null) return;
        var merged = app.Resources.MergedDictionaries;
        var index = -1;
        for (var i = 0; i < merged.Count; i++)
        {
            var src = merged[i].Source?.OriginalString ?? "";
            if (src.EndsWith("Themes/Light.xaml", StringComparison.OrdinalIgnoreCase) || src.EndsWith("Themes/Dark.xaml", StringComparison.OrdinalIgnoreCase)) { index = i; break; }
        }
        var loadedDark = index >= 0 && (merged[index].Source?.OriginalString ?? "").EndsWith("Dark.xaml", StringComparison.OrdinalIgnoreCase);
        if (index >= 0 && loadedDark == dark && IsDark == dark) return;
        try
        {
            if (index >= 0 && loadedDark != dark)
                merged[index] = new ResourceDictionary { Source = new Uri($"pack://application:,,,/Magpie;component/Themes/{(dark ? "Dark" : "Light")}.xaml", UriKind.Absolute) };
        }
        catch (Exception ex)
        {
            Log.Error("theme switch failed", ex);
            return;
        }
        IsDark = dark;
        Icons.Dark = dark;
        foreach (Window w in app.Windows) SetTitleBar(w);
        Log.Info("theme: " + (dark ? "dark" : "light") + " (" + _mode + ")");
        Changed?.Invoke();
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Native title bars (Settings, dialogs) follow the theme; Windows 10 1809+ / Windows 11.</summary>
    private static void SetTitleBar(Window w)
    {
        try
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd == IntPtr.Zero) return;
            var on = IsDark ? 1 : 0;
            if (DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int));   // DWMWA_USE_IMMERSIVE_DARK_MODE before Windows 10 20H1
        }
        catch { }
    }
}
