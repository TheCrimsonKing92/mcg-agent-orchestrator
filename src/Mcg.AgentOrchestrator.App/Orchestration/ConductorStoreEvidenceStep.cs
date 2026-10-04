using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class ConductorStoreEvidenceStep(
    IOperatorIntentStore intents, ConductEventLogWriter conduct, Func<string?> storeRoot, IClock clock)
{
    internal const string Channel = "conductor-store-resolver";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HashSet<string> _submitted = new(StringComparer.Ordinal);
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);

    internal static ConductorStoreEvidenceStep CreateDefault(OrchestratorWorkspace workspace) => new(
        SqliteOperatorIntentStore.ForDirectories(workspace.OrchestratorDirectory, workspace.LogDirectory),
        new ConductEventLogWriter(workspace.ConductEventsLogPath), () => workspace.OrchestratorDirectory,
        new SystemClock());

    internal IReadOnlySet<GoalId> ServiceTick(AgentOrchestratorKernel kernel, string? onlyGoalId = null)
    {
        var changed = new HashSet<GoalId>();
        foreach (var goal in kernel.Goals.Where(goal => !goal.IsTerminal &&
                     (onlyGoalId is null || goal.Id.Value == onlyGoalId)))
        {
            if (ConductorOwnerQuestionHolds.ExcludesFromWalk(goal.CurrentHold?.State)) continue;
            foreach (var request in kernel.HumanInputRequests.Where(request => request.GoalId == goal.Id &&
                         request.Kind == HumanWaitKind.PlannerPrerequisiteEvidence && request.StoreReference is not null &&
                         !request.IsCompleted && request.SupersededByRequestId is null && request.TaskId is not null &&
                         goal.Tasks.Any(task => task.Id == request.TaskId && task.RequiredRole == AgentRole.Planner &&
                             task.Status == WorkTaskStatus.WaitingForHuman)))
            {
                if (_submitted.Contains(request.Id.Value) || HasQueuedAnswer(goal, request)) continue;
                var typed = request.StoreReference!;
                var references = WorkerStoreReferenceResolver.Parse([typed.ToStoreRefLine()]);
                var resolution = references.Count == 1
                    ? WorkerStoreReferenceResolver.Resolve(references[0], storeRoot(), clock, out _)
                    : new StoreReferenceResolution(null, "parse-error");
                if (string.IsNullOrWhiteSpace(resolution.Content))
                {
                    var reason = resolution.ReasonCode ?? "empty-excerpt";
                    var eventId = $"store-evidence-unresolved:{request.Id.Value}:{reason}";
                    if (!_reported.Contains(eventId) && conduct.AppendRequired("store-evidence", goal.Id.Value,
                            $"STORE_EVIDENCE_UNRESOLVED goal={goal.Id.Value} request={request.Id.Value} reason={reason}",
                            timestamp: clock.UtcNow, eventId: eventId))
                        _reported.Add(eventId);
                    continue;
                }

                // Recheck precedence after store I/O so a newly queued human answer wins.
                if (HasQueuedAnswer(goal, request)) continue;
                var answer = typed.ToStoreRefLine() + Environment.NewLine +
                    $"Provenance: kind={typed.Kind}; locator={typed.Locator}; sha256={resolution.Sha256}";
                var payload = new AnswerOperatorIntentPayload(OperatorAnswerTargetKind.HumanInput, request.Id.Value,
                    goal.Id.Value, answer, OperatorActorKind.Agent, [$"store-evidence={request.Id.Value}"]);
                intents.EnqueueAsync(new OperatorIntentRecord(Guid.NewGuid().ToString("N"),
                    $"store-evidence-{request.Id.Value}", OperatorIntentVerbs.Answer, goal.Id.Value, null,
                    JsonSerializer.Serialize(payload, Json), [], "store-resolver", Channel, "conductor", clock.UtcNow,
                    ActorKind: OperatorActorKind.Agent)).GetAwaiter().GetResult();
                _submitted.Add(request.Id.Value);
                changed.Add(goal.Id);
            }
        }
        return changed;
    }

    private bool HasQueuedAnswer(Goal goal, HumanInputRequest request) =>
        intents.ListForGoalAsync(goal.Id.Value, int.MaxValue).GetAwaiter().GetResult().Any(intent =>
            intent.Verb == OperatorIntentVerbs.Answer &&
            (intent.Status is OperatorIntentStatus.Pending or OperatorIntentStatus.Claimed || intent.Channel == Channel) &&
            TargetsRequest(intent.PayloadJson, request.Id.Value));

    private static bool TargetsRequest(string json, string requestId)
    {
        try
        {
            var payload = JsonSerializer.Deserialize<AnswerOperatorIntentPayload>(json, Json);
            return payload?.TargetKind == OperatorAnswerTargetKind.HumanInput && payload.TargetId == requestId;
        }
        catch (JsonException) { return false; }
    }
}
