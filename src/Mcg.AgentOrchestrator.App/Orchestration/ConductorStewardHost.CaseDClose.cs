using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorStewardHost
{
    private void HarvestCaseDClose(AgentOrchestratorKernel kernel, Goal goal,
        ConductorStewardStoredTrigger stored, ConductorStewardAdjudication adjudication,
        string worktree, long? version, HashSet<GoalId> changed, string versionDetail)
    {
        var trigger = stored.Trigger;
        var rejection = ConductorStewardRoutePolicy.RejectionReason(trigger, adjudication, goal, worktree, _evidence);
        if (rejection is null && _triggers.HasAppliedRoute(trigger)) rejection = "route-bound-exceeded";
        if (rejection is null && _caseDSources is null) rejection = "case-d-sources-unavailable";
        if (rejection is null && AcceptanceFailingTestIndex.CountRegates(_caseDSources!.Index.Read(), goal.Id.Value) >=
            _caseDSources.RegateCap) rejection = "apparatus re-gate cap spent";
        if (rejection is not null)
        {
            Question(kernel, goal, trigger,
                $"Steward proposal needs owner decision ({rejection}): {adjudication.Text}",
                adjudication.EvidenceReferences ?? [], changed, versionDetail);
            _triggers.MarkServiced(stored.Key, "owner-question", null);
            return;
        }
        if (version is null)
        {
            _triggers.MarkServiced(stored.Key, "no-action", null);
            Record(trigger, "no-action", "goal-version-unavailable", null, versionDetail);
            return;
        }
        var references = trigger.EvidenceReferences.Concat(adjudication.EvidenceReferences ?? [])
            .Append($"steward-trigger={stored.Key}").Append("steward-case=D");
        if (!string.IsNullOrWhiteSpace(adjudication.Precedent))
            references = references.Append($"model-precedent={adjudication.Precedent}");
        var task = goal.Tasks.Single(item => item.Id.Value == trigger.TaskId);
        var payload = new AdjudicateOperatorIntentPayload("close",
            ConductorStewardCaseDAdmission.ComposeText(adjudication.Text, trigger.WorkerResult),
            references.Distinct(StringComparer.Ordinal).ToArray(), version.Value, worktree,
            Reversibility: adjudication.Reversibility ?? "reversible-with-cost",
            Precedent: $"steward-case=D trigger={trigger.Identity}",
            Precondition: AdjudicationPrecondition.Capture(goal, task));
        if (payload.EvidenceReferences.Any(reference => !_evidence.TryResolve(reference, goal, payload, out _)))
        {
            _triggers.MarkServiced(stored.Key, "no-action", null);
            Record(trigger, "no-action", "evidence-reference-unresolved", null, versionDetail);
            return;
        }
        var intentId = Guid.NewGuid().ToString("N");
        var intent = _intents.EnqueueAsync(new OperatorIntentRecord(
            intentId, $"steward-{stored.Key}", OperatorIntentVerbs.Adjudicate,
            trigger.GoalId, trigger.TaskId, JsonSerializer.Serialize(payload, _json), [],
            "steward", "conductor-steward", OperatorIntentAdjudication.StewardAssurance,
            _utcNow(), ActorKind: OperatorActorKind.Agent)).GetAwaiter().GetResult();
        // Reuse the existing submitted/applied lifecycle so the task/candidate action bound includes closes.
        _triggers.MarkServiced(stored.Key, "route-submitted", intent.Id);
        Record(trigger, "close", "close-submitted", intent.Id, versionDetail);
    }
}
