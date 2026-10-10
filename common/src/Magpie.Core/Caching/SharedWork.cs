using System.Collections.Concurrent;

namespace Magpie.Core.Caching;

/// <summary>One operation per key; cancelling a caller's wait does not cancel other callers' work.</summary>
internal sealed class SharedWork<TKey, TValue> where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, Lazy<Task<TValue>>> _work = new();

    public Task<TValue> RunAsync(TKey key, Func<Task<TValue>> factory, CancellationToken waitToken)
    {
        waitToken.ThrowIfCancellationRequested();
        return _work.GetOrAdd(key, _ => Create(key, factory)).Value.WaitAsync(waitToken);
    }

    private Lazy<Task<TValue>> Create(TKey key, Func<Task<TValue>> factory)
    {
        Lazy<Task<TValue>> entry = null!;
        entry = new Lazy<Task<TValue>>(() =>
        {
            Task<TValue> task;
            try { task = factory(); }
            catch (Exception ex) { task = Task.FromException<TValue>(ex); }
            _ = task.ContinueWith(t =>
            {
                if (t.IsFaulted) _ = t.Exception; // Observe failures even after every reader stopped waiting.
                _work.TryRemove(new KeyValuePair<TKey, Lazy<Task<TValue>>>(key, entry));
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return task;
        });
        return entry;
    }
}
