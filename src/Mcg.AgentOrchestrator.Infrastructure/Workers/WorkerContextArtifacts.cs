using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public static class WorkerContextArtifacts
{
    private const int PriorVerificationMaxChars = 40000;
    private const int GuidanceFileMaxChars = 30000;
    private const int DigestTextMaxChars = 700;
    private const int DigestEvidenceMaxChars = 500;
    private const int SummaryFieldMaxChars = 350;

    public static string Write(
        Goal goal,
        TaskSpec task,
        string workingDirectory)
    {
        var contextDirectory = Path.Combine(workingDirectory, ".orchestrator-context", goal.Id.Value);
        Directory.CreateDirectory(contextDirectory);

        WriteText(Path.Combine(contextDirectory, "objective.md"), BuildObjective(goal));
        WriteText(Path.Combine(contextDirectory, "current-task.md"), BuildCurrentTask(task, workingDirectory));
        WriteText(Path.Combine(contextDirectory, "prior-task-summaries.md"), BuildPriorTaskSummaries(goal.Tasks, task.Id));
        WriteText(Path.Combine(contextDirectory, "prior-task-evidence.md"), BuildPriorTaskEvidence(goal.Tasks, task.Id));
        WriteText(Path.Combine(contextDirectory, "digest.md"), BuildDigest(goal, task, workingDirectory));

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

    private static string BuildDigest(Goal goal, TaskSpec task, string workingDirectory)
    {
        var priorTasks = goal.Tasks
            .TakeWhile(t => t.Id != task.Id)
            .Where(t => t.Status == WorkTaskStatus.Completed)
            .ToList();
        var openRisks = BuildOpenRiskLines(goal, task);
        var lines = new List<string>
        {
            "# Worker Context Digest",
            string.Empty,
            $"Goal id: {goal.Id.Value}",
            $"Current task id: {task.Id.Value}",
            $"Role: {task.RequiredRole}",
            $"Working directory: {workingDirectory}",
            string.Empty,
            "## Objective",
            TrimDigestText(goal.Objective),
            string.Empty,
            "## Current Task",
            TrimDigestText(task.Description),
            string.Empty,
            "## Role Focus",
            BuildRoleFocus(task.RequiredRole)
        };

        lines.Add(string.Empty);
        lines.Add("## Role Artifact Priorities");
        lines.AddRange(BuildRoleArtifactPriorities(task.RequiredRole));

        if (!string.IsNullOrWhiteSpace(task.VerificationPlan))
        {
            lines.Add(string.Empty);
            lines.Add("## Verification Plan");
            lines.Add(TrimDigestText(task.VerificationPlan));
        }

        lines.Add(string.Empty);
        lines.Add("## Prior Completed Outcomes");
        if (priorTasks.Count == 0)
        {
            lines.Add("- None.");
        }
        else
        {
            foreach (var priorTask in priorTasks.TakeLast(5))
            {
                lines.Add($"- {priorTask.RequiredRole}: {TrimDigestTitle(priorTask.Description)}");
                if (priorTask.LastVerification is null)
                {
                    lines.Add("  Verification: no verification record.");
                    continue;
                }

                lines.Add($"  Verification: exit {priorTask.LastVerification.ExitCode} from `{priorTask.LastVerification.Command}`.");
                if (!string.IsNullOrWhiteSpace(priorTask.LastVerification.ModelFitNote))
                {
                    lines.Add($"  Model fit: {priorTask.LastVerification.ModelFitNote}");
                }

                var evidence = TrimDigestEvidence(priorTask.LastVerification.StandardOutput);
                if (!string.IsNullOrWhiteSpace(evidence))
                {
                    lines.Add($"  Outcome: {evidence}");
                }

                AddEvidencePointers(lines, priorTask.LastVerification);
            }
        }

        lines.Add(string.Empty);
        lines.Add("## Evidence Pointers");
        lines.Add("- current-task.md: current task brief and verification plan.");
        lines.Add("- objective.md: full goal objective.");
        lines.Add("- prior-task-summaries.md: compact prior task summaries; read before full evidence.");
        lines.Add("- prior-task-evidence.md: fuller prior verification output; read after summaries when needed.");
        if (File.Exists(Path.Combine(workingDirectory, ".orchestrator-handoff.md")))
        {
            lines.Add("- .orchestrator-handoff.md: prior task handoff in the working directory.");
        }

        lines.Add(string.Empty);
        lines.Add("## Open Risks And Blockers");
        if (openRisks.Count == 0)
        {
            lines.Add("- None recorded.");
        }
        else
        {
            lines.AddRange(openRisks);
        }

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

    private static string BuildPriorTaskSummaries(IReadOnlyList<TaskSpec> goalTasks, TaskId taskId)
    {
        var priorCompletedTasks = goalTasks
            .TakeWhile(t => t.Id != taskId)
            .Where(t => t.Status == WorkTaskStatus.Completed && t.LastVerification is not null)
            .ToList();

        if (priorCompletedTasks.Count == 0)
        {
            return "# Prior Task Summaries" + Environment.NewLine + Environment.NewLine + "No prior completed task summaries.";
        }

        var lines = new List<string> { "# Prior Task Summaries" };
        foreach (var priorTask in priorCompletedTasks)
        {
            var verification = priorTask.LastVerification!;
            lines.Add(string.Empty);
            lines.Add($"## {priorTask.RequiredRole}: {TrimDigestTitle(priorTask.Description)}");
            lines.Add($"Task id: {priorTask.Id.Value}");
            lines.Add($"Changed files: {SummarizeEvidenceField(verification, "changed files", "changed file", "files changed")}");
            lines.Add($"Behavior changes: {SummarizeEvidenceField(verification, "behavior changes", "behavior change", "behavior", "changes")}");
            lines.Add($"Verification: `{verification.Command}` in `{verification.WorkingDirectory}` exited {verification.ExitCode}.");
            lines.Add($"Verification result: {SummarizeVerificationResult(verification)}");
            lines.Add($"Risks: {SummarizeRisks(verification)}");
            lines.Add($"Model fit: {SummarizeModelFit(verification)}");
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
            "- digest.md: compact role-aware summary of objective, current task, prior outcomes, verification status, evidence pointers, and open risks/blockers.",
            "- objective.md: full goal objective and goal status.",
            "- current-task.md: current task description, role, working directory, and verification plan.",
            "- prior-task-summaries.md: compact prior task summaries with changed files, behavior changes, verification commands/results, risks, and model fit.",
            "- prior-task-evidence.md: prior completed task verification evidence with a larger file budget than inline prompts."
        };

        foreach (var guidanceFile in guidanceFiles)
        {
            lines.Add($"- {guidanceFile}: repository-local guidance copied from the working directory.");
        }

        lines.Add(string.Empty);
        lines.Add("## Role Artifact Priorities");
        lines.AddRange(BuildRoleArtifactPriorities(task.RequiredRole));

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

    private static string TrimDigestText(string value)
    {
        return TrimArtifactBlock(value, DigestTextMaxChars).ReplaceLineEndings(" ");
    }

    private static string TrimDigestEvidence(string value)
    {
        return TrimArtifactBlock(value, DigestEvidenceMaxChars).ReplaceLineEndings(" ");
    }

    private static string TrimDigestTitle(string value)
    {
        var trimmed = value.Trim().ReplaceLineEndings(" ");
        while (trimmed.Contains("  ", StringComparison.Ordinal))
        {
            trimmed = trimmed.Replace("  ", " ", StringComparison.Ordinal);
        }

        return trimmed.Length <= 160 ? trimmed : trimmed[..157] + "...";
    }

    private static string BuildRoleFocus(AgentRole role)
    {
        return role switch
        {
            AgentRole.Planner => "Clarify approach, sequencing, risks, and handoff decisions; do not modify repository files.",
            AgentRole.Researcher => "Inspect source and report evidence-backed findings; do not modify repository files.",
            AgentRole.Developer => "Implement scoped source changes and verify them with repository-local commands.",
            AgentRole.Tester => "Strengthen or run focused verification and report exact failures or coverage gaps.",
            AgentRole.Reviewer => "Prioritize bugs, regressions, risk, and missing tests before summaries.",
            _ => "Complete the assigned task and report evidence."
        };
    }

    private static IReadOnlyList<string> BuildRoleArtifactPriorities(AgentRole role)
    {
        return role switch
        {
            AgentRole.Planner =>
            [
                "- 1. objective.md: preserve the goal, scope, and acceptance path.",
                "- 2. digest.md: identify current role focus, blockers, and prior outcomes.",
                "- 3. prior-task-summaries.md: use prior decisions before opening full evidence."
            ],
            AgentRole.Researcher =>
            [
                "- 1. digest.md: start with role focus, prior outcomes, and open risks.",
                "- 2. prior-task-summaries.md: inspect compact prior findings before full logs.",
                "- 3. AGENTS.md and repo guidance files: apply local survey and evidence rules."
            ],
            AgentRole.Developer =>
            [
                "- 1. current-task.md: anchor implementation scope and verification plan.",
                "- 2. prior-task-summaries.md: read compact prior behavior, file, risk, and model-fit evidence first.",
                "- 3. prior-task-evidence.md: open only when summaries do not explain a prior result."
            ],
            AgentRole.Tester =>
            [
                "- 1. prior-task-summaries.md: identify changed files, behavior claims, risks, and verification gaps.",
                "- 2. current-task.md: map the required checks to the task verification plan.",
                "- 3. prior-task-evidence.md: confirm exact command output when a summary is insufficient."
            ],
            AgentRole.Reviewer =>
            [
                "- 1. prior-task-summaries.md: review changed files, behavior changes, verification, risks, and model fit before full logs.",
                "- 2. digest.md: check open blockers and role focus before findings.",
                "- 3. prior-task-evidence.md: inspect full evidence only for disputed or high-risk findings."
            ],
            _ =>
            [
                "- 1. digest.md: start with compact context.",
                "- 2. current-task.md: confirm the assigned work.",
                "- 3. prior-task-summaries.md: inspect prior completed work before full evidence."
            ]
        };
    }

    private static List<string> BuildOpenRiskLines(Goal goal, TaskSpec task)
    {
        var risks = new List<string>();
        foreach (var priorTask in goal.Tasks.TakeWhile(t => t.Id != task.Id))
        {
            if (priorTask.Status is WorkTaskStatus.Failed or WorkTaskStatus.Cancelled)
            {
                risks.Add($"- Prior {priorTask.RequiredRole} task {priorTask.Id.Value} is {priorTask.Status}.");
            }
            else if (priorTask.LastVerification is { Succeeded: false } verification)
            {
                risks.Add($"- Prior {priorTask.RequiredRole} verification failed with exit {verification.ExitCode}: {TrimDigestTitle(priorTask.Description)}");
            }
        }

        if (task.LastVerification is { Succeeded: false } lastVerification)
        {
            risks.Add($"- Current task last verification failed with exit {lastVerification.ExitCode}: `{lastVerification.Command}`.");
        }

        if (task.Status == WorkTaskStatus.WaitingForHuman)
        {
            risks.Add("- Current task is waiting for human input.");
        }

        return risks;
    }

    private static string SummarizeEvidenceField(TaskVerificationRecord verification, params string[] labels)
    {
        foreach (var line in SplitEvidenceLines(verification))
        {
            foreach (var label in labels)
            {
                if (!TryExtractLabelValue(line, label, out var value))
                {
                    continue;
                }

                return TrimSummaryField(value);
            }
        }

        return "Not reported.";
    }

    private static string SummarizeVerificationResult(TaskVerificationRecord verification)
    {
        var labeledResult = SummarizeEvidenceField(verification, "verification result", "result");
        if (labeledResult != "Not reported.")
        {
            return labeledResult;
        }

        var evidence = SplitEvidenceLines(verification)
            .FirstOrDefault(line => line.Contains("passed", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("warning", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(evidence))
        {
            return TrimSummaryField(evidence);
        }

        return verification.Succeeded
            ? "Verification command succeeded."
            : "Verification command failed; inspect prior-task-evidence.md for output.";
    }

    private static string SummarizeRisks(TaskVerificationRecord verification)
    {
        var labelValue = SummarizeEvidenceField(verification, "risks", "risk", "blockers", "blocker");
        if (labelValue != "Not reported.")
        {
            return labelValue;
        }

        if (!verification.Succeeded)
        {
            return "Verification failed.";
        }

        if (!string.IsNullOrWhiteSpace(verification.StandardError))
        {
            return TrimSummaryField(verification.StandardError.ReplaceLineEndings(" "));
        }

        return "None reported.";
    }

    private static string SummarizeModelFit(TaskVerificationRecord verification)
    {
        return verification.ModelFitNote ??
            ModelFitEvidence.TryExtractNote(verification.StandardOutput, verification.StandardError) ??
            "Not reported.";
    }

    private static bool TryExtractLabelValue(string line, string label, out string value)
    {
        var normalizedLine = line.Trim().TrimStart('-', '*', ' ');
        var prefix = label + ":";
        if (normalizedLine.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            value = normalizedLine[prefix.Length..].Trim();
            return !string.IsNullOrWhiteSpace(value);
        }

        value = string.Empty;
        return false;
    }

    private static string TrimSummaryField(string value)
    {
        var trimmed = value.Trim().ReplaceLineEndings(" ");
        while (trimmed.Contains("  ", StringComparison.Ordinal))
        {
            trimmed = trimmed.Replace("  ", " ", StringComparison.Ordinal);
        }

        return trimmed.Length <= SummaryFieldMaxChars ? trimmed : trimmed[..(SummaryFieldMaxChars - 3)] + "...";
    }

    private static IEnumerable<string> SplitEvidenceLines(TaskVerificationRecord verification)
    {
        return verification.StandardOutput
            .ReplaceLineEndings("\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Concat(verification.StandardError
                .ReplaceLineEndings("\n")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static void AddEvidencePointers(List<string> lines, TaskVerificationRecord verification)
    {
        if (!string.IsNullOrWhiteSpace(verification.StandardOutputPath))
        {
            lines.Add($"  Stdout path: {verification.StandardOutputPath}");
        }

        if (!string.IsNullOrWhiteSpace(verification.StandardErrorPath))
        {
            lines.Add($"  Stderr path: {verification.StandardErrorPath}");
        }
    }

    private static void WriteText(string path, string content)
    {
        File.WriteAllText(path, content);
    }
}
