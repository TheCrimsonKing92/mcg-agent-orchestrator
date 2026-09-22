namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>
/// Owns one native enumeration and memoizes identity reads for one caller operation.
/// Cached observations are reporting evidence only; destructive callers must revalidate live identity.
/// </summary>
internal sealed class ProcessInspectionOperation
{
    private const int MaxEdgeVerdicts = 8;
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
            .Where(seed => query.ProcessNames.Any(pattern => MatchesName(pattern, seed.Name)))
            .Select(seed => seed.ProcessId));

        IReadOnlyList<ProcessTreeEdgeVerdict> edgeVerdicts = [];
        var truncatedEdgeVerdictCount = 0;
        if (query.IncludeChildren)
        {
            (edgeVerdicts, truncatedEdgeVerdictCount) = AddDescendants(selected);
        }

        foreach (var processId in query.AncestorProcessIds)
        {
            AddAncestors(processId, selected);
        }

        return ReadSeeds(
            selected.OrderBy(id => id).Select(id => _seedsById[id]),
            query.ProcessIds) with
        {
            EdgeVerdicts = edgeVerdicts,
            TruncatedEdgeVerdictCount = truncatedEdgeVerdictCount
        };
    }

    private static bool MatchesName(string pattern, string processName) =>
        pattern.EndsWith('*')
            ? processName.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase)
            : processName.Equals(pattern, StringComparison.OrdinalIgnoreCase);

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
            result[seed.ProcessId] = ReadSeed(seed);
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

    private (IReadOnlyList<ProcessTreeEdgeVerdict> Verdicts, int TruncatedCount) AddDescendants(
        HashSet<int> selected)
    {
        var childrenByParent = _seeds
            .Where(seed => seed.ParentProcessId > 0)
            .GroupBy(seed => seed.ParentProcessId)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var queue = new Queue<(int ProcessId, ProcessTreeIdentityAnchor? Anchor)>(selected.Select(processId =>
        {
            var record = ReadSeed(_seedsById[processId]);
            return (processId, IdentityAnchor(record));
        }));
        var verdicts = new List<ProcessTreeEdgeVerdict>();
        var truncatedCount = 0;
        while (queue.TryDequeue(out var parentId))
        {
            if (!childrenByParent.TryGetValue(parentId.ProcessId, out var children))
            {
                continue;
            }

            foreach (var childSeed in children)
            {
                var child = ReadSeed(childSeed);
                var verdict = ProcessTreeEdgeEligibility.Evaluate(
                    parentId.ProcessId,
                    parentId.Anchor,
                    child);
                // Unverified edges remain visible and are diagnosed by the CLI from the
                // retained records. Rejected edges must be carried because the child and
                // its subtree are intentionally absent from the returned snapshot.
                if (verdict.Decision == ProcessTreeEdgeDecision.TemporalInversion)
                {
                    if (verdicts.Count < MaxEdgeVerdicts)
                    {
                        verdicts.Add(verdict);
                    }
                    else
                    {
                        truncatedCount++;
                    }
                }

                if (verdict.Decision == ProcessTreeEdgeDecision.TemporalInversion)
                {
                    continue;
                }

                if (selected.Add(childSeed.ProcessId))
                {
                    var childAnchor = verdict.Decision == ProcessTreeEdgeDecision.Eligible
                        ? IdentityAnchor(child)
                        : parentId.Anchor;
                    queue.Enqueue((childSeed.ProcessId, childAnchor));
                }
            }
        }

        return (verdicts, truncatedCount);
    }

    private ProcessInspectionRecord ReadSeed(WindowsNativeProcessInspection.ProcessInspectionSeed seed)
    {
        if (!_records.TryGetValue(seed.ProcessId, out var record))
        {
            record = _readOne(seed);
            _records[seed.ProcessId] = record;
        }

        return record;
    }

    private static ProcessTreeIdentityAnchor? IdentityAnchor(ProcessInspectionRecord record) =>
        record.Status is ProcessInspectionStatus.Exited or ProcessInspectionStatus.DeadOrRecycled
            ? null
            : record.StartedAt is { } startedAt
                ? new ProcessTreeIdentityAnchor(record.ProcessId, startedAt)
                : null;

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
