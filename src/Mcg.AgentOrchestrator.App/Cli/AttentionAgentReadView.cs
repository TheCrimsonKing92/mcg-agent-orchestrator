using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class AttentionAgentReadView
{
    internal static string Render(IReadOnlyList<HumanInputRequest> waits,
        IReadOnlyList<CollaborationItem> items, TimeProvider? time = null,
        IReadOnlyList<ProviderUsageHoldRow>? providerHolds = null, IReadOnlyCollection<Goal>? goals = null,
        IReadOnlyList<AttentionGateEvidence>? gateEvidence = null,
        IReadOnlyList<OwnerQuestionHoldBlocker>? ownerQuestions = null,
        IReadOnlyList<AttentionPostLandingDuty>? postLandingDuties = null) =>
        AgentReadEnvelope.Serialize(AgentReadEnvelope.AttentionSchema, AgentReadEnvelope.AttentionSchemaVersion,
            WithPostLandingDuties(new Dictionary<string, object?>
            {
                ["waits"] = WaitRows(waits),
                ["gateEvidence"] = (gateEvidence ?? (goals ?? []).SelectMany(goal => goal.GateTestObligations
                    .Where(item => item.BriefVersion == goal.AuthoritativeBrief.Version)
                    .Select(item => new AttentionGateEvidence(goal.Id.Value, item))).ToArray())
                    .Select(row => new { goalId = row.GoalId, identity = row.Obligation.PlannerIdentity,
                        owner = "acceptance-gate", availability = "post-implementation", Selectors = row.Obligation.Selectors,
                        payload = row.Obligation.PlannerEvidence, nextAction = "Acceptance executes exact-candidate selectors" }).ToArray(),
                ["items"] = ItemRows(items),
                ["providerHolds"] = (providerHolds ?? []).Select(ProviderUsageHoldJson.Row).ToArray(),
                ["ownerQuestionHolds"] = OwnerQuestionRows(ownerQuestions, goals)
            }, DutyRows(postLandingDuties, goals)), new JsonSerializerOptions(JsonSerializerDefaults.Web), time);

    internal static string RenderSummary(IReadOnlyList<HumanInputRequest> waits,
        IReadOnlyList<CollaborationItem> items, TimeProvider? time = null,
        IReadOnlyList<ProviderUsageHoldRow>? providerHolds = null,
        IReadOnlyList<OwnerQuestionHoldBlocker>? ownerQuestions = null,
        IReadOnlyList<AttentionPostLandingDuty>? postLandingDuties = null) =>
        JsonSerializer.Serialize(new
        {
            schema = "mcg.attention-summary",
            schemaVersion = 1,
            generatedAt = (time ?? TimeProvider.System).GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            omittedSections = new[] { "gateEvidence" },
            payload = WithPostLandingDuties(new Dictionary<string, object?>
            {
                ["waits"] = WaitRows(waits),
                ["items"] = ItemRows(items),
                ["providerHolds"] = (providerHolds ?? []).Select(ProviderUsageHoldJson.Row).ToArray(),
                ["ownerQuestionHolds"] = OwnerQuestionRows(ownerQuestions, null)
            }, DutyRows(postLandingDuties, null))
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    internal static Dictionary<string, object?> WithPostLandingDuties(
        Dictionary<string, object?> payload, object[] duties)
    {
        // Schema 1's existing bytes and property order remain unchanged when no duty is pending.
        if (duties.Length > 0)
            payload.Add("postLandingDuties", duties);
        return payload;
    }

    internal static object[] DutyRows(IReadOnlyList<AttentionPostLandingDuty>? duties,
        IReadOnlyCollection<Goal>? goals) =>
        (duties ?? (goals ?? []).SelectMany(goal => goal.OutstandingPostLandingDuties
            .Select(item => new AttentionPostLandingDuty(goal.Id.Value, item))).ToArray())
        .Select(row => new
        {
            goalId = row.GoalId, obligationId = row.Obligation.Id,
            criterion = row.Obligation.Criterion, criterionVersion = row.Obligation.CriterionVersion,
            criterionNumber = row.Obligation.CriterionNumber, phase = row.Obligation.Phase.ToString(),
            state = row.Obligation.State.ToString(), owner = row.Obligation.Owner.ToString(),
            originalArtifact = row.Obligation.Publication?.CandidateRevision ?? row.Obligation.ExpectedCandidateSha,
            publication = row.Obligation.Publication, observationScope = row.Obligation.ObservationScope,
            nextAction = $"criterion-evidence-record --goal {row.GoalId} {row.Obligation.Id} operator <original-candidate> <receipt-id> \"{row.Obligation.RequiredScope}\" passed <detail> --observation-json <attributed-activation-and-observation>"
        }).ToArray();

    private static object[] WaitRows(IReadOnlyList<HumanInputRequest> waits) =>
        waits.Select(wait => new
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
        }).ToArray();

    private static object[] ItemRows(IReadOnlyList<CollaborationItem> items) =>
        items.Select(item => new
        {
            id = item.Id,
            type = item.Type.ToString(),
            goalId = item.GoalId,
            status = item.Status.ToString(),
            subject = item.Subject,
            body = item.Body,
            correlationKey = item.CorrelationKey,
            raisedAt = item.RaisedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)
        }).ToArray();

    private static object[] OwnerQuestionRows(IReadOnlyList<OwnerQuestionHoldBlocker>? ownerQuestions,
        IReadOnlyCollection<Goal>? goals) =>
        (ownerQuestions ?? (goals ?? [])
            .Where(goal => goal.Status is not (GoalStatus.Completed or GoalStatus.Cancelled or GoalStatus.Superseded))
            .Select(OwnerQuestionHoldBlocker.TryRead).OfType<OwnerQuestionHoldBlocker>().ToArray())
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
            }).ToArray();
}
