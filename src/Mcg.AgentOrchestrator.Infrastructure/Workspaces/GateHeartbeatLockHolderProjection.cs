namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class GateHeartbeatLockHolderProjection
{
    internal static IEnumerable<BuildLockHolder> Build(
        GateHeartbeatSnapshot? snapshot,
        DotnetBuildEnvironment environment,
        Func<IEnumerable<int>, ProcessCommandLineSnapshot>? processSnapshotFactory = null)
    {
        if (snapshot is null ||
            snapshot.CommandLine is not null &&
            !snapshot.CommandLine.Contains(environment.ArtifactsPath, StringComparison.OrdinalIgnoreCase))
        {
            yield break;
        }

        var pids = new[] { snapshot.ProcessId, snapshot.ChildPid }
            .Where(pid => pid.HasValue)
            .Select(pid => pid!.Value)
            .Distinct()
            .ToArray();
        var processSnapshot = (processSnapshotFactory ?? ProcessCommandLines.Snapshot)(pids);
        foreach (var pid in pids)
        {
            if (!processSnapshot.TryGetRecord(pid, out var record))
            {
                yield return new BuildLockHolder(
                    pid,
                    "process-inspection-unavailable",
                    snapshot.CommandLine,
                    false);
                continue;
            }

            var available = record.Status == ProcessInspectionStatus.Available;
            yield return new BuildLockHolder(
                pid,
                available && !string.IsNullOrWhiteSpace(record.Name)
                    ? record.Name
                    : $"process-inspection-{record.Status}",
                string.IsNullOrWhiteSpace(record.CommandLine) ? snapshot.CommandLine : record.CommandLine,
                available,
                record.StartedAt);
        }
    }
}
