using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorStewardHost
{
    private void HarvestCaseEClose(AgentOrchestratorKernel kernel, Goal goal,
        ConductorStewardStoredTrigger stored, ConductorStewardAdjudication adjudication,
        string worktree, long? version, HashSet<GoalId> changed, string versionDetail)
    {
        var trigger = stored.Trigger;
        var developer = goal.Tasks.Single(item => item.Id.Value == trigger.TaskId);
        var reviewer = ConductorStewardTriggerDetector.ResolveCaseEReviewer(goal, developer);
        var rejection = ConductorStewardRoutePolicy.RejectionReason(trigger, adjudication, goal, worktree, _evidence);
        if (rejection is null && _triggers.HasAppliedRoute(trigger)) rejection = "route-bound-exceeded";
        if (rejection is null && reviewer is null) rejection = "reviewer-task-unresolved";
        if (rejection is null && string.IsNullOrWhiteSpace(trigger.WorkerResult)) rejection = "developer-explanation-unavailable";
        if (rejection is not null)
        {
            Refuse(rejection);
            return;
        }
        if (version is null)
        {
            _triggers.MarkServiced(stored.Key, "no-action", null);
            Record(trigger, "no-action", "goal-version-unavailable", null, versionDetail);
            return;
        }
        var references = trigger.EvidenceReferences.Concat(adjudication.EvidenceReferences ?? [])
            .Append($"steward-trigger={stored.Key}").Append("steward-case=E");
        if (!string.IsNullOrWhiteSpace(adjudication.Precedent))
            references = references.Append($"model-precedent={adjudication.Precedent}");
        var close = new AdjudicateOperatorIntentPayload("close",
            ConductorStewardCaseDAdmission.ComposeText(adjudication.Text, trigger.WorkerResult),
            references.Distinct(StringComparer.Ordinal).ToArray(), version.Value, worktree,
            Reversibility: adjudication.Reversibility ?? "reversible-with-cost",
            Precedent: $"steward-case=E trigger={trigger.Identity}",
            Precondition: AdjudicationPrecondition.Capture(goal, developer));
        var closeId = Guid.NewGuid().ToString("N");
        // The Reviewer receives the worker's text, never the Steward's judgment.
        var retry = new AdjudicateOperatorIntentPayload("route",
            ConductorStewardCaseDAdmission.ComposeText(
                $"Developer task {trigger.TaskId} made no commit; candidate {trigger.CandidateSha} is unchanged. Re-review the finding against this explanation.",
                trigger.WorkerResult),
            [$"steward-trigger={stored.Key}", "steward-case=E", $"developer-task={developer.Id.Value}",
                $"developer-close-intent={closeId}", $"dispatch-base={trigger.CandidateSha}",
                $"developer-dispatch={developer.LastDispatch!.DispatchedAt.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture)}"],
            version.Value, worktree, nameof(RetryCause.ContractClarification), "reversible",
            $"steward-case=E trigger={trigger.Identity}");
        if (close.EvidenceReferences.Concat(retry.EvidenceReferences)
            .Any(reference => !_evidence.TryResolve(reference, goal, close, out _)))
        {
            Refuse("evidence-reference-unresolved");
            return;
        }
        var createdAt = _utcNow();
        var closeIntent = Enqueue(closeId, $"steward-{stored.Key}", developer, close, createdAt);
        // Idempotent recovery must bind the retry to the actual accepted close record.
        retry = retry with { EvidenceReferences = retry.EvidenceReferences.Select(reference =>
            reference == $"developer-close-intent={closeId}" ? $"developer-close-intent={closeIntent.Id}" : reference).ToArray() };
        var reviewerIntent = Enqueue(Guid.NewGuid().ToString("N"), $"steward-{stored.Key}-reviewer-retry",
            reviewer!, retry, closeIntent.CreatedAt.AddMilliseconds(1));
        // Count the close even if the dependent Reviewer retry is later refused.
        _triggers.MarkServiced(stored.Key, "route-submitted", closeIntent.Id);
        Record(trigger, "close", "close-submitted", closeIntent.Id, versionDetail);
        Record(trigger, "route", "reviewer-retry-submitted", reviewerIntent.Id, versionDetail);

        OperatorIntentRecord Enqueue(string id, string key, TaskSpec target,
            AdjudicateOperatorIntentPayload payload, DateTimeOffset at) =>
            _intents.EnqueueAsync(new OperatorIntentRecord(id, key, OperatorIntentVerbs.Adjudicate,
                trigger.GoalId, target.Id.Value, JsonSerializer.Serialize(payload, _json), [],
                "steward", "conductor-steward", OperatorIntentAdjudication.StewardAssurance,
                at, ActorKind: OperatorActorKind.Agent)).GetAwaiter().GetResult();

        void Refuse(string reason)
        {
            Question(kernel, goal, trigger,
                $"Steward proposal needs owner decision ({reason}): {adjudication.Text}",
                adjudication.EvidenceReferences ?? [], changed, versionDetail);
            _triggers.MarkServiced(stored.Key, "owner-question", null);
        }
    }
}
