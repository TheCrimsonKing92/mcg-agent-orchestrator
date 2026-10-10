using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ExperimentExtensionApplier
{
    internal static ExperimentRecord Extend(ExperimentStore store, string reference, int newCount,
        string reason, DateTimeOffset at)
    {
        var record = store.ResolveAsync(reference).GetAwaiter().GetResult()
            ?? throw new InvalidOperationException($"Experiment '{reference}' was not found.");
        if (!store.ExtendStopRuleAsync(record.Id, newCount, reason, at).GetAwaiter().GetResult())
            throw new InvalidOperationException($"Experiment '{record.Id}' stop rule changed concurrently; nothing was recorded.");
        return store.ResolveAsync(record.Id).GetAwaiter().GetResult()!;
    }
}
