namespace Magpie.Core.Caching;

/// <summary>A thread-safe least-recently-used cache bounded by both entries and retained bytes.</summary>
public sealed class LruCache<TKey, TValue> where TKey : notnull
{
    private readonly object _gate = new();
    private readonly int _capacity;
    private readonly long _maxBytes;
    private readonly Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value, long Bytes)>> _items = new();
    private readonly LinkedList<(TKey Key, TValue Value, long Bytes)> _recent = new();
    private long _bytes;

    public LruCache(int capacity, long maxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        _capacity = capacity;
        _maxBytes = maxBytes;
    }

    public int Count { get { lock (_gate) return _items.Count; } }
    public long Bytes { get { lock (_gate) return _bytes; } }

    public bool TryGet(TKey key, out TValue value)
    {
        lock (_gate)
        {
            if (!_items.TryGetValue(key, out var node)) { value = default!; return false; }
            _recent.Remove(node);
            _recent.AddLast(node);
            value = node.Value.Value;
            return true;
        }
    }

    public void Set(TKey key, TValue value, long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        lock (_gate)
        {
            RemoveCore(key);
            if (bytes > _maxBytes) return; // A single large newsletter must not evict the entire cache.
            while (_items.Count >= _capacity || _bytes > _maxBytes - bytes)
                RemoveCore(_recent.First!.Value.Key);
            var node = _recent.AddLast((key, value, bytes));
            _items.Add(key, node);
            _bytes += bytes;
        }
    }

    public void Remove(TKey key) { lock (_gate) RemoveCore(key); }
    private void RemoveCore(TKey key)
    {
        if (!_items.Remove(key, out var node)) return;
        _recent.Remove(node);
        _bytes -= node.Value.Bytes;
    }

    public void Clear()
    {
        lock (_gate) { _items.Clear(); _recent.Clear(); _bytes = 0; }
    }
}
