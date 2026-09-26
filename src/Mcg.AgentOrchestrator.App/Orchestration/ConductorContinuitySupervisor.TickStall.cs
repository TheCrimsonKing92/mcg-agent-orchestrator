using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorContinuitySupervisor
{
    internal const string TickStallBudgetEnvironmentVariable = "MCG_ORCHESTRATOR_TICK_STALL_BUDGET_MINUTES";
    private static readonly TimeSpan TickStallGrace = TimeSpan.FromMinutes(5);

    internal static TimeSpan ResolveTickStallBudget(string? configuredValue) =>
        double.TryParse(configuredValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes) &&
        minutes > 0 && minutes <= TimeSpan.MaxValue.TotalMinutes
            ? TimeSpan.FromMinutes(minutes)
            : AcceptanceCheckTimeouts.DefaultTimeout + TickStallGrace;

    private sealed record TickStallOutcome(
        ConductorSupervisorProcessResult Result,
        bool Stalled,
        bool TerminationConfirmed,
        string? Failure = null);

    private async Task<TickStallOutcome> WatchTickProgressAsync(
        Task<ConductorSupervisorProcessResult> runTask,
        ActivationMonitor monitor,
        Func<int> processIdSource,
        CancellationTokenSource processCts,
        int attempt,
        ConductorActivationBuild build,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        var watchStartedAt = _timeProvider.GetUtcNow();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (runTask.IsCompleted)
            {
                return new(await runTask.ConfigureAwait(false), false, true);
            }

            var snapshot = monitor.Snapshot();
            var lastEnd = monitor.LastTickEnd();
            var remaining = _tickStallBudget - (_timeProvider.GetUtcNow() - (lastEnd.EndedAt ?? watchStartedAt));
            using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var deadline = _tickStallDelay(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero,
                deadlineCts.Token);
            var completed = await Task.WhenAny(runTask, snapshot.Changed, deadline).ConfigureAwait(false);
            deadlineCts.Cancel();
            if (runTask.IsCompleted)
            {
                return new(await runTask.ConfigureAwait(false), false, true);
            }

            if (completed == snapshot.Changed || monitor.Snapshot().Changed != snapshot.Changed)
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var currentEnd = monitor.LastTickEnd();
            if (currentEnd.EndedAt != lastEnd.EndedAt)
            {
                continue;
            }

            var processId = processIdSource();
            ConductorDumpCaptureResult dump;
            try
            {
                dump = await _dumpCapture.CaptureAsync(processId > 0 ? processId : null,
                    outputDirectory, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                dump = ConductorDumpCaptureResult.NotCaptured($"error={ex.GetType().Name}");
            }

            var failure = $"tick-stall lastTick={currentEnd.Tick?.ToString(CultureInfo.InvariantCulture) ?? "none"}";
            RecordTickStall("detected", attempt, build, currentEnd, processId, dump);
            processCts.Cancel();
            var stopped = await ObserveStoppedSuccessor(runTask).ConfigureAwait(false);
            if (stopped?.TerminationConfirmed != true)
            {
                RecordTickStall("escalated", attempt, build, currentEnd, processId, dump);
                return new(stopped ?? new ConductorSupervisorProcessResult(-1, processId), true, false,
                    failure + " terminationConfirmed=false");
            }

            return new(stopped, true, true, failure);
        }
    }

    private void RecordTickStall(
        string status,
        int attempt,
        ConductorActivationBuild build,
        (int? Tick, DateTimeOffset? EndedAt) lastEnd,
        int processId,
        ConductorDumpCaptureResult dump)
    {
        var now = _timeProvider.GetUtcNow();
        var line = $"CONDUCTOR_TICK_STALL lastTick={lastEnd.Tick?.ToString(CultureInfo.InvariantCulture) ?? "none"} " +
            $"lastTickEndAt={lastEnd.EndedAt?.ToString("O", CultureInfo.InvariantCulture) ?? "none"} " +
            $"budgetSeconds={_tickStallBudget.TotalSeconds.ToString(CultureInfo.InvariantCulture)} " +
            $"commit={build.CommitSha} build={build.StagedBuildId} " +
            $"pid={(processId > 0 ? processId.ToString(CultureInfo.InvariantCulture) : "unknown")} " +
            (dump.Captured ? $"dump=captured path={dump.DumpPath}" :
                $"dump=not-captured reason={dump.Reason}");
        TryAppendConductEvent(line);
        try
        {
            eventStore.AppendAsync(new RunEventAppend(
                    RunEventTypes.ConductorSupervision, null, "tick-stall", status, line,
                    JsonSerializer.Serialize(new
                    {
                        lastTick = lastEnd.Tick,
                        lastTickEndAt = lastEnd.EndedAt,
                        budgetSeconds = _tickStallBudget.TotalSeconds,
                        build = new { commitSha = build.CommitSha, stagedBuildId = build.StagedBuildId },
                        processId = processId > 0 ? (int?)processId : null,
                        dump = new { captured = dump.Captured, path = dump.DumpPath, reason = dump.Reason },
                        attempt,
                        occurredAt = now
                    }), now))
                .GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[conduct supervisor] Could not record tick stall: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
