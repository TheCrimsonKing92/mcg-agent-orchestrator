using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public enum DispatchRecoveryAction
{
    ReconcileFromExit,
    MarkStale,
    RetryStale,
    BudgetExhausted,
    Hold,
    ClassifyBlocker,
    Reap
}

public sealed record DispatchRecoveryDecision(
    DispatchRecoveryAction Action,
    string ActionName,
    string EvidencePath,
    string Reason,
    string? Blocker = null);

public sealed class DispatchRecoveryPolicy
{
    public static readonly TimeSpan DefaultRecentHeartbeatGrace = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan DefaultLiveIdleTimeout = TimeSpan.FromMinutes(30);
    public const int DefaultStaleDispatchRetries = 2;

    private readonly IClock _clock;
    private readonly TimeSpan _recentHeartbeatGrace;
    private readonly TimeSpan _liveIdleTimeout;

    public DispatchRecoveryPolicy(
        IClock? clock = null,
        TimeSpan? recentHeartbeatGrace = null,
        TimeSpan? liveIdleTimeout = null)
    {
        _clock = clock ?? new SystemClock();
        _recentHeartbeatGrace = recentHeartbeatGrace ?? DefaultRecentHeartbeatGrace;
        _liveIdleTimeout = liveIdleTimeout ?? DefaultLiveIdleTimeout;
    }

    public DispatchRecoveryDecision Evaluate(
        TaskProcessRecord process,
        bool hasLiveProcess,
        int staleRetryBudgetRemaining = 0,
        bool hasDirtyWorktreeEvidence = false)
    {
        var exitPath = process.ExitCodePath;
        var heartbeat = ProcessLogReader.ReadHeartbeat(process, _clock.UtcNow);

        if (!hasLiveProcess && File.Exists(exitPath))
        {
            return Decision(
                DispatchRecoveryAction.ReconcileFromExit,
                exitPath,
                "no live process and exit artifact exists");
        }

        if (!hasLiveProcess)
        {
            var heartbeatEvidence = heartbeat.IsAvailable ? heartbeat.Path : "heartbeat-absent";
            var heartbeatStale = !heartbeat.IsAvailable ||
                heartbeat.HeartbeatAge is null ||
                heartbeat.HeartbeatAge >= _recentHeartbeatGrace;
            if (heartbeatStale)
            {
                if (staleRetryBudgetRemaining > 0 && !hasDirtyWorktreeEvidence)
                {
                    return Decision(
                        DispatchRecoveryAction.MarkStale,
                        heartbeatEvidence,
                        $"no live process, exit-absent, stale retry budget remaining={staleRetryBudgetRemaining}");
                }

                var blocker = staleRetryBudgetRemaining <= 0
                    ? "stale-dispatch retry budget exhausted"
                    : "stale-dispatch retry blocked by dirty worktree evidence";
                return Decision(
                    staleRetryBudgetRemaining <= 0 ? DispatchRecoveryAction.BudgetExhausted : DispatchRecoveryAction.MarkStale,
                    heartbeatEvidence,
                    $"no live process, exit-absent, heartbeat {(heartbeat.IsAvailable ? "stale" : "absent")}",
                    blocker);
            }

            return Decision(
                DispatchRecoveryAction.MarkStale,
                heartbeat.Path,
                "no live process, exit-absent, heartbeat present but process liveness is absent",
                "stale-dispatch process missing despite recent heartbeat");
        }

        if (heartbeat.IsAvailable)
        {
            if (heartbeat.ChildProcessId is not null &&
                heartbeat.HeartbeatAge <= _recentHeartbeatGrace)
            {
                return Decision(
                    DispatchRecoveryAction.Hold,
                    heartbeat.Path,
                    $"live process with recent heartbeat age={FormatDuration(heartbeat.HeartbeatAge.Value)}");
            }

            if (heartbeat.IdleDuration is { } idleDuration &&
                idleDuration < _liveIdleTimeout &&
                heartbeat.LastProgressAt is not null)
            {
                return Decision(
                    DispatchRecoveryAction.Hold,
                    heartbeat.Path,
                    $"live process with recent CPU activity or output progress idle_for={FormatDuration(idleDuration)} ownedCpuMs={heartbeat.OwnedCpuMs}");
            }

            if (heartbeat.IdleDuration >= _liveIdleTimeout &&
                heartbeat.StandardOutputBytes == 0 &&
                heartbeat.StandardErrorBytes == 0 &&
                SafeFileLength(process.StandardOutputPath) == 0 &&
                SafeFileLength(process.StandardErrorPath) == 0)
            {
                return Decision(
                    DispatchRecoveryAction.ClassifyBlocker,
                    heartbeat.Path,
                    $"live process idle for {FormatDuration(heartbeat.IdleDuration.Value)} with no heartbeat/output/CPU progress",
                    "live-idle-no-progress");
            }
        }

        return Decision(
            DispatchRecoveryAction.Hold,
            heartbeat.IsAvailable ? heartbeat.Path : "heartbeat-absent",
            heartbeat.IsAvailable
                ? "live process has no terminal artifact and has not exceeded live-idle policy"
                : "live process has no heartbeat; CPU sampling unavailable, holding pending other detectors");
    }

