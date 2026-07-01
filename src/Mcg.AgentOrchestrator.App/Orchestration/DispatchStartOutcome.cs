using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal enum DispatchStartOutcomeCategory { Started, EmptyBatch, SpawnFailed, RecoverableSandboxPrep }

internal sealed record DispatchStartOutcome(
    DispatchStartOutcomeCategory Category,
    string? Reason,
    WorkerSandboxPrepRecoverableAction? SandboxPrepRecoveryAction = null)
{
    internal static DispatchStartOutcome Started() => new(DispatchStartOutcomeCategory.Started, null);
    internal static DispatchStartOutcome EmptyBatch(string reason) => new(DispatchStartOutcomeCategory.EmptyBatch, reason);
    internal static DispatchStartOutcome SpawnFailed(string reason) => new(DispatchStartOutcomeCategory.SpawnFailed, reason);
    internal static DispatchStartOutcome RecoverableSandboxPrep(WorkerSandboxPrepRecoverableAction action) =>
        new(DispatchStartOutcomeCategory.RecoverableSandboxPrep, action.Reason, action);
}
