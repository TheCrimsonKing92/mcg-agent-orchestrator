using System.Globalization;
using System.Text.Json;
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
        private readonly object _gate = new();
        private bool _started;
        private int _ticks;
        private TaskCompletionSource _changed = NewSignal();

        internal void OnLine(string line)
        {
            lock (_gate)
            {
                if (line.StartsWith("LOOP_START ", StringComparison.Ordinal) && !_started)
                {
                    _started = true;
                }
                else if (line.StartsWith("TICK_END ", StringComparison.Ordinal) && _started)
                {
                    _ticks++;
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
            var timeout = snapshot.Started ? _activationStallTimeout : _readinessTimeout;
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
        ConductorActivationBuild failed,
        ConductorActivationBuild restored,
        ConductorActivationRevertReason? reason,
        string detail)
    {
        var line = $"ACTIVATION_{status.ToUpperInvariant().Replace('-', '_')} " +
            $"failedCommit={failed.CommitSha} failedBuild={failed.StagedBuildId} " +
            $"restoredCommit={restored.CommitSha} restoredBuild={restored.StagedBuildId} " +
            $"reason={reason?.ToString() ?? "none"} detail={Sanitize(detail)}";
        TryAppendConductEvent(line);
        var now = _timeProvider.GetUtcNow();
        eventStore.AppendAsync(new RunEventAppend(
                RunEventTypes.ConductorSupervision,
                null,
                "activation",
                status,
                line,
                JsonSerializer.Serialize(new
                {
                    failedBuild = new { commitSha = failed.CommitSha, stagedBuildId = failed.StagedBuildId },
                    restoredBuild = new { commitSha = restored.CommitSha, stagedBuildId = restored.StagedBuildId },
                    reason = reason?.ToString(),
                    detail,
                    attempt,
                    occurredAt = now
                }),
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
