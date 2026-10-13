using System.Runtime.InteropServices;
using Magpie.Core;

namespace Magpie.App.Services;

/// <summary>
/// Design I1: lets the installer update a running Magpie. The installer closes Magpie through Windows' Restart
/// Manager (Magpie closes as it does when Windows shuts down: unsent messages are kept), installs, and starts it
/// again — Windows only restarts programs that registered here. Only for updates: not after a crash, hang or reboot.
/// </summary>
public static class RestartRegistration
{
    private const int RestartNoCrash = 1, RestartNoHang = 2, RestartNoReboot = 8;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterApplicationRestart(string? commandLine, int flags);

    private static bool? _inTray;

    /// <summary>Call when the main window is shown or hidden: a Magpie that sat in the notification area comes back there.</summary>
    public static void Update(bool inTray)
    {
        if (_inTray == inTray) return;
        _inTray = inTray;
        try
        {
            var hr = RegisterApplicationRestart(inTray ? "--tray" : null, RestartNoCrash | RestartNoHang | RestartNoReboot);
            if (hr != 0) Log.Warn($"restart registration failed (0x{hr:X8})");
        }
        catch (Exception ex) { Log.Warn("restart registration: " + ex.Message); }
    }
}
