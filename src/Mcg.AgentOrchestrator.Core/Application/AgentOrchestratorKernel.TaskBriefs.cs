namespace Mcg.AgentOrchestrator.Core;

public sealed partial class AgentOrchestratorKernel
{
    public HumanInputRequest GetHumanInputRequest(HumanInputRequestId requestId)
    {
        return _humanInputRequests.TryGetValue(requestId, out var request)
            ? request
            : throw new KeyNotFoundException($"Human input request '{requestId}' was not found.");
    }

    public TaskSpec GetTask(GoalId goalId, TaskId taskId) => GetGoal(goalId).FindTask(taskId);

    public int EstimatePriorTaskEvidenceCharacterCount(GoalId goalId, TaskId taskId)
    {
        return EstimatePriorTaskEvidenceCharacterCount(GetGoal(goalId), taskId);
    }

    public static int EstimatePriorTaskEvidenceCharacterCount(Goal goal, TaskId taskId)
    {
        var task = goal.FindTask(taskId);
        var complexity = TaskComplexityEstimator.Estimate(task.Description, goal.Objective, task.RequiredRole);
        var lines = PromptContextFormatter.BuildPriorTaskEvidenceLines(goal.Tasks, taskId, complexity);
        return lines.Count == 0
            ? 0
            : string.Join(Environment.NewLine, lines).Length;
    }

    public TaskBrief BuildTaskBrief(
        GoalId goalId,
        TaskId taskId,
        string? modelFitTarget = null,
        string? workingDirectory = null,
        string? contextDirectory = null)
    {
        var goal = GetGoal(goalId);
        var task = goal.FindTask(taskId);
        var pendingInput = GetPendingHumanInput(goalId)
            .Where(request => request.TaskId == taskId || request.TaskId is null)
            .ToList();
        var complexity = TaskComplexityEstimator.Estimate(task.Description, goal.Objective, task.RequiredRole);
        var timeline = PromptContextFormatter.SelectPromptTimelineEvents(
            goal.Timeline.Where(evt => (evt.TaskId == taskId || evt.TaskId is null) && !IsRedundantBriefTimelineEvent(task, evt)),
            maxEvents: TimelineEventBudget(complexity),
            complexity);

        var usesFileAccessContext = !string.IsNullOrWhiteSpace(workingDirectory) && !string.IsNullOrWhiteSpace(contextDirectory);
        var headerLines = new List<string>
        {
            "# Agent Task Brief",
            string.Empty,
            $"Goal: {PromptContextFormatter.TrimPrimaryContextBlock(goal.Objective, complexity)}",
            $"Goal id: {goal.Id.Value}",
            $"Goal status: {goal.Status}",
            "Decision context: embedded in this brief and .orchestrator-handoff.md in the working directory when present; do not attempt to reach dashboard APIs or orchestrator state.",
            $"Task: {PromptContextFormatter.TrimPrimaryContextBlock(task.Description, complexity)}",
            $"Task role: {task.RequiredRole}",
            $"Task status: {task.Status}",
            $"Task id: {task.Id.Value}",
        };

        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            headerLines.Add($"Working directory, use absolute paths: {workingDirectory}");
        }

        if (!string.IsNullOrWhiteSpace(contextDirectory))
        {
            headerLines.Add($"Context files: read {Path.Combine(contextDirectory, "digest.md")} first, then {Path.Combine(contextDirectory, "prior-task-summaries.md")} for prior summaries before prior-task-evidence.md; use manifest.md for role-specific artifact priorities and repo-local guidance references.");
        }

        var segments = new List<TaskBriefSegment>
        {
            TaskBriefSegment.Fixed(headerLines)
        };

        var instructionLines = new List<string>
        {
            string.Empty,
            "## Instructions"
        };
        instructionLines.AddRange(BuildTaskBriefInstructions(complexity, modelFitTarget));
        var responseBudgetGuidance = PromptContextFormatter.BuildResponseBudgetGuidance(complexity);
        if (!string.IsNullOrWhiteSpace(responseBudgetGuidance))
        {
            instructionLines.Add(responseBudgetGuidance);
        }

        instructionLines.Add(string.Empty);
        segments.Add(TaskBriefSegment.Fixed(instructionLines));

        var roleLines = new List<string>();
        roleLines.AddRange(SdlcRolePromptRequirements.Build(task.RequiredRole, complexity));
        roleLines.Add(string.Empty);
        segments.Add(TaskBriefSegment.Fixed(roleLines));

