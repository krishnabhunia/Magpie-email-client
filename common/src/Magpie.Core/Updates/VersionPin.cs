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
