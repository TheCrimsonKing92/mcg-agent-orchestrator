using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record ConductorAuthorItem(
    OperatorAnswerTargetKind TargetKind, string TargetId, string GoalId,
    string Question, string? ForkKind)
{
    internal string Identity => $"{GoalId}:{TargetKind}:{TargetId}";
}

internal static class ConductorAuthorItems
{
    internal static IReadOnlyList<ConductorAuthorItem> Detect(
        Goal goal, IReadOnlyList<CollaborationItem> collaborationItems,
        IEnumerable<HumanInputRequest> humanInputRequests,
        IReadOnlyList<Mcg.AgentOrchestrator.Infrastructure.OperatorIntentRecord> intents)
    {
        var result = new List<ConductorAuthorItem>();
        foreach (var item in collaborationItems)
        {
            if (item.Type != CollaborationItemType.Clarification ||
                !string.Equals(item.GoalId, goal.Id.Value, StringComparison.Ordinal) ||
                item.CorrelationKey?.StartsWith("spec-clarification:", StringComparison.Ordinal) != true ||
                CollaborationItemLifecycle.IsTerminal(item.Status) ||
                item.AuthoritativeAnswer is not null || HasQueuedHumanAnswer(intents, OperatorAnswerTargetKind.Clarification, item.Id))
                continue;
            result.Add(new ConductorAuthorItem(OperatorAnswerTargetKind.Clarification, item.Id,
                goal.Id.Value, ExtractQuestion(item.Body), ExtractForkKind(item.Body)));
        }
        foreach (var request in humanInputRequests)
        {
            if (request.GoalId != goal.Id || request.TaskId is null ||
                !HumanWaitPolicyDefaults.IsSpecClarificationClass(request.Kind) ||
                request.StoreReference is not null || request.IsCompleted ||
                !goal.Tasks.Any(task => task.Id == request.TaskId && task.Status == WorkTaskStatus.WaitingForHuman) ||
                request.AuthoritativeAnswer is not null ||
                HasQueuedHumanAnswer(intents, OperatorAnswerTargetKind.HumanInput, request.Id.Value))
                continue;
            result.Add(new ConductorAuthorItem(OperatorAnswerTargetKind.HumanInput, request.Id.Value,
                goal.Id.Value, request.Question, request.Kind == HumanWaitKind.PlannerPrerequisiteEvidence
                    ? "planner-prerequisite-evidence" : "worker-spec-clarification"));
        }
        return result;
    }

    private static bool HasQueuedHumanAnswer(
        IReadOnlyList<Mcg.AgentOrchestrator.Infrastructure.OperatorIntentRecord> intents,
        OperatorAnswerTargetKind kind, string targetId) => intents.Any(intent =>
        intent.Verb == OperatorIntentVerbs.Answer && intent.ActorKind == OperatorActorKind.Human &&
        intent.Status is Mcg.AgentOrchestrator.Infrastructure.OperatorIntentStatus.Pending or
            Mcg.AgentOrchestrator.Infrastructure.OperatorIntentStatus.Claimed &&
        AnswerTargets(intent.PayloadJson, kind, targetId));

    private static bool AnswerTargets(string json, OperatorAnswerTargetKind kind, string targetId)
    {
        try
        {
            var payload = System.Text.Json.JsonSerializer.Deserialize<AnswerOperatorIntentPayload>(json,
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
            return payload?.TargetKind == kind && payload.TargetId == targetId;
        }
        catch (System.Text.Json.JsonException) { return false; }
    }

    private static string ExtractQuestion(string body)
    {
        var line = body.Split('\n').FirstOrDefault(part => part.TrimStart().StartsWith("Question:", StringComparison.OrdinalIgnoreCase));
        return line is null ? body : line[(line.IndexOf(':') + 1)..].Trim();
    }

    private static string? ExtractForkKind(string body)
    {
        var line = body.Split('\n').FirstOrDefault(part => part.TrimStart().StartsWith("Fork kind:", StringComparison.OrdinalIgnoreCase));
        return line is null ? null : line[(line.IndexOf(':') + 1)..].Trim();
    }
}
