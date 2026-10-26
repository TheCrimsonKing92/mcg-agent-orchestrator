using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.ReadModels;

internal static class ExperimentMembership
{
    internal static ExperimentRecord Effective(ExperimentRecord record, IReadOnlyList<ExperimentMemberEvent> events)
    {
        if (events.Count == 0) return record;
        var baseline = record.Spec.Baseline;
        IReadOnlyList<string> Append(IReadOnlyList<string>? specIds, string arm) =>
            (specIds ?? []).Concat(events.Where(member => member.Arm == arm).Select(member => member.GoalId))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return record with { Spec = record.Spec with { Baseline = baseline with
        {
            BaselineGoalIds = Append(baseline.BaselineGoalIds, "baseline"),
            ComparisonGoalIds = Append(baseline.ComparisonGoalIds, "comparison")
        } } };
    }
}