    public static string ToActionName(DispatchRecoveryAction action) =>
        action switch
        {
            DispatchRecoveryAction.ReconcileFromExit => "reconcile-from-exit",
            DispatchRecoveryAction.MarkStale => "mark-stale",
            DispatchRecoveryAction.RetryStale => "retry-stale",
            DispatchRecoveryAction.BudgetExhausted => "budget-exhausted",
            DispatchRecoveryAction.Hold => "hold",
            DispatchRecoveryAction.ClassifyBlocker => "classify-blocker",
            DispatchRecoveryAction.Reap => "reap",
            _ => action.ToString()
        };

    public static int GetStaleRetryBudgetRemaining(TaskSpec task)
    {
        var consumed = CountConsecutiveStaleDispatchRetries(task);
        return Math.Max(0, DefaultStaleDispatchRetries - consumed);
    }

    public static bool IsStaleDispatchRetryVerification(TaskVerificationRecord verification) =>
        verification.StandardError.Contains("Dispatch recovery policy action='retry-stale'", StringComparison.Ordinal) ||
        (verification.StandardError.Contains("Dispatch recovery policy action='mark-stale'", StringComparison.Ordinal) &&
         verification.StandardError.Contains("stale retry budget remaining=", StringComparison.Ordinal) &&
         !verification.StandardError.Contains("blocker='", StringComparison.Ordinal));

    public static bool IsStaleDispatchRecoveryVerification(TaskVerificationRecord verification) =>
        IsStaleDispatchRetryVerification(verification) ||
        verification.StandardError.Contains("Dispatch recovery policy action='mark-stale'", StringComparison.Ordinal) ||
        verification.StandardError.Contains("Dispatch recovery policy action='budget-exhausted'", StringComparison.Ordinal);

    private static int CountConsecutiveStaleDispatchRetries(TaskSpec task)
    {
        var consumed = 0;
        for (var i = task.VerificationHistory.Count - 1; i >= 0; i--)
        {
            if (!IsStaleDispatchRetryVerification(task.VerificationHistory[i]))
            {
                break;
            }

            consumed++;
        }

        return consumed;
    }

    private static DispatchRecoveryDecision Decision(
        DispatchRecoveryAction action,
        string evidencePath,
        string reason,
        string? blocker = null) =>
        new(action, ToActionName(action), evidencePath, reason, blocker);

    private static long SafeFileLength(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0L;
        }
        catch (IOException)
        {
            return 0L;
        }
        catch (UnauthorizedAccessException)
        {
            return 0L;
        }
    }

    private static string FormatDuration(TimeSpan duration) =>
        duration < TimeSpan.Zero ? TimeSpan.Zero.ToString("c") : duration.ToString("c");
}
