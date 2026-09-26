namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed class OwnedRunRootWriteFailureBuffer
{
    private sealed record Failure(string StorageRootPath, string Message);

    private readonly object _gate = new();
    private readonly LinkedList<Failure> _pending = new();
    private readonly Dictionary<string, Queue<LinkedListNode<Failure>>> _byStorageRoot = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public void Enqueue(string storageRootPath, string message)
    {
        lock (_gate)
        {
            if (!_byStorageRoot.TryGetValue(storageRootPath, out var queue))
                _byStorageRoot[storageRootPath] = queue = new Queue<LinkedListNode<Failure>>();
            queue.Enqueue(_pending.AddLast(new Failure(storageRootPath, message)));
        }
    }

    public IReadOnlyList<string> Drain(int maxCount, string? storageRootPath)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCount, 1);
        var failures = new List<string>(maxCount);
        lock (_gate)
        {
            if (storageRootPath is not null)
            {
                DrainRoot(storageRootPath, failures, maxCount);
            }
            else
            {
                while (failures.Count < maxCount && _pending.First is { } first)
                {
                    var queue = _byStorageRoot[first.Value.StorageRootPath];
                    if (!ReferenceEquals(queue.Dequeue(), first))
                        throw new InvalidOperationException("Owned-root write-failure queues are inconsistent.");
                    failures.Add(first.Value.Message);
                    _pending.RemoveFirst();
                    if (queue.Count == 0)
                        _byStorageRoot.Remove(first.Value.StorageRootPath);
                }
            }
        }
        return failures;
    }

    private void DrainRoot(string root, List<string> failures, int maxCount)
    {
        if (!_byStorageRoot.TryGetValue(root, out var queue))
            return;
        while (failures.Count < maxCount && queue.TryDequeue(out var failure))
        {
            failures.Add(failure.Value.Message);
            _pending.Remove(failure);
        }
        if (queue.Count == 0)
            _byStorageRoot.Remove(root);
    }
}
