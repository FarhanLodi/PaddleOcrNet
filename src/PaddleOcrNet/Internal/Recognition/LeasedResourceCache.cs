namespace PaddleOcrNet.Internal.Recognition;

/// <summary>A strict count-bounded LRU cache. Loading and leased resources cannot be evicted.</summary>
internal sealed class LeasedResourceCache<T> : IAsyncDisposable where T : class, IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _capacity;
    private TaskCompletionSource _changed = NewSignal();
    private bool _closed;
    private long _clock;

    internal LeasedResourceCache(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        _capacity = capacity;
    }

    internal async Task<Lease> AcquireAsync(string key, Func<Task<T>> loader, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Entry? entry = null;
            Task? wait = null;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_closed, this);
                if (!_entries.TryGetValue(key, out entry))
                {
                    if (_capacity > 0 && _entries.Count >= _capacity)
                    {
                        var idle = _entries.Values.Where(e => e.Users == 0 && e.Load.IsCompleted).MinBy(e => e.LastUsed);
                        if (idle is not null)
                        {
                            _entries.Remove(idle.Key);
                            DisposeLoaded(idle); // Finish native disposal before admitting another resident.
                        }
                    }
                    if (_capacity == 0 || _entries.Count < _capacity)
                    {
                        entry = new Entry(key, Task.Run(loader));
                        _entries.Add(key, entry);
                        _ = entry.Load.ContinueWith(_ => OnLoaded(entry), CancellationToken.None,
                            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    }
                    else wait = _changed.Task;
                }
                if (entry is not null) { entry.Users++; entry.LastUsed = ++_clock; }
            }
            if (entry is null)
            {
                await wait!.WaitAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }
            try
            {
                var resource = await entry.Load.WaitAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return new Lease(resource, () => Release(entry));
            }
            catch { Release(entry); throw; }
        }
    }

    private void OnLoaded(Entry entry)
    {
        lock (_gate)
        {
            RemoveFailedIdle(entry);
            Pulse();
        }
    }

    private void Release(Entry entry)
    {
        lock (_gate)
        {
            entry.Users--;
            entry.LastUsed = ++_clock;
            RemoveFailedIdle(entry);
            Pulse();
        }
    }

    private void RemoveFailedIdle(Entry entry)
    {
        if (entry.Users == 0 && (entry.Load.IsFaulted || entry.Load.IsCanceled)
            && _entries.TryGetValue(entry.Key, out var current) && ReferenceEquals(current, entry))
        {
            _ = entry.Load.Exception; // Observe shared faults even if every waiter canceled.
            _entries.Remove(entry.Key);
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate) { _closed = true; Pulse(); }
        while (true)
        {
            Task wait;
            lock (_gate)
            {
                if (_entries.Values.All(e => e.Users == 0 && e.Load.IsCompleted))
                {
                    foreach (var entry in _entries.Values) DisposeLoaded(entry);
                    _entries.Clear();
                    return;
                }
                wait = _changed.Task;
            }
            await wait.ConfigureAwait(false);
        }
    }

    private static void DisposeLoaded(Entry entry)
    {
        if (entry.Load.IsCompletedSuccessfully)
        {
            try { entry.Load.Result.Dispose(); }
            catch { /* Native cleanup is best effort, as in engine shutdown. */ }
        }
        else _ = entry.Load.Exception;
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private void Pulse() { var previous = _changed; _changed = NewSignal(); previous.TrySetResult(); }

    private sealed class Entry(string key, Task<T> load)
    {
        internal string Key { get; } = key;
        internal Task<T> Load { get; } = load;
        internal int Users;
        internal long LastUsed;
    }

    internal sealed class Lease(T value, Action release) : IDisposable
    {
        private Action? _release = release;
        internal T Value { get; } = value;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
