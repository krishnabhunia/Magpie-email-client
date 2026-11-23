using System.Runtime.InteropServices;
using System.Windows;
using Magpie.Core;
using Magpie.Core.Mail;

namespace Magpie.App.Services;

/// <summary>
/// The monitors of this PC as a window sees them (design OW1). A window's saved place is checked against each monitor,
/// not against the rectangle around all of them: with monitors of different sizes or heights that rectangle has
/// corners that belong to no monitor.
/// </summary>
internal static class Screens
{
    private const int SmXVirtualScreen = 76, SmYVirtualScreen = 77, SmCxVirtualScreen = 78, SmCyVirtualScreen = 79;
    private const uint MonitorInfoPrimary = 1;

    /// <summary>
    /// Every monitor's work area (without the taskbar) in the units of <see cref="Window.Left"/> / <see cref="Window.Top"/>,
    /// the main monitor first. When Windows cannot be asked, the one rectangle around all monitors — what Magpie's other
    /// windows go by.
    /// </summary>
    public static List<EmailWindow.Area> WorkAreas()
    {
        var whole = new EmailWindow.Area(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        try
        {
            // Windows answers in pixels; WPF's own numbers for the same rectangle give the scale between the two.
            double pxLeft = GetSystemMetrics(SmXVirtualScreen), pxTop = GetSystemMetrics(SmYVirtualScreen);
            double pxWidth = GetSystemMetrics(SmCxVirtualScreen), pxHeight = GetSystemMetrics(SmCyVirtualScreen);
            if (pxWidth <= 0 || pxHeight <= 0 || whole.Width <= 0 || whole.Height <= 0) return new() { whole };
            double sx = whole.Width / pxWidth, sy = whole.Height / pxHeight;
            var found = new List<(bool Main, EmailWindow.Area Area)>();
            MonitorEnumProc each = (IntPtr monitor, IntPtr hdc, ref Rect bounds, IntPtr data) =>
            {
                var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                if (GetMonitorInfo(monitor, ref info))
                {
                    var w = info.Work;
                    var area = new EmailWindow.Area(whole.Left + (w.Left - pxLeft) * sx, whole.Top + (w.Top - pxTop) * sy, (w.Right - w.Left) * sx, (w.Bottom - w.Top) * sy);
                    if (area.Width > 0 && area.Height > 0) found.Add(((info.Flags & MonitorInfoPrimary) != 0, area));
                }
                return true;
            };
            var ok = EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, each, IntPtr.Zero);
            GC.KeepAlive(each);
            if (!ok || found.Count == 0) return new() { whole };
            return found.OrderByDescending(f => f.Main).Select(f => f.Area).ToList();
        }
        catch (Exception ex)
        {
            Log.Warn("monitors: " + ex.Message);
            return new() { whole };
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public Rect Monitor; public Rect Work; public uint Flags; }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, ref Rect bounds, IntPtr data);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
