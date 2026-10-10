using System.Windows;
using Magpie.Core;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace Magpie.App.Services;

/// <summary>Notification-area icon (Windows Forms NotifyIcon) with a menu and balloon/toast notifications.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly WF.NotifyIcon _icon;
    private Action? _balloonClick;
    private bool _disposed;

    public event Action? OpenRequested;
    public event Action? ComposeRequested;
    public event Action? SyncRequested;
    public event Action? ExitRequested;

    static TrayIcon()
    {
        try { WF.Application.EnableVisualStyles(); } catch { }
    }

    public TrayIcon()
    {
        var menu = new WF.ContextMenuStrip();
        menu.Items.Add(Item("Open Magpie", () => OpenRequested?.Invoke(), bold: true));
        menu.Items.Add(Item("New message", () => ComposeRequested?.Invoke()));
        menu.Items.Add(Item("Check for mail", () => SyncRequested?.Invoke()));
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add(Item("Exit Magpie", () => ExitRequested?.Invoke()));
        _icon = new WF.NotifyIcon { Icon = LoadIcon(), Text = "Magpie", ContextMenuStrip = menu, Visible = true };
        _icon.MouseClick += (_, e) => { if (e.Button == WF.MouseButtons.Left) OpenRequested?.Invoke(); };
        _icon.BalloonTipClicked += (_, _) => { var a = _balloonClick; _balloonClick = null; a?.Invoke(); };
        _icon.BalloonTipClosed += (_, _) => _balloonClick = null;
    }

    private static WF.ToolStripMenuItem Item(string text, Action onClick, bool bold = false)
    {
        var item = new WF.ToolStripMenuItem(text);
        if (bold) item.Font = new SD.Font(item.Font, SD.FontStyle.Bold);
        item.Click += (_, _) => onClick();
        return item;
    }

    private static SD.Icon LoadIcon()
    {
        try
        {
            var res = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico", UriKind.Absolute));
            if (res != null)
            {
                using var stream = res.Stream;
                return new SD.Icon(stream, new SD.Size(32, 32));
            }
        }
        catch (Exception ex) { Log.Warn("tray icon: " + ex.Message); }
        return SD.SystemIcons.Application;
    }

    public void SetTooltip(string text) => _icon.Text = text.Length > 127 ? text[..127] : text;

    public void ShowBalloon(string title, string text, Action? onClick = null, int timeoutMs = 8000)
    {
        if (_disposed) return;
        _balloonClick = onClick;
        try { _icon.ShowBalloonTip(timeoutMs, title, text.Length > 250 ? text[..250] : text, WF.ToolTipIcon.None); }
        catch (Exception ex) { Log.Warn("balloon: " + ex.Message); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _icon.Visible = false; _icon.Dispose(); } catch { }
    }
}

/// <summary>"Start with Windows" via the per-user Run key.</summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Magpie";
    public static string ExePath => Environment.ProcessPath ?? "";

    public static bool IsEnabled()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(ValueName) is string;
        }
        catch { return false; }
    }

    public static string? Set(bool enabled)
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled) k.SetValue(ValueName, $"\"{ExePath}\" --tray");
            else k.DeleteValue(ValueName, throwOnMissingValue: false);
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }
}
