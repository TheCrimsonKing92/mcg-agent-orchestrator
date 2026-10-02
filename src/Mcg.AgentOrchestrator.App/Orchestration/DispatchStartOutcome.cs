using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal enum DispatchStartOutcomeCategory { Started, EmptyBatch, Deferred, SpawnFailed, RecoverableSandboxPrep }

internal sealed record DispatchedTaskIdentity(TaskId TaskId, AgentRole Role);

internal sealed record DispatchStartOutcome(
    DispatchStartOutcomeCategory Category,
    string? Reason,
    WorkerSandboxPrepRecoverableAction? SandboxPrepRecoveryAction = null,
    IReadOnlyList<DispatchedTaskIdentity>? DispatchedTasks = null)
{
    internal const string HoldOwnerDataKey = "ConductorHoldOwner";
    public ConductorHoldOwner HoldOwner { get; init; } = ConductorHoldOwner.None;

    internal static DispatchStartOutcome Started(IReadOnlyList<TaskSpec>? tasks = null) =>
        new(
            DispatchStartOutcomeCategory.Started,
            null,
            DispatchedTasks: tasks?.Select(task => new DispatchedTaskIdentity(task.Id, task.RequiredRole)).ToArray() ?? []);
    internal static DispatchStartOutcome EmptyBatch(string reason) => new(DispatchStartOutcomeCategory.EmptyBatch, reason);
    internal static DispatchStartOutcome Deferred(string reason) => new(DispatchStartOutcomeCategory.Deferred, reason);
    internal static DispatchStartOutcome SpawnFailed(string reason) => new(DispatchStartOutcomeCategory.SpawnFailed, reason);
    internal static DispatchStartOutcome RecoverableSandboxPrep(WorkerSandboxPrepRecoverableAction action) =>
        new(DispatchStartOutcomeCategory.RecoverableSandboxPrep, action.Reason, action);

    internal const string SpecRefinementPendingPrefix = "SPEC_REFINEMENT_PENDING";

    internal static bool IsSpecRefinementPending(Exception ex) =>
        ex is InvalidOperationException &&
        ex.Message.StartsWith(SpecRefinementPendingPrefix, StringComparison.Ordinal);

    internal static DispatchStartOutcome FromDispatchException(Exception ex, string failurePrefix) =>
        IsSpecRefinementPending(ex)
            ? Deferred(ex.Message) with
            {
                HoldOwner = ex.Data[HoldOwnerDataKey] is ConductorHoldOwner owner
                    ? owner : ConductorHoldOwner.None
            }
            : SpawnFailed($"{failurePrefix}: {ex.Message}");
}
