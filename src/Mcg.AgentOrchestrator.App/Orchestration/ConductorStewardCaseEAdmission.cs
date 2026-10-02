using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ConductorStewardCaseEAdmission
{
    internal static bool Admits(Goal goal, TaskSpec task, AdjudicateOperatorIntentPayload payload, string? head) =>
        payload.Shape?.Trim().Equals("close", StringComparison.OrdinalIgnoreCase) == true &&
        HasCaseE(payload) && payload.Precedent?.StartsWith("steward-case=E trigger=", StringComparison.Ordinal) == true &&
        ConductorStewardTriggerDetector.IsCaseETask(goal, task, head);

    // The close changes goal state, so a harvest-time Reviewer precondition would be stale.
    // Instead require the paired close's applied receipt and the same Developer round/candidate.
    internal static bool AdmitsReviewerRetry(Goal goal, TaskSpec reviewer,
        AdjudicateOperatorIntentPayload payload, string? head)
    {
        if (goal.IsTerminal || reviewer.RequiredRole != AgentRole.Reviewer || !HasCaseE(payload) ||
            payload.Shape != "route" || payload.Cause != nameof(RetryCause.ContractClarification)) return false;
        var developerId = Reference(payload, "developer-task=");
        var closeId = Reference(payload, "developer-close-intent=");
        var dispatchTicks = Reference(payload, "developer-dispatch=");
        var candidate = Reference(payload, "dispatch-base=");
        var developer = goal.Tasks.SingleOrDefault(task => task.Id.Value == developerId);
        return developer is { Status: WorkTaskStatus.Completed, LastDispatch: { } dispatch } &&
               developer == goal.Tasks.LastOrDefault(task => task.RequiredRole == AgentRole.Developer) &&
               !string.IsNullOrWhiteSpace(closeId) && !string.IsNullOrWhiteSpace(candidate) &&
               dispatch.DispatchedAt.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture) == dispatchTicks &&
               string.Equals(candidate, dispatch.BaseCommit, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(candidate, head, StringComparison.OrdinalIgnoreCase) &&
               ConductorStewardTriggerDetector.ResolveCaseEReviewer(goal, developer) == reviewer &&
               // Intent receipts are goal-level events; their typed payload owns the task identity.
               goal.Timeline.Any(item => item.OperatorIntentApplied is { Actor: "steward", Channel: "conductor-steward",
                       ActorKind: OperatorActorKind.Agent, AuthenticationAssurance: "steward", Outcome: "applied" } applied &&
                   applied.IntentId == closeId && applied.TaskId == developer.Id.Value &&
                   item.Message.Contains("shape=close outcome=applied", StringComparison.Ordinal));
    }

    private static bool HasCaseE(AdjudicateOperatorIntentPayload payload) =>
        (payload.EvidenceReferences ?? []).Where(reference => reference.StartsWith("steward-case=", StringComparison.Ordinal))
            .SequenceEqual(["steward-case=E"]);

    private static string? Reference(AdjudicateOperatorIntentPayload payload, string prefix)
    {
        var values = (payload.EvidenceReferences ?? []).Where(reference => reference.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        return values.Length == 1 ? values[0][prefix.Length..] : null;
    }
}
