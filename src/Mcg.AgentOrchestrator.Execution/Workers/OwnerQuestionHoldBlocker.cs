using System.Globalization;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Read-only projection of every hold state that excludes a goal from the conductor walk.
public sealed record OwnerQuestionHoldBlocker(
    GoalId GoalId, string State, string Identity, DateTimeOffset StartedAt, string Question,
    string? TaskId, int? TaskNumber, string ResolvingVerb, string NextAction)
{
    public string? TriggerIdentity { get; private init; }

    public static OwnerQuestionHoldBlocker? TryRead(Goal goal)
    {
        if (goal.CurrentHold is not { State: "steward-owner-question" or "author-owner-question" } hold)
            return null;

        var goal8 = goal.Id.Value[..8];
        var fallback = new OwnerQuestionHoldBlocker(goal.Id, hold.State, hold.Identity, hold.StartedAt,
            hold.Blocker, null, null, hold.State == "steward-owner-question" ? "retry" : "answer",
            $"attention show {goal8}");
        if (hold.State == "author-owner-question") return ReadAuthor(hold.Blocker, goal8, fallback);

        const string triggerMarker = " trigger=";
        const string questionMarker = " question=";
        var triggerStart = hold.Blocker.IndexOf(triggerMarker, StringComparison.Ordinal);
        if (triggerStart < 0) return fallback;
        triggerStart += triggerMarker.Length;
        var triggerEnd = hold.Blocker.IndexOf(' ', triggerStart);
        if (triggerEnd < 0) return fallback;
        var trigger = hold.Blocker[triggerStart..triggerEnd];
        var parts = trigger.Split(':');
        if (parts.Length != 4 || parts.Any(string.IsNullOrWhiteSpace)) return fallback;
        var questionStart = hold.Blocker.IndexOf(questionMarker, triggerEnd, StringComparison.Ordinal);
        if (questionStart < 0) return fallback;
        questionStart += questionMarker.Length;
        var questionEnd = hold.Blocker.LastIndexOf(" evidence=[", StringComparison.Ordinal);
        if (questionEnd <= questionStart) return fallback;

        int? taskNumber = null;
        for (var index = 0; index < goal.Tasks.Count; index++)
            if (goal.Tasks[index].Id.Value == parts[1]) { taskNumber = index + 1; break; }
        return fallback with
        {
            Question = hold.Blocker[questionStart..questionEnd], TaskId = parts[1], TaskNumber = taskNumber,
            TriggerIdentity = trigger,
            NextAction = $"retry --goal {goal8} {taskNumber?.ToString(CultureInfo.InvariantCulture) ?? "<task-number>"} --cause <cause> --text-file <path>"
        };
    }

    private static OwnerQuestionHoldBlocker ReadAuthor(string blocker, string goal8,
        OwnerQuestionHoldBlocker fallback)
    {
        // Execution cannot reference App; mirror AuthorOwnerQuestionHold.TryParse's grammar.
        const string prefix = "author-owner-question item=";
        const string reasonMarker = " reason=";
        const string questionMarker = " question=";
        const string recommendationMarker = " recommendation=";
        if (!blocker.StartsWith(prefix, StringComparison.Ordinal)) return fallback;
        var itemEnd = blocker.IndexOf(' ', prefix.Length);
        if (itemEnd < 0) return fallback;
        var item = blocker[prefix.Length..itemEnd];
        var colon = item.IndexOf(':');
        if (colon <= 0 || colon == item.Length - 1) return fallback;
        var reasonStart = blocker.IndexOf(reasonMarker, itemEnd, StringComparison.Ordinal);
        if (reasonStart != itemEnd) return fallback;
        reasonStart += reasonMarker.Length;
        var questionStart = blocker.IndexOf(questionMarker, reasonStart, StringComparison.Ordinal);
        if (questionStart <= reasonStart) return fallback;
        questionStart += questionMarker.Length;
        var recommendationStart = blocker.LastIndexOf(recommendationMarker, StringComparison.Ordinal);
        if (recommendationStart <= questionStart ||
            recommendationStart + recommendationMarker.Length == blocker.Length) return fallback;
        var targetId = item[(colon + 1)..];
        var command = item[..colon] switch
        {
            "HumanInput" => $"answer {targetId} --text-file <path>",
            // The full item id is emitted by attention show JSON and accepted by attention answer.
            "Clarification" => $"attention answer {goal8} {targetId} --text-file <path>",
            _ => null
        };
        return command is null ? fallback : fallback with
        {
            Question = blocker[questionStart..recommendationStart], NextAction = command
        };
    }
}
