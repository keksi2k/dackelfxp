using DackelFxp.Core.Infrastructure;

namespace DackelFxp.Core.Queue;

/// <summary>
/// Thread-safe ordered list of transfers. The engine works on it directly; the UI mirrors it
/// via the events, which are raised through <see cref="CoreDispatcher"/>.
/// </summary>
public sealed class TransferQueue
{
    private readonly Lock _lock = new();
    private readonly List<QueueItem> _items = [];

    /// <summary>Items inserted at the given index.</summary>
    public event Action<int, IReadOnlyList<QueueItem>>? Inserted;
    public event Action<IReadOnlyList<QueueItem>>? Removed;
    /// <summary>The order changed; the argument is the new complete order.</summary>
    public event Action<IReadOnlyList<QueueItem>>? Reordered;
    /// <summary>Signalled when new work may be available.</summary>
    public event Action? WorkAvailable;

    public IReadOnlyList<QueueItem> Snapshot()
    {
        lock (_lock) return _items.ToList();
    }

    public int Count
    {
        get { lock (_lock) return _items.Count; }
    }

    public void Add(IEnumerable<QueueItem> items) => Insert(int.MaxValue, items);

    /// <summary>Inserts directly after <paramref name="after"/> (directory expansion keeps children together).</summary>
    public void InsertAfter(QueueItem after, IEnumerable<QueueItem> items)
    {
        int index;
        lock (_lock)
        {
            index = _items.IndexOf(after);
            // Skip already inserted siblings/descendants of the same parent so order stays stable.
            index = index < 0 ? _items.Count : index + 1;
        }
        Insert(index, items);
    }

    private void Insert(int index, IEnumerable<QueueItem> items)
    {
        var list = items.ToList();
        if (list.Count == 0) return;
        lock (_lock)
        {
            var at = Math.Clamp(index, 0, _items.Count);
            _items.InsertRange(at, list);
            Notify(() => Inserted?.Invoke(at, list));
        }
        WorkAvailable?.Invoke();
    }

    public void Remove(IEnumerable<QueueItem> items)
    {
        var set = items.ToHashSet();
        lock (_lock)
        {
            var removed = _items.Where(i => set.Contains(i) && i.Status != QueueStatus.Running).ToList();
            _items.RemoveAll(removed.Contains);
            if (removed.Count > 0) Notify(() => Removed?.Invoke(removed));
        }
    }

    public void RemoveFinished() => Remove(Snapshot().Where(i => i.Status is QueueStatus.Done or QueueStatus.Skipped));

    public void Clear() => Remove(Snapshot());

    /// <summary>Moves items up (negative) or down (positive), or to top/bottom with int.MinValue/int.MaxValue.</summary>
    public void Move(IReadOnlyCollection<QueueItem> items, int delta)
    {
        lock (_lock)
        {
            var moving = _items.Where(items.Contains).ToList();
            if (moving.Count == 0) return;
            if (delta == int.MinValue || delta == int.MaxValue)
            {
                _items.RemoveAll(moving.Contains);
                _items.InsertRange(delta == int.MinValue ? 0 : _items.Count, moving);
            }
            else
            {
                var sequence = delta < 0 ? moving : Enumerable.Reverse(moving);
                foreach (var item in sequence)
                {
                    var from = _items.IndexOf(item);
                    var to = Math.Clamp(from + delta, 0, _items.Count - 1);
                    _items.RemoveAt(from);
                    _items.Insert(to, item);
                }
            }
            var order = _items.ToList();
            Notify(() => Reordered?.Invoke(order));
        }
    }

    /// <summary>Takes the next pending item (marks it Running). Items whose endpoints are busy can be excluded.</summary>
    public QueueItem? TakeNext(Func<QueueItem, bool>? canRun = null)
    {
        lock (_lock)
        {
            var now = DateTimeOffset.Now;
            foreach (var item in _items)
            {
                if (item.Status != QueueStatus.Pending) continue;
                if (item.NotBefore is { } nb && nb > now) continue;
                if (canRun is not null && !canRun(item)) continue;
                item.Status = QueueStatus.Running;
                return item;
            }
            return null;
        }
    }

    public bool HasPendingWork()
    {
        lock (_lock) return _items.Any(i => i.Status is QueueStatus.Pending or QueueStatus.Running);
    }

    public void SetStatus(IEnumerable<QueueItem> items, QueueStatus status)
    {
        foreach (var item in items)
        {
            if (item.Status == QueueStatus.Running) continue;
            item.Status = status;
            if (status == QueueStatus.Pending)
            {
                item.Error = null;
                item.Attempts = 0;
                item.NotBefore = null;
            }
        }
        WorkAvailable?.Invoke();
    }

    public void Save(string file) => Json.Save(file, Snapshot().Where(i => i.Status != QueueStatus.Done).ToList());

    public void Load(string file, bool append = true)
    {
        var items = Json.Load<List<QueueItem>>(file) ?? [];
        foreach (var item in items)
            if (item.Status is QueueStatus.Running) item.Status = QueueStatus.Pending;
        if (!append) Clear();
        Add(items);
    }

    private static void Notify(Action action) => CoreDispatcher.Post(action);
}
