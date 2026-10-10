using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ExperimentDecisionApplier
{
    internal static ExperimentOutcomeState ParseOutcome(string text) => text switch
    {
        "confirmed" => ExperimentOutcomeState.Confirmed,
        "refuted" => ExperimentOutcomeState.Refuted,
        "inconclusive" => ExperimentOutcomeState.Inconclusive,
        _ => throw new ArgumentException("outcome: expected confirmed, refuted or inconclusive.")
    };

    internal static ExperimentRecord Decide(ExperimentStore store, string reference,
        ExperimentOutcomeState outcome, string evidence, string action)
    {
        store.DecideAsync(reference, outcome, evidence, action).GetAwaiter().GetResult();
        return store.ResolveAsync(reference).GetAwaiter().GetResult()!;
    }
}
