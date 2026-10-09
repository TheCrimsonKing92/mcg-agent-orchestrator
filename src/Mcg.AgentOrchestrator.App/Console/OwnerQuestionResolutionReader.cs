using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed record OwnerQuestionResolution(string Id, string GoalId, string GoalTitle, string Stage,
    string Question, string Outcome, string Answerer, DateTimeOffset ResolvedAt, string? Answer,
    IReadOnlyList<string> Evidence, string NextStage);

// Opens existing stores only; the console never creates or mutates answer state.
internal sealed class OwnerQuestionResolutionReader(IOrchestratorStateQueries state, IGoalEventTail tail,
    string orchestratorDirectory, string logDirectory, TimeProvider clock)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal async Task<IReadOnlyList<OwnerQuestionResolution>> ListForGoalAsync(string goalId, CancellationToken token)
    {
        var kernel = await state.LoadGoalsAsync([new GoalId(goalId)], token);
        var goal = kernel.Goals.SingleOrDefault(goal => goal.Id.Value == goalId);
        if (goal is null) return [];
        var candidates = new List<Candidate>();
        foreach (var request in kernel.HumanInputRequests.Where(request => request.GoalId == goal.Id && request.IsCompleted))
        {
            var task = goal.Tasks.FirstOrDefault(task => task.Id == request.TaskId);
            var stage = task is null ? request.Kind == HumanWaitKind.SpecClarification ?
                "spec clarification before Planner dispatch" : "goal clarification" :
                $"{task.RequiredRole} clarification on task {Prefix(task.Id.Value)}";
            candidates.Add(new(request.Id.Value, stage, request.Question, request.AnsweredAt ?? request.RequestedAt,
                request.SupersededByRequestId is not null ? "Superseded" : request.WasDismissed ? "Dismissed" : "Cleared",
                OperatorAnswerTargetKind.HumanInput));
        }
        if (File.Exists(Path.Combine(orchestratorDirectory, "collaboration-items.db")))
        {
            var items = await CollaborationItemStore.OpenExisting(orchestratorDirectory).ListAsync(goalId, token);
            foreach (var item in items.Where(item => item.CorrelationKey?.StartsWith("spec-clarification:", StringComparison.Ordinal) == true &&
                         CollaborationItemLifecycle.IsTerminal(item.Status)))
                candidates.Add(new(item.Id, "spec clarification before Planner dispatch", Question(item.Body) ?? item.Subject,
                    item.ResolvedAt ?? item.RaisedAt,
                    item.Resolution?.Contains("supersed", StringComparison.OrdinalIgnoreCase) == true ? "Superseded" : "Dismissed",
                    OperatorAnswerTargetKind.Clarification));
        }

        var answers = new Dictionary<string, (OperatorIntentRecord Intent, AnswerOperatorIntentPayload Payload)>();
        if (File.Exists(Path.Combine(orchestratorDirectory, SqliteOperatorIntentStore.DatabaseFileName)))
        {
            var store = SqliteOperatorIntentStore.OpenExisting(orchestratorDirectory, logDirectory);
            // Use the exact intent read used by operator-intent-status, from durable application references.
            foreach (var id in goal.Timeline.Select(item => item.OperatorIntentApplied)
                         .Where(item => item?.Verb == OperatorIntentVerbs.Answer).Select(item => item!.IntentId).Distinct())
            {
                var intent = await store.GetAsync(id, token);
                if (intent is not { Status: OperatorIntentStatus.Applied, CompletedAt: not null } ||
                    intent.GoalId != goalId || intent.Verb != OperatorIntentVerbs.Answer) continue;
                try
                {
                    var payload = JsonSerializer.Deserialize<AnswerOperatorIntentPayload>(intent.PayloadJson, JsonOptions);
                    if (payload is null || payload.GoalId != goalId || string.IsNullOrEmpty(payload.TargetId) || payload.Text is null ||
                        !candidates.Any(candidate => candidate.Id == payload.TargetId && candidate.TargetKind == payload.TargetKind)) continue;
                    if (!answers.TryGetValue(payload.TargetId, out var previous) || previous.Intent.CompletedAt < intent.CompletedAt)
                        answers[payload.TargetId] = (intent, payload);
                }
                catch (JsonException) { /* A malformed intent cannot prove an answer. */ }
            }
        }
        var roles = goal.Tasks.ToDictionary(task => task.Id.Value, task => task.RequiredRole);
        var events = tail.ReadLast(goalId, 100).Select(line => OwnerGoalLifecycleEvent.TryParse(line, goalId, out var item) ? item : null)
            .OfType<OwnerConductEvent>().Select(item => OwnerGoalLifecycleEvent.WithRole(item, roles)).ToArray();
        return candidates.Select(candidate =>
        {
            var answered = answers.TryGetValue(candidate.Id, out var answer);
            var at = answered ? answer.Intent.CompletedAt!.Value : candidate.At;
            var next = events.Where(item => item.Timestamp > at && item.Detail.StartsWith("TaskDispatched", StringComparison.Ordinal))
                .OrderBy(item => item.Timestamp).FirstOrDefault();
            var nextText = next is null ? "not yet resumed" :
                $"{OwnerActivityNarrator.Field(next, "role") ?? "Worker"} started at {Local(next.Timestamp)}";
            return new OwnerQuestionResolution(candidate.Id, goalId, OwnerGoalTitle.Full(goal.Objective), candidate.Stage,
                candidate.Question, answered ? "Answered" : candidate.Outcome,
                answered ? Actor(answer.Intent) : "resolving actor not recorded", at,
                answered ? answer.Payload.Text : null, answered ? answer.Payload.EvidenceReferences ?? [] : [], nextText);
        }).OrderByDescending(item => item.ResolvedAt).ToArray();
    }

    internal async Task<OwnerQuestionResolution?> ReadAsync(OwnerConsoleActivityItem item, CancellationToken token)
    {
        var metadata = await state.ListGoalMetadataAsync(token);
        var matches = metadata.Where(goal => item.GoalId is not null ? goal.Id == item.GoalId :
            goal.Id.StartsWith(item.GoalPrefix, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1) return null;
        var entries = await ListForGoalAsync(matches[0].Id, token);
        if (item.OwnerQuestionId is not null) return entries.FirstOrDefault(entry => entry.Id == item.OwnerQuestionId);
        return entries.FirstOrDefault(entry => entry.ResolvedAt <= item.Timestamp &&
            Normalize(entry.Question) == Normalize(item.Question ?? item.Subject ?? ""));
    }

    private string Local(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, clock.LocalTimeZone).ToString("HH:mm:ss");
    private static string Prefix(string id) => OwnerConsoleViewModelBuilder.Prefix(id);
    private static string Normalize(string text) => string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    private static string Actor(OperatorIntentRecord intent) => intent.Actor.Equals("author", StringComparison.OrdinalIgnoreCase) ||
        intent.Channel == "conductor-author" ? "the Author" : intent.ActorKind == OperatorActorKind.Agent ? "the conductor" :
        intent.Actor.Equals("owner", StringComparison.OrdinalIgnoreCase) ? "the owner" : "the operator";
    private static string? Question(string body)
    {
        var lines = body.Replace("\r", "").Split('\n');
        var start = Array.FindIndex(lines, line => line.TrimStart().StartsWith("Question:", StringComparison.OrdinalIgnoreCase));
        if (start < 0) return null;
        var result = new List<string> { lines[start].TrimStart()["Question:".Length..].TrimStart() };
        foreach (var line in lines.Skip(start + 1))
        {
            if (new[] { "Blast radius:", "Refiner confidence:", "Proposed default:", "Default:", "Topic:" }
                .Any(label => line.TrimStart().StartsWith(label, StringComparison.OrdinalIgnoreCase))) break;
            result.Add(line);
        }
        return string.Join("\n", result).TrimEnd();
    }
    private sealed record Candidate(string Id, string Stage, string Question, DateTimeOffset At, string Outcome,
        OperatorAnswerTargetKind TargetKind);
}
