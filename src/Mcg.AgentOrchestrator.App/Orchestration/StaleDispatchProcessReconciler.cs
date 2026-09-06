using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class StaleDispatchProcessReconciler
{
    private static readonly IReadOnlySet<WorkTaskStatus> ConductorStatuses =
        new HashSet<WorkTaskStatus> { WorkTaskStatus.Assigned, WorkTaskStatus.Running };
    internal static readonly IReadOnlySet<WorkTaskStatus> AssignedOnly =
        new HashSet<WorkTaskStatus> { WorkTaskStatus.Assigned };

    internal static int Reconcile(AgentOrchestratorKernel kernel)
    {
        ArgumentNullException.ThrowIfNull(kernel);
        return kernel.Goals.Sum(goal => Reconcile(kernel, goal, ConductorStatuses));
    }

    internal static int Reconcile(
        AgentOrchestratorKernel kernel,
        Goal goal,
        IReadOnlySet<WorkTaskStatus> eligibleStatuses,
        Func<int, bool>? isProcessRunning = null,
        Func<int, SpawnProcessIdentity?>? readProcessIdentity = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        ArgumentNullException.ThrowIfNull(kernel);
        ArgumentNullException.ThrowIfNull(goal);
        ArgumentNullException.ThrowIfNull(eligibleStatuses);
        isProcessRunning ??= IsProcessRunning;
        readProcessIdentity ??= DispatchProcessIdentityEvidence.ReadCurrent;
        utcNow ??= () => DateTimeOffset.UtcNow;
        var reconciled = 0;

        foreach (var task in goal.Tasks)
        {
            if (!eligibleStatuses.Contains(task.Status) ||
                task.LastProcess is not { IsRunning: true } process ||
                !DispatchExitArtifacts.TryRead(process.ExitCodePath, out var exitArtifact) ||
                exitArtifact.Origin == DispatchExitArtifactOrigin.Synthetic ||
                HasLiveTrackedProcess(process, isProcessRunning, readProcessIdentity))
            {
                continue;
            }

            var completed = process with
            {
                CompletedAt = utcNow(),
                ExitCode = exitArtifact.ExitCode,
                ExitArtifactOrigin = exitArtifact.Origin,
                ExitArtifactReason = exitArtifact.Reason
            };
            kernel.RecordTaskProcessRefreshed(goal.Id, task.Id, completed, verification: null);
            kernel.RecordTaskNote(
                goal.Id,
                task.Id,
                $"Auto-cleared stale LastProcess.IsRunning before dispatch; pid {process.ProcessId} had exit artifact {process.ExitCodePath} with exit {exitArtifact.ExitCode}.");
            reconciled++;
        }

        return reconciled;
    }

    internal static bool HasLiveTrackedProcess(
        TaskProcessRecord process,
        Func<int, bool> isProcessRunning,
        Func<int, SpawnProcessIdentity?> readProcessIdentity)
    {
        var heartbeat = ProcessLogReader.ReadHeartbeat(process);
        if (!heartbeat.IsAvailable)
        {
            return false;
        }

        var candidates = heartbeat.OwnedProcessIds
            .Concat(heartbeat.ChildProcessId is > 0 ? [heartbeat.ChildProcessId.Value] : [])
            .Concat(heartbeat.ProcessId > 0 ? [heartbeat.ProcessId] : []);
        return DispatchProcessIdentityEvidence.GetLiveRecordedOwnerProcessIds(
            candidates,
            heartbeat.OwnedProcessIdentities,
            isProcessRunning,
            readProcessIdentity).Count > 0;
    }

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
