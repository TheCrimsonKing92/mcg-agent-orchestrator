namespace Mcg.AgentOrchestrator.Infrastructure;

// A focused member owns this acceptance occupant only while its checks make progress.
internal sealed class AttributionOccupancyHeartbeat : IDisposable
{
    private readonly object _gate = new();
    private readonly string? _path;
    private readonly string _goalId;
    private readonly string _cohortId;
    private readonly int? _slotIndex;
    private readonly Func<DateTimeOffset> _clock;
    private readonly DateTimeOffset _startedAt;
    private DateTimeOffset _newestProgressAt;
    private DateTimeOffset _lastWriteAt;
    private string _target;
    private bool _progressWritten;
    private bool _disposed;

    private AttributionOccupancyHeartbeat(string goalId, int? slotIndex, string cohortId, int ordinal,
        DotnetBuildStorageRoot storageRoot, Func<DateTimeOffset> clock)
    {
        _goalId = goalId;
        _cohortId = cohortId;
        _slotIndex = slotIndex;
        _clock = clock;
        _startedAt = _newestProgressAt = clock();
        _target = $"{cohortId} member={ordinal}";
        if (slotIndex is >= 0 and < DotnetBuildEnvironmentManager.StableSlotCount)
            _path = GateHeartbeatArtifacts.GetRunScopedStableSlotPath(
                slotIndex.Value, cohortId + "|attribution-" + ordinal, storageRoot);
    }

    internal static AttributionOccupancyHeartbeat Start(string goalId, int? slotIndex, string cohortId,
        int ordinal, DotnetBuildStorageRoot storageRoot, Func<DateTimeOffset> clock)
    {
        var heartbeat = new AttributionOccupancyHeartbeat(goalId, slotIndex, cohortId, ordinal, storageRoot, clock);
        lock (heartbeat._gate) heartbeat.Write(heartbeat._startedAt);
        return heartbeat;
    }

    internal void Observe(AcceptanceGateProgress progress)
    {
        lock (_gate)
        {
            if (_disposed || _path is null || progress.LastProgressAt <= _newestProgressAt) return;
            _newestProgressAt = progress.LastProgressAt;
            _target = progress.CurrentTarget;
            var now = _clock();
            if (_progressWritten && now - _lastWriteAt < TimeSpan.FromSeconds(15)) return;
            Write(now);
            _progressWritten = true;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (_path is not null) GateHeartbeatArtifacts.TryDelete(_path);
        }
    }

    private void Write(DateTimeOffset now)
    {
        if (_path is null) return;
        GateHeartbeatArtifacts.TryWrite(_path, new(
            _goalId, "cohort-attribution", _target, _slotIndex, Environment.ProcessId, Environment.ProcessId,
            "running", _startedAt, now, _newestProgressAt, 0, 0, 0,
            RunClass: GateHeartbeatRunClass.Acceptance, RunId: _cohortId));
        _lastWriteAt = now;
    }
}
