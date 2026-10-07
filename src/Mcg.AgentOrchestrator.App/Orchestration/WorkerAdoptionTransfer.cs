using System.Diagnostics;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record WorkerAdoptionTransferResult(WorkerAdoptionCensusRow Row, string Outcome);

internal static class WorkerAdoptionTransfer
{
    public static string? Decide(WorkerAdoptionCensusRow row, TaskProcessRecord? recorded)
    {
        if (row.Verdict is not ("inheritable" or "identity-unproven")) return null;
        if (row.Verdict == "identity-unproven") return "deferred-identity-unproven";
        if (row.Lifecycle != SpawnRegistryLifecycle.ConductorDetached) return "lifecycle-not-conductor-detached";
        if (recorded is null) return "task-missing";
        if (!recorded.IsRunning) return "task-not-running";
        if (!recorded.WasGracefullyDetachedByConductor) return "task-not-detached";
        if (recorded.ProcessId != row.WorkerProcessId) return "task-pid-mismatch";
        if (recorded.ProcessIdentityStartedAt is null) return "task-identity-unproven";
        if (recorded.ProcessIdentityStartedAt != row.WorkerStartedAt) return "task-start-mismatch";
        return "adopt";
    }

    public static IReadOnlyList<WorkerAdoptionTransferResult> Run(
        IReadOnlyList<WorkerAdoptionCensusRow> rows,
        Func<string, TaskProcessRecord?> readRecordedProcess,
        Func<WorkerAdoptionCensusRow, bool> transfer)
    {
        var results = new List<WorkerAdoptionTransferResult>();
        foreach (var row in rows)
        {
            if (row.Verdict is not ("inheritable" or "identity-unproven")) continue;
            var outcome = Decide(row, row.Verdict == "inheritable" ? readRecordedProcess(row.OwnerId) : null)!;
            if (outcome == "adopt") outcome = transfer(row) ? "adopted" : "transfer-lost";
            results.Add(new WorkerAdoptionTransferResult(row, outcome));
        }
        return results;
    }

    public static Func<IReadOnlyList<WorkerAdoptionCensusRow>, IReadOnlyList<WorkerAdoptionTransferResult>> CreateDefault(
        string stateDatabasePath,
        Func<IReadOnlyCollection<string>, AgentOrchestratorKernel> loadGoals) => rows =>
    {
        var inherited = rows.Where(row => row.Verdict == "inheritable").ToArray();
        if (inherited.Length == 0)
            return Run(rows, _ => null, _ => throw new InvalidOperationException("No inheritable worker to transfer."));

        var goalIds = inherited.Select(row => SplitOwnerId(row.OwnerId)?.GoalId)
            .OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
        var kernel = loadGoals(goalIds);
        using var currentProcess = Process.GetCurrentProcess();
        if (!SpawnProcessIdentityReader.TryRead(currentProcess, out var current))
            throw new InvalidOperationException("Current process identity could not be read.");

        var registry = new SpawnRegistry(stateDatabasePath);
        return Run(rows, ownerId =>
        {
            var ids = SplitOwnerId(ownerId);
            if (ids is null) return null;
            return kernel.Goals.FirstOrDefault(goal => goal.Id.Value == ids.Value.GoalId)?
                .Tasks.FirstOrDefault(task => task.Id.Value == ids.Value.TaskId)?.LastProcess;
        }, row => registry.TryTransferDetachedOwner(
            row.OwnerId, row.WorkerProcessId, row.WorkerStartedAt, row.OwnerProcessId, row.OwnerStartedAt,
            current, $"spawn_registry: adopted-by-successor pid={current.ProcessId}"));
    };

    private static (string GoalId, string TaskId)? SplitOwnerId(string ownerId)
    {
        var colon = ownerId.IndexOf(':');
        return colon < 0 ? null : (ownerId[..colon], ownerId[(colon + 1)..]);
    }
}
