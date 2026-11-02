namespace Magpie.Core.Updates;

/// <summary>Prevents new work and waits for already-started async operations before a version snapshot.</summary>
internal sealed class VersionChangeOperations
{
    private readonly object _gate = new();
    private bool _stopped;
    private int _active;
    private TaskCompletionSource? _drained;
    private readonly AsyncLocal<Lease?> _current = new();

    public IDisposable Enter()
    {
        lock (_gate)
        {
            var parent = _current.Value;
            // A previously admitted action must be allowed to finish its nested local cleanup (for example, an
            // accepted send removing its saved draft). A fresh UI callback has no live admission and is refused.
            if (_stopped && parent?.IsActive != true) throw new OperationCanceledException("Magpie is stopping for a version change.");
            _active++;
            var lease = new Lease(this, parent);
            _current.Value = lease;
            return lease;
        }
    }

    public Task StopAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            _stopped = true;
            if (_active == 0) return Task.CompletedTask;
            _drained ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
            return _drained.Task.WaitAsync(ct);
        }
    }

    private sealed class Lease(VersionChangeOperations owner, Lease? parent) : IDisposable
    {
        private VersionChangeOperations? _owner = owner;
        public bool IsActive => Volatile.Read(ref _owner) != null;
        public void Dispose()
        {
            var instance = Interlocked.Exchange(ref _owner, null);
            if (instance == null) return;
            lock (instance._gate)
            {
                if (ReferenceEquals(instance._current.Value, this)) instance._current.Value = parent;
                instance._active--;
                if (instance._active == 0) instance._drained?.TrySetResult();
            }
        }
    }
}
