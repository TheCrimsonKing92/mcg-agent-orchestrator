using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class GateTestObligationWaivers
{
    internal static EffectiveAcceptanceCriteriaCorrection? FindWaiver(Goal goal, GateTestObligation obligation)
    {
        if (obligation.PlannerIdentity is not { } identity || goal.RefinedSpec is not { } spec ||
            identity.CriterionIndex < 1 || identity.CriterionIndex > spec.AcceptanceCriteria.Count)
            return null;

        var criterion = spec.AcceptanceCriteria[identity.CriterionIndex - 1].Trim();
        return goal.EffectiveAcceptanceCriteriaCorrections.LastOrDefault(correction =>
            correction.IsWaiver && string.Equals(correction.SupersededCriterion, criterion, StringComparison.OrdinalIgnoreCase));
    }

    internal static string FormatReason(string reason)
    {
        var normalized = string.Join(' ', reason.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= 120 ? normalized : normalized[..120] + "...";
    }

    internal static string? DescribeWaiver(Goal goal, GateTestObligation obligation) =>
        FindWaiver(goal, obligation)?.WaiverReason is { } reason ? "waived: " + FormatReason(reason) : null;
}