        if (!string.IsNullOrWhiteSpace(task.VerificationPlan))
        {
            segments.Add(new TaskBriefSegment(
                [
                    "## Verification Plan",
                    PromptContextFormatter.TrimVerificationPlanBlock(task.VerificationPlan, complexity),
                    string.Empty
                ],
                [
                    "## Verification Plan",
                    "Read current-task.md in the context directory for the full verification plan; inline plan collapsed to stay under the role file-access prompt budget.",
                    string.Empty
                ],
                CollapsePriority: 50));
        }

        if (pendingInput.Count > 0)
        {
            var pendingInputLines = new List<string> { "## Pending Human Input" };
            foreach (var request in pendingInput)
            {
                pendingInputLines.Add($"- {request.Id}: {PromptContextFormatter.TrimPromptBlock(request.Question)}");
            }
            pendingInputLines.Add(string.Empty);
            segments.Add(TaskBriefSegment.Fixed(pendingInputLines));
        }

        if (task.LastExecution is not null)
        {
            segments.Add(new TaskBriefSegment(
                [
                    "## Last Model Output",
                    PromptContextFormatter.TrimEvidenceBlock(task.LastExecution.Output, complexity),
                    string.Empty
                ],
                [
                    "## Last Model Output",
                    "Read current-task.md in the context directory for last model output; inline output collapsed to stay under the role file-access prompt budget.",
                    string.Empty
                ],
                CollapsePriority: 30));
        }

        if (task.LastDispatch is not null)
        {
            segments.Add(new TaskBriefSegment(
                [
                    "## Last Dispatch",
                    $"Worker: {task.LastDispatch.WorkerName}",
                    $"Command: {PromptContextFormatter.TrimPromptBlock(task.LastDispatch.Command)}",
                    $"Working directory: {task.LastDispatch.WorkingDirectory}",
                    string.Empty
                ],
                [
                    "## Last Dispatch",
                    "Read current-task.md in the context directory for last dispatch details; inline command collapsed to stay under the role file-access prompt budget.",
                    string.Empty
                ],
                CollapsePriority: 20));
        }

        if (task.LastVerification is not null)
        {
            segments.Add(new TaskBriefSegment(
                [
                    "## Last Verification",
                    $"Command: {task.LastVerification.Command}",
                    $"Exit code: {task.LastVerification.ExitCode}",
                    $"Verification history count: {task.VerificationHistory.Count}",
                    $"Stdout: {PromptContextFormatter.TrimEvidenceBlock(task.LastVerification.StandardOutput, complexity)}",
                    $"Stderr: {PromptContextFormatter.TrimEvidenceBlock(task.LastVerification.StandardError, complexity)}",
                    string.Empty
                ],
                [
                    "## Last Verification",
                    $"Command: {task.LastVerification.Command}",
                    $"Exit code: {task.LastVerification.ExitCode}",
                    $"Verification history count: {task.VerificationHistory.Count}",
                    "Read current-task.md in the context directory for stdout and stderr; inline verification output collapsed to stay under the role file-access prompt budget.",
                    string.Empty
                ],
                CollapsePriority: 40));
        }

        var priorEvidence = usesFileAccessContext
            ? PromptContextFormatter.BuildPriorTaskEvidencePointerLines(goal.Tasks, taskId)
            : PromptContextFormatter.BuildPriorTaskEvidenceLines(goal.Tasks, taskId, complexity);
        if (priorEvidence.Count > 0 && !string.IsNullOrWhiteSpace(workingDirectory))
        {
            var priorEvidenceLines = priorEvidence.ToList();
            priorEvidenceLines.Add(string.IsNullOrWhiteSpace(contextDirectory)
                ? "Full evidence available at .orchestrator-handoff.md relative to the working directory."
                : "Full evidence available in the context files; keep inline prior evidence as orientation only.");
            priorEvidenceLines.Add(string.Empty);
            segments.Add(new TaskBriefSegment(
                priorEvidenceLines,
                [
                    "## Prior Task Evidence",
                    "Read prior-task-summaries.md first for compact prior files, behavior, verification, risks, and model fit; open prior-task-evidence.md second only when fuller verification output is needed.",
                    string.Empty
                ],
                CollapsePriority: 10));
        }
        else if (priorEvidence.Count > 0)
        {
            segments.Add(TaskBriefSegment.Fixed(priorEvidence));
        }

        if (timeline.Count > 0)
        {
            var timelineLines = new List<string> { "## Recent Timeline" };
            foreach (var evt in timeline)
            {
                timelineLines.Add(PromptContextFormatter.FormatTimelineEvent(evt, includeTimestamp: true, complexity));
            }

            segments.Add(new TaskBriefSegment(
                timelineLines,
                [
                    "## Recent Timeline",
                    "Read digest.md and current-task.md in the context directory for current status; inline timeline collapsed to stay under the role file-access prompt budget."
                ],
                CollapsePriority: 0));
        }

