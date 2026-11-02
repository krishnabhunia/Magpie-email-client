using System.Threading;

namespace Magpie.App.Services;

/// <summary>
/// A replacement acknowledges that its managed entry point is running, then waits for the previous process
/// to relinquish the profile. Acquire and dispose on the same thread: Windows mutex ownership is thread-bound.
/// </summary>
public sealed class WindowsStartup : IDisposable
{
    public const string MutexName = @"Local\Magpie.Mail.SingleInstance";
    public const string StartedEvent = @"Local\Magpie.Mail.UpdateStarted";
    private readonly Mutex _mutex;
    private bool _disposed;
    public bool OwnsInstance { get; private set; }

    private WindowsStartup(Mutex mutex, bool ownsInstance) => (_mutex, OwnsInstance) = (mutex, ownsInstance);

    public static EventWaitHandle CreateAcknowledgement(string name = StartedEvent) =>
        new(false, EventResetMode.ManualReset, name);

    public static WindowsStartup Acquire(bool afterUpdate, bool afterRestart, string mutexName = MutexName,
        string startedEvent = StartedEvent, TimeSpan? timeout = null)
    {
        // Failure to open the synchronization object is not proof of exclusive profile ownership.
        var mutex = new Mutex(true, mutexName, out var ownsInstance);
        try
        {
            if (afterUpdate)
            {
                // Establish that the mutex can be opened before accepting the handoff, but signal before
                // waiting: the old process must still own the profile until it receives this acknowledgement.
                try { if (EventWaitHandle.TryOpenExisting(startedEvent, out var started)) using (started) started.Set(); }
                catch { }
            }
            if (!ownsInstance && (afterUpdate || afterRestart))
            {
                try { ownsInstance = mutex.WaitOne(timeout ?? TimeSpan.FromSeconds(60)); }
                catch (AbandonedMutexException) { ownsInstance = true; }
            }
            return new WindowsStartup(mutex, ownsInstance);
        }
        catch { mutex.Dispose(); throw; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { if (OwnsInstance) _mutex.ReleaseMutex(); }
        finally { OwnsInstance = false; _mutex.Dispose(); }
    }
}
