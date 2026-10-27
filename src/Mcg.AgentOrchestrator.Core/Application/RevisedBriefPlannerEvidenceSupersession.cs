namespace Mcg.AgentOrchestrator.Core;

// Called under the revision lock: identity-less requests present here predate the new brief.
internal static class RevisedBriefPlannerEvidenceSupersession
{
    internal static IReadOnlyList<HumanInputRequestId> Complete(
        IEnumerable<HumanInputRequest> requests, Goal goal, GoalBriefVersion version,
        DateTimeOffset at, IGoalLifecycleEventWriter eventWriter)
    {
        var eligible = requests.Where(request =>
                request.GoalId == goal.Id && !request.IsCompleted &&
                request.Kind == HumanWaitKind.ProspectiveAcceptanceEvidence &&
                (request.PlannerIdentity is null || request.PlannerIdentity.BriefVersion < version.Version))
            .OrderBy(request => request.Id.Value, StringComparer.Ordinal).ToArray();
        if (eligible.Length == 0) return [];

        foreach (var request in eligible)
            request.CompleteAsBriefSuperseded(version.Version, version.Reason, at);

        var ids = eligible.Select(request => request.Id).ToArray();
        var progress = new ProgressEvent(goal.Id, null, ProgressKind.HumanInputSuperseded,
            $"kind=planner-evidence-superseded briefVersion=v{version.Version} " +
            $"requests={string.Join(',', ids.Select(id => id.Value))} reason={eligible[0].BriefSupersession!.Reason}",
            at, HumanInputSuperseded: new HumanInputSupersededPayload([]));
        goal.Append(progress);
        eventWriter.AppendTimelineEvent(progress);
        return ids;
    }
}
