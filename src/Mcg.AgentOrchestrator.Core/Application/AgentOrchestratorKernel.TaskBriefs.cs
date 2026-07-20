using System.Text.Json;

namespace Mcg.AgentOrchestrator.Core;

public sealed partial class AgentOrchestratorKernel
{
    private const int FailureReceiptMaxChars = 2000;
    private const int FailureReceiptStreamTailChars = 700;
    private const int ReviewerExecutedTestEvidenceMaxLines = 12;
    private const int ReviewerChangedFileScopeMaxLines = 120;
    private const int AccumulatedRetryFeedbackMaxEntries = 8;
    private const int AccumulatedRetryFeedbackMaxChars = 3500;
    private static readonly JsonSerializerOptions GoalOperationJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

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
        string? targetHeadCommit = null,
        IReadOnlyList<string>? reviewerScopeChangedFiles = null,
        string? reviewerScopeMergeBase = null,
        int? reviewerScopeTotalChangedFileCount = null,
        bool? reviewerMergeTreeClean = null,
        IReadOnlyList<string>? reviewerMergeTreeConflictPaths = null,
        int? reviewerMergeTreeTotalConflictPathCount = null)
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
        headerLines.AddRange(BuildAccumulatedRetryFeedbackBriefBlock(goal, task, workingDirectory, targetBranchName, targetHeadCommit));
        headerLines.AddRange(BuildEffectiveAcceptanceCriteriaCorrectionsBriefBlock(goal, task));
        headerLines.AddRange(BuildAcceptanceFailureBriefBlock(goal, task, workingDirectory));
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
        instructionLines.AddRange(BuildTaskBriefInstructions(complexity, modelFitTarget, task.RequiredRole));
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
        roleLines.AddRange(SdlcRolePromptRequirements.Build(
            task.RequiredRole,
            complexity,
            SdlcRolePromptRequirements.HasHighRiskOrComplexIntakeRiskLabel(goal)));
        roleLines.Add(string.Empty);
        segments.Add(TaskBriefSegment.Fixed(roleLines));

        var practiceLines = EngineeringPracticePromptRenderer.RenderBriefSection(
            task.RequiredRole,
            MatchEngineeringPractices(goal, task, reviewerScopeChangedFiles));
        if (practiceLines.Count > 0)
        {
            segments.Add(TaskBriefSegment.Fixed(practiceLines));
        }

        var reviewerChangedFileScope = BuildReviewerChangedFileScopeBriefBlock(
            task,
            reviewerScopeChangedFiles,
            reviewerScopeMergeBase,
            reviewerScopeTotalChangedFileCount,
            reviewerMergeTreeClean,
            reviewerMergeTreeConflictPaths,
            reviewerMergeTreeTotalConflictPathCount);
        if (reviewerChangedFileScope.Count > 0)
        {
            segments.Add(TaskBriefSegment.Fixed(reviewerChangedFileScope));
        }

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

        var reviewerExecutedTestEvidence = BuildReviewerExecutedTestEvidenceBriefBlock(goal, task);
        if (reviewerExecutedTestEvidence.Count > 0)
        {
            segments.Add(TaskBriefSegment.Fixed(reviewerExecutedTestEvidence));
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

    private static IReadOnlyList<string> BuildTaskBriefInstructions(TaskComplexity complexity, string? modelFitTarget, AgentRole role)
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

    private static IReadOnlyList<string> BuildAcceptanceFailureBriefBlock(Goal goal, TaskSpec task, string? workingDirectory)
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
        lines.AddRange(BuildStructuredFailureReceiptLines(
            "acceptance/verification",
            failure.FailedChecks,
            task,
            retryEvent.OccurredAt,
            ReadLatestFailedAcceptanceOperation(workingDirectory, goal.Id, retryEvent.OccurredAt)));
        lines.Add("<!-- ACCEPTANCE_FAILURE_END -->");
        lines.Add(string.Empty);
        return lines;
    }

    private static List<string> BuildAccumulatedRetryFeedbackBriefBlock(
        Goal goal,
        TaskSpec task,
        string? workingDirectory,
        string? targetBranchName,
        string? targetHeadCommit)
    {
        if (task.RequiredRole is not (AgentRole.Developer or AgentRole.Tester or AgentRole.Reviewer))
        {
            return [];
        }

        var retryEvents = goal.Timeline
            .Where(IsAccumulatedRetryRoundEvent)
            .OrderBy(evt => evt.OccurredAt)
            .ToList();
        if (retryEvents.Count == 0)
        {
            return [];
        }

        var feedbackEvents = goal.Timeline
            .Where(IsAccumulatedRetryFeedbackEvent)
            .OrderByDescending(evt => evt.OccurredAt)
            .ThenByDescending(evt => (int)evt.Kind)
            .ToList();
        if (feedbackEvents.Count == 0)
        {
            return [];
        }

        var latestRetry = retryEvents.LastOrDefault();
        var priorOutcomeEvent = latestRetry is null
            ? null
            : goal.Timeline
                .Where(evt =>
                    evt.TaskId == latestRetry.TaskId &&
                    evt.OccurredAt <= latestRetry.OccurredAt &&
                    IsRetryPriorOutcomeEvent(evt))
                .OrderByDescending(evt => evt.OccurredAt)
                .FirstOrDefault();

        var lines = new List<string>
        {
            "<!-- ACCUMULATED_RETRY_FEEDBACK_START -->",
            "## Accumulated retry/review feedback",
            $"Newest first; capped at {AccumulatedRetryFeedbackMaxEntries} entries and {AccumulatedRetryFeedbackMaxChars} chars. Status legend: still-open, resolved-in-round-N, superseded.",
            "Use this as the current correction context before relying on original task wording, prior task history, branch evidence, or context digests.",
        };

        if (latestRetry is not null)
        {
            lines.Add($"Most recent retry: Retry {retryEvents.Count} of {retryEvents.Count}; {latestRetry.OccurredAt:u}; {DescribeTimelineTask(goal, latestRetry)}.");
        }

        if (task.RequiredRole == AgentRole.Developer &&
            (!string.IsNullOrWhiteSpace(targetBranchName) || !string.IsNullOrWhiteSpace(targetHeadCommit)))
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

        if (priorOutcomeEvent is not null)
        {
            lines.Add($"Prior outcome: {priorOutcomeEvent.OccurredAt:u}; {DescribeTimelineTask(goal, priorOutcomeEvent)}; {priorOutcomeEvent.Kind}: {PromptContextFormatter.TrimPromptBlock(priorOutcomeEvent.Message)}");
        }

        if (task.RequiredRole is AgentRole.Tester or AgentRole.Reviewer &&
            latestRetry?.TaskId is { } retriedTaskId)
        {
            var retriedTask = goal.FindTask(retriedTaskId);
            lines.AddRange(BuildStructuredFailureReceiptLines(
                "operator retry/verification",
                priorOutcomeEvent is null ? [] : [$"{priorOutcomeEvent.Kind}: {priorOutcomeEvent.Message}"],
                retriedTask,
                latestRetry.OccurredAt,
                ReadLatestFailedAcceptanceOperation(workingDirectory, goal.Id, latestRetry.OccurredAt)));
        }

        var sectionChars = string.Join(Environment.NewLine, lines).Length;
        var emittedCount = 0;
        var omittedCount = 0;
        for (var index = 0; index < feedbackEvents.Count; index++)
        {
            var evt = feedbackEvents[index];
            var message = evt.Message.Trim();
            if (message.Length == 0)
            {
                continue;
            }

            var status = DescribeAccumulatedRetryFeedbackStatus(retryEvents, feedbackEvents, evt);
            var retryDescriptor = retryEvents.Count == 0
                ? "Retry n/a"
                : $"Retry {RetryOrdinalAt(retryEvents, evt.OccurredAt)} of {retryEvents.Count}";
            var line = $"- [{status}] {retryDescriptor}; {evt.OccurredAt:u}; {DescribeTimelineTask(goal, evt)}; {evt.Kind}: {PromptContextFormatter.TrimPromptBlock(message)}";
            if (emittedCount >= AccumulatedRetryFeedbackMaxEntries ||
                sectionChars + line.Length + Environment.NewLine.Length > AccumulatedRetryFeedbackMaxChars)
            {
                omittedCount = feedbackEvents.Count - index;
                break;
            }

            lines.Add(line);
            sectionChars += line.Length + Environment.NewLine.Length;
            emittedCount++;
        }

        if (omittedCount > 0)
        {
            lines.Add($"- Omitted {omittedCount} oldest retry/review feedback entr{(omittedCount == 1 ? "y" : "ies")} to preserve prompt budget.");
        }

        lines.Add("<!-- ACCUMULATED_RETRY_FEEDBACK_END -->");
        lines.Add(string.Empty);
        return lines;
    }

    private static List<string> BuildEffectiveAcceptanceCriteriaCorrectionsBriefBlock(Goal goal, TaskSpec task)
    {
        if (task.RequiredRole is not (AgentRole.Developer or AgentRole.Reviewer) ||
            goal.EffectiveAcceptanceCriteriaCorrections.Count == 0)
        {
            return [];
        }

        var lines = new List<string>
        {
            "<!-- EFFECTIVE_ACCEPTANCE_CRITERIA_CORRECTIONS_START -->",
            "## EFFECTIVE ACCEPTANCE CRITERIA - OPERATOR CORRECTIONS",
            "Operator corrections in this overlay supersede conflicting brief text. Do not enforce or re-raise findings that apply only to superseded criteria.",
            "Correction marker convention: CRITERIA CORRECTION: supersedes=\"<brief text/ref>\"; correction=\"<effective criterion>\".",
        };

        foreach (var correction in goal.EffectiveAcceptanceCriteriaCorrections.OrderByDescending(item => item.RecordedAt))
        {
            var taskReference = correction.SourceTaskId is null ? "goal timeline" : $"task {correction.SourceTaskId.Value[..8]}";
            lines.Add($"- Supersedes: {PromptContextFormatter.TrimPromptBlock(correction.SupersededCriterion)}");
            lines.Add($"  Effective criterion: {PromptContextFormatter.TrimPromptBlock(correction.Correction)}");
            lines.Add($"  Provenance: {correction.Actor}; {correction.RecordedAt:u}; {correction.SourceKind}; {taskReference}.");
        }

        lines.Add("<!-- EFFECTIVE_ACCEPTANCE_CRITERIA_CORRECTIONS_END -->");
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

    private static IReadOnlyList<string> BuildReviewerExecutedTestEvidenceBriefBlock(Goal goal, TaskSpec task)
    {
        if (task.RequiredRole != AgentRole.Reviewer)
        {
            return [];
        }

        var receipts = goal.Tasks
            .Where(candidate => candidate.Id != task.Id)
            .SelectMany(candidate => candidate.VerificationHistory.Select(verification => new
            {
                Task = candidate,
                Verification = verification
            }))
            .OrderByDescending(item => item.Verification.CompletedAt)
            .ToList();

        var lines = new List<string>
        {
            "## Executed Test Evidence",
            $"Reviewer is read-only; use these existing verification receipts before asking for reruns. Newest first; capped at {ReviewerExecutedTestEvidenceMaxLines} receipt line(s)."
        };

        if (receipts.Count == 0)
        {
            lines.Add("No executed test evidence exists for this goal yet.");
            lines.Add(string.Empty);
            return lines;
        }

        var emitted = 0;
        foreach (var receipt in receipts)
        {
            if (emitted >= ReviewerExecutedTestEvidenceMaxLines)
            {
                break;
            }

            lines.Add(FormatReviewerVerificationReceipt(goal, receipt.Task, receipt.Verification));
            emitted++;

            if (emitted >= ReviewerExecutedTestEvidenceMaxLines)
            {
                break;
            }

            if (WorkerResultBlockers.TryFindTests(receipt.Verification, out var tests))
            {
                lines.Add(FormatReviewerWorkerResultTestsReceipt(goal, receipt.Task, receipt.Verification, tests));
                emitted++;
            }
        }

        if (receipts.Count > 0 && emitted >= ReviewerExecutedTestEvidenceMaxLines)
        {
            var omitted = receipts.Sum(item => WorkerResultBlockers.TryFindTests(item.Verification, out _) ? 2 : 1) - emitted;
            if (omitted > 0)
            {
                lines.Add($"- Omitted {omitted} older executed-test evidence line(s).");
            }
        }

        lines.Add(string.Empty);
        return lines;
    }

    private static IReadOnlyList<string> BuildReviewerChangedFileScopeBriefBlock(
        TaskSpec task,
        IReadOnlyList<string>? changedFiles,
        string? mergeBase,
        int? totalChangedFileCount,
        bool? mergeTreeClean,
        IReadOnlyList<string>? mergeTreeConflictPaths,
        int? totalMergeTreeConflictPathCount)
    {
        if (task.RequiredRole != AgentRole.Reviewer || (changedFiles is null && !mergeTreeClean.HasValue))
        {
            return [];
        }

        var boundedChangedFiles = (changedFiles ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Take(ReviewerChangedFileScopeMaxLines)
            .ToArray();
        var total = Math.Max(totalChangedFileCount ?? boundedChangedFiles.Length, boundedChangedFiles.Length);
        var lines = new List<string>
        {
            "## Reviewer Changed-File Scope",
            "Authoritative dispatch-preparation scope: git diff --name-only main...HEAD using three-dot merge-base semantics against current main.",
            $"Merge base: {(string.IsNullOrWhiteSpace(mergeBase) ? "unknown" : mergeBase.Trim())}",
            $"Changed files: {total}; showing {boundedChangedFiles.Length}."
        };

        if (mergeTreeClean is true)
        {
            lines.Add("Merge-tree status: clean against current main (git merge-tree --write-tree --name-only main HEAD).");
        }
        else if (mergeTreeClean is false)
        {
            var boundedConflictPaths = (mergeTreeConflictPaths ?? [])
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Take(ReviewerChangedFileScopeMaxLines)
                .ToArray();
            var conflictTotal = Math.Max(totalMergeTreeConflictPathCount ?? boundedConflictPaths.Length, boundedConflictPaths.Length);
            lines.Add("Merge-tree status: conflicted against current main (git merge-tree --write-tree --name-only main HEAD).");
            lines.Add($"Conflicting paths: {conflictTotal}; showing {boundedConflictPaths.Length}.");
            foreach (var path in boundedConflictPaths)
            {
                lines.Add($"- conflict: {path}");
            }

            if (boundedConflictPaths.Length < conflictTotal)
            {
                lines.Add($"- Omitted {conflictTotal - boundedConflictPaths.Length} additional conflict path(s) to preserve prompt budget.");
            }
        }

        lines.Add("Staleness policy: branch-behind-main alone is NOT a blocker; the deterministic acceptance gate rebases and verifies the integrated result. Staleness may block only with concrete integration-risk evidence: merge-tree conflicts, semantic overlap with landed changes in the same files, or a diff that no longer applies. Otherwise record staleness as advisory.");

        if (boundedChangedFiles.Length == 0)
        {
            lines.Add("- No changed files reported by git diff --name-only main...HEAD.");
        }
        else
        {
            foreach (var path in boundedChangedFiles)
            {
                lines.Add($"- {path}");
            }
        }

        if (boundedChangedFiles.Length < total)
        {
            lines.Add($"- Omitted {total - boundedChangedFiles.Length} additional changed file(s) to preserve prompt budget.");
        }

        lines.Add("Independent scope checks must use git diff main...HEAD. Do not use two-dot diffs such as main..HEAD, git diff HEAD, git status, or working-tree-only comparisons as scope verdict evidence.");
        lines.Add(string.Empty);
        return lines;
    }

    private static string FormatReviewerVerificationReceipt(
        Goal goal,
        TaskSpec task,
        TaskVerificationRecord verification)
    {
        var status = verification.Succeeded ? "pass" : "fail";
        var freshness = DescribeVerificationFreshness(task, verification);
        var paths = DescribeVerificationArtifactPaths(verification);
        var evidence = DescribeVerificationOutputEvidence(verification);
        return $"- {verification.CompletedAt:u}; provenance: Task {TaskDisplayNumber.Resolve(goal, task.Id)} {task.RequiredRole} verification; result: {status} (exit {verification.ExitCode}); command/run context: {PromptContextFormatter.TrimPromptBlock(verification.Command)} @ {PromptContextFormatter.TrimPromptBlock(verification.WorkingDirectory)}; freshness: {freshness}{paths}{evidence}";
    }

    private static string FormatReviewerWorkerResultTestsReceipt(
        Goal goal,
        TaskSpec task,
        TaskVerificationRecord verification,
        string tests)
    {
        return $"- {verification.CompletedAt:u}; provenance: Task {TaskDisplayNumber.Resolve(goal, task.Id)} {task.RequiredRole} WORKER_RESULT tests; tests: {PromptContextFormatter.TrimPromptBlock(tests)}; freshness: {DescribeVerificationFreshness(task, verification)}";
    }

    private static string DescribeVerificationFreshness(TaskSpec task, TaskVerificationRecord verification)
    {
        if (TryGetWorkerResultField(verification, "commit", out var workerResultCommit) &&
            !IsNoneValue(workerResultCommit))
        {
            return $"verified commit {workerResultCommit}";
        }

        if (!string.IsNullOrWhiteSpace(task.LastDispatch?.ResultCommit))
        {
            return $"verified commit {task.LastDispatch.ResultCommit.Trim()}";
        }

        if (!string.IsNullOrWhiteSpace(task.LastDispatch?.BaseCommit))
        {
            return $"base commit {task.LastDispatch.BaseCommit.Trim()}";
        }

        return "unknown commit";
    }

    private static string DescribeVerificationArtifactPaths(TaskVerificationRecord verification)
    {
        var paths = new List<string>();
        if (!string.IsNullOrWhiteSpace(verification.StandardOutputPath))
        {
            paths.Add($"stdout {verification.StandardOutputPath.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(verification.StandardErrorPath))
        {
            paths.Add($"stderr {verification.StandardErrorPath.Trim()}");
        }

        return paths.Count == 0
            ? string.Empty
            : $"; artifacts: {string.Join(", ", paths)}";
    }

    private static string DescribeVerificationOutputEvidence(TaskVerificationRecord verification)
    {
        var summaries = new List<string>();
        if (!string.IsNullOrWhiteSpace(verification.StandardOutput))
        {
            summaries.Add($"stdout {TailPreferredSingleLine(verification.StandardOutput, 240)}");
        }

        if (!string.IsNullOrWhiteSpace(verification.StandardError))
        {
            summaries.Add($"stderr {TailPreferredSingleLine(verification.StandardError, 240)}");
        }

        return summaries.Count == 0
            ? string.Empty
            : $"; evidence: {string.Join(" | ", summaries)}";
    }

    private static string TailPreferredSingleLine(string text, int maxChars)
    {
        var normalized = text.Trim().ReplaceLineEndings(" ");
        if (normalized.Length <= maxChars)
        {
            return normalized;
        }

        return $"...[truncated {normalized.Length - maxChars} chars before evidence tail]...{normalized[^maxChars..]}";
    }

    private static bool TryGetWorkerResultField(TaskVerificationRecord verification, string fieldName, out string value)
    {
        foreach (var line in (verification.StandardOutput + Environment.NewLine + verification.StandardError)
            .Split(["\r\n", "\n"], StringSplitOptions.None))
        {
            var trimmed = line.Trim();
            var separator = trimmed.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            var key = trimmed[..separator].TrimStart('-', ' ').Trim();
            if (string.Equals(key, fieldName, StringComparison.OrdinalIgnoreCase))
            {
                value = trimmed[(separator + 1)..].Trim();
                return value.Length > 0;
            }
        }

        value = string.Empty;
        return false;
    }

    private static bool IsNoneValue(string value) =>
        string.Equals(value.Trim(), "none", StringComparison.OrdinalIgnoreCase);

    private static bool IsAccumulatedRetryFeedbackEvent(ProgressEvent evt)
    {
        return IsAccumulatedRetryRoundEvent(evt) ||
               evt.Kind is (
                   ProgressKind.TaskSubscriptionLimitReviewAcknowledged or
                   ProgressKind.ReviewerEvidenceRequestReceived or
                   ProgressKind.ReviewerEvidenceRunRecorded) ||
               (evt.Kind == ProgressKind.TaskNote && IsAccumulatedRetryFeedbackTaskNote(evt.Message));
    }

    private static bool IsAccumulatedRetryFeedbackTaskNote(string message)
    {
        var trimmed = message.Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        return !trimmed.StartsWith("CLASSIFIER ", StringComparison.Ordinal) &&
               !trimmed.StartsWith("RESOURCE ", StringComparison.Ordinal) &&
               !trimmed.StartsWith("TaskOutputCommitted:", StringComparison.Ordinal) &&
               !trimmed.StartsWith("Ignored stale dispatch execution evidence from ", StringComparison.Ordinal) &&
               !trimmed.StartsWith("Ignored duplicate dispatch execution evidence for already settled dispatch:", StringComparison.Ordinal) &&
               !trimmed.StartsWith("Reconciled failed dispatch verification to Completed from structured WORKER_RESULT evidence", StringComparison.Ordinal) &&
               !trimmed.StartsWith("Auto-cleared stale LastProcess.IsRunning before dispatch;", StringComparison.Ordinal) &&
               !trimmed.StartsWith("StaleDispatchAutoRequeued:", StringComparison.Ordinal) &&
               !trimmed.StartsWith("StaleDispatchAutoRequeueCapExhausted:", StringComparison.Ordinal);
    }

    private static bool IsAccumulatedRetryRoundEvent(ProgressEvent evt) =>
        evt.Kind == ProgressKind.TaskRetried && !IsDownstreamInvalidationRetryEvent(evt);

    private static bool IsDownstreamInvalidationRetryEvent(ProgressEvent evt) =>
        evt.Message.StartsWith("Invalidated ", StringComparison.Ordinal) &&
        evt.Message.Contains(" task because upstream ", StringComparison.Ordinal) &&
        evt.Message.EndsWith(" was retried.", StringComparison.Ordinal);

    private static string DescribeAccumulatedRetryFeedbackStatus(
        IReadOnlyList<ProgressEvent> retryEvents,
        IReadOnlyList<ProgressEvent> feedbackEvents,
        ProgressEvent feedbackEvent)
    {
        if (TryFindResolutionEvent(feedbackEvents, feedbackEvent, out var resolutionEvent))
        {
            return $"resolved-in-round-{RetryOrdinalAt(retryEvents, resolutionEvent.OccurredAt)}";
        }

        if (feedbackEvent.TaskId is { } sameTaskId &&
            retryEvents.Any(evt => evt.TaskId == sameTaskId && evt.OccurredAt > feedbackEvent.OccurredAt))
        {
            return "superseded";
        }

        return "still-open";
    }

    private static bool TryFindResolutionEvent(
        IReadOnlyList<ProgressEvent> feedbackEvents,
        ProgressEvent feedbackEvent,
        out ProgressEvent resolutionEvent)
    {
        foreach (var candidate in feedbackEvents)
        {
            if (candidate.OccurredAt <= feedbackEvent.OccurredAt ||
                candidate.TaskId != feedbackEvent.TaskId ||
                !ContainsResolutionSignal(candidate.Message) ||
                !MessageReferencesFeedback(feedbackEvent.Message, candidate.Message))
            {
                continue;
            }

            resolutionEvent = candidate;
            return true;
        }

        resolutionEvent = default!;
        return false;
    }

    private static bool ContainsResolutionSignal(string message)
    {
        return message.Contains("resolved", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("fixed", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("addressed", StringComparison.OrdinalIgnoreCase);
    }

    private static bool MessageReferencesFeedback(string feedbackMessage, string candidateMessage)
    {
        var feedback = NormalizeFeedbackReference(feedbackMessage);
        var candidate = NormalizeFeedbackReference(candidateMessage);
        if (feedback.Length == 0 || candidate.Length == 0)
        {
            return false;
        }

        if (candidate.Contains(feedback, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var meaningfulTokens = feedback
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => token.Length >= 6)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return meaningfulTokens.Count > 0 &&
               meaningfulTokens.Count(token => candidate.Contains(token, StringComparison.OrdinalIgnoreCase)) >= Math.Min(2, meaningfulTokens.Count);
    }

    private static string NormalizeFeedbackReference(string value)
    {
        var chars = value
            .Trim()
            .ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : ' ')
            .ToArray();
        return string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static IReadOnlyList<string> BuildStructuredFailureReceiptLines(
        string source,
        IReadOnlyList<string> failedChecks,
        TaskSpec task,
        DateTimeOffset retryOccurredAt,
        GoalOperationFailureReceipt? acceptanceFailure = null)
    {
        var verification = LatestFailedVerificationBefore(task, retryOccurredAt);
        if (verification is null && failedChecks.Count == 0 && task.LastProcess is null && acceptanceFailure is null)
        {
            return [];
        }

        var lines = new List<string>
        {
            "Structured failure receipt (bounded):",
            $"Source: {source}",
            $"Receipt cap: {FailureReceiptMaxChars} chars; output is tail-preferred."
        };

        if (failedChecks.Count > 0)
        {
            lines.Add("Failed checks/criteria:");
            lines.AddRange(failedChecks.Select(check => $"- {PromptContextFormatter.TrimPromptBlock(check)}"));
        }

        if (acceptanceFailure is not null)
        {
            lines.Add($"Acceptance operation: {acceptanceFailure.Operation}");
            lines.Add($"Acceptance operation failed: {acceptanceFailure.At:u}");
            AddPathLine(lines, "Goal operation journal path", acceptanceFailure.JournalPath);
            AddTail(lines, "Acceptance operation detail tail", acceptanceFailure.Detail);
        }

        if (verification is not null)
        {
            lines.Add($"Verification command: {verification.Command}");
            lines.Add($"Verification exit code: {verification.ExitCode}");
            lines.Add($"Verification completed: {verification.CompletedAt:u}");
            AddPathLine(lines, "Verification stdout path", verification.StandardOutputPath);
            AddPathLine(lines, "Verification stderr path", verification.StandardErrorPath);
        }

        if (task.LastProcess is not null)
        {
            AddPathLine(lines, "Process stdout path", task.LastProcess.StandardOutputPath);
            AddPathLine(lines, "Process stderr path", task.LastProcess.StandardErrorPath);
        }

        if (verification is not null)
        {
            AddTail(lines, "Stdout tail", verification.StandardOutput);
            AddTail(lines, "Stderr tail", verification.StandardError);
        }

        return CapReceiptLines(lines);
    }

    private static TaskVerificationRecord? LatestFailedVerificationBefore(TaskSpec task, DateTimeOffset retryOccurredAt)
    {
        return task.VerificationHistory
            .Where(verification => !verification.Succeeded && verification.CompletedAt <= retryOccurredAt)
            .OrderByDescending(verification => verification.CompletedAt)
            .FirstOrDefault();
    }

    private static GoalOperationFailureReceipt? ReadLatestFailedAcceptanceOperation(
        string? workingDirectory,
        GoalId goalId,
        DateTimeOffset retryOccurredAt)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            return null;
        }

        string journalPath;
        try
        {
            journalPath = Path.Combine(
                Path.GetFullPath(workingDirectory),
                ".orchestrator",
                "goal-operations",
                $"{goalId.Value}.jsonl");
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        if (!File.Exists(journalPath))
        {
            return null;
        }

        return File.ReadLines(journalPath)
            .Select(TryReadGoalOperationReceiptEntry)
            .Where(entry =>
                entry is not null &&
                IsAcceptanceOperation(entry.Operation) &&
                string.Equals(entry.Status, "Failed", StringComparison.OrdinalIgnoreCase) &&
                entry.At <= retryOccurredAt)
            .OrderByDescending(entry => entry!.At)
            .Select(entry => new GoalOperationFailureReceipt(
                entry!.Operation!.Trim(),
                entry.At,
                entry.Detail ?? string.Empty,
                journalPath))
            .FirstOrDefault();
    }

    private static GoalOperationReceiptEntry? TryReadGoalOperationReceiptEntry(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<GoalOperationReceiptEntry>(line, GoalOperationJsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsAcceptanceOperation(string? operation) =>
        operation is not null &&
        (operation.Equals("conductor:acceptance", StringComparison.OrdinalIgnoreCase) ||
         operation.Equals("acceptance", StringComparison.OrdinalIgnoreCase));

    private sealed record GoalOperationFailureReceipt(
        string Operation,
        DateTimeOffset At,
        string Detail,
        string JournalPath);

    private sealed class GoalOperationReceiptEntry
    {
        public string? Operation { get; set; }

        public string? Status { get; set; }

        public DateTimeOffset At { get; set; }

        public string? Detail { get; set; }
    }

    private static void AddPathLine(List<string> lines, string label, string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            lines.Add($"{label}: {path.Trim()}");
        }
    }

    private static void AddTail(List<string> lines, string label, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        lines.Add($"{label}:");
        lines.Add(TailPreferredText(text.Trim(), FailureReceiptStreamTailChars));
    }

    private static IReadOnlyList<string> CapReceiptLines(List<string> lines)
    {
        var text = string.Join(Environment.NewLine, lines);
        if (text.Length <= FailureReceiptMaxChars)
        {
            return lines;
        }

        const int headChars = 700;
        var marker = $"{Environment.NewLine}...[failure receipt truncated for prompt budget]...{Environment.NewLine}";
        var tailChars = FailureReceiptMaxChars - headChars - marker.Length;
        if (tailChars <= 0)
        {
            return [text[^FailureReceiptMaxChars..]];
        }

        var capped = text[..headChars] + marker + text[^tailChars..];
        return capped.Split(Environment.NewLine).ToList();
    }

    private static string TailPreferredText(string text, int maxChars)
    {
        if (text.Length <= maxChars)
        {
            return text;
        }

        return $"...[truncated {text.Length - maxChars} chars before failure tail]..." + Environment.NewLine + text[^maxChars..];
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
