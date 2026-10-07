namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed class GateHeartbeatRuntime
{
    private readonly GateHeartbeatContext _context;
    private readonly int _processId;
    private readonly string _stdoutPath;
    private readonly string _stderrPath;
    private readonly TimeSpan _timeout;
    private readonly Action<AcceptanceGateProgress>? _progressSink;
    private readonly TimeSpan _progressInterval;
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private DateTimeOffset _lastProgressAt;
    private DateTimeOffset _lastProgressEmittedAt = DateTimeOffset.MinValue;
    private long _lastOutputBytes = -1;

    public GateHeartbeatRuntime(
        GateHeartbeatContext context,
        int processId,
        string stdoutPath,
        string stderrPath,
        TimeSpan timeout,
        Action<AcceptanceGateProgress>? progressSink = null,
        TimeSpan? progressInterval = null)
    {
        _context = context;
        _processId = processId;
        _stdoutPath = stdoutPath;
        _stderrPath = stderrPath;
        _timeout = timeout;
        _progressSink = progressSink;
        _progressInterval = progressInterval ?? TimeSpan.FromSeconds(30);
        _lastProgressAt = _startedAt;
    }

    public void WriteRunning(bool emitProgress)
    {
        var snapshot = BuildSnapshot("running", childPid: _processId);
        GateHeartbeatArtifacts.TryWrite(_context.HeartbeatPath, snapshot);
        MirrorToStableSlot(snapshot);
        if (emitProgress || snapshot.LastObservedAt - _lastProgressEmittedAt >= _progressInterval)
        {
            _lastProgressEmittedAt = snapshot.LastObservedAt;
            _progressSink?.Invoke(ToProgress(snapshot));
        }
    }

    public void WriteFinal(string state, int? childPid, int? exitCode)
    {
        var snapshot = BuildSnapshot(state, childPid, exitCode);
        GateHeartbeatArtifacts.TryWrite(_context.HeartbeatPath, snapshot);
        if (_context.StableSlotHeartbeatPath is { } stableSlotPath)
        {
            GateHeartbeatArtifacts.TryDelete(stableSlotPath);
        }
    }

    // Mirror only the live beat. WriteFinal removes this run's uniquely keyed mirror, leaving any
    // concurrent sibling visible and preventing completed state from accumulating indefinitely.
    private void MirrorToStableSlot(GateHeartbeatSnapshot snapshot)
    {
        if (_context.StableSlotHeartbeatPath is { } stableSlotPath)
        {
            GateHeartbeatArtifacts.TryWrite(stableSlotPath, snapshot);
        }
    }

    private GateHeartbeatSnapshot BuildSnapshot(string state, int? childPid, int? exitCode = null)
    {
        var now = DateTimeOffset.UtcNow;
        var stdoutBytes = TryGetLength(_stdoutPath);
        var stderrBytes = TryGetLength(_stderrPath);
        var outputBytes = stdoutBytes + stderrBytes;
        if (outputBytes != _lastOutputBytes)
        {
            _lastOutputBytes = outputBytes;
            _lastProgressAt = now;
        }

        return new GateHeartbeatSnapshot(
            _context.GoalId,
            _context.Phase,
            _context.CurrentTarget,
            _context.SlotIndex,
            _processId,
            childPid,
            state,
            _startedAt,
            now,
            _lastProgressAt,
            stdoutBytes,
            stderrBytes,
            outputBytes,
            _context.CommandLine,
            exitCode,
            _stdoutPath,
            _stderrPath, RunClass: _context.RunClass, RunId: _context.RunId);
    }

    private AcceptanceGateProgress ToProgress(GateHeartbeatSnapshot snapshot) =>
        new(
            snapshot.GoalId,
            snapshot.Phase,
            snapshot.CurrentTarget,
            snapshot.SlotIndex,
            snapshot.ProcessId,
            snapshot.ChildPid,
            snapshot.StartedAt,
            snapshot.LastObservedAt,
            snapshot.LastProgressAt,
            Positive(snapshot.LastObservedAt - snapshot.StartedAt),
            snapshot.OutputBytes,
            _context.HeartbeatPath);

    private static long TryGetLength(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0L;
        }
        catch
        {
            return 0L;
        }
    }

    private static TimeSpan Positive(TimeSpan value) =>
        value < TimeSpan.Zero ? TimeSpan.Zero : value;
}
