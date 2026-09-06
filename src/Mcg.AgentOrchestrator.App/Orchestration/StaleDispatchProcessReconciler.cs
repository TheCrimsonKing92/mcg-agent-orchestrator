using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Diagnostics;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class StaleDispatchProcessReconciler
{
    internal static readonly IReadOnlySet<WorkTaskStatus> AssignedOnly =
        new HashSet<WorkTaskStatus> { WorkTaskStatus.Assigned };

    internal static int Reconcile(AgentOrchestratorKernel kernel) =>
        Reconcile(kernel, new BackgroundDispatchRunner());

    internal static int Reconcile(
        AgentOrchestratorKernel kernel,
        BackgroundDispatchRunner runner)
    {
        ArgumentNullException.ThrowIfNull(kernel);
        ArgumentNullException.ThrowIfNull(runner);
        return kernel.Goals.Sum(goal => Reconcile(kernel, runner, goal, AssignedOnly));
    }

    internal static int Reconcile(
        AgentOrchestratorKernel kernel,
        BackgroundDispatchRunner runner,
        Goal goal,
        IReadOnlySet<WorkTaskStatus> eligibleStatuses,
        Func<int, bool>? isProcessRunning = null,
        Func<int, SpawnProcessIdentity?>? readProcessIdentity = null)
    {
        ArgumentNullException.ThrowIfNull(kernel);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(goal);
        ArgumentNullException.ThrowIfNull(eligibleStatuses);
        isProcessRunning ??= IsProcessRunning;
        readProcessIdentity ??= DispatchProcessIdentityEvidence.ReadCurrent;
        var reconciled = 0;

        foreach (var task in goal.Tasks)
        {
            if (!eligibleStatuses.Contains(task.Status) ||
                task.LastProcess is not { IsRunning: true } process ||
                !File.Exists(process.ExitCodePath) ||
                (DispatchExitArtifacts.TryRead(process.ExitCodePath, out var exitArtifact) &&
                 exitArtifact.Origin == DispatchExitArtifactOrigin.Synthetic))
            {
                continue;
            }

            if (HasLiveOrUnknownTrackedProcess(process, isProcessRunning, readProcessIdentity))
            {
                continue;
            }

            var outcome = runner.ReconcileLatestProcess(kernel, goal.Id, task.Id);
            if (outcome.ProcessRecord.IsRunning ||
                outcome.RecoveryDecision?.Action == DispatchRecoveryAction.Hold)
            {
                continue;
            }

            runner.ApplyRefreshOutcomeAndWriteDiagnostics(kernel, goal.Id, task.Id, outcome);
            var refreshedTask = kernel.GetTask(goal.Id, task.Id);
            if (refreshedTask.LastProcess is not { IsRunning: false } completed ||
                !DispatchProcessCompletionState.HasAlreadyBeenApplied(refreshedTask, completed))
            {
                continue;
            }

            kernel.RecordTaskNote(
                goal.Id,
                task.Id,
                $"Reconciled completed dispatch before preparing another dispatch; pid {process.ProcessId} exited with {completed.ExitCode}.");
            reconciled++;
        }

        return reconciled;
    }

    internal static bool HasLiveOrUnknownTrackedProcess(
        TaskProcessRecord process,
        Func<int, bool> isProcessRunning,
        Func<int, SpawnProcessIdentity?> readProcessIdentity)
    {
        var heartbeat = ProcessLogReader.ReadHeartbeat(process);
        if (!heartbeat.IsAvailable)
        {
            return process.CompletionTrackedProcessIds.Any(isProcessRunning);
        }

        var candidates = process.CompletionTrackedProcessIds
            .Concat(heartbeat.OwnedProcessIds)
            .Concat(heartbeat.ChildProcessId is > 0 ? [heartbeat.ChildProcessId.Value] : [])
            .Concat(heartbeat.ProcessId > 0 ? [heartbeat.ProcessId] : []);
        return candidates
            .Where(processId => processId > 0)
            .Distinct()
            .Where(isProcessRunning)
            .Any(processId => DispatchProcessIdentityEvidence.ClassifyRecordedOwner(
                processId,
                heartbeat.OwnedProcessIdentities,
                readProcessIdentity) != SpawnTrackedProcessStatus.DeadOrRecycled);
    }

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }
}
