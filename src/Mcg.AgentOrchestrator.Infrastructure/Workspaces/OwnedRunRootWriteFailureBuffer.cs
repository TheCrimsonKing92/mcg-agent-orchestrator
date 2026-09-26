namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed class OwnedRunRootWriteFailureBuffer
{
    private sealed record Failure(string? StorageRootPath, string Message);

    private readonly object _gate = new();
    private readonly LinkedList<Failure> _pending = new();
    private readonly Dictionary<string, Queue<LinkedListNode<Failure>>> _byStorageRoot = new(
        StringComparer.OrdinalIgnoreCase);

    public void Enqueue(string? storageRootPath, string message)
    {
        var root = storageRootPath is null ? null : NormalizeRoot(storageRootPath);
        lock (_gate)
        {
            if (root is null)
            {
                _pending.AddLast(new Failure(null, message));
                return;
            }
            if (!_byStorageRoot.TryGetValue(root, out var queue))
                _byStorageRoot[root] = queue = new Queue<LinkedListNode<Failure>>();
            queue.Enqueue(_pending.AddLast(new Failure(root, message)));
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
                DrainRoot(NormalizeRoot(storageRootPath), failures, maxCount);
            }
            else
            {
                while (failures.Count < maxCount && _pending.First is { } first)
                {
                    if (first.Value.StorageRootPath is { } root)
                    {
                        var queue = _byStorageRoot[root];
                        if (!ReferenceEquals(queue.Dequeue(), first))
                            throw new InvalidOperationException("Owned-root write-failure queues are inconsistent.");
                        if (queue.Count == 0)
                            _byStorageRoot.Remove(root);
                    }
                    failures.Add(first.Value.Message);
                    _pending.RemoveFirst();
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

    private static string NormalizeRoot(string root) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
}
