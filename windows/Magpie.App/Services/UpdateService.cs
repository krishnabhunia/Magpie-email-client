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
using Microsoft.Win32;

namespace Magpie.App.Services;

public enum UpdateState { Idle, Checking, UpToDate, Available, Preparing, Downloading, Ready, Installing, Error }

/// <summary>
/// Checks GitHub when enabled and installs only an explicitly confirmed release after compatibility,
/// checksum and paired application/data backup checks. The previous version remains recoverable.
/// </summary>
public sealed partial class UpdateService : ObservableObject
{
    public const string StartedEvent = WindowsStartup.StartedEvent;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly UpdateClient _client = new(Http);
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromHours(1) };
    private CancellationTokenSource? _cts;
    private bool _restarting;
    private bool _selectingVersion;
    private bool IsTransitioning => _selectingVersion || _restarting;
    private bool _startCheckDone;
    /// <summary>"Later": not offered again by automatic checks before this time.</summary>
    private DateTimeOffset _laterUntil = DateTimeOffset.MinValue;
    private ReleaseInfo? _release;
    private string? _downloaded;
    private string? _downloadedSha;
    private ReleaseCompatibility? _compatibility;
    private VersionSnapshot? _snapshot;
    private string? _historyId;
    private string? _restoreSnapshotId;
    private UpdateProgressWindow? _progressWindow;
    private bool _engineStopped;
    private bool _unsafeToRestart;
    private Window[] _disabledWindows = [];
    private int _availableCount;
    public bool IsProfileFrozen => _engineStopped || AppServices.Engine.IsStoppedForVersionChange;

    public static string InstallKind => AppPaths.Default().IsPortable ? UpdateInstallKinds.WindowsPortable
        : IsAllUsersInstall() ? UpdateInstallKinds.WindowsAllUsers : UpdateInstallKinds.WindowsPerUser;

    private static bool IsAllUsersInstall()
    {
        var folder = Path.GetFullPath(AppPaths.ExeFolder()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 }
            .Select(Environment.GetFolderPath).Where(p => !string.IsNullOrWhiteSpace(p))
            .Any(p => folder.StartsWith(Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))) return true;
        const string uninstall = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{6A1D5E3C-8B2F-4C71-9E0A-3F5B7D2C9A48}_is1";
        bool Registered(RegistryHive hive)
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var key = root.OpenSubKey(uninstall);
                var location = (key?.GetValue("InstallLocation") ?? key?.GetValue("Inno Setup: App Path")) as string;
                if (!string.IsNullOrWhiteSpace(location) && string.Equals(folder,
                    Path.GetFullPath(location).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
        try
        {
            if (Registered(RegistryHive.LocalMachine)) return true;
            if (Registered(RegistryHive.CurrentUser)) return false;
            // An installed copy with unknown registration must not be treated as a writable per-user install.
            return File.Exists(Path.Combine(AppPaths.ExeFolder(), AppPaths.InstallerUninstaller));
        }
        catch { return true; }
    }

    public static AppVersion Current { get; } = ReadCurrent();
    /// <summary>%LOCALAPPDATA%\Magpie\updates — not the roaming profile (the EXE is ~80 MB); next to the EXE when portable.</summary>
    public static string DownloadFolder => AppPaths.Default().Updates;
    public static string VersionPinPath => Path.Combine(AppPaths.Default().Root, "version-pin.txt");
    public static string? PinnedVersion
    {
        get
        {
            try { return File.Exists(VersionPinPath) ? File.ReadAllText(VersionPinPath).Trim() : null; }
            catch { return null; }
        }
    }
    public static void ClearVersionPin()
    {
        if (File.Exists(VersionPinPath)) File.Delete(VersionPinPath);
    }


    private static AppVersion ReadCurrent()
    {
        var info = typeof(UpdateService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return AppVersion.TryParse(info) ?? AppVersion.TryParse(typeof(UpdateService).Assembly.GetName().Version?.ToString(3)) ?? AppVersion.TryParse("0.0.0")!;
    }

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ButtonText), nameof(ButtonTip), nameof(PrimaryText), nameof(ShowPrimary), nameof(ShowProgress), nameof(ShowNotes), nameof(ShowLaterSkip),
        nameof(IsUpdateVisible), nameof(IsBusy), nameof(CanCancel), nameof(IsPreparing), nameof(Badge), nameof(IsIdleLike), nameof(IsChecking), nameof(IsAvailable), nameof(IsDownloading), nameof(IsReady), nameof(IsError))]
    private UpdateState _state = UpdateState.Idle;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ButtonText), nameof(Badge))] private double _progress;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private string _notes = "";
    [ObservableProperty] private string _lastChecked = "";
    /// <summary>The flyout under the title-bar button (design UB1).</summary>
    [ObservableProperty] private bool _flyoutOpen;
    private DateTimeOffset _flyoutClosedAt = DateTimeOffset.MinValue;

    public string CurrentText => "Magpie " + Current;
    public string NewVersion => _release?.Version.ToString() ?? "";
    public bool ShowProgress => State is UpdateState.Preparing or UpdateState.Downloading;
    public bool IsPreparing => State == UpdateState.Preparing;
    public bool CanCancel => State is UpdateState.Preparing or UpdateState.Downloading;
    public bool ShowNotes => State is UpdateState.Available or UpdateState.Ready && Notes.Length > 0;
    public bool ShowLaterSkip => State is UpdateState.Available or UpdateState.Ready;
    public bool IsBusy => State is UpdateState.Checking or UpdateState.Preparing or UpdateState.Downloading or UpdateState.Installing;
    /// <summary>While downloading the flyout shows Cancel instead of the main button.</summary>
    public bool ShowPrimary => !IsBusy;
    // The title-bar button's looks (design UB1): one DataTrigger per state in Button.Update.
    public bool IsIdleLike => State is UpdateState.Idle or UpdateState.UpToDate;
    public bool IsChecking => State == UpdateState.Checking;
    public bool IsAvailable => State == UpdateState.Available;
    public bool IsDownloading => State == UpdateState.Downloading;
    public bool IsReady => State == UpdateState.Ready;
    public bool IsError => State == UpdateState.Error;
    private string StateKey => State.ToString().ToLowerInvariant();
    /// <summary>Only a newer release confirmed by GitHub earns a title-bar action.</summary>
    public bool IsUpdateVisible => _release != null && _release.Version.CompareTo(Current) > 0
        && State is UpdateState.Available or UpdateState.Downloading or UpdateState.Ready or UpdateState.Error;
    public string ButtonText => NewVersion.Length > 0 ? $"Update to v{NewVersion}" : "";

    partial void OnStateChanged(UpdateState value)
    {
        if (!IsUpdateVisible) FlyoutOpen = false;
    }
    public string ButtonTip => UpdateText.ButtonTip(StateKey, Current.ToString(), NewVersion, AppServices.Engine.Config.Updates.LastCheck, DateTimeOffset.Now);
    public string Badge => State switch
    {
        UpdateState.UpToDate => "UP TO DATE",
        UpdateState.Checking => "CHECKING",
        UpdateState.Preparing => "BACKING UP",
        UpdateState.Installing => "INSTALLING",
        UpdateState.Available => _availableCount > 1 ? $"{_availableCount} UPDATES" : "NEW",
        UpdateState.Downloading => UpdateText.Percent(Progress),
        UpdateState.Ready => "READY",
        UpdateState.Error => "PROBLEM",
        _ => "",
    };
    public string PrimaryText => State switch
    {
        UpdateState.Checking => "Checking…",
        UpdateState.Available => "Choose a version",
        UpdateState.Preparing => "Backing up…",
        UpdateState.Installing => "Installing…",
        UpdateState.Downloading => "Downloading…",
        UpdateState.Ready => $"Install v{NewVersion}",
        UpdateState.UpToDate => "Check again",
        UpdateState.Error => "Try again",
        _ => "Check for updates",
    };
    public string PageUrl => _release?.PageUrl ?? UpdateClient.ReleasesPage;

    /// <summary>The two switches of Settings → Updates, also in the flyout; saved at once, the way the Settings page saves them.</summary>
    public bool AutoUpdate
    {
        get => AppServices.Engine.Config.Updates.AutoUpdate ?? true;
        set
        {
            if (IsTransitioning) return;
            var c = AppServices.Engine.Config;
            if ((c.Updates.AutoUpdate ?? true) == value) return;
            c.Updates.AutoUpdate = value;
            c.Updates.AutoCheck = value;
            c.Updates.AutoDownload = false;
            AppServices.Engine.Settings.Save(notify: false);
            OnPropertyChanged();
        }
    }

    public bool IncludePrerelease
    {
        get => AppServices.Engine.Config.Updates.IncludePrerelease;
        set
        {
            var c = AppServices.Engine.Config;
            if (c.Updates.IncludePrerelease == value) return;
            c.Updates.IncludePrerelease = value;
            AppServices.Engine.Settings.Save(notify: false);
            OnPropertyChanged();
        }
    }

    public void Start()
    {
        var cfg = AppServices.Engine.Config.Updates;
        LastChecked = cfg.LastCheck is { } lc ? "Last checked " + lc.LocalDateTime.ToString("d MMM, HH:mm") : "Not checked yet";
        Idle("Magpie " + Current, "Check GitHub for a newer version.");
        _timer.Tick += (_, _) => { OnPropertyChanged(nameof(ButtonTip)); _ = AutoCheckAsync(); };
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
        // Pausing automatic checks also pauses the startup check. Installation is always explicit.
        if (IsBusy || IsTransitioning || State is UpdateState.Ready or UpdateState.Available) return;
        if (cfg.AutoUpdate != true) return;
        if (DateTimeOffset.Now < _laterUntil) return;
        // Once at start, then at most once a day.
        if (_startCheckDone && cfg.LastCheck is { } last && DateTimeOffset.Now - last < TimeSpan.FromHours(23)) return;
        _startCheckDone = true;
        await CheckAsync(automatic: true);
    }

    [RelayCommand]
    private async Task Primary()
    {
        if (IsTransitioning) return;
        switch (State)
        {
            case UpdateState.Available: await OpenVersionHistory(); break;
            case UpdateState.Ready: await RestartAsync(); break;
            case UpdateState.Checking or UpdateState.Preparing or UpdateState.Downloading or UpdateState.Installing: break;
            default: await CheckAsync(automatic: false); break;
        }
    }

    /// <summary>
    /// Left click on the title-bar button (design UB1): idle or up to date → check now, then show the flyout; available,
    /// downloading or a problem → the flyout (it has Try again); ready → restart into the new version; checking → nothing.
    /// </summary>
    [RelayCommand]
    private async Task TitleBarClick()
    {
        if (IsTransitioning) return;
        // The click that closed the flyout (StaysOpen=False) must not open it again at once.
        if (DateTimeOffset.Now - _flyoutClosedAt < TimeSpan.FromMilliseconds(400)) return;
        switch (State)
        {
            case UpdateState.Checking: return;
            case UpdateState.Ready: await RestartAsync(); return;
            case UpdateState.Available: await OpenVersionHistory(); return;
            case UpdateState.Downloading or UpdateState.Error: OpenFlyout(); return;   // Error: the flyout has Try again
            default:
                await CheckNowAsync();
                OpenFlyout();
                return;
        }
    }

    /// <summary>A check the user asked for: ignores the once-a-day throttle and "Later", works with Auto update off, records the time.</summary>
    public async Task CheckNowAsync()
    {
        if (IsBusy || IsTransitioning) return;
        await CheckAsync(automatic: false);
    }

    /// <summary>Right-click, or a left click in the states that show it.</summary>
    public void OpenFlyout()
    {
        OnPropertyChanged(nameof(AutoUpdate));
        OnPropertyChanged(nameof(IncludePrerelease));
        OnPropertyChanged(nameof(ButtonTip));
        FlyoutOpen = true;
    }

    partial void OnFlyoutOpenChanged(bool value)
    {
        if (!value) _flyoutClosedAt = DateTimeOffset.Now;
    }

    /// <summary>The tooltip says how long ago the last check was; refreshed when the mouse arrives.</summary>
    public void RefreshTip() => OnPropertyChanged(nameof(ButtonTip));

    /// <summary>Cancel in the flyout while downloading: the partial file stays for a later retry; the update is still offered.</summary>
    [RelayCommand]
    private void CancelDownload()
    {
        if (CanCancel) _cts?.Cancel();
    }

    [RelayCommand]
    private void Later()
    {
        if (IsTransitioning) return;
        _laterUntil = DateTimeOffset.Now.AddHours(20);
        Idle("Magpie " + Current, $"Magpie {NewVersion} is waiting — it will be offered again tomorrow.");
    }

    [RelayCommand]
    private void Skip()
    {
        if (IsTransitioning) return;
        var engine = AppServices.Engine;
        engine.Config.Updates.SkippedVersion = NewVersion;
        engine.Settings.Save(notify: false);
        Idle("Magpie " + Current, $"Skipped {NewVersion}. You'll be told about the next version.");
    }

    [RelayCommand]
    private async Task OpenVersionHistory()
    {
        if (IsBusy || IsTransitioning) return;
        _selectingVersion = true;
        _historyId = null;
        _restoreSnapshotId = null;
        try { await SelectAndInstallVersionAsync(); }
        catch (OperationCanceledException)
        {
            SetHistoryStatus(UpdateHistory.Cancelled, "Cancelled before installation; the current application is unchanged.");
            Title = "Version change cancelled"; Detail = "Your current application and data are unchanged."; State = UpdateState.Available;
        }
        catch (Exception ex) { FailTransition("The version change stopped", ex); }
        finally { FinishProgress(); _selectingVersion = false; }
    }

    private async Task SelectAndInstallVersionAsync()
    {
        var paths = AppPaths.Default();
        var picker = new VersionHistoryWindow(_client, Current, UpdateCompatibility.ReadProfile(paths), InstallKind)
            { Owner = Application.Current.MainWindow };
        if (picker.ShowDialog() != true || picker.Selection is not { } chosen) return;
        var rollback = chosen.Version.CompareTo(Current) < 0;
        var answer = MessageBox.Show(Application.Current.MainWindow,
            $"{(rollback ? "Roll back" : "Update")} Magpie v{Current} to v{chosen.Version}{(picker.IsBeta ? " (Beta)" : "")}?\n\n" +
            "Magpie will check compatibility and the download checksum, then save a paired application and local-data backup before installing. " +
            "Restoring that backup later returns local data to the time it was saved.\n\n" +
            $"Install exactly v{chosen.Version}?", "Confirm selected version", MessageBoxButton.YesNo,
            MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes || !await AppServices.Current.CloseComposeWindowsAsync()) return;
        _cts?.Dispose(); _cts = new CancellationTokenSource();
        _historyId = UpdateHistory.Record(paths, Current.ToString(), chosen.Version.ToString(), rollback ? "Rollback" : "Update",
            UpdateHistory.Preparing, "Checking the selected release and saving a paired backup.");
        _compatibility = await _client.LoadCompatibilityAsync(chosen, _cts.Token);
        var eligibility = UpdateCompatibility.Evaluate(chosen, _compatibility, UpdateCompatibility.ReadProfile(paths), InstallKind);
        if (!eligibility.CanInstall) throw new InvalidOperationException(eligibility.Reason);
        _ = RequireReplaceableExecutable();
        _release = chosen; _downloaded = null; _downloadedSha = null;
        NotifyRelease(); Notes = TrimNotes(chosen.Notes);
        ShowProgressWindow();
        State = UpdateState.Available;
        await DownloadAsync();
        if (State == UpdateState.Ready) await RestartAsync();
    }

    private static string RequireReplaceableExecutable()
    {
        if (BackupUi.IsBusy) throw new InvalidOperationException("Finish the settings backup or restore before changing application versions.");
        if (Application.Current.Windows.OfType<Magpie.App.Views.AddAccountWindow>().Any())
            throw new InvalidOperationException("Finish or close the account sign-in window before changing application versions.");
        var exe = Environment.ProcessPath;
        if (exe == null || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Start the installed or portable Magpie.exe before changing versions.");
        if (InstallKind == UpdateInstallKinds.WindowsAllUsers || !SelfUpdate.CanReplace(exe))
            throw new InvalidOperationException("This installation needs an administrator to replace the application. " +
                "A safe application-and-data transition is not available for it yet. Keep this copy until a compatible installer is available.");
        return exe;
    }

    private async Task CreateTransitionBackupAsync(string exe)
    {
        State = UpdateState.Preparing; Progress = 0;
        Title = "Saving your application and local data…";
        Detail = "Backing up mail, drafts, settings and encrypted sign-ins. You can cancel before installation.";
        AppServices.Engine.Settings.Save(notify: false);
        _snapshot = await Task.Run(() => VersionBackup.Create(AppPaths.Default(), Current, exe, _cts!.Token, InstallKind), _cts!.Token);
        UpdateHistory.AttachSnapshot(AppPaths.Default(), _historyId!, _snapshot.Id);
    }

    private void NotifyRelease()
    {
        OnPropertyChanged(nameof(NewVersion)); OnPropertyChanged(nameof(IsUpdateVisible));
        OnPropertyChanged(nameof(ButtonText)); OnPropertyChanged(nameof(PrimaryText));
    }

    private void ShowProgressWindow()
    {
        _progressWindow = new UpdateProgressWindow(this) { Owner = Application.Current.MainWindow };
        _progressWindow.Show();
    }

    private void FinishProgress()
    {
        _progressWindow?.Finish(); _progressWindow = null;
    }

    private void SetHistoryStatus(string status, string detail)
    {
        if (_historyId == null) return;
        try { UpdateHistory.SetStatus(AppPaths.Default(), _historyId, status, detail); }
        catch (Exception ex) { Log.Error("could not update version history", ex); }
    }

    private void FailTransition(string title, Exception ex)
    {
        Log.Error(title, ex); SetHistoryStatus(_unsafeToRestart ? UpdateHistory.Installing : UpdateHistory.Failed,
            _unsafeToRestart ? "Recovery needs attention: " + ex.Message : ex.Message);
        Title = title; Detail = ex.Message; State = UpdateState.Error;
        Ui.Error(title, ex.Message);
    }

    [RelayCommand]
    private async Task OpenTransitionHistory()
    {
        if (IsBusy || IsTransitioning) return;
        _selectingVersion = true;
        _historyId = null;
        _restoreSnapshotId = null;
        try
        {
            var paths = AppPaths.Default();
            var history = new UpdateHistoryWindow(paths) { Owner = Application.Current.MainWindow };
            if (history.ShowDialog() != true || history.SnapshotId is not { } id) return;
            var snapshot = VersionBackup.Read(paths, id);
            var answer = MessageBox.Show(Application.Current.MainWindow,
                $"Restore Magpie v{snapshot.Version} and its local data saved on {snapshot.Created.LocalDateTime:g}?\n\n" +
                "This replaces local mail, drafts, settings and sign-ins with the saved snapshot. Local changes made since then will be undone. " +
                "Your current application and data will first be saved as another backup. Mail already sent cannot be recalled.\n\nContinue?",
                "Restore application and data", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes || !await AppServices.Current.CloseComposeWindowsAsync()) return;
            _ = RequireReplaceableExecutable();
            _cts?.Dispose(); _cts = new CancellationTokenSource();
            _historyId = UpdateHistory.Record(paths, Current.ToString(), snapshot.Version, "Restore", UpdateHistory.Preparing,
                "Checking the saved application and preserving the current profile.");
            await Task.Run(() => VersionBackup.Validate(snapshot), _cts.Token);
            if (snapshot.InstallKind != InstallKind || snapshot.ApplicationKind != "file")
                throw new InvalidOperationException("The backup belongs to a different installation type.");
            ShowProgressWindow();
            _restoreSnapshotId = snapshot.Id;
            _release = new ReleaseInfo { Version = AppVersion.TryParse(snapshot.Version)! };
            _compatibility = null; _downloaded = snapshot.ApplicationPath;
            _downloadedSha = await UpdateClient.HashFileAsync(_downloaded, _cts.Token);
            NotifyRelease(); State = UpdateState.Ready;
            await RestartAsync();
        }
        catch (OperationCanceledException)
        {
            SetHistoryStatus(UpdateHistory.Cancelled, "The restore was cancelled before installation.");
            Title = "Restore cancelled"; Detail = "Your current application and data are unchanged."; State = UpdateState.Idle;
        }
        catch (Exception ex) { FailTransition("The restore stopped", ex); }
        finally { FinishProgress(); _selectingVersion = false; }
    }

    [RelayCommand] private void OpenReleasePage() => Ui.OpenExternal(PageUrl);

    public async Task CheckAsync(bool automatic)
    {
        if (IsBusy || IsTransitioning) return;
        var engine = AppServices.Engine;
        var cfg = engine.Config.Updates;
        _cts = new CancellationTokenSource();
        State = UpdateState.Checking;
        // A fresh check must not retain a stale offer if GitHub reports no update or fails.
        _release = null;
        OnPropertyChanged(nameof(NewVersion));
        OnPropertyChanged(nameof(ButtonText));
        OnPropertyChanged(nameof(IsUpdateVisible));
        Title = "Looking for a newer version on GitHub…";
        Detail = "This takes a second.";
        try
        {
            var catalog = await _client.ListVersionsAsync(Current, _cts.Token);
            var offers = UpdateClient.SelectVersions(catalog.Where(r => r.Info.AssetAvailable), Current, older: false, cfg.IncludePrerelease);
            var rel = offers.FirstOrDefault().Info;
            _availableCount = offers.Count;
            OnPropertyChanged(nameof(Badge));
            cfg.LastCheck = DateTimeOffset.Now;
            engine.Settings.Save(notify: false);
            LastChecked = "Last checked " + DateTime.Now.ToString("d MMM, HH:mm");
            if (automatic && PinnedVersion is { Length: > 0 } pinned)
            {
                Idle("Magpie version pinned", $"Pinned to v{pinned}. Open Settings → Updates to change it.");
                return;
            }
            if (rel == null)
            {
                Title = $"Magpie {Current} is up to date";
                Detail = "Checked GitHub just now.";
                State = UpdateState.UpToDate;
                return;
            }
            if (automatic && rel.Version.ToString() == cfg.SkippedVersion) { Idle("Magpie " + Current, $"Skipped {rel.Version}."); return; }
            _release = rel;
            OnPropertyChanged(nameof(NewVersion));
            Notes = TrimNotes(rel.Notes);
            Title = $"Magpie {rel.Version} is available";
            Detail = $"You have {Current}" + (rel.Published is { } p ? " · released " + p.LocalDateTime.ToString("d MMM yyyy") : "") + (rel.ExeSize > 0 ? $" · {rel.ExeSize / 1048576.0:0} MB" : "");
            State = UpdateState.Available;
            Log.Info($"update available: {rel.Version}");
            // The badge opens the complete chooser. Checks never download or install a release.
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
        if (_release is not { } selected || _compatibility == null || _historyId == null) return;
        _cts ??= new CancellationTokenSource();
        State = UpdateState.Downloading; Progress = 0;
        Title = $"Downloading Magpie v{selected.Version}";
        Detail = "You can keep working. The final application and data backup is made just before installation.";
        UpdateHistory.SetStatus(AppPaths.Default(), _historyId, UpdateHistory.Downloading, "Downloading the exact selected release.");
        var progress = new Progress<double>(p => { if (Math.Floor(p * 100) != Math.Floor(Progress * 100)) Progress = p; });
        var result = await _client.DownloadCheckedAsync(selected, DownloadFolder, progress, _cts.Token, _compatibility);
        _downloaded = result.Path; _downloadedSha = result.Sha256;
        Title = $"Magpie v{selected.Version} is ready";
        Detail = "The application matches the release checksum and compatibility information.";
        State = UpdateState.Ready;
    }

    /// <summary>The managed child first waits for this process to exit before it can open the shared profile.</summary>
    private async Task RestartAsync()
    {
        if (_restarting) return;
        _restarting = true;
        try { await RestartCoreAsync(); }
        catch (OperationCanceledException)
        {
            SetHistoryStatus(UpdateHistory.Cancelled, "Cancelled before executable replacement.");
            Title = "Version change cancelled"; Detail = "The current application was kept."; State = UpdateState.Available;
        }
        catch (Exception ex) { FailTransition("The version change stopped", ex); }
        finally
        {
            _restarting = false;
            FinishProgress();
            if (_engineStopped)
            {
                if (!_unsafeToRestart) RestartCurrentAfterAbort();
                else AppServices.Current.ExitForUpdate();
            }
            else foreach (var window in _disabledWindows) if (window.IsLoaded) window.IsEnabled = true;
            _disabledWindows = [];
        }
    }

    private async Task RestartCoreAsync()
    {
        if (_release == null || _downloaded == null || _historyId == null)
            throw new InvalidOperationException("Choose a version again before installing.");
        if (!UpdateClient.MatchesSha256(_downloaded, _downloadedSha))
            throw new InvalidDataException("The staged application changed after verification. Choose the version again to download a fresh copy.");
        var exe = RequireReplaceableExecutable();
        if (_restoreSnapshotId == null)
        {
            var result = UpdateCompatibility.Evaluate(_release, _compatibility,
                UpdateCompatibility.ReadProfile(AppPaths.Default()), InstallKind);
            if (!result.CanInstall) throw new InvalidOperationException(result.Reason);
        }
        if (!await AppServices.Current.CloseComposeWindowsAsync())
        {
            SetHistoryStatus(UpdateHistory.Cancelled, "Installation postponed because a compose window remained open.");
            return;
        }
        if (BackupUi.IsBusy) throw new InvalidOperationException("Finish the settings backup or restore before changing application versions.");
        if (Application.Current.Windows.OfType<Magpie.App.Views.AddAccountWindow>().Any())
            throw new InvalidOperationException("Finish or close the account sign-in window before changing application versions.");
        _cts ??= new CancellationTokenSource();
        _cts.Token.ThrowIfCancellationRequested();
        if (_progressWindow == null) ShowProgressWindow();
        _disabledWindows = Application.Current.Windows.Cast<Window>().Where(w => w.IsEnabled && w != _progressWindow).ToArray();
        foreach (var window in _disabledWindows) window.IsEnabled = false;
        State = UpdateState.Preparing;
        Title = "Pausing mail before the final backup…";
        Detail = "Mail and editing will resume after Magpie restarts. You can still cancel before installation.";
        _engineStopped = true;
        AppServices.Tray?.Dispose(); AppServices.Tray = null;
        (Application.Current.MainWindow as MainWindow)?.SavePlacement();
        if (AppServices.Main != null) await AppServices.Main.CommitPendingBulk();
        if (AppServices.ModelRefresh != null) await AppServices.ModelRefresh.StopForVersionChangeAsync(_cts.Token);
        await AppServices.Engine.StopForVersionChangeAsync(_cts.Token);
        await CreateTransitionBackupAsync(exe);
        _cts.Token.ThrowIfCancellationRequested();
        if (!UpdateClient.MatchesSha256(_downloaded, _downloadedSha))
            throw new InvalidDataException("The selected application no longer matches its checksum. Installation was stopped.");
        if (_restoreSnapshotId is { } restoreId) VersionBackup.StageRestore(AppPaths.Default(), restoreId, InstallKind);
        State = UpdateState.Installing;
        Title = $"Starting Magpie v{_release.Version}…";
        Detail = "Your saved application and local data are available in recovery history.";
        await HandoffSelectedVersionAsync(exe);
    }

    private async Task HandoffSelectedVersionAsync(string exe)
    {
        if (_release is not { } selected || _downloaded is not { } downloaded || _snapshot == null || _historyId == null)
            throw new InvalidOperationException("The selected release has no complete recovery point.");
        VersionPin.Handoff? pin = null;
        var swapped = false;
        Process? child = null;
        try
        {
            // Persist older releases' legacy opt-out before touching the executable.
            pin = VersionPin.Prepare(AppServices.Engine.Settings, VersionPinPath, selected.Version);
            OnPropertyChanged(nameof(AutoUpdate));
            UpdateHistory.SetStatus(AppPaths.Default(), _historyId, UpdateHistory.Installing,
                "The exact selected application is starting; completion is recorded after its main window and data load.");
            InstalledUpdate.Write(DownloadFolder, Current.ToString(), selected.Version.ToString());
            SelfUpdate.Swap(exe, downloaded);
            swapped = true;
            using var started = WindowsStartup.CreateAcknowledgement();
            child = Process.Start(new ProcessStartInfo(exe, "--after-update " + Current) { UseShellExecute = false });
            if (child == null || !await Task.Run(() => started.WaitOne(TimeSpan.FromSeconds(25))))
                throw new TimeoutException("The selected application did not reach its startup handoff.");
            pin.Commit(); pin.Dispose(); pin = null;
            _engineStopped = false; // the selected child owns the next startup; do not launch another process in finally
            AppServices.Current.ExitForUpdate();
        }
        catch
        {
            var stopped = child == null;
            if (child != null)
            {
                try { if (!child.HasExited) child.Kill(); stopped = await Task.Run(() => child.WaitForExit(5000)); }
                catch (Exception ex) { Log.Error("could not stop the selected application", ex); }
            }
            var restored = VersionBackup.MatchesApplication(_snapshot, exe);
            if (!restored && stopped)
            {
                try { if (swapped || File.Exists(SelfUpdate.BackupPathFor(exe))) SelfUpdate.Rollback(exe); }
                catch (Exception ex) { Log.Error("could not recover the renamed application", ex); }
                try
                {
                    restored = VersionBackup.MatchesApplication(_snapshot, exe);
                    if (!restored) restored = WindowsVersionRecovery.RestoreApplicationSnapshot(exe, _snapshot);
                }
                catch (Exception ex) { Log.Error("could not put back the current application", ex); }
            }
            if (restored && stopped)
            {
                // The child waits for our single-instance lock, so it has not opened or migrated the profile yet.
                if (_restoreSnapshotId != null) VersionBackup.CancelPendingForVersion(AppPaths.Default(), selected.Version.ToString());
                pin?.Dispose(); pin = null;
            }
            else
            {
                _unsafeToRestart = true;
                pin?.Commit(); pin?.Dispose(); pin = null;
                Ui.Error("Recovery needs attention", "Magpie could not safely restore the previous application. " +
                    "Automatic installation remains off. The paired application/data backup has been kept in your profile; do not replace only the executable.");
            }
            if (!_unsafeToRestart) InstalledUpdate.Clear(DownloadFolder);
            throw;
        }
        finally { child?.Dispose(); }
    }

    private void RestartCurrentAfterAbort()
    {
        try
        {
            var exe = Environment.ProcessPath ?? throw new InvalidOperationException("The current application path is unavailable.");
            Process.Start(new ProcessStartInfo(exe, "--restart") { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            Ui.Error("Restart Magpie", "Magpie stopped its background work to protect your data. Close and reopen it to continue.\n\n" + ex.Message);
        }
        _engineStopped = false;
        AppServices.Current.ExitForUpdate();
    }

    /// <summary>A startup failure restores the saved app and stages its matching data before any old engine restarts.</summary>
    public static bool OfferRollback(Exception error)
    {
        var paths = AppPaths.Default();
        try
        {
            var transition = UpdateHistory.List(paths).FirstOrDefault(e => e.Status == UpdateHistory.Installing && e.To == Current.ToString());
            if (transition?.SnapshotId is not { } id) return false;
            var snapshot = VersionBackup.Read(paths, id);
            VersionBackup.Validate(snapshot);
            var answer = MessageBox.Show($"Magpie v{Current} could not start:\n\n{error.Message}\n\n" +
                $"Restore Magpie v{snapshot.Version} and the local data saved immediately before this change?",
                "Restore the last working version", MessageBoxButton.YesNo, MessageBoxImage.Error, MessageBoxResult.Yes);
            if (answer != MessageBoxResult.Yes) return false;
            var exe = Environment.ProcessPath ?? throw new InvalidOperationException("The application path is unavailable.");
            WindowsVersionRecovery.StageFailedStartup(paths, Current, exe, InstallKind, transition, snapshot);
            try { AppServices.Engine?.Dispose(); } catch { }
            // --restart waits for this process to exit, keeping all old writers away from the data restore.
            if (Process.Start(new ProcessStartInfo(exe, "--restart") { UseShellExecute = false }) == null)
                throw new InvalidOperationException("The saved application could not be started. Reopen Magpie to finish restoring its data.");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("paired application and data recovery failed", ex);
            MessageBox.Show("Magpie could not finish recovery:\n\n" + ex.Message + "\n\nYour recovery files have been retained.",
                "Magpie recovery", MessageBoxButton.OK, MessageBoxImage.Error);
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
