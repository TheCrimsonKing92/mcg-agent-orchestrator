namespace Mcg.AgentOrchestrator.Infrastructure;

// Runner membership can supply liveness only until the job supplies its own heartbeat.
// The owning handle guards this state with its diagnostics lock.
internal sealed class RemoteLaneQueuedLiveness(string jobName, DateTimeOffset startedAt)
{
    internal static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(300);
    internal static readonly TimeSpan StateFreshness = TimeSpan.FromSeconds(120);
    private int _probes, _listedProbes;
    private string? _lastListedAs;
    private DateTimeOffset? _lastListedAt, _ownHeartbeatAt;
    private string _lastReason = "not-listed";

    internal bool ShouldProbe(DateTimeOffset now)
    {
        if (_ownHeartbeatAt is not null) return false;
        if (now - startedAt <= Ceiling) return true;
        _lastReason = "ceiling-reached";
        return false;
    }

    internal DateTimeOffset? Observe(RemoteLaneExecutorQueueState? state, DateTimeOffset now)
    {
        _probes++;
        if (_ownHeartbeatAt is not null) return null;
        if (state is null) { _lastReason = "unreadable"; return null; }
        if ((now - state.Utc).Duration() > StateFreshness)
        { _lastReason = "stale-state"; return null; }
        var listedAs = state.Running.Any(Matches) ? "running" : state.Queued.Any(Matches) ? "queued" : null;
        if (listedAs is null) { _lastReason = "not-listed"; return null; }
        _listedProbes++;
        _lastListedAs = listedAs;
        _lastListedAt = now;
        _lastReason = "listed-" + listedAs;
        return now;
    }

    private bool Matches(string entry) => string.Equals(
        entry.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? entry[..^5] : entry,
        jobName, StringComparison.OrdinalIgnoreCase);

    internal void OwnHeartbeatSeen(DateTimeOffset at)
    {
        _ownHeartbeatAt ??= at;
        _lastReason = "own-heartbeat";
    }

    internal RemoteLaneQueueWait Summary() => new(startedAt, _probes, _listedProbes,
        _lastListedAs, _lastListedAt, _ownHeartbeatAt, _lastReason, (int)Ceiling.TotalSeconds);

    internal static async Task<RemoteLaneExecutorQueueState?> ReadAsync(
        Func<string[], string, TimeSpan, CancellationToken, Task<GoalAcceptanceVerifier.CommandResult>> transport,
        RemoteLaneExecutorEntry entry, string folder, string staging, CancellationToken stop)
    {
        try
        {
            using var bound = CancellationTokenSource.CreateLinkedTokenSource(stop);
            bound.CancelAfter(TimeSpan.FromSeconds(30));
            var copy = await transport([SshRemoteLaneExecutor.ScpPath, "-o", "BatchMode=yes",
                $"{entry.RunnerAlias}:{entry.RunRoot}/runner-state.json", Path.GetFileName(folder) + "/runner-state.json"],
                staging, TimeSpan.FromSeconds(30), bound.Token).WaitAsync(bound.Token).ConfigureAwait(false);
            stop.ThrowIfCancellationRequested();
            if (copy.ExitCode != 0 || copy.TimedOut) return null;
            var text = await File.ReadAllTextAsync(Path.Combine(folder, "runner-state.json"), bound.Token).ConfigureAwait(false);
            return RemoteLaneExecutorQueueState.TryParse(text, out var state) ? state : null;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { throw; }
        catch (Exception) { return null; }
    }
}

internal sealed record RemoteLaneQueueWait(DateTimeOffset StartedAt, int Probes, int ListedProbes,
    string? LastListedAs, DateTimeOffset? LastListedAt, DateTimeOffset? OwnHeartbeatAt,
    string LastReason, int CeilingSeconds);
