using System.Text.Json;

namespace Mcg.AgentOrchestrator.Core;

public sealed partial class AgentOrchestratorKernel
{
    private const int FailureReceiptMaxChars = 2000;
    private const int FailureReceiptStreamTailChars = 700;
    private const int ReviewerExecutedTestEvidenceMaxLines = 12;
    private const int ReviewerChangedFileScopeMaxLines = 120;
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
        int? reviewerScopeTotalChangedFileCount = null)
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
        roleLines.AddRange(SdlcRolePromptRequirements.Build(task.RequiredRole, complexity));
        roleLines.Add(string.Empty);
        segments.Add(TaskBriefSegment.Fixed(roleLines));

        var reviewerChangedFileScope = BuildReviewerChangedFileScopeBriefBlock(
            task,
            reviewerScopeChangedFiles,
            reviewerScopeMergeBase,
            reviewerScopeTotalChangedFileCount);
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

        var recentRetryFeedback = BuildRecentRetryFeedbackBriefBlock(goal, task, workingDirectory);
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
        int? totalChangedFileCount)
    {
        if (task.RequiredRole != AgentRole.Reviewer || changedFiles is null)
        {
            return [];
        }

        var boundedChangedFiles = changedFiles
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

    private static IReadOnlyList<string> BuildRecentRetryFeedbackBriefBlock(Goal goal, TaskSpec task, string? workingDirectory)
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

        if (latestRetry.TaskId is { } retriedTaskId)
        {
            var retriedTask = goal.FindTask(retriedTaskId);
            lines.AddRange(BuildStructuredFailureReceiptLines(
                "operator retry/verification",
                priorOutcomeEvent is null ? [] : [$"{priorOutcomeEvent.Kind}: {priorOutcomeEvent.Message}"],
                retriedTask,
                latestRetry.OccurredAt,
                ReadLatestFailedAcceptanceOperation(workingDirectory, goal.Id, latestRetry.OccurredAt)));
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
