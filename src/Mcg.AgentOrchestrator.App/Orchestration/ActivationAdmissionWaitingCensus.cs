using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// One tick's admission decisions; held goals are not evidence of missed admission.
internal sealed class ActivationAdmissionWaitingCensus
{
    private readonly Dictionary<string, ConductorBatchLoop.SoloAcceptanceAdmissionKind> _decisions = new(StringComparer.Ordinal);

    internal void Clear() => _decisions.Clear();

    internal void Record(GoalId goalId, ConductorBatchLoop.SoloAcceptanceAdmissionKind kind) =>
        _decisions[goalId.Value] = kind;

    internal int Count(IReadOnlyList<Goal> orderedEligible, IReadOnlyDictionary<string, ParallelLandingOutcome> results) =>
        orderedEligible.Count(goal =>
        {
            if (goal.Status != GoalStatus.Verified) return false;
            var hasResult = results.TryGetValue(goal.Id.Value, out var result);
            if (result?.SlotIndex is not null) return false;
            return _decisions.TryGetValue(goal.Id.Value, out var kind)
                ? kind == ConductorBatchLoop.SoloAcceptanceAdmissionKind.Admissible
                : !hasResult;
        });
}
