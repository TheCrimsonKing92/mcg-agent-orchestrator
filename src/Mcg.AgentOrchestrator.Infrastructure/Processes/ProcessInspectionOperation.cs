namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>
/// Owns one native enumeration and memoizes identity reads for one caller operation.
/// Cached observations are reporting evidence only; destructive callers must revalidate live identity.
/// </summary>
internal sealed class ProcessInspectionOperation
{
    private readonly IReadOnlyList<WindowsNativeProcessInspection.ProcessInspectionSeed> _seeds;
    private readonly IReadOnlyDictionary<int, WindowsNativeProcessInspection.ProcessInspectionSeed> _seedsById;
    private readonly Func<WindowsNativeProcessInspection.ProcessInspectionSeed, ProcessInspectionRecord> _readOne;
    private readonly Dictionary<int, ProcessInspectionRecord> _records = [];
    private readonly ProcessInspectionFailure? _failure;

    internal ProcessInspectionOperation(
        WindowsNativeProcessInspection.ProcessEnumerationResult enumeration,
        Func<WindowsNativeProcessInspection.ProcessInspectionSeed, ProcessInspectionRecord> readOne)
    {
        _failure = enumeration.Failure;
        _seeds = enumeration.Processes
            .Where(seed => seed.ProcessId > 0)
            .GroupBy(seed => seed.ProcessId)
            .Select(group => group.First())
            .ToArray();
        _seedsById = _seeds.ToDictionary(seed => seed.ProcessId);
        _readOne = readOne;
    }

    internal WindowsNativeProcessInspection.ProcessInspectionResult ReadAll() =>
        ReadSeeds(_seeds, includeMissingIds: null);

    internal WindowsNativeProcessInspection.ProcessInspectionResult ReadRequested(IEnumerable<int> processIds)
    {
        var ids = processIds.Where(id => id > 0).Distinct().ToArray();
        return ReadSeeds(
            ids.Where(_seedsById.ContainsKey).Select(id => _seedsById[id]),
            ids);
    }

    internal WindowsNativeProcessInspection.ProcessInspectionResult ReadByNames(IReadOnlySet<string> processNames) =>
        ReadSeeds(_seeds.Where(seed => processNames.Contains(seed.Name)), includeMissingIds: null);

    internal WindowsNativeProcessInspection.ProcessInspectionResult ReadCandidates(ProcessInspectionQuery query)
    {
        if (_failure is not null)
        {
            return WindowsNativeProcessInspection.ProcessInspectionResult.Failed(_failure);
        }

        var selected = new HashSet<int>();
        if (query.IncludeAll)
        {
            selected.UnionWith(_seedsById.Keys);
        }

        selected.UnionWith(query.ProcessIds.Where(_seedsById.ContainsKey));
        selected.UnionWith(_seeds
            .Where(seed => query.ParentProcessIds.Contains(seed.ParentProcessId))
            .Select(seed => seed.ProcessId));
        selected.UnionWith(_seeds
            .Where(seed => query.ProcessNames.Contains(seed.Name))
            .Select(seed => seed.ProcessId));

        if (query.IncludeChildren)
        {
            AddDescendants(selected);
        }

        foreach (var processId in query.AncestorProcessIds)
        {
            AddAncestors(processId, selected);
        }

        return ReadSeeds(
            selected.OrderBy(id => id).Select(id => _seedsById[id]),
            query.ProcessIds);
    }

    private WindowsNativeProcessInspection.ProcessInspectionResult ReadSeeds(
        IEnumerable<WindowsNativeProcessInspection.ProcessInspectionSeed> seeds,
        IEnumerable<int>? includeMissingIds)
    {
        if (_failure is not null)
        {
            return WindowsNativeProcessInspection.ProcessInspectionResult.Failed(_failure);
        }

        var result = new Dictionary<int, ProcessInspectionRecord>();
        foreach (var seed in seeds)
        {
            if (!_records.TryGetValue(seed.ProcessId, out var record))
            {
                record = _readOne(seed);
                _records[seed.ProcessId] = record;
            }

            result[seed.ProcessId] = record;
        }

        if (includeMissingIds is not null)
        {
            foreach (var processId in includeMissingIds.Where(id => id > 0).Distinct())
            {
                if (!result.ContainsKey(processId) && !_seedsById.ContainsKey(processId))
                {
                    result[processId] = new ProcessInspectionRecord(
                        processId,
                        0,
                        string.Empty,
                        null,
                        null,
                        null,
                        ProcessInspectionStatus.Exited);
                }
            }
        }

        return WindowsNativeProcessInspection.ProcessInspectionResult.Success(result);
    }

    private void AddDescendants(HashSet<int> selected)
    {
        var childrenByParent = _seeds
            .Where(seed => seed.ParentProcessId > 0)
            .GroupBy(seed => seed.ParentProcessId)
            .ToDictionary(group => group.Key, group => group.Select(seed => seed.ProcessId).ToArray());
        var queue = new Queue<int>(selected);
        while (queue.TryDequeue(out var parentId))
        {
            if (!childrenByParent.TryGetValue(parentId, out var children))
            {
                continue;
            }

            foreach (var childId in children)
            {
                if (selected.Add(childId))
                {
                    queue.Enqueue(childId);
                }
            }
        }
    }

    private void AddAncestors(int processId, HashSet<int> selected)
    {
        var seen = new HashSet<int>();
        while (processId > 0 && seen.Add(processId) && _seedsById.TryGetValue(processId, out var seed))
        {
            selected.Add(processId);
            processId = seed.ParentProcessId;
        }
    }
}

internal sealed record ProcessInspectionQuery(
    IReadOnlySet<int> ProcessIds,
    IReadOnlySet<int> ParentProcessIds,
    IReadOnlySet<string> ProcessNames,
    bool IncludeChildren,
    bool IncludeAll,
    IReadOnlySet<int> AncestorProcessIds);
