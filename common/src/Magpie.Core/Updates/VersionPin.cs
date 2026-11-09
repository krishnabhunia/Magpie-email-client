using Magpie.Core.Settings;

namespace Magpie.Core.Updates;

/// <summary>
/// Persists an exact version choice before another executable starts. Older releases do not read the pin,
/// so their automatic-update settings must also be disabled in the shared settings file.
/// </summary>
public static class VersionPin
{
    /// <summary>
    /// Prepares a handoff, throwing if the pin or legacy opt-out cannot be saved. Commit after a successful
    /// handoff; otherwise disposing restores the previous pin and update flags without replacing other settings.
    /// </summary>
    public static Handoff Prepare(SettingsStore settings, string pinPath, AppVersion selected)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentException.ThrowIfNullOrWhiteSpace(pinPath);
        var handoff = new Handoff(settings, Path.GetFullPath(pinPath));
        try
        {
            handoff.Persist(selected);
            return handoff;
        }
        catch (Exception preparationError)
        {
            try { handoff.Dispose(); }
            catch (Exception recoveryError)
            {
                throw new AggregateException("The selected version could not be prepared and the previous update settings could not be fully restored.",
                    preparationError, recoveryError);
            }
            throw;
        }
    }

    /// <summary>
    /// Only going back (a rollback) pins the selected version and pauses automatic checks. Moving to a newer
    /// version must leave checks running, so the next release still shows "Update to vx.y.z" after a restart.
    /// </summary>
    public static bool PinsAfterChange(AppVersion current, AppVersion selected) => selected.CompareTo(current) < 0;

    /// <summary>
    /// The handoff to a newer version. Without a pin, the user's Auto update choice stays as it is. A pin left by an
    /// earlier rollback ends here, and the automatic checks that rollback paused are switched back on (the choice
    /// from before the rollback isn't kept anywhere; checking on every start is the standing rule). Commit after a
    /// successful handoff; otherwise disposing puts the previous pin and switches back.
    /// </summary>
    public static Handoff PrepareForward(SettingsStore settings, string pinPath)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(pinPath);
        var handoff = new Handoff(settings, Path.GetFullPath(pinPath));
        try
        {
            handoff.PersistForward();
            return handoff;
        }
        catch (Exception preparationError)
        {
            try { handoff.Dispose(); }
            catch (Exception recoveryError)
            {
                throw new AggregateException("The selected version could not be prepared and the previous update settings could not be fully restored.",
                    preparationError, recoveryError);
            }
            throw;
        }
    }

    /// <summary>
    /// Runs once the running version has started successfully. Before 18.2.1 every version change, also an update to
    /// a newer version, pinned that version and switched Auto update off, so no newer release was offered again.
    /// Clears such a pin and switches automatic checks back on when <paramref name="running"/> was reached by moving
    /// forward: the newest history entry into it (failed or cancelled ones aside) is a successful "Update" from an
    /// older version, or the pin names an older version than the one running (e.g. the installer was run after a
    /// rollback). A pin from a rollback or a restore of the running version is kept, also while that entry hasn't
    /// been marked successful yet; with an unreadable history entry nothing is changed. Returns true when something
    /// changed. Settings are saved before the pin is removed, so a failed save leaves the pin for the next start.
    /// </summary>
    public static bool ReleaseAfterForwardMove(SettingsStore settings, string pinPath, AppVersion running,
        IEnumerable<UpdateHistoryEntry> history)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(running);
        if (!File.Exists(pinPath)) return false;
        var entries = history.ToList();
        if (entries.Any(e => e.Action == "History record")) return false;   // unreadable: it may be the rollback
        var pinned = AppVersion.TryParse(File.ReadAllText(pinPath).Trim());
        bool forward;
        if (pinned == null) forward = true;                              // unreadable: it can't protect anything
        else if (pinned.CompareTo(running) > 0) return false;           // running an older version than the pin
        else if (pinned.CompareTo(running) < 0) forward = true;         // moved past the pinned version
        else
        {
            var into = entries
                .Where(e => e.To == running.ToString() && e.Status is not (UpdateHistory.Failed or UpdateHistory.Cancelled))
                .OrderByDescending(e => e.Created)
                .FirstOrDefault();
            forward = into is { Action: "Update", Status: UpdateHistory.Succeeded }
                      && AppVersion.TryParse(into.From) is { } from && from.CompareTo(running) < 0;
        }
        if (!forward) return false;
        var updates = settings.Current.Updates;
        updates.AutoUpdate = updates.AutoCheck = updates.AutoDownload = true;
        settings.Save(notify: false);
        File.Delete(pinPath);
        return true;
    }

    private static void WriteAtomically(string path, string content)
    {
        var temporary = path + ".next-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, content);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            // A cleanup error must not hide the original persistence error.
            try { File.Delete(temporary); } catch { }
        }
    }

    public sealed class Handoff : IDisposable
    {
        private readonly SettingsStore _settings;
        private readonly string _pinPath;
        private readonly string? _previousPin;
        private readonly bool? _autoUpdate;
        private readonly bool _autoCheck;
        private readonly bool _autoDownload;
        private bool _pinWritten;
        private bool _settingsWritten;
        private bool _finished;

        internal Handoff(SettingsStore settings, string pinPath)
        {
            _settings = settings;
            _pinPath = pinPath;
            _previousPin = File.Exists(pinPath) ? File.ReadAllText(pinPath) : null;
            var updates = settings.Current.Updates;
            (_autoUpdate, _autoCheck, _autoDownload) = (updates.AutoUpdate, updates.AutoCheck, updates.AutoDownload);
        }

        internal void Persist(AppVersion selected)
        {
            WriteAtomically(_pinPath, selected.ToString());
            _pinWritten = true;
            var updates = _settings.Current.Updates;
            // Save normalises both legacy switches from AutoUpdate, so all three must be false.
            updates.AutoUpdate = updates.AutoCheck = updates.AutoDownload = false;
            _settings.Save(notify: false);
            _settingsWritten = true;
        }

        /// <summary>A newer version: no pin. Only a pin left by a rollback is removed, with the checks it paused.</summary>
        internal void PersistForward()
        {
            if (_previousPin == null) return;   // nothing pinned: the user's own Auto update choice stays
            File.Delete(_pinPath);
            _pinWritten = true;
            var updates = _settings.Current.Updates;
            updates.AutoUpdate = updates.AutoCheck = updates.AutoDownload = true;
            _settings.Save(notify: false);
            _settingsWritten = true;
        }

        /// <summary>The selected executable accepted the handoff; retain its pin and legacy opt-out.</summary>
        public void Commit() => _finished = true;

        public void Dispose()
        {
            if (_finished) return;
            _finished = true;
            var failures = new List<Exception>();
            var updates = _settings.Current.Updates;
            (updates.AutoUpdate, updates.AutoCheck, updates.AutoDownload) = (_autoUpdate, _autoCheck, _autoDownload);
            // SettingsStore.Save replaces the file atomically. A failed first save left the prior file intact.
            if (_settingsWritten)
            {
                try { _settings.Save(notify: false); }
                catch (Exception ex) { failures.Add(ex); }
            }
            if (_pinWritten)
            {
                try
                {
                    if (_previousPin == null) File.Delete(_pinPath);
                    else WriteAtomically(_pinPath, _previousPin);
                }
                catch (Exception ex) { failures.Add(ex); }
            }
            if (failures.Count > 0)
                throw new AggregateException("The previous version pin or update settings could not be restored.", failures);
        }
    }
}
