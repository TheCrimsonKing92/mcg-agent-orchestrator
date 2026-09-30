namespace Mcg.AgentOrchestrator.Core;

public sealed record OrchestratorCommitMessage(string Subject, string Body)
{
    private const int MaxSubjectLength = 72;

    public static OrchestratorCommitMessage ForWorker(
        Goal goal,
        TaskSpec task,
        string? dispatchId,
        string? summary,
        IReadOnlyList<string> committedPaths)
    {
        var title = GoalCommitTitle.Resolve(goal.Objective, task.Description);
        if (title.Length == 0 || CarriesProvenanceMarker(title))
        {
            title = GoalCommitTitle.Normalize(task.Description);
        }

        if (title.Length == 0 || CarriesProvenanceMarker(title))
        {
            title = "worker edits";
        }

        var id = goal.Id.Value;
        var prefix = $"{task.RequiredRole}({id[..Math.Min(8, id.Length)]}): ";
        var subject = prefix + title;
        if (subject.Length > MaxSubjectLength)
        {
            if (subject[MaxSubjectLength] == ' ')
            {
                subject = subject[..MaxSubjectLength];
            }
            else
            {
                var cut = subject.LastIndexOf(' ', MaxSubjectLength - 1, MaxSubjectLength);
                subject = cut >= prefix.Length ? subject[..cut] : subject[..MaxSubjectLength];
            }
        }

        var lines = new List<string>();
        AddSafeLine(lines, "Task", task.Description);
        AddSafeLine(lines, "Summary", summary);
        AddSafeLine(lines, "Files", string.Join(", ", committedPaths));
        var trailers = new List<string>();
        AddSafeLine(trailers, "Goal", goal.Id.Value);
        AddSafeLine(trailers, "Task-Id", task.Id.Value);
        AddSafeLine(trailers, "Dispatch", dispatchId);
        var body = lines.Count == 0
            ? string.Join('\n', trailers)
            : string.Join('\n', lines) + "\n\n" + string.Join('\n', trailers);
        return new OrchestratorCommitMessage(subject, body);
    }

    public static string? GoalTitleBody(Goal goal)
    {
        var title = GoalCommitTitle.Resolve(goal.Objective, null);
        return title.Length == 0 || CarriesProvenanceMarker(title) ? null : $"Goal: {title}";
    }

    public static bool CarriesProvenanceMarker(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        return value.StartsWith("Checkpoint-", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("Orchestrator-Checkpoint", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("Integrate goal/", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddSafeLine(List<string> lines, string label, string? value)
    {
        var normalized = GoalCommitTitle.Normalize(value);
        if (normalized.Length > 0 && !CarriesProvenanceMarker(normalized))
        {
            lines.Add($"{label}: {normalized}");
        }
    }
}
