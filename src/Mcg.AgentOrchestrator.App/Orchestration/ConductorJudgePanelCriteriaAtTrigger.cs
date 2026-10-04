using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record PanelCriteriaSnapshot(string Version, IReadOnlyList<string> Criteria,
    string Provenance = "as-recorded");

internal static class ConductorJudgePanelCriteriaAtTrigger
{
    internal static PanelCriteriaSnapshot Resolve(GoalSnapshot? goal, DateTimeOffset triggerTime)
    {
        // SupersededByVersion is a later fact: historical selection uses RecordedAt, not current authority.
        var version = goal?.RefinedSpecVersions?.Where(item => item.RecordedAt <= triggerTime)
            .OrderBy(item => item.RecordedAt).ThenBy(item => item.Version).LastOrDefault();
        if (version is null)
            return new("no-refined-spec", ["No versioned acceptance criteria were recorded at the trigger."]);
        // Owner-approved limitation: feasibility re-scopes replace this version in place.
        // Its retained text is authoritative here; do not reconstruct lost text from events
        // or apply the goal's separate correction ledger to the recorded criteria.
        var recorded = goal!.RefinedSpecVersions!.Single(item => item.Version == version.Version);
        var provenance = recorded.Spec.Decisions.Any(item => item.Rationale.StartsWith(
            "Feasibility disposition applied and re-checked", StringComparison.Ordinal))
            ? "criteria-rescoped-in-place" : "as-recorded";
        // Slice 1 case currency uses the criteria hash, rather than the integer spec version.
        var spec = new RefinedSpec("", recorded.Spec.AcceptanceCriteria, VerificationClass.TestVerifiable, [], []);
        return new(EffectiveAcceptanceCriteriaVersion.ComputeHash(spec, []),
            recorded.Spec.AcceptanceCriteria, provenance);
    }
}
