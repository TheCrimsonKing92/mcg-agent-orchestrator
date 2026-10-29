namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerActivityAttempts
{
    internal static int? Number(IReadOnlyList<OwnerConductEvent> ordered, int index)
    {
        var item = ordered[index];
        if (item.EventKind != "goal-lifecycle" ||
            Head(item) is not ("TaskDispatched" or "TaskCompleted" or "TaskFailed") ||
            OwnerEventFields.Field(item, "task") is not { } task) return null;

        var count = 0;
        for (var previous = 0; previous <= index; previous++)
        {
            var candidate = ordered[previous];
            if (candidate.EventKind == "goal-lifecycle" && Head(candidate) == "TaskDispatched" &&
                Prefix(candidate.GoalId).Equals(Prefix(item.GoalId), StringComparison.OrdinalIgnoreCase) &&
                string.Equals(OwnerEventFields.Field(candidate, "task"), task, StringComparison.Ordinal))
                count++;
        }
        return count == 0 ? null : count;
    }

    internal static string Label(string phrase, int insertAt, int? attempt) =>
        attempt is >= 2 ? phrase.Insert(insertAt, $" (attempt {attempt})") : phrase;

    private static string Head(OwnerConductEvent item) => item.Detail.Split(' ', 2)[0];
    private static string Prefix(string? id) => id is null ? "" : id[..Math.Min(8, id.Length)];
}
