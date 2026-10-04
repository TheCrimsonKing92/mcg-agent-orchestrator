using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record PanelCriteriaSnapshot(string Version, IReadOnlyList<string> Criteria);

internal static class ConductorJudgePanelCriteriaAtTrigger
{
    internal static PanelCriteriaSnapshot Resolve(GoalSnapshot? goal, DateTimeOffset triggerTime)
    {
        // SupersededByVersion is a later fact: historical selection uses RecordedAt, not current authority.
        var version = goal?.RefinedSpecVersions?.Where(item => item.RecordedAt <= triggerTime)
            .OrderBy(item => item.RecordedAt).ThenBy(item => item.Version).LastOrDefault();
        if (version is null)
            return new("no-refined-spec", ["No versioned acceptance criteria were recorded at the trigger."]);
        var spec = new RefinedSpec("", version.Spec.AcceptanceCriteria, VerificationClass.TestVerifiable, [], []);
        var corrections = (goal!.EffectiveAcceptanceCriteriaCorrections ?? [])
            .Where(item => item.RecordedAt <= triggerTime)
            .Select(item => new EffectiveAcceptanceCriteriaCorrection(item.SupersededCriterion,
                item.Correction, item.Actor, item.RecordedAt, null, item.SourceKind, item.IsWaiver)).ToArray();
        return new(EffectiveAcceptanceCriteriaVersion.ComputeHash(spec, corrections),
            EffectiveAcceptanceCriteriaVersion.BuildSnapshot(spec, corrections));
    }
}
