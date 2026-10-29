using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class AttentionShowAgentReadView
{
    internal static string Render(string? goalId, IReadOnlyList<HumanInputRequest> waits,
        IReadOnlyList<CollaborationItem> items, Func<CollaborationItem, string?> clarificationId,
        TimeProvider? time = null, IReadOnlyCollection<Goal>? goals = null) =>
        AgentReadEnvelope.Serialize(AgentReadEnvelope.AttentionShowSchema, AgentReadEnvelope.AttentionShowSchemaVersion,
            AttentionAgentReadView.WithPostLandingDuties(new Dictionary<string, object?>
            {
                ["goalId"] = goalId,
                ["waits"] = waits.Select(wait => new
                {
                    id = wait.Id.Value,
                    goalId = wait.GoalId.Value,
                    taskId = wait.TaskId?.Value,
                    kind = wait.Kind.ToString(),
                    question = wait.Question,
                    plannerIdentity = wait.PlannerIdentity,
                    plannerEvidence = wait.PlannerEvidence,
                    owner = wait.EvidenceOwner ?? "operator",
                    availability = wait.PlannerEvidence?.Availability,
                    nextAction = wait.ResumeCommand,
                    requestedAt = wait.RequestedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                    isAutoDefaultable = wait.IsAutoDefaultable,
                    isDismissible = wait.IsDismissible,
                    isAnswerRequired = wait.IsAnswerRequired,
                    isExternallyBlocked = wait.IsExternallyBlocked,
                    resumeCommand = wait.ResumeCommand
                }).ToArray(),
                ["gateEvidence"] = (goals ?? []).Where(goal => goalId is null || goal.Id.Value == goalId)
                    .SelectMany(goal => goal.GateTestObligations.Where(item => item.BriefVersion == goal.AuthoritativeBrief.Version)
                        .Select(item => new { goalId = goal.Id.Value, identity = item.PlannerIdentity,
                            owner = "acceptance-gate", availability = "post-implementation", item.Selectors,
                            payload = item.PlannerEvidence, nextAction = "Acceptance executes exact-candidate selectors" })).ToArray(),
                ["items"] = items.Select(item => new
                {
                    id = item.Id,
                    clarificationId = clarificationId(item),
                    type = item.Type.ToString(),
                    goalId = item.GoalId,
                    status = item.Status.ToString(),
                    subject = item.Subject,
                    body = item.Body,
                    correlationKey = item.CorrelationKey,
                    raisedAt = item.RaisedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)
                }).ToArray(),
                ["ownerQuestionHolds"] = (goals ?? []).Where(goal => goalId is null || goal.Id.Value == goalId)
                    .Where(goal => goal.Status is not (GoalStatus.Completed or GoalStatus.Cancelled or GoalStatus.Superseded))
                    .Select(OwnerQuestionHoldBlocker.TryRead).OfType<OwnerQuestionHoldBlocker>()
                    .Select(hold => new
                    {
                        goalId = hold.GoalId.Value,
                        state = hold.State,
                        identity = hold.Identity,
                        question = hold.Question,
                        taskId = hold.TaskId,
                        taskNumber = hold.TaskNumber,
                        startedAt = hold.StartedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                        resolvingVerb = hold.ResolvingVerb,
                        nextAction = hold.NextAction
                    }).ToArray()
            }, AttentionAgentReadView.DutyRows(null,
                (goals ?? []).Where(goal => goalId is null || goal.Id.Value == goalId).ToArray())),
            new JsonSerializerOptions(JsonSerializerDefaults.Web), time);
}
