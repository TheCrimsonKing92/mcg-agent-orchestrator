namespace Mcg.AgentOrchestrator.Infrastructure;

// One gate attempt owns this file; parallel remote lanes share its lifetime.
internal sealed class RemoteLaneOccupancyHeartbeat(
    string? goalId, int slotIndex, DotnetBuildStorageRoot storageRoot, string? runClass, string? runId)
{
    private readonly object _gate = new();
    private readonly SortedSet<string> _lanes = new(StringComparer.Ordinal);
    private DateTimeOffset? _startedAt;
    private DateTimeOffset _lastWriteAt;
    private DateTimeOffset _lastProgressAt;

    internal void Beat(string lane, DateTimeOffset remoteHeartbeat, DateTimeOffset now)
    {
        if (slotIndex < 0 || slotIndex >= DotnetBuildEnvironmentManager.StableSlotCount) return;
        lock (_gate)
        {
            var changed = _lanes.Add(lane);
            _startedAt ??= now;
            if (remoteHeartbeat > _lastProgressAt) _lastProgressAt = remoteHeartbeat;
            if (changed || now - _lastWriteAt >= TimeSpan.FromSeconds(15)) Write("running", now);
        }
    }

    internal void End(string lane, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_lanes.Remove(lane)) return;
            Write(_lanes.Count == 0 ? "completed" : "running", now);
        }
    }

    private void Write(string state, DateTimeOffset now)
    {
        var path = GateHeartbeatArtifacts.GetRunScopedStableSlotPath(
            slotIndex, (runId ?? goalId ?? string.Empty) + "|remote-lane-wait", storageRoot);
        GateHeartbeatArtifacts.TryWrite(path, new(
            goalId, "remote-lane-wait", string.Join(",", _lanes), slotIndex,
            Environment.ProcessId, Environment.ProcessId, state, _startedAt!.Value,
            now, _lastProgressAt, 0, 0, 0, RunClass: runClass, RunId: runId));
        _lastWriteAt = now;
    }
}
