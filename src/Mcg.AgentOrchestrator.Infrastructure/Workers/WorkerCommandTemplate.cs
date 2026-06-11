using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record WorkerDispatchPreparation(string PromptPath, string Command, int PromptCharacterCount);

public static class WorkerCommandTemplate
{
    private const int HandoffVerificationMaxChars = 20000;

    public static void WriteHandoffFile(
        IReadOnlyList<TaskSpec> goalTasks,
        TaskId taskId,
        string workingDirectory)
    {
        var priorCompletedTasks = goalTasks
            .TakeWhile(t => t.Id != taskId)
            .Where(t => t.Status == WorkTaskStatus.Completed && t.LastVerification is not null)
            .ToList();

        if (priorCompletedTasks.Count == 0)
        {
            return;
        }

        var lines = new List<string> { "# Prior Task Handoff" };
        foreach (var priorTask in priorCompletedTasks)
        {
            lines.Add(string.Empty);
            lines.Add($"## {priorTask.RequiredRole}: {priorTask.Description}");
            if (!string.IsNullOrWhiteSpace(priorTask.LastVerification!.ModelFitNote))
            {
                lines.Add($"Notes: {priorTask.LastVerification.ModelFitNote}");
            }
            lines.Add(string.Empty);
            lines.Add("### Verification Output");
            var stdout = priorTask.LastVerification.StandardOutput;
            if (stdout.Length > HandoffVerificationMaxChars)
            {
                var truncated = stdout.Length - HandoffVerificationMaxChars;
                lines.Add(stdout[..HandoffVerificationMaxChars]);
                lines.Add($"...[truncated {truncated} chars]...");
            }
            else
            {
                lines.Add(stdout);
            }
            lines.Add(string.Empty);
            lines.Add("---");
        }

        Directory.CreateDirectory(workingDirectory);
        File.WriteAllText(
            Path.Combine(workingDirectory, ".orchestrator-handoff.md"),
            string.Join(Environment.NewLine, lines));
    }

    public static WorkerDispatchPreparation Prepare(
        TaskBrief brief,
        string workerName,
        string commandTemplate,
        string promptRoot,
        IReadOnlyDictionary<string, string?>? variables = null)
    {
        if (string.IsNullOrWhiteSpace(workerName))
        {
            throw new ArgumentException("Value cannot be empty.", nameof(workerName));
        }

        if (string.IsNullOrWhiteSpace(commandTemplate))
        {
            throw new ArgumentException("Value cannot be empty.", nameof(commandTemplate));
        }

        Directory.CreateDirectory(promptRoot);
        var promptPath = Path.Combine(promptRoot, $"{brief.GoalId.Value[..8]}-{brief.TaskId.Value[..8]}-{Sanitize(workerName)}.md");
        File.WriteAllText(promptPath, brief.Content);

        var command = commandTemplate
            .Replace("{promptPath}", Quote(promptPath), StringComparison.OrdinalIgnoreCase)
            .Replace("{goalId}", brief.GoalId.Value, StringComparison.OrdinalIgnoreCase)
            .Replace("{taskId}", brief.TaskId.Value, StringComparison.OrdinalIgnoreCase)
            .Replace("{role}", brief.Role.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{title}", Quote(brief.Title), StringComparison.OrdinalIgnoreCase);
        if (variables is not null)
        {
            foreach (var (key, value) in variables)
            {
                command = command.Replace(
                    "{" + key + "}",
                    string.IsNullOrWhiteSpace(value) ? string.Empty : Quote(value),
                    StringComparison.OrdinalIgnoreCase);
            }
        }

        return new WorkerDispatchPreparation(promptPath, command, brief.Content.Length);
    }

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(ch => invalid.Contains(ch) ? '-' : ch).ToArray());
    }

    private static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
}
