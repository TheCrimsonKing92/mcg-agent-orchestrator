namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// Correlate recorded progress, keeping presentation sentences in the narrator.
internal static class OwnerActivityProgress
{
    internal static OwnerActivityResolution? RetryAfter(OwnerConductEvent source, IReadOnlyList<OwnerConductEvent> events)
    {
        if (source.EventKind != "goal-escalation" &&
            !(source.EventKind == "goal-lifecycle" && Head(source) == "TaskFailed")) return null;
        var retry = events.FirstOrDefault(value => value.Timestamp > source.Timestamp && SameGoal(value.GoalId, source.GoalId) &&
            value.EventKind == "goal-lifecycle" && Head(value) == "TaskDispatched" &&
            (Field(source, "task") is null || Field(source, "task") == Field(value, "task")));
        return retry is null ? null : new(retry.Timestamp, true);
    }

    internal static OwnerActivityResolution? Resolution(OwnerAttentionObservation entry, IReadOnlyList<OwnerConductEvent> events)
    {
        if (entry.ResolvedAt is not { } at) return null;
        var answer = events.LastOrDefault(item => item.Timestamp >= entry.FirstSeen && item.Timestamp <= at &&
            SameGoal(item.GoalId, entry.Question.GoalId) && item.EventKind == "goal-lifecycle" &&
            (Field(item, "answer-target")?.Split(':').Last() == entry.Question.ItemId ||
             entry.Question.Kind == OwnerQuestionKind.StewardHold && Field(item, "resolution-verb") is "retry" or "adjudicate"));
        return answer is null ? new(at) : new(answer.Timestamp, Actor: Field(answer, "resolution-actor"));
    }

    private static bool SameGoal(string? left, string? right) => left is not null && right is not null &&
        left[..Math.Min(8, left.Length)].Equals(right[..Math.Min(8, right.Length)], StringComparison.OrdinalIgnoreCase);
    private static string Head(OwnerConductEvent item) => item.Detail.Split(' ', 2)[0];
    private static string? Field(OwnerConductEvent item, string name) => OwnerActivityNarrator.Field(item, name);
}
