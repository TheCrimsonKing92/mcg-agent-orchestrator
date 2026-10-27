namespace Mcg.AgentOrchestrator.Core;

// Resolves current-brief report criteria when unchanged criteria retain an older spec stamp.
internal static class ReportCriterionLookup
{
    internal static string? Find(Goal goal, PlannerEvidenceIdentity? identity)
    {
        var version = goal.AuthoritativeRefinedSpecVersion;
        if (identity is null || identity.BriefVersion != goal.AuthoritativeBrief.Version ||
            version is null || version.BriefVersion > identity.BriefVersion)
            return null;

        var index = identity.CriterionIndex - 1;
        return index >= 0 && index < version.Spec.AcceptanceCriteria.Count
            ? version.Spec.AcceptanceCriteria[index] : null;
    }
}
