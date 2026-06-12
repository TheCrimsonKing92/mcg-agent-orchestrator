using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public static class WorkerContextArtifacts
{
    private const int PriorVerificationMaxChars = 40000;
    private const int GuidanceFileMaxChars = 30000;

    public static string Write(
        Goal goal,
        TaskSpec task,
        string workingDirectory)
    {
        var contextDirectory = Path.Combine(workingDirectory, ".orchestrator-context", goal.Id.Value);
        Directory.CreateDirectory(contextDirectory);

        WriteText(Path.Combine(contextDirectory, "objective.md"), BuildObjective(goal));
        WriteText(Path.Combine(contextDirectory, "current-task.md"), BuildCurrentTask(task, workingDirectory));
        WriteText(Path.Combine(contextDirectory, "prior-task-evidence.md"), BuildPriorTaskEvidence(goal.Tasks, task.Id));

        var guidanceFiles = CopyGuidanceFiles(workingDirectory, contextDirectory);
        WriteText(Path.Combine(contextDirectory, "manifest.md"), BuildManifest(goal, task, workingDirectory, guidanceFiles));

        return contextDirectory;
    }

    private static string BuildObjective(Goal goal)
    {
        var lines = new List<string>
        {
            "# Goal Objective",
            string.Empty,
            goal.Objective,
            string.Empty,
            $"Goal id: {goal.Id.Value}",
            $"Goal status: {goal.Status}"
        };
        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildCurrentTask(TaskSpec task, string workingDirectory)
    {
        var lines = new List<string>
        {
            "# Current Task",
            string.Empty,
            $"Task id: {task.Id.Value}",
            $"Role: {task.RequiredRole}",
            $"Status: {task.Status}",
            $"Working directory: {workingDirectory}",
            string.Empty,
            "## Description",
            task.Description
        };

        if (!string.IsNullOrWhiteSpace(task.VerificationPlan))
        {
            lines.Add(string.Empty);
            lines.Add("## Verification Plan");
            lines.Add(task.VerificationPlan);
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildPriorTaskEvidence(IReadOnlyList<TaskSpec> goalTasks, TaskId taskId)
    {
        var priorCompletedTasks = goalTasks
            .TakeWhile(t => t.Id != taskId)
            .Where(t => t.Status == WorkTaskStatus.Completed && t.LastVerification is not null)
            .ToList();

        if (priorCompletedTasks.Count == 0)
        {
            return "# Prior Task Evidence" + Environment.NewLine + Environment.NewLine + "No prior completed task evidence.";
        }

        var lines = new List<string> { "# Prior Task Evidence" };
        foreach (var priorTask in priorCompletedTasks)
        {
            var verification = priorTask.LastVerification!;
            lines.Add(string.Empty);
            lines.Add($"## {priorTask.RequiredRole}: {priorTask.Description}");
            lines.Add($"Task id: {priorTask.Id.Value}");
            lines.Add($"Verification command: {verification.Command}");
            lines.Add($"Verification working directory: {verification.WorkingDirectory}");
            lines.Add($"Verification exit code: {verification.ExitCode}");
            if (!string.IsNullOrWhiteSpace(verification.ModelFitNote))
            {
                lines.Add($"Model fit: {verification.ModelFitNote}");
            }

            lines.Add(string.Empty);
            lines.Add("### Stdout");
            lines.Add(TrimArtifactBlock(verification.StandardOutput, PriorVerificationMaxChars));
            if (!string.IsNullOrWhiteSpace(verification.StandardError))
            {
                lines.Add(string.Empty);
                lines.Add("### Stderr");
                lines.Add(TrimArtifactBlock(verification.StandardError, PriorVerificationMaxChars));
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static List<string> CopyGuidanceFiles(string workingDirectory, string contextDirectory)
    {
        var copied = new List<string>();
        foreach (var fileName in new[] { "AGENTS.md", "BACKLOG.md", "DOGFOOD_LOG.md" })
        {
            var sourcePath = Path.Combine(workingDirectory, fileName);
            if (!File.Exists(sourcePath))
            {
                continue;
            }

            var targetPath = Path.Combine(contextDirectory, fileName);
            WriteText(targetPath, TrimArtifactBlock(File.ReadAllText(sourcePath), GuidanceFileMaxChars));
            copied.Add(fileName);
        }

        return copied;
    }

    private static string BuildManifest(
        Goal goal,
        TaskSpec task,
        string workingDirectory,
        IReadOnlyList<string> guidanceFiles)
    {
        var lines = new List<string>
        {
            "# Worker Context Manifest",
            string.Empty,
            $"Goal id: {goal.Id.Value}",
            $"Task id: {task.Id.Value}",
            $"Role: {task.RequiredRole}",
            $"Working directory: {workingDirectory}",
            string.Empty,
            "## Artifacts",
            "- objective.md: full goal objective and goal status.",
            "- current-task.md: current task description, role, working directory, and verification plan.",
            "- prior-task-evidence.md: prior completed task verification evidence with a larger file budget than inline prompts."
        };

        foreach (var guidanceFile in guidanceFiles)
        {
            lines.Add($"- {guidanceFile}: repository-local guidance copied from the working directory.");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string TrimArtifactBlock(string value, int maxChars)
    {
        var trimmed = value.Trim();
        if (trimmed.Length <= maxChars)
        {
            return trimmed;
        }

        return trimmed[..maxChars] +
            Environment.NewLine +
            $"...[truncated {trimmed.Length - maxChars} chars for context artifact budget]...";
    }

    private static void WriteText(string path, string content)
    {
        File.WriteAllText(path, content);
    }
}
