using System.Globalization;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal enum ConductorActivationRevertReason
{
    SuccessorExited,
    ReadinessNeverReported,
    TickStall,
    AdmissionCensusFailing,
    AdmissionStalled
}

internal sealed record ConductorActivationBuild(
    string CommitSha,
    string StagedBuildId,
    IReadOnlyList<string>? CommandPrefix,
    IDisposable? Lease)
{
    internal static ConductorActivationBuild FromSuccessor(
        ConductorPreparedSuccessor successor,
        string dotnetPath) =>
        new(successor.RepositoryHead, successor.RunDirectory, [dotnetPath, successor.AppDllPath],
            successor.RunDirectoryLease);
}

internal sealed partial class ConductorContinuitySupervisor
{
    internal const string ActivationHealthyTicksEnvironmentVariable = "MCG_ORCHESTRATOR_ACTIVATION_HEALTHY_TICKS";
    internal const int DefaultActivationHealthyTicks = 3;
    internal const string ActivationStallTimeoutEnvironmentVariable = "MCG_ORCHESTRATOR_ACTIVATION_STALL_MINUTES";
    // The successor's first tick after a relaunch is its heaviest (sweeps, dispatch and evidence starts for
    // every goal). On 2026-10-16 a full board took over 2 minutes, and both the new and restored builds were
    // reverted as stalled. Measured tick spacing that day: p90 128 s, p99 191 s.
    internal static readonly TimeSpan DefaultActivationStallTimeout = TimeSpan.FromMinutes(10);

    internal static TimeSpan ResolveActivationStallTimeout(string? configuredValue) =>
        double.TryParse(configuredValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes) &&
        minutes > 0 && minutes <= 24 * 60
            ? TimeSpan.FromMinutes(minutes)
            : DefaultActivationStallTimeout;

    internal static int ResolveActivationHealthyTicks(string? configuredValue) =>
        int.TryParse(configuredValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count)
            ? Math.Max(1, count)
            : DefaultActivationHealthyTicks;

    private sealed class ActivationMonitor
    {
        private readonly Func<DateTimeOffset> _utcNow;
        private readonly object _gate = new();
        private readonly ActivationAdmissionHealthTracker _admissionHealth = new();
        private bool _admissionHealthyAtLastTick = true;
        private bool _started;
        private int _ticks;
        private int? _lastTick;
        private DateTimeOffset? _lastTickEndAt;
        private DateTimeOffset? _lastDrainAt;
        private DateTimeOffset _lastLineAt;
        private DateTimeOffset? _loopReadyAt;
        private TaskCompletionSource _changed = NewSignal();

        internal ActivationMonitor(Func<DateTimeOffset> utcNow)
        {
            _utcNow = utcNow;
            _lastLineAt = utcNow();
        }

        internal void OnLine(string line)
        {
            lock (_gate)
            {
                var nonBlank = !string.IsNullOrWhiteSpace(line);
                if (nonBlank) _lastLineAt = _utcNow();
                if (!_started && _loopReadyAt is null &&
                    line.StartsWith(LoopReadyLinePrefix, StringComparison.Ordinal))
                {
                    _loopReadyAt = _lastLineAt;
                }
                if (line.StartsWith("LOOP_START ", StringComparison.Ordinal) && !_started)
                {
                    _started = true;
                }
                else if (line.StartsWith("TICK_END ", StringComparison.Ordinal) &&
                    line.Contains(" activation=true", StringComparison.Ordinal) && _started)
                {
                    _ticks++;
                    var tickStart = line.IndexOf("tick=", StringComparison.Ordinal);
                    var tickText = tickStart < 0 ? string.Empty : line[(tickStart + 5)..].Split(' ')[0];
                    _lastTick = int.TryParse(tickText, NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out var tick) ? tick : null;
                    _lastTickEndAt = _utcNow();
                    _admissionHealthyAtLastTick = _admissionHealth.Verdict.State == ActivationAdmissionHealthState.Healthy;
                }
                else if (_started && line.StartsWith("LOOP_RELAUNCH_DRAIN ", StringComparison.Ordinal))
                {
                    _lastDrainAt = _utcNow();
                    return;
                }
                else if (_started && ActivationAdmissionSample.TryParse(line, out var sample))
                {
                    var wasTripped = _admissionHealth.Verdict.State == ActivationAdmissionHealthState.Tripped;
                    var verdict = _admissionHealth.Observe(sample, _utcNow());
                    if (wasTripped || verdict.State != ActivationAdmissionHealthState.Tripped) return;
                }
                else if (_started || _loopReadyAt is null || !nonBlank)
                {
                    return;
                }

                var changed = _changed;
                _changed = NewSignal();
                changed.TrySetResult();
            }
        }

