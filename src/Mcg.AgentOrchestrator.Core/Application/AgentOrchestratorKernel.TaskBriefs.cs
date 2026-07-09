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
        string? contextDirectory = null,
        string? targetBranchName = null,
        string? targetHeadCommit = null)
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
            string.Empty
        };
        headerLines.AddRange(BuildLatestDeveloperRetryBlock(goal, task, targetBranchName, targetHeadCommit));
        headerLines.AddRange(BuildAcceptanceFailureBriefBlock(goal, task));
        headerLines.AddRange([
            $"Goal: {PromptContextFormatter.TrimPrimaryContextBlock(goal.Objective, complexity)}",
            $"Goal id: {goal.Id.Value}",
            $"Goal status: {goal.Status}",
            "Decision context: embedded in this brief and .orchestrator-handoff.md in the working directory when present; do not attempt to reach dashboard APIs or orchestrator state.",
            $"Task: {PromptContextFormatter.TrimPrimaryContextBlock(task.Description, complexity)}",
            $"Task role: {task.RequiredRole}",
            $"Task status: {task.Status}",
            $"Task id: {task.Id.Value}",
        ]);

        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            headerLines.Add($"Working directory, use absolute paths: {workingDirectory}");
        }

        if (!string.IsNullOrWhiteSpace(contextDirectory))
        {
            headerLines.Add($"Context files: read {Path.Combine(contextDirectory, "digest.md")} first; use artifact-registry.json for hashes/freshness and manifest.md for role-specific artifact priorities before opening larger evidence.");
        }

        if (!string.IsNullOrWhiteSpace(targetBranchName) || !string.IsNullOrWhiteSpace(targetHeadCommit))
        {
            headerLines.Add("Current target context:");
            if (!string.IsNullOrWhiteSpace(targetBranchName))
            {
                headerLines.Add($"- Branch: {targetBranchName.Trim()}");
            }

            if (!string.IsNullOrWhiteSpace(targetHeadCommit))
            {
                headerLines.Add($"- HEAD commit: {targetHeadCommit.Trim()}");
            }
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
        instructionLines.AddRange(BuildTaskBriefInstructions(task.RequiredRole, complexity, modelFitTarget));
        var responseBudgetGuidance = PromptContextFormatter.BuildResponseBudgetGuidance(complexity);
        if (!string.IsNullOrWhiteSpace(responseBudgetGuidance))
        {
            instructionLines.Add(responseBudgetGuidance);
        }

        instructionLines.Add(string.Empty);
        segments.Add(TaskBriefSegment.Fixed(instructionLines));

        if (goal.RefinedSpec is { } refinedSpec)
        {
            var specLines = new List<string>
            {
                "## Refined Spec",
                $"Behavioral contract: {refinedSpec.BehavioralContract}",
                string.Empty,
                "Acceptance criteria:"
            };
            foreach (var criterion in refinedSpec.AcceptanceCriteria)
                specLines.Add($"- {criterion}");
            if (refinedSpec.Decisions.Count > 0)
            {
                specLines.Add(string.Empty);
                specLines.Add("Decisions:");
                foreach (var decision in refinedSpec.Decisions)
                    specLines.Add($"- {decision.Question} → {decision.Choice} ({decision.Rationale})");
            }
            specLines.Add(string.Empty);
            segments.Add(TaskBriefSegment.Fixed(specLines));
        }

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

        if (task.CriterionRetryFeedback.Count > 0)
        {
            var feedbackLines = new List<string>
            {
                "## Unmet acceptance criteria from the prior attempt - fix these:"
            };
            feedbackLines.AddRange(task.CriterionRetryFeedback.Select(item => $"- {PromptContextFormatter.TrimPromptBlock(item)}"));
            feedbackLines.Add(string.Empty);
            segments.Add(TaskBriefSegment.Fixed(feedbackLines));
        }

        var recentRetryFeedback = BuildRecentRetryFeedbackBriefBlock(goal, task);
        if (recentRetryFeedback.Count > 0)
        {
            segments.Add(TaskBriefSegment.Fixed(recentRetryFeedback));
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
        var budget = TaskBriefCharacterBudget(role, usesFileAccessContext);
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

    public static int TaskBriefCharacterBudget(AgentRole role, bool usesFileAccessContext) =>
        PromptContextFormatter.TaskBriefCharacterBudget(role, usesFileAccessContext);

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

    private static IReadOnlyList<string> BuildTaskBriefInstructions(AgentRole role, TaskComplexity complexity, string? modelFitTarget)
    {
        var modelFitInstruction = BuildModelFitInstruction(modelFitTarget);
        if (complexity == TaskComplexity.Simple)
        {
            var simpleLines = new List<string>
            {
                "Complete this SDLC task. Report only changed files, verification evidence, blockers, or HUMAN_INPUT: <question>.",
                "Use repository-local verification when practical; do not claim completion without evidence.",
                "When surveying files, start with the dashboard source survey or /api/source-survey?max=8, or use rg excluding **/bin/**, **/obj/**, .scratch, and prototype state.",
                "Do not stage or commit changes; the orchestrator commits verified Developer/Tester diffs."
            };
            simpleLines.AddRange(AgentOutputDirectives.WorkerResultTemplateLinesForRole(role));
            simpleLines.Add(modelFitInstruction);
            return simpleLines;
        }

        var complexLines = new List<string>
        {
            "Complete this task as the assigned SDLC role. Report concrete changes, verification evidence, blockers, and any human input required.",
            "If you cannot proceed without operator input, write a line that starts with HUMAN_INPUT: followed by the exact question.",
            "Use repository-local commands for evidence when possible. Do not mark work complete without verification.",
            "Avoid generic status summaries. Tie conclusions to repository files, command output, or cited source material.",
            "When surveying files, exclude generated output such as **/bin/**, **/obj/**, .scratch, and prototype state unless the task explicitly concerns those artifacts.",
            "Prefer the dashboard source survey or /api/source-survey?max=8 as the starting repository map before broad recursive file reads.",
            "Do not stage or commit changes; the orchestrator commits verified Developer/Tester diffs."
        };
        complexLines.AddRange(AgentOutputDirectives.WorkerResultTemplateLinesForRole(role));
        complexLines.Add(modelFitInstruction);
        return complexLines;
    }

    private static IReadOnlyList<string> BuildAcceptanceFailureBriefBlock(Goal goal, TaskSpec task)
    {
        if (goal.LatestAcceptanceFailure is not { } failure)
        {
            return [];
        }

        var retryEvent = goal.Timeline
            .Where(evt =>
                evt.TaskId == task.Id &&
                evt.Kind == ProgressKind.TaskRetried &&
                evt.OccurredAt >= failure.OccurredAt)
            .OrderByDescending(evt => evt.OccurredAt)
            .FirstOrDefault();
        if (retryEvent is null)
        {
            return [];
        }

        var lines = new List<string>
        {
            "<!-- ACCEPTANCE_FAILURE_START -->",
            "## ACCEPTANCE FAILURE - FIX FIRST",
            "This retry follows a failed acceptance round. Address this before using prior task history or context digests.",
            string.Empty,
            "Operator feedback (verbatim):",
            retryEvent.Message,
            string.Empty,
            "Failing tests/checks:",
        };
        lines.AddRange(failure.FailedChecks.Select(check => $"- {check}"));
        lines.Add("<!-- ACCEPTANCE_FAILURE_END -->");
        lines.Add(string.Empty);
        return lines;
    }

    private static List<string> BuildLatestDeveloperRetryBlock(
        Goal goal,
        TaskSpec task,
        string? targetBranchName,
        string? targetHeadCommit)
    {
        if (task.RequiredRole != AgentRole.Developer)
        {
            return [];
        }

        var latestRetry = goal.Timeline
            .Where(evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskRetried)
            .OrderByDescending(evt => evt.OccurredAt)
            .FirstOrDefault();
        if (latestRetry is null || string.IsNullOrWhiteSpace(latestRetry.Message))
        {
            return [];
        }

        var lines = new List<string>
        {
            "<!-- LATEST_DEVELOPER_RETRY_BLOCKER_START -->",
            "## LATEST DEVELOPER RETRY BLOCKER - FIX FIRST",
            "This is the latest retry feedback for this Developer task. Address it before using prior task history, branch evidence, or context digests.",
            $"Source: {latestRetry.OccurredAt:u}; {DescribeTimelineTask(goal, latestRetry)}; {latestRetry.Kind}.",
        };

        if (!string.IsNullOrWhiteSpace(targetBranchName) || !string.IsNullOrWhiteSpace(targetHeadCommit))
        {
            lines.Add("Current branch/head for this retry:");
            if (!string.IsNullOrWhiteSpace(targetBranchName))
            {
                lines.Add($"- Branch: {targetBranchName.Trim()}");
            }

            if (!string.IsNullOrWhiteSpace(targetHeadCommit))
            {
                lines.Add($"- HEAD commit: {targetHeadCommit.Trim()}");
            }
        }

        lines.Add(string.Empty);
        lines.Add("Retry feedback (verbatim):");
        lines.Add(latestRetry.Message);
        lines.Add("<!-- LATEST_DEVELOPER_RETRY_BLOCKER_END -->");
        lines.Add(string.Empty);
        return lines;
    }

    private static string BuildModelFitInstruction(string? modelFitTarget)
    {
        var target = string.IsNullOrWhiteSpace(modelFitTarget)
            ? "<provider>/<model or launcher>"
            : modelFitTarget.Trim();
        return $"Include a final model-selection note: {ModelFitEvidence.BuildNoteTemplate(target)}.";
    }

    private static IReadOnlyList<string> BuildRecentRetryFeedbackBriefBlock(Goal goal, TaskSpec task)
    {
        if (task.RequiredRole is not (AgentRole.Tester or AgentRole.Reviewer))
        {
            return [];
        }

        var retryEvents = goal.Timeline
            .Where(evt => evt.Kind == ProgressKind.TaskRetried)
            .OrderBy(evt => evt.OccurredAt)
            .ToList();
        if (retryEvents.Count == 0)
        {
            return [];
        }

        var latestRetry = retryEvents[^1];
        var latestRetryOrdinal = retryEvents.Count;
        var priorOutcomeEvent = goal.Timeline
            .Where(evt =>
                evt.TaskId == latestRetry.TaskId &&
                evt.OccurredAt <= latestRetry.OccurredAt &&
                IsRetryPriorOutcomeEvent(evt))
            .OrderByDescending(evt => evt.OccurredAt)
            .FirstOrDefault();
        var feedbackEvents = goal.Timeline
            .Where(evt =>
                evt.OccurredAt >= latestRetry.OccurredAt &&
                evt.Kind is ProgressKind.TaskRetried or ProgressKind.TaskNote or ProgressKind.TaskSubscriptionLimitReviewAcknowledged)
            .OrderByDescending(evt => evt.OccurredAt)
            .ThenByDescending(evt => (int)evt.Kind)
            .ToList();

        var lines = new List<string>
        {
            "## Recent retry/recovery feedback",
            $"Most recent retry: Retry {latestRetryOrdinal} of {retryEvents.Count}; {latestRetry.OccurredAt:u}; {DescribeTimelineTask(goal, latestRetry)}.",
        };
        if (priorOutcomeEvent is not null)
        {
            lines.Add($"Prior outcome: {priorOutcomeEvent.OccurredAt:u}; {DescribeTimelineTask(goal, priorOutcomeEvent)}; {priorOutcomeEvent.Kind}: {PromptContextFormatter.TrimPromptBlock(priorOutcomeEvent.Message)}");
        }

        lines.Add("Use this as the current correction context; older duplicate retry/recovery notes are omitted.");

        var emittedMessages = new HashSet<string>(StringComparer.Ordinal);
        var emittedCount = 0;
        var omittedCount = 0;
        var sectionChars = string.Join(Environment.NewLine, lines).Length;
        foreach (var evt in feedbackEvents)
        {
            var message = evt.Message.Trim();
            if (message.Length == 0 || !emittedMessages.Add(message))
            {
                omittedCount++;
                continue;
            }

            var retryOrdinal = RetryOrdinalAt(retryEvents, evt.OccurredAt);
            var line = $"- Retry {retryOrdinal} of {retryEvents.Count}; {evt.OccurredAt:u}; {DescribeTimelineTask(goal, evt)}; {evt.Kind}: {PromptContextFormatter.TrimPromptBlock(message)}";
            if (emittedCount >= 3 || sectionChars + line.Length + Environment.NewLine.Length > 2500)
            {
                omittedCount++;
                continue;
            }

            lines.Add(line);
            sectionChars += line.Length + Environment.NewLine.Length;
            emittedCount++;
        }

        if (omittedCount > 0)
        {
            lines.Add($"- Omitted {omittedCount} older, duplicate, or over-budget retry/recovery note(s).");
        }

        lines.Add(string.Empty);
        return lines;
    }

    private static bool IsRetryPriorOutcomeEvent(ProgressEvent evt)
    {
        return evt.Kind is
            ProgressKind.TaskFailed or
            ProgressKind.TaskCancelled or
            ProgressKind.TaskVerificationRecorded;
    }

    private static int RetryOrdinalAt(IReadOnlyList<ProgressEvent> retryEvents, DateTimeOffset occurredAt)
    {
        var ordinal = retryEvents.Count(evt => evt.OccurredAt <= occurredAt);
        return Math.Max(1, ordinal);
    }

    private static string DescribeTimelineTask(Goal goal, ProgressEvent evt)
    {
        if (evt.TaskId is not { } taskId)
        {
            return "Goal-level event";
        }

        var task = goal.FindTask(taskId);
        return $"Task {TaskDisplayNumber.Resolve(goal, taskId)} {task.RequiredRole}";
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
