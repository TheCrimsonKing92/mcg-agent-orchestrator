using System.Globalization;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal enum ConductorActivationRevertReason
{
    SuccessorExited,
    ReadinessNeverReported,
    TickStall
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

    internal static int ResolveActivationHealthyTicks(string? configuredValue) =>
        int.TryParse(configuredValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count)
            ? Math.Max(1, count)
            : DefaultActivationHealthyTicks;

    private sealed class ActivationMonitor
    {
        private readonly Func<DateTimeOffset> _utcNow;
        private readonly object _gate = new();
        private bool _started;
        private int _ticks;
        private int? _lastTick;
        private DateTimeOffset? _lastTickEndAt;
        private DateTimeOffset _lastLineAt;
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
                if (!string.IsNullOrWhiteSpace(line)) _lastLineAt = _utcNow();
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
                }
                else
                {
                    return;
                }

                var changed = _changed;
                _changed = NewSignal();
                changed.TrySetResult();
            }
        }

        internal (bool Started, int Ticks, Task Changed) Snapshot()
        {
            lock (_gate)
            {
                return (_started, _ticks, _changed.Task);
            }
        }

        internal (int? Tick, DateTimeOffset? EndedAt) LastTickEnd()
        {
            lock (_gate)
            {
                return (_lastTick, _lastTickEndAt);
            }
        }

        internal DateTimeOffset LastLineAt()
        {
            lock (_gate)
            {
                return _lastLineAt;
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
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = monitor.Snapshot();
            if (snapshot.Ticks >= _activationHealthyTicks)
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

            using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var timeout = !snapshot.Started ? _readinessTimeout :
                snapshot.Ticks == 0 ? _activationStallTimeout : tickGapTimeout;
            var deadline = _activationDelay(timeout, deadlineCts.Token);
            var completed = await Task.WhenAny(runTask, snapshot.Changed, deadline).ConfigureAwait(false);
            deadlineCts.Cancel();
            if (completed == snapshot.Changed || completed == runTask || runTask.IsCompleted ||
                monitor.Snapshot().Changed != snapshot.Changed)
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            processCts.Cancel();
            var stopped = await ObserveStoppedSuccessor(runTask).ConfigureAwait(false);
            return new(false,
                snapshot.Started ? ConductorActivationRevertReason.TickStall :
                    ConductorActivationRevertReason.ReadinessNeverReported,
                snapshot.Started ? $"lastHealthyTick={snapshot.Ticks}" : "LOOP_START missing",
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