        internal (bool Started, int Ticks, Task Changed, ActivationAdmissionHealthVerdict AdmissionHealth,
            bool AdmissionHealthyAtLastTick) Snapshot()
        {
            lock (_gate)
            {
                return (_started, _ticks, _changed.Task, _admissionHealth.Verdict, _admissionHealthyAtLastTick);
            }
        }

        internal (int? Tick, DateTimeOffset? EndedAt) LastTickEnd()
        {
            lock (_gate)
            {
                return (_lastTick, _lastTickEndAt);
            }
        }

        internal DateTimeOffset? LastDrainAt()
        {
            lock (_gate)
            {
                return _lastDrainAt;
            }
        }

        internal DateTimeOffset LastLineAt()
        {
            lock (_gate)
            {
                return _lastLineAt;
            }
        }

        internal DateTimeOffset? LoopReadyAt()
        {
            lock (_gate)
            {
                return _loopReadyAt;
            }
        }

        private static TaskCompletionSource NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record ActivationWindowResult(
        bool Adopted,
        ConductorActivationRevertReason? Reason,
        string Detail,
        ConductorSupervisorProcessResult? ProcessResult,
        bool TerminationConfirmed,
        bool DeliberateStop = false);

    private static bool HasDeliberateStopArtifact(string artifactPath)
    {
        var artifact = ConductorContinuityExitArtifact.TryRead(artifactPath);
        return artifact?.StopReason is
            "stop-file" or "stop-file-during-sleep" or "stop-while-idle" or
            "stop-after-operator-intent" or "max-duration" or "max-iter" or
            "self-relaunch-handoff" or "blocked-recheck-exhausted" or
            "no-progress-no-watch" or "all-terminal" or "no-recheckable-work";
    }

