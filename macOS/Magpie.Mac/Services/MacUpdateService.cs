using System.Net.Http;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Magpie.Core;
using Magpie.Core.Updates;

namespace Magpie.Mac.Services;

public enum UpdateState { Idle, Checking, UpToDate, Available, Downloading, Ready, Error }

/// <summary>
/// Updates from GitHub Releases, the Mac way (Krishna's rules, GitHub Skills/versions_management.md): every start
/// checks; Auto update also checks once a day, downloads Magpie_&lt;version&gt;.dmg, checks its SHA-256 and installs
/// it when Magpie quits; the top-right "Update to vx.y.z" button shows only when GitHub has a newer version and, when
/// clicked, downloads (if needed), installs and restarts at once. The rules are Core's <see cref="UpdatePolicy"/>;
/// the disk-image work is <see cref="MacInstaller"/>.
/// </summary>
public sealed partial class MacUpdateService : ObservableObject
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(15) };
    private readonly UpdateClient _client = new(Http, UpdateAsset.MacDmg);
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromHours(1) };
    private CancellationTokenSource? _cts;
    private bool _startCheckDone;
    /// <summary>This copy has put the new version in place (button or quit): nothing more to install.</summary>
    private bool _installed;
    private ReleaseInfo? _release;
    private string? _downloaded;
    /// <summary>The published SHA-256 the download was checked against; checked again just before installing.</summary>
    private string? _expectedSha;

    private static MailEngine E => AppServices.Engine;
    public static AppVersion Current => AppServices.Current;
    public static string DownloadFolder => E.Paths.Updates;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ButtonText), nameof(IsUpdateVisible), nameof(IsBusy), nameof(ShowProgress), nameof(ButtonTip))]
    private UpdateState _state = UpdateState.Idle;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ButtonTip))] private double _progress;
    /// <summary>One line for Settings → Updates ("Magpie 8.0.0 is up to date", "Downloading Magpie 8.0.1 · 40 %" …).</summary>
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _lastChecked = "";
    [ObservableProperty] private string _notes = "";

    public string NewVersion => _release?.Version.ToString() ?? "";
    /// <summary>"Update to v8.0.1" — empty while there is nothing newer.</summary>
    public string ButtonText => UpdatePolicy.ButtonText(_release?.Version, Current);
    public bool IsUpdateVisible => ButtonText.Length > 0 && State is UpdateState.Available or UpdateState.Downloading or UpdateState.Ready or UpdateState.Error;
    public bool IsBusy => State is UpdateState.Checking or UpdateState.Downloading;
    public bool ShowProgress => State == UpdateState.Downloading;
    public string ButtonTip => State switch
    {
        UpdateState.Downloading => $"Downloading Magpie {NewVersion} · {UpdateText.Percent(Progress)}",
        UpdateState.Ready => $"Magpie {NewVersion} is ready · click to restart into it",
        UpdateState.Error => "The last try didn't work · click to try again",
        _ => $"Magpie {NewVersion} is available · click to download, install and restart",
    };

    /// <summary>Settings → Updates: Auto update (check daily, download and install by itself). Saved at once.</summary>
    public bool AutoUpdate
    {
        get => E.Config.Updates.AutoUpdate ?? true;
        set
        {
            var c = E.Config;
            if ((c.Updates.AutoUpdate ?? true) == value) return;
            c.Updates.AutoUpdate = value;
            c.Updates.AutoCheck = c.Updates.AutoDownload = value;
            E.Settings.Save(notify: false);
            OnPropertyChanged();
        }
    }

    /// <summary>Settings → Updates: Include test versions (pre-releases).</summary>
    public bool IncludePrerelease
    {
        get => E.Config.Updates.IncludePrerelease;
        set
        {
            if (E.Config.Updates.IncludePrerelease == value) return;
            E.Config.Updates.IncludePrerelease = value;
            E.Settings.Save(notify: false);
            OnPropertyChanged();
        }
    }

    public void Start()
    {
        var cfg = E.Config.Updates;
        LastChecked = cfg.LastCheck is { } lc ? "Last checked " + lc.LocalDateTime.ToString("d MMM, HH:mm") : "Not checked yet";
        Status = $"Magpie {Current}";
        _timer.Tick += (_, _) => _ = AutoCheckAsync();
        _timer.Start();
        // The check at start, once mail has had its turn.
        var first = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        first.Tick += (_, _) => { first.Stop(); _ = AutoCheckAsync(); };
        first.Start();
        CleanOldDownloads();
    }

    private async Task AutoCheckAsync()
    {
        var cfg = E.Config.Updates;
        if (!UpdatePolicy.ShouldAutoCheck(_startCheckDone, cfg.AutoUpdate == true, cfg.LastCheck, DateTimeOffset.Now,
                IsBusy, State is UpdateState.Available or UpdateState.Ready)) return;
        _startCheckDone = true;
        await CheckAsync(automatic: true);
    }

    /// <summary>Settings → Updates → Check now (and Magpie → Check for Updates…): ignores the daily rule and Skip.</summary>
    [RelayCommand]
    public async Task CheckNowAsync()
    {
        if (IsBusy) return;
        await CheckAsync(automatic: false);
    }

    public async Task CheckAsync(bool automatic)
    {
        if (IsBusy || _installed) return;
        var cfg = E.Config.Updates;
        _cts = new CancellationTokenSource();
        _release = null;
        OnPropertyChanged(nameof(NewVersion));
        State = UpdateState.Checking;
        Status = "Looking for a newer version on GitHub…";
        try
        {
            var rel = await _client.FindNewerAsync(Current, cfg.IncludePrerelease, _cts.Token);
            cfg.LastCheck = DateTimeOffset.Now;
            E.Settings.Save(notify: false);
            LastChecked = "Last checked " + DateTime.Now.ToString("d MMM, HH:mm");
            if (rel == null || !UpdatePolicy.ShouldOffer(rel.Version, Current, automatic, cfg.SkippedVersion))
            {
                Status = rel == null ? $"Magpie {Current} is up to date" : $"Skipped {rel.Version}.";
                State = UpdateState.UpToDate;
                return;
            }
            _release = rel;
            OnPropertyChanged(nameof(NewVersion));
            Notes = rel.Notes;
            Status = $"Magpie {rel.Version} is available" + (rel.ExeSize > 0 ? $" · {rel.ExeSize / 1048576.0:0} MB" : "");
            State = UpdateState.Available;
            Log.Info($"update available: {rel.Version}");
            if (UpdatePolicy.DownloadsByItself(cfg.AutoUpdate == true)) _ = DownloadAsync();
        }
        catch (OperationCanceledException) { State = UpdateState.Idle; Status = $"Magpie {Current}"; }
        catch (Exception ex)
        {
            Log.Warn("update check failed: " + ex.Message);
            Status = ex is HttpRequestException ? "No connection to GitHub. Magpie will try again later." : "Couldn't check for updates: " + ex.Message;
            State = UpdateState.Error;
        }
    }

    private async Task<bool> DownloadAsync()
    {
        if (_release == null || State == UpdateState.Downloading) return false;
        _cts = new CancellationTokenSource();
        State = UpdateState.Downloading;
        Progress = 0;
        Status = $"Downloading Magpie {_release.Version}…";
        try
        {
            var progress = new Progress<double>(p =>
            {
                if (Math.Floor(p * 100) == Math.Floor(Progress * 100)) return;
                Progress = p;
                Status = $"Downloading Magpie {NewVersion} · {UpdateText.Percent(p)}";
            });
            var release = _release;
            (_downloaded, _expectedSha) = await Task.Run(() => _client.DownloadCheckedAsync(release, DownloadFolder, progress, _cts.Token));
            State = UpdateState.Ready;
            Status = $"Magpie {_release.Version} is ready (SHA-256 checked)" + (AutoUpdate ? " · it installs when you quit Magpie" : "");
            return true;
        }
        catch (OperationCanceledException) { State = UpdateState.Available; return false; }
        catch (Exception ex)
        {
            Log.Warn("update download failed: " + ex.Message);
            Status = ex is HttpRequestException ? "The connection dropped. Try again when you're online." : ex.Message;
            State = UpdateState.Error;
            return false;
        }
    }

    /// <summary>The top-right "Update to vx.y.z" button: download if needed, then install and restart.</summary>
    [RelayCommand]
    public async Task UpdateNowAsync()
    {
        switch (State)
        {
            case UpdateState.Checking or UpdateState.Downloading: return;
            case UpdateState.Error when _release == null: await CheckNowAsync(); return;
            case UpdateState.Available or UpdateState.Error:
                if (!await DownloadAsync()) return;
                break;
        }
        if (_installed || State != UpdateState.Ready || _downloaded == null || _release == null) return;
        var plan = MacInstaller.Plan();
        if (plan.Problem != null)
        {
            await Dialogs.Info("Update", plan.Problem);
            if (plan.OpenImageInstead) Shell.OpenLocal(_downloaded);
            return;
        }
        if (!await App.CloseComposeWindowsAsync()) return;   // the user chose Cancel on an unsent message
        Status = $"Installing Magpie {NewVersion}…";
        try
        {
            var (dmg, sha, app) = (_downloaded, _expectedSha, plan.App!);
            await Task.Run(() => MacInstaller.Install(dmg, sha, app));
        }
        catch (Exception ex)
        {
            Log.Error("update install failed", ex);
            Status = "The update couldn't be installed.";
            State = UpdateState.Error;
            var manual = _downloaded != null && File.Exists(_downloaded) ? $" The disk image is at {_downloaded}: open it and drag Magpie to Applications." : "";
            await Dialogs.Error("Update", $"Magpie couldn't install {NewVersion}:\n\n{ex.Message}\n\nYour Magpie is unchanged.{manual}");
            return;
        }
        // Installed: quitting now must not install it a second time (that would swap again and replace the kept
        // previous version with this new one).
        var installedVersion = NewVersion;   // MarkInstalled clears the release
        MarkInstalled();
        Log.Info($"updating {Current} → {installedVersion}: restarting");
        MacInstaller.RelaunchAfterExit(plan.App!, Current.ToString());
        App.QuitForUpdate();
    }

    /// <summary>
    /// Magpie is quitting: with Auto update on, a downloaded and checked version is put in place now, so the next start
    /// runs it (the Mac twin of the Windows "installed in the background").
    /// </summary>
    public void InstallOnQuit()
    {
        if (_release == null || !UpdatePolicy.InstallsOnQuit(AutoUpdate, State == UpdateState.Ready && _downloaded != null, _installed)) return;
        var plan = MacInstaller.Plan();
        if (plan.Problem != null) { Log.Info("update not installed on quit: " + plan.Problem); return; }
        try
        {
            MacInstaller.Install(_downloaded!, _expectedSha, plan.App!);
            InstalledUpdate.Write(DownloadFolder, Current.ToString(), _release.Version.ToString());
            MarkInstalled();
            Log.Info($"update {Current} → {NewVersion} installed on quit; runs from the next start");
        }
        catch (Exception ex) { Log.Error("install on quit failed", ex); }
    }

    /// <summary>The new version is in place: the download is spent and the button goes away.</summary>
    private void MarkInstalled()
    {
        _installed = true;
        _downloaded = null;
        _expectedSha = null;
        _release = null;
        OnPropertyChanged(nameof(NewVersion));
        State = UpdateState.Idle;
        Status = "The new version is installed · it runs from the next start";
    }

    /// <summary>For the headless tests: what quitting would do now.</summary>
    internal bool WouldInstallOnQuit => _release != null && UpdatePolicy.InstallsOnQuit(AutoUpdate, State == UpdateState.Ready && _downloaded != null, _installed);

    /// <summary>For the headless tests: a downloaded, checked version ready to install.</summary>
    internal void PretendReady(ReleaseInfo release, string dmg, string sha)
    {
        _release = release;
        _downloaded = dmg;
        _expectedSha = sha;
        State = UpdateState.Ready;
    }

    /// <summary>For the headless tests: the state after the button installed it.</summary>
    internal void PretendInstalled() => MarkInstalled();

    private static void CleanOldDownloads()
    {
        try
        {
            if (!Directory.Exists(DownloadFolder)) return;
            foreach (var f in Directory.GetFiles(DownloadFolder))
                if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-7)) File.Delete(f);
        }
        catch { }
    }
}
