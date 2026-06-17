namespace Mcg.AgentOrchestrator.App.Orchestration;

internal enum DispatchStartOutcomeCategory { Started, EmptyBatch, SpawnFailed }

internal sealed record DispatchStartOutcome(DispatchStartOutcomeCategory Category, string? Reason)
{
    internal static DispatchStartOutcome Started() => new(DispatchStartOutcomeCategory.Started, null);
    internal static DispatchStartOutcome EmptyBatch(string reason) => new(DispatchStartOutcomeCategory.EmptyBatch, reason);
    internal static DispatchStartOutcome SpawnFailed(string reason) => new(DispatchStartOutcomeCategory.SpawnFailed, reason);
}