        var lines = ApplyTaskBriefBudget(segments, task.RequiredRole, usesFileAccessContext);

        return new TaskBrief(
            goal.Id,
            task.Id,
            task.RequiredRole,
            $"{task.RequiredRole}: {PromptContextFormatter.TrimPromptTitle(task.Description)}",
            string.Join(Environment.NewLine, lines));
    }

    private static List<string> ApplyTaskBriefBudget(
        IReadOnlyList<TaskBriefSegment> segments,
        AgentRole role,
        bool usesFileAccessContext)
    {
        var budget = PromptContextFormatter.TaskBriefCharacterBudget(role, usesFileAccessContext);
        var rendered = RenderTaskBriefSegments(segments);
        if (!usesFileAccessContext || CountTaskBriefCharacters(rendered) <= budget)
        {
            return rendered;
        }

        var collapsedSegments = segments.ToList();
        foreach (var index in collapsedSegments
            .Select((segment, index) => new { segment, index })
            .Where(item => item.segment.CollapsedLines is not null)
            .OrderBy(item => item.segment.CollapsePriority)
            .Select(item => item.index))
        {
            var segment = collapsedSegments[index];
            collapsedSegments[index] = segment with
            {
                Lines = segment.CollapsedLines!,
                CollapsedLines = null
            };

            rendered = RenderTaskBriefSegments(collapsedSegments);
            if (CountTaskBriefCharacters(rendered) <= budget)
            {
                break;
            }
        }

        return rendered;
    }

    private static List<string> RenderTaskBriefSegments(IEnumerable<TaskBriefSegment> segments)
    {
        return segments.SelectMany(segment => segment.Lines).ToList();
    }

    private static int CountTaskBriefCharacters(IReadOnlyList<string> lines)
    {
        return string.Join(Environment.NewLine, lines).Length;
    }

    private static int TimelineEventBudget(TaskComplexity complexity)
    {
        return complexity == TaskComplexity.Complex ? 20 : 8;
    }

    private static IReadOnlyList<string> BuildTaskBriefInstructions(TaskComplexity complexity, string? modelFitTarget)
    {
        var modelFitInstruction = BuildModelFitInstruction(modelFitTarget);
        if (complexity == TaskComplexity.Simple)
        {
            return
            [
                "Complete this SDLC task. Report only changed files, verification evidence, blockers, or HUMAN_INPUT: <question>.",
                "Use repository-local verification when practical; do not claim completion without evidence.",
                "When surveying files, start with the dashboard source survey or /api/source-survey?max=8, or use rg excluding **/bin/**, **/obj/**, .scratch, and prototype state.",
                modelFitInstruction
            ];
        }

        return
        [
            "Complete this task as the assigned SDLC role. Report concrete changes, verification evidence, blockers, and any human input required.",
            "If you cannot proceed without operator input, write a line that starts with HUMAN_INPUT: followed by the exact question.",
            "Use repository-local commands for evidence when possible. Do not mark work complete without verification.",
            "Avoid generic status summaries. Tie conclusions to repository files, command output, or cited source material.",
            "When surveying files, exclude generated output such as **/bin/**, **/obj/**, .scratch, and prototype state unless the task explicitly concerns those artifacts.",
            "Prefer the dashboard source survey or /api/source-survey?max=8 as the starting repository map before broad recursive file reads.",
            modelFitInstruction
        ];
    }

    private static string BuildModelFitInstruction(string? modelFitTarget)
    {
        var target = string.IsNullOrWhiteSpace(modelFitTarget)
            ? "<provider>/<model or launcher>"
            : modelFitTarget.Trim();
        return $"Include a final model-selection note: {ModelFitEvidence.BuildNoteTemplate(target)}.";
    }

    private static bool IsRedundantBriefTimelineEvent(TaskSpec task, ProgressEvent evt)
    {
        if (evt.TaskId != task.Id)
        {
            return false;
        }

        return evt.Kind switch
        {
            ProgressKind.TaskOutputRecorded => task.LastExecution?.CompletedAt == evt.OccurredAt,
            ProgressKind.TaskDispatchRecorded => task.LastDispatch?.DispatchedAt == evt.OccurredAt,
            ProgressKind.TaskVerificationRecorded => task.LastVerification?.CompletedAt == evt.OccurredAt,
            _ => false
        };
    }

    private sealed record TaskBriefSegment(
        IReadOnlyList<string> Lines,
        IReadOnlyList<string>? CollapsedLines = null,
        int CollapsePriority = int.MaxValue)
    {
        public static TaskBriefSegment Fixed(IReadOnlyList<string> lines) => new(lines);
    }
}
