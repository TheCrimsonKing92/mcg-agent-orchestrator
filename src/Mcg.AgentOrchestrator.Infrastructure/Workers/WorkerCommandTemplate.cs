using Mcg.AgentOrchestrator.Core;
using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record WorkerDispatchPreparation(string PromptPath, string Command, int PromptCharacterCount);

public static partial class WorkerCommandTemplate
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

        OrchestratorGeneratedArtifactIgnore.EnsureIgnored(
            workingDirectory,
            "/.orchestrator-handoff.md");

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
        IReadOnlyDictionary<string, string?>? variables = null,
        DateTimeOffset? dispatchedAt = null)
    {
        if (string.IsNullOrWhiteSpace(workerName))
        {
            throw new ArgumentException("Value cannot be empty.", nameof(workerName));
        }

        if (string.IsNullOrWhiteSpace(commandTemplate))
        {
            throw new ArgumentException("Value cannot be empty.", nameof(commandTemplate));
        }

        var stamp = (dispatchedAt ?? DateTimeOffset.UtcNow).UtcDateTime.ToString("yyyyMMddHHmmssfffffff");
        var nonce = Guid.NewGuid().ToString("N")[..8];
        var promptPath = Path.Combine(promptRoot, $"{brief.GoalId.Value[..8]}-{brief.TaskId.Value[..8]}-{stamp}-{nonce}-{Sanitize(workerName)}.md");
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

        ThrowIfUnresolvedTemplateVariables(workerName, command);
        Directory.CreateDirectory(promptRoot);
        File.WriteAllText(promptPath, brief.Content);

        return new WorkerDispatchPreparation(promptPath, command, brief.Content.Length);
    }

    private static void ThrowIfUnresolvedTemplateVariables(string workerName, string command)
    {
        var variables = TemplateVariableRegex()
            .Matches(command)
            .Select(match => match.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (variables.Count == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Worker command template for '{workerName}' left unresolved template variable(s): {string.Join(", ", variables)}. " +
            "Supported generic variables are {promptPath}, {goalId}, {taskId}, {role}, {title}, {workingDirectory}, {sandboxMode}, and {permissionMode}; " +
            "subscription variables such as {subscriptionModelName} and {subscriptionReasoningEffort} are supplied only by subscription-dispatch or by profile-dispatch when the selected profile matches the assigned subscription agent.");
    }

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(ch => invalid.Contains(ch) ? '-' : ch).ToArray());
    }

    private static string Quote(string value) => "'" + value.Replace("'", "''") + "'";

    [GeneratedRegex(@"\{[A-Za-z_][A-Za-z0-9_]*\}")]
    private static partial Regex TemplateVariableRegex();
}
