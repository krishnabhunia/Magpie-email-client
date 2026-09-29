using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Magpie.Core;
using Magpie.Core.Updates;

namespace Magpie.App.Services;

public enum UpdateState { Idle, Checking, UpToDate, Available, Downloading, Ready, Error }

/// <summary>
/// Updates from GitHub Releases (design U1): checks at start and once a day, downloads in the background,
/// verifies the SHA-256, and swaps Magpie.exe on "Restart now" (the old EXE is kept as Magpie.previous.exe).
/// </summary>
public sealed partial class UpdateService : ObservableObject
{
    public const string StartedEvent = @"Local\Magpie.Mail.UpdateStarted";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly UpdateClient _client = new(Http);
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromHours(1) };
    private CancellationTokenSource? _cts;
    private bool _restarting;
    private bool _startCheckDone;
    /// <summary>"Later": not offered again by automatic checks before this time.</summary>
    private DateTimeOffset _laterUntil = DateTimeOffset.MinValue;
    private ReleaseInfo? _release;
    private string? _downloaded;

    public static AppVersion Current { get; } = ReadCurrent();
    /// <summary>%LOCALAPPDATA%\Magpie\updates — not the roaming profile (the EXE is ~80 MB); next to the EXE when portable.</summary>
    public static string DownloadFolder => AppPaths.Default().Updates;
    /// <summary>Auto update (design A1) put the new EXE in place already; it runs from the next start.</summary>
    private bool _installed;

    private static AppVersion ReadCurrent()
    {
        var info = typeof(UpdateService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return AppVersion.TryParse(info) ?? AppVersion.TryParse(typeof(UpdateService).Assembly.GetName().Version?.ToString(3)) ?? AppVersion.TryParse("0.0.0")!;
    }

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowPill), nameof(PillText), nameof(PrimaryText), nameof(ShowProgress), nameof(ShowNotes), nameof(ShowLaterSkip), nameof(IsBusy), nameof(Badge))]
    private UpdateState _state = UpdateState.Idle;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(PillText), nameof(Badge))] private double _progress;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private string _notes = "";
    [ObservableProperty] private string _lastChecked = "";

    public string CurrentText => "Magpie " + Current;
    public string NewVersion => _release?.Version.ToString() ?? "";
    public bool ShowPill => State is UpdateState.Available or UpdateState.Downloading or UpdateState.Ready;
    public bool ShowProgress => State == UpdateState.Downloading;
    public bool ShowNotes => State is UpdateState.Available or UpdateState.Ready && Notes.Length > 0;
    public bool ShowLaterSkip => State is UpdateState.Available or UpdateState.Ready;
    public bool IsBusy => State is UpdateState.Checking or UpdateState.Downloading;
    public string PillText => State switch
    {
        UpdateState.Available => $"Update {NewVersion} available",
        UpdateState.Downloading => $"Downloading {NewVersion} · {Progress:P0}",
        UpdateState.Ready => _installed ? $"Magpie {NewVersion} installed — restart" : $"Update {NewVersion} ready — restart",
        _ => "",
    };
    public string Badge => State switch
    {
        UpdateState.UpToDate => "UP TO DATE",
        UpdateState.Checking => "CHECKING",
        UpdateState.Available => "NEW",
        UpdateState.Downloading => $"{Progress:P0}",
        UpdateState.Ready => "READY",
        UpdateState.Error => "PROBLEM",
        _ => "",
    };
    public string PrimaryText => State switch
    {
        UpdateState.Checking => "Checking…",
        UpdateState.Available => "Download and install",
        UpdateState.Downloading => "Downloading…",
        UpdateState.Ready => "Restart now",
        _ => "Check for updates",
    };
    public string PageUrl => _release?.PageUrl ?? UpdateClient.ReleasesPage;

    public void Start()
    {
        var cfg = AppServices.Engine.Config.Updates;
        LastChecked = cfg.LastCheck is { } lc ? "Last checked " + lc.LocalDateTime.ToString("d MMM, HH:mm") : "Not checked yet";
        Idle("Magpie " + Current, "Check GitHub for a newer version.");
        _timer.Tick += (_, _) => _ = AutoCheckAsync();
        _timer.Start();
        // First automatic check shortly after start, when mail has had its turn.
        var first = new DispatcherTimer { Interval = TimeSpan.FromSeconds(45) };
        first.Tick += (_, _) => { first.Stop(); _ = AutoCheckAsync(); };
        first.Start();
        CleanOldDownloads();
    }

    private void Idle(string title, string detail) { Title = title; Detail = detail; State = UpdateState.Idle; }

    private async Task AutoCheckAsync()
    {
        var cfg = AppServices.Engine.Config.Updates;
        if (cfg.AutoUpdate != true || IsBusy || State is UpdateState.Ready or UpdateState.Available) return;
        if (DateTimeOffset.Now < _laterUntil) return;
        // Once at start, then at most once a day.
        if (_startCheckDone && cfg.LastCheck is { } last && DateTimeOffset.Now - last < TimeSpan.FromHours(23)) return;
        _startCheckDone = true;
        await CheckAsync(automatic: true);
    }

    [RelayCommand]
    private async Task Primary()
    {
        switch (State)
        {
            case UpdateState.Available: await DownloadAsync(); break;
            case UpdateState.Ready: await RestartAsync(); break;
            case UpdateState.Checking or UpdateState.Downloading: break;
            default: await CheckAsync(automatic: false); break;
        }
    }

    /// <summary>Title-bar pill: download if needed, restart when ready, otherwise open Settings → Updates.</summary>
    [RelayCommand]
    private async Task Pill()
    {
        if (State == UpdateState.Ready) await RestartAsync();
        else Views.SettingsWindow.Open("Updates");
    }

    [RelayCommand]
    private void Later()
    {
        _laterUntil = DateTimeOffset.Now.AddHours(20);
        Idle("Magpie " + Current, $"Magpie {NewVersion} is waiting — it will be offered again tomorrow.");
    }

    [RelayCommand]
    private void Skip()
    {
        var engine = AppServices.Engine;
        engine.Config.Updates.SkippedVersion = NewVersion;
        engine.Settings.Save(notify: false);
        Idle("Magpie " + Current, $"Skipped {NewVersion}. You'll be told about the next version.");
    }

    [RelayCommand] private void OpenReleasePage() => Ui.OpenExternal(PageUrl);

    public async Task CheckAsync(bool automatic)
    {
        if (IsBusy) return;
        var engine = AppServices.Engine;
        var cfg = engine.Config.Updates;
        _cts = new CancellationTokenSource();
        State = UpdateState.Checking;
        Title = "Looking for a newer version on GitHub…";
        Detail = "This takes a second.";
        try
        {
            var rel = await _client.FindNewerAsync(Current, cfg.IncludePrerelease, _cts.Token);
            cfg.LastCheck = DateTimeOffset.Now;
            engine.Settings.Save(notify: false);
            LastChecked = "Last checked " + DateTime.Now.ToString("d MMM, HH:mm");
            if (rel == null)
            {
                Title = $"Magpie {Current} is the latest version";
                Detail = "Checked GitHub just now.";
                State = UpdateState.UpToDate;
                return;
            }
            if (automatic && rel.Version.ToString() == cfg.SkippedVersion) { Idle("Magpie " + Current, $"Skipped {rel.Version}."); return; }
            _release = rel;
            OnPropertyChanged(nameof(NewVersion));
            Notes = TrimNotes(rel.Notes);
            Title = $"Magpie {rel.Version} is available";
            Detail = (rel.Published is { } p ? "Released " + p.LocalDateTime.ToString("d MMM yyyy") : "New release") + (rel.ExeSize > 0 ? $" · {rel.ExeSize / 1048576.0:0} MB" : "");
            State = UpdateState.Available;
            Log.Info($"update available: {rel.Version}");
            if (cfg.AutoUpdate == true) await DownloadAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn("update check failed: " + ex.Message);
            Title = "Couldn't check for updates";
            Detail = ex is HttpRequestException ? "No connection to GitHub. Magpie will try again later." : ex.Message;
            State = UpdateState.Error;
        }
        catch (OperationCanceledException) { Idle("Magpie " + Current, ""); }
    }

    private static string TrimNotes(string md)
    {
        var lines = md.Replace("\r", "").Split('\n').Where(l => !l.StartsWith("## ")).Select(l => l.TrimEnd()).Where(l => l.Length > 0).Take(12);
        return string.Join("\n", lines.Select(l => l.StartsWith("- ") ? "· " + l[2..].Replace("**", "") : l.Replace("**", "")));
    }

    private async Task DownloadAsync()
    {
        if (_release == null || IsBusy) return;
        _cts = new CancellationTokenSource();
        State = UpdateState.Downloading;
        Title = $"Downloading Magpie {_release.Version}";
        Detail = "You can keep working.";
        Progress = 0;
        try
        {
            var folder = DownloadFolder;
            // Whole percents only: an 80 MB download reports thousands of times.
            var progress = new Progress<double>(p => { if (Math.Floor(p * 100) != Math.Floor(Progress * 100)) Progress = p; });
            _downloaded = await Task.Run(() => _client.DownloadAsync(_release, folder, progress, _cts.Token));
            Title = $"Magpie {_release.Version} is ready";
            Detail = "Download checked (SHA-256 matches). Restarting takes a few seconds.";
            State = UpdateState.Ready;
            if (AppServices.Engine.Config.Updates.AutoUpdate == true) InstallInBackground();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn("update download failed: " + ex.Message);
            Title = "The download didn't finish";
            Detail = ex is HttpRequestException ? "The connection dropped. Try again when you're online." : ex.Message;
            State = UpdateState.Error;
        }
        catch (OperationCanceledException) { State = UpdateState.Available; }
    }

    /// <summary>
    /// Auto update (design A1): puts the checked download in place of Magpie.exe while this copy keeps running (Windows
    /// lets a running EXE be renamed). The new version runs from the next start; "Restart now" still works at once.
    /// Not possible for an all-users install in Program Files: then it stays "ready" and the installer does it.
    /// </summary>
    private void InstallInBackground()
    {
        if (_installed || _downloaded == null || _release == null) return;
        var exe = Environment.ProcessPath;
        if (exe == null || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !SelfUpdate.CanReplace(exe))
        {
            Detail = $"Download checked. Restart to use it (or run Magpie-Setup-{NewVersion}.exe if Magpie is installed for all users).";
            return;
        }
        try
        {
            SelfUpdate.Swap(exe, _downloaded);
            InstalledUpdate.Write(DownloadFolder, Current.ToString(), _release.Version.ToString());
            _installed = true;
            Log.Info($"update {Current} → {NewVersion} installed in the background; runs from the next start");
            Title = $"Magpie {NewVersion} is installed";
            Detail = "It starts the next time you open Magpie. Restart now to use it straight away.";
            OnPropertyChanged(nameof(PillText));
        }
        catch (Exception ex)
        {
            Log.Warn("background install failed: " + ex.Message);
            Detail = "Download checked. Restart now to install it.";
        }
    }

    /// <summary>
    /// Swaps the EXE and restarts: open compose windows are asked first, the new EXE is started with
    /// --after-update, and this copy quits once the new one reports that it is running. If it doesn't,
    /// the previous version is put back and Magpie keeps running.
    /// </summary>
    private async Task RestartAsync()
    {
        if (_restarting) return;
        _restarting = true;
        try { await RestartCoreAsync(); }
        finally { _restarting = false; }
    }

    private async Task RestartCoreAsync()
    {
        if (_downloaded == null || !File.Exists(_downloaded)) { State = UpdateState.Available; await DownloadAsync(); return; }
        var exe = Environment.ProcessPath;
        if (exe == null || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            Ui.Error("Update", "This copy of Magpie wasn't started from Magpie.exe, so it can't replace itself.");
            return;
        }
        if (!SelfUpdate.CanReplace(exe))
        {
            // All-users install (Program Files): the installer does the update.
            Ui.Error("Update", $"Magpie can't write to {Path.GetDirectoryName(exe)}. Opening the release page — download the installer (Magpie-Setup-{NewVersion}.exe) and run it.");
            Ui.OpenExternal(PageUrl);
            return;
        }
        if (!await AppServices.Current.CloseComposeWindowsAsync()) return;   // user chose Cancel on an unsent message

        try { if (!_installed) SelfUpdate.Swap(exe, _downloaded); }
        catch (Exception ex)
        {
            Log.Error("update swap failed", ex);
            Ui.Error("Update", $"Magpie couldn't replace {exe}:\n\n{ex.Message}\n\nThe new version is saved at {_downloaded}. Close Magpie and copy it over Magpie.exe, or install Magpie with the installer so updates can replace it.");
            Ui.OpenExternal(Path.GetDirectoryName(_downloaded)!);
            return;
        }

        using var started = new EventWaitHandle(false, EventResetMode.ManualReset, StartedEvent);
        Process? p = null;
        try
        {
            p = Process.Start(new ProcessStartInfo(exe, "--after-update " + Current) { UseShellExecute = false });
            var ok = await Task.Run(() => started.WaitOne(TimeSpan.FromSeconds(25)));
            if (!ok || p == null) throw new TimeoutException("The new version didn't start.");
        }
        catch (Exception ex)
        {
            Log.Error("updated Magpie failed to start — rolling back", ex);
            try { if (p is { HasExited: false }) p.Kill(); } catch { }
            try { SelfUpdate.Rollback(exe); } catch (Exception rex) { Log.Error("rollback failed", rex); }
            InstalledUpdate.Clear(DownloadFolder);
            _installed = false;
            Title = "The new version didn't start";
            Detail = "Magpie went back to " + Current + ". Details are in the log.";
            State = UpdateState.Error;
            return;
        }
        Log.Info($"updating {Current} → {NewVersion}: handing over to the new EXE");
        AppServices.Current.ExitForUpdate();
    }

    /// <summary>
    /// The new version failed during start-up: offer to go back to the previous one (kept as Magpie.previous.exe).
    /// True when the previous version was put back and started (the caller then exits).
    /// </summary>
    public static bool OfferRollback(Exception ex)
    {
        var exe = Environment.ProcessPath;
        if (exe == null || !File.Exists(SelfUpdate.BackupPathFor(exe))) return false;
        var answer = MessageBox.Show($"Magpie {Current} could not start:\n\n{ex.Message}\n\nGo back to the previous version ({Program.UpdatedFrom})?",
            "Magpie", MessageBoxButton.YesNo, MessageBoxImage.Error);
        if (answer != MessageBoxResult.Yes) return false;
        try
        {
            SelfUpdate.Rollback(exe);
            InstalledUpdate.Clear(DownloadFolder);
            // Close the database before the other copy can open it.
            try { if (AppServices.Engine != null) AppServices.Engine.Dispose(); } catch { }
            Program.ReleaseSingleInstance();   // otherwise the relaunched copy would see "already running" and quit
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false });
            Log.Info($"rolled back to {Program.UpdatedFrom}");
            return true;
        }
        catch (Exception rex)
        {
            Log.Error("rollback failed", rex);
            MessageBox.Show("Going back failed: " + rex.Message + "\n\nThe previous version is Magpie.previous.exe next to Magpie.exe.", "Magpie", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private static void CleanOldDownloads()
    {
        try
        {
            var folder = DownloadFolder;
            if (!Directory.Exists(folder)) return;
            foreach (var f in Directory.GetFiles(folder))
                if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-7)) File.Delete(f);
        }
        catch { }
    }
}