    private async Task<ActivationWindowResult> ObserveActivationAsync(
        Task<ConductorSupervisorProcessResult> runTask,
        ActivationMonitor monitor,
        string artifactPath,
        CancellationTokenSource processCts,
        TimeSpan tickGapTimeout,
        bool checkAdmissionHealth,
        CancellationToken cancellationToken)
    {
        TimeSpan? drainWait = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = monitor.Snapshot();
            var health = snapshot.AdmissionHealth;
            if (checkAdmissionHealth && health.State == ActivationAdmissionHealthState.Tripped)
            {
                processCts.Cancel();
                var admissionStopped = await ObserveStoppedSuccessor(runTask).ConfigureAwait(false);
                return new(false, health.Reason, health.Detail, admissionStopped, admissionStopped?.TerminationConfirmed == true);
            }
            if (snapshot.Ticks >= _activationHealthyTicks &&
                (!checkAdmissionHealth || (health.State == ActivationAdmissionHealthState.Healthy &&
                    snapshot.AdmissionHealthyAtLastTick)))
            {
                return new(true, null, $"healthyTicks={snapshot.Ticks}", null, true);
            }

            if (runTask.IsCompleted)
            {
                var ended = await ObserveStoppedSuccessor(runTask).ConfigureAwait(false);
                if (ended is not null && HasDeliberateStopArtifact(artifactPath))
                {
                    return new(false, null, "deliberate-stop", ended,
                        ended?.TerminationConfirmed == true, DeliberateStop: true);
                }

                return new(false, ConductorActivationRevertReason.SuccessorExited,
                    $"exit={ended?.ExitCode.ToString(CultureInfo.InvariantCulture) ?? "unknown"}",
                    ended, ended?.TerminationConfirmed == true);
            }

            var timeout = !snapshot.Started ? _readinessTimeout :
                snapshot.Ticks == 0 ? _activationStallTimeout : drainWait ?? tickGapTimeout;
            var loopReadyAt = !snapshot.Started ? monitor.LoopReadyAt() : null;
            var ceilingReached = false;
            if (loopReadyAt is { } readyAt)
            {
                var startup = ConductorStartupProgressDeadline.Compute(
                    readyAt, monitor.LastLineAt(), _timeProvider.GetUtcNow(), _readinessTimeout);
                timeout = startup.Delay;
                ceilingReached = startup.CeilingReached;
            }

            if (!ceilingReached)
            {
                using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var deadline = _activationDelay(timeout, deadlineCts.Token);
                var completed = await Task.WhenAny(runTask, snapshot.Changed, deadline).ConfigureAwait(false);
                deadlineCts.Cancel();
                if (completed == snapshot.Changed || completed == runTask)
                {
                    drainWait = null;
                    continue;
                }
            }

            var current = monitor.Snapshot();
            if (runTask.IsCompleted || current.Started != snapshot.Started ||
                (!ceilingReached && current.Changed != snapshot.Changed))
            {
                drainWait = null;
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (loopReadyAt is { } deadlineReadyAt)
            {
                ceilingReached = ConductorStartupProgressDeadline.Compute(
                    deadlineReadyAt, monitor.LastLineAt(), _timeProvider.GetUtcNow(), _readinessTimeout)
                    .CeilingReached;
            }
            if (snapshot.Started && snapshot.Ticks > 0)
            {
                drainWait = ActivationDrainStallDecision.Evaluate(_timeProvider.GetUtcNow(),
                    monitor.LastTickEnd().EndedAt, monitor.LastDrainAt(), tickGapTimeout, _tickStallBudget);
                if (drainWait is not null)
                {
                    continue;
                }
            }
            processCts.Cancel();
            var stopped = await ObserveStoppedSuccessor(runTask).ConfigureAwait(false);
            return new(false,
                snapshot.Started ? ConductorActivationRevertReason.TickStall :
                    ConductorActivationRevertReason.ReadinessNeverReported,
                snapshot.Started ? $"lastHealthyTick={snapshot.Ticks}" : ceilingReached
                    ? $"LOOP_START missing ceiling={(int)ConductorStartupProgressDeadline.Ceiling.TotalSeconds}"
                    : "LOOP_START missing",
                stopped, stopped?.TerminationConfirmed == true);
        }
    }

    private void RecordActivation(
        string status,
        int attempt,
        ConductorActivationBuild primaryBuild,
        ConductorActivationBuild? secondaryBuild,
        ConductorActivationRevertReason? reason,
        string detail)
    {
        var (primaryRole, secondaryRole) = ActivationBuildRoles(status);
        var line = $"ACTIVATION_{status.ToUpperInvariant().Replace('-', '_')} " +
            $"{primaryRole}Commit={primaryBuild.CommitSha} {primaryRole}Build={primaryBuild.StagedBuildId} " +
            $"{secondaryRole}Commit={secondaryBuild?.CommitSha ?? "none"} {secondaryRole}Build={secondaryBuild?.StagedBuildId ?? "none"} " +
            $"reason={reason?.ToString() ?? "none"} detail={Sanitize(detail)}";
        TryAppendConductEvent(line);
        var now = _timeProvider.GetUtcNow();
        eventStore.AppendAsync(new RunEventAppend(
                RunEventTypes.ConductorSupervision,
                null,
                "activation",
                status,
                line,
                SerializeActivationPayload(status, primaryBuild, secondaryBuild, reason, detail, attempt, now),
                now))
            .GetAwaiter().GetResult();
    }

    private void RaiseFailedBothAttention(
        string workingDirectory,
        ConductorActivationBuild failed,
        ConductorActivationBuild restored,
        string detail)
    {
        var body = $"Failed successor: {failed.CommitSha} ({failed.StagedBuildId}). " +
            $"Failed restoration: {restored.CommitSha} ({restored.StagedBuildId}). " +
            $"Reason: {Sanitize(detail)}. No conductor remains running.";
        if (_raiseActivationAttention is not null)
        {
            _raiseActivationAttention(body);
            return;
        }

        CollaborationItemStore.ForDirectory(Path.Combine(workingDirectory, ".orchestrator"))
            .RaiseAsync(CollaborationItemType.Decision, null,
                "Conductor activation failed for both builds", body,
                $"conductor-activation-failed-both:{failed.CommitSha}:{restored.CommitSha}")
            .GetAwaiter().GetResult();
    }
}
