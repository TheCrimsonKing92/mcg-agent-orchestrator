using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.Core;

public sealed partial class AgentOrchestratorKernel
{
    private const int FailureReceiptMaxChars = 2000;
    private const int FailureReceiptStreamTailChars = 700;
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

    public IReadOnlyList<ReviewFinding> GetReviewFindingState(GoalId goalId) =>
        GetReviewFindingState(goalId, out _);

    /// <summary>
    /// Derives current finding state by replaying stored reviewer rounds. Replaying history must never
    /// fail: a round that cannot be folded is skipped and reported through <paramref name="inconsistencies"/>,
    /// leaving the prior state intact so the finding stays open at the anchor it was opened against. New
    /// rounds are still validated where they are recorded, so skipping here does not weaken convergence.
    /// </summary>
    public IReadOnlyList<ReviewFinding> GetReviewFindingState(
        GoalId goalId,
        out IReadOnlyList<string> inconsistencies) =>
        ReplayStoredReviewFindingRounds(GetGoal(goalId), AgentRole.Reviewer, out inconsistencies);

    private static IReadOnlyList<ReviewFinding> ReplayStoredReviewFindingRounds(
        Goal goal,
        AgentRole role,
        out IReadOnlyList<string> inconsistencies)
    {
        IReadOnlyList<ReviewFinding> state = [];
        List<string>? skipped = null;
        var latestFindingOccurrences = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        string? lastAcceptedReviewedCommit = null;
        foreach (var verification in goal.Tasks
            .Where(candidate => candidate.RequiredRole == role)
            .SelectMany(candidate => candidate.VerificationHistory)
            .OrderBy(candidate => candidate.CompletedAt))
        {
            if (!WorkerResultBlockers.TryFindReviewFindingRound(verification, out var round, out var parseDiagnostic))
            {
                // RECORD the parse failure instead of discarding it. This used to be `out _` plus a bare
                // continue, which made a reviewer whose findings could not be parsed indistinguishable from a
                // reviewer that submitted none - the operator was told "merged finding state was EMPTY" while
                // twelve valid findings sat in the log. Convergence failures below were already collected;
                // parse failures were the asymmetric silent case, and that asymmetry cost a full day of
                // misdirected diagnosis onto reviewers who had done nothing wrong.
                if (!string.IsNullOrWhiteSpace(parseDiagnostic))
                {
                    (skipped ??= []).Add(
                        $"{role.ToString().ToLowerInvariant()} findings could not be parsed and were skipped: {parseDiagnostic}");
                }

                continue;
            }

            var recordedViolation = verification.ReviewFindingContractViolation;
            if (!ShouldReplayStoredReviewFindingRound(verification))
            {
                (skipped ??= []).Add($"{recordedViolation!.Code}: {recordedViolation.Message}");
                continue;
            }

            try
            {
                state = recordedViolation switch
                {
                    null => ReviewFindingConvergence.ApplyRound(state, round),
                    { } rejected when ReviewFindingConvergence.IsRejectedCapResolutionRound(rejected) =>
                        ReviewFindingConvergence.ApplyRejectedCapResolutionRound(state, round, rejected),
                    { } rejected when ReviewFindingConvergence.IsRejectedIdentityTransitionRound(rejected) =>
                        ReviewFindingConvergence.ApplyRejectedIdentityTransitionRound(state, round, rejected),
                    _ => state
                };
                if (recordedViolation is not null)
                {
                    (skipped ??= []).Add($"{recordedViolation.Code}: {recordedViolation.Message}");
                }

                foreach (var finding in round.Findings.Where(finding =>
                             recordedViolation is null ||
                             (!ReviewFindingConvergence.IsRejectedCapResolutionTransition(recordedViolation, finding.StableId) &&
                              !ReviewFindingConvergence.IsRejectedIdentityTransition(recordedViolation, finding.StableId))))
                {
                    latestFindingOccurrences[finding.StableId] = verification.CompletedAt;
                }
                state = ApplyHumanInputSupersedeFindingResolutions(goal, state, latestFindingOccurrences);
                if (!string.IsNullOrWhiteSpace(verification.ReviewedCommit))
                {
                    lastAcceptedReviewedCommit = verification.ReviewedCommit.Trim();
                }
            }
            catch (ReviewFindingConvergenceException error)
            {
                var replayError = error;
                if (ReviewFindingConvergence.CanCanonicalizeIdentityTransitions(error.Violation) &&
                    SameNonEmptyReviewedCommit(lastAcceptedReviewedCommit, verification.ReviewedCommit))
                {
                    try
                    {
                        state = ReviewFindingConvergence.ApplyCanonicalizedIdentityTransitionRound(
                            state,
                            round,
                            error.Violation,
                            out _);
                        foreach (var finding in round.Findings)
                        {
                            latestFindingOccurrences[finding.StableId] = verification.CompletedAt;
                        }
                        state = ApplyHumanInputSupersedeFindingResolutions(goal, state, latestFindingOccurrences);
                        lastAcceptedReviewedCommit = verification.ReviewedCommit!.Trim();
                        continue;
                    }
                    catch (ReviewFindingConvergenceException canonicalizedError)
                    {
                        replayError = canonicalizedError;
                    }
                }

                (skipped ??= []).Add($"{replayError.Code}: {replayError.Message}");
            }
        }

        inconsistencies = skipped ?? [];
        return state;
    }

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

    public TaskBriefSource BuildTaskBriefSource(
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
        int? reviewerMergeTreeTotalConflictPathCount = null,
        IReadOnlyList<ReviewFindingLocation>? reviewerRoundTouchedAnchors = null,
        string? reviewerRoundTouchProofDiagnostic = null,
        ReviewRetryCapReceipt? reviewRetryCap = null,
        bool measureWithTypedSourceBoundaries = false,
        IReadOnlyList<ChangedExistingTest>? changedExistingTests = null,
        string? changedExistingTestsDiagnostic = null, string? acceptanceManifestPromptPath = null)
    {
        var goal = GetGoal(goalId);
        var task = goal.FindTask(taskId);
        var pendingInput = GetPendingHumanInput(goalId)
            .Where(request => request.TaskId == taskId || request.TaskId is null)
            .ToList();
        var resolvedInput = HumanInputRequests
            .Where(request =>
                request.GoalId == goalId &&
                (request.TaskId == taskId || request.TaskId is null) &&
                request.IsCompleted &&
                !request.WasDismissed &&
                !request.IsSyntheticParkedHumanWaitCompletion &&
                request.SupersededByRequestId is null &&
                !string.IsNullOrWhiteSpace(request.Answer))
            .OrderByDescending(request => request.AnsweredAt)
            .Take(8)
            .OrderBy(request => request.AnsweredAt)
            .ToList();
        // Answered prerequisite evidence reaches later same-goal roles. This is a sibling query, not
        // a widening of resolvedInput: widening would also change the originating task's own brief
        // and the dedup/retraction surfaces. Goal isolation is the first clause and is not optional;
        // requests the Planner raised for itself stay task-private unless typed as evidence, so
        // historical records that predate the classification decode to SpecClarification and do not
        // propagate.
        var prerequisiteEvidence = HumanInputRequests
            .Where(request =>
                request.GoalId == goalId &&
                request.Kind == HumanWaitKind.PlannerPrerequisiteEvidence &&
                request.TaskId is not null &&
                request.TaskId != taskId &&
                request.IsCompleted &&
                !request.WasDismissed &&
                !request.IsSyntheticParkedHumanWaitCompletion &&
                request.SupersededByRequestId is null &&
                !string.IsNullOrWhiteSpace(request.Answer))
            .OrderBy(request => request.AnsweredAt)
            .Select(request => new PrerequisiteEvidenceEntry(
                request.Id.Value,
                request.Question,
                request.AuthoritativeAnswer!.Text,
                request.AuthoritativeAnswer.BriefVersion))
            .ToList();
        var prerequisiteEvidenceSection = PrerequisiteEvidenceDigest.RenderSection(
            prerequisiteEvidence,
            goal.AuthoritativeBrief.Version);
        var complexity = TaskComplexityEstimator.Estimate(task.Description, goal.Objective, task.RequiredRole);
        var timeline = PromptContextFormatter.SelectPromptTimelineEvents(
            goal.Timeline.Where(evt => (evt.TaskId == taskId || evt.TaskId is null) && !IsRedundantBriefTimelineEvent(task, evt)),
            maxEvents: TimelineEventBudget(complexity),
            complexity);

        var usesFileAccessContext = !string.IsNullOrWhiteSpace(workingDirectory) && !string.IsNullOrWhiteSpace(contextDirectory);
        var hasDurableResearch = usesFileAccessContext &&
            task.RequiredRole == AgentRole.Planner &&
            File.Exists(Path.Combine(contextDirectory!, "research-notes.md")) &&
            new FileInfo(Path.Combine(contextDirectory!, "research-notes.md")).Length > 0;
        var isScoutPlanner = ScoutRoundPolicy.IsScoutPlanner(goal, task);
        var hasAuthoritativePlannerCriteria =
            task.RequiredRole == AgentRole.Planner &&
            goal.RefinedSpec is { AcceptanceCriteria.Count: > 0 };
        var renderedObjective = hasAuthoritativePlannerCriteria
            ? AcceptanceCriteriaParser.RemoveDeclaredSection(goal.Objective)
            : goal.Objective;
        var headerLines = new List<string>
        {
            "# Agent Task Brief",
            string.Empty
        };
        headerLines.AddRange(BuildAccumulatedRetryFeedbackBriefBlock(goal, task, workingDirectory, targetBranchName, targetHeadCommit));
        headerLines.AddRange(PreTesterEvidenceIndexLines.ForTester(goal, task, targetHeadCommit));
        headerLines.AddRange(BuildDeveloperPreReviewRepeatBriefBlock(goal, task));
        headerLines.AddRange(BuildEffectiveAcceptanceCriteriaCorrectionsBriefBlock(goal, task));
        headerLines.AddRange(BuildAcceptanceFailureBriefBlock(goal, task, workingDirectory));
        headerLines.AddRange([
            $"Goal: {PromptContextFormatter.TrimPrimaryContextBlock(renderedObjective, complexity)}",
            $"Goal id: {goal.Id.Value}",
            $"Goal status: {goal.Status}",
            "Decision context: embedded in this brief and .orchestrator-handoff.md in the working directory when present; do not attempt to reach orchestrator state.",
            "A Planner must map evidence that was never recorded as disposition=undecidable, naming what would settle it, its required source, and why it is unavailable, while planning all remaining criteria in the same round. Escalate only when operator action is required; classified evidence escalations must state retrievable plus the inaccessible store, or never-recorded.",
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

        if (task.RequiredRole == AgentRole.Reviewer && reviewRetryCap is not null)
        {
            headerLines.Add($"Review retry budget: round {reviewRetryCap.Round}/{reviewRetryCap.StopRound}.");
            headerLines.Add(reviewRetryCap.IsAtCap
                ? "This dispatch is at the automatic review-retry cap. If blocking findings remain, use `verdict: blocked-at-cap` and list every open blocker; never return `pass` for known-incomplete work. The orchestrator will surface the findings for an operator decision."
                : $"If blockers remain, use `needs-work`; `blocked-at-cap` is only valid at round {reviewRetryCap.StopRound}/{reviewRetryCap.StopRound}.");
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
        instructionLines.AddRange(BuildTaskBriefInstructions(complexity, modelFitTarget, task.RequiredRole, hasDurableResearch, contextDirectory));
        if (!string.IsNullOrWhiteSpace(contextDirectory))
            CriteriaSelfCheckPromptContext.Externalize(instructionLines, task.RequiredRole, includeReference: true);
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
                $"Behavioral contract: {(hasAuthoritativePlannerCriteria ? AcceptanceCriteriaParser.RemoveDeclaredSection(refinedSpec.BehavioralContract) : refinedSpec.BehavioralContract)}",
                string.Empty,
                hasAuthoritativePlannerCriteria
                    ? "Acceptance criteria (authoritative list validated by PlannerOutputContract):"
                    : "Acceptance criteria:"
            };
            for (var criterionIndex = 0; criterionIndex < refinedSpec.AcceptanceCriteria.Count; criterionIndex++)
            {
                var criterion = refinedSpec.AcceptanceCriteria[criterionIndex];
                var normalizedCriterion = criterion.Trim();
                var waiver = goal.EffectiveAcceptanceCriteriaCorrections
                    .Where(correction =>
                        correction.IsWaiver &&
                        string.Equals(correction.SupersededCriterion, normalizedCriterion, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(correction => correction.RecordedAt)
                    .FirstOrDefault();
                if (waiver is null)
                {
                    specLines.Add(hasAuthoritativePlannerCriteria
                        ? $"{criterionIndex + 1}. {criterion}"
                        : $"- {criterion}");
                    continue;
                }

                specLines.Add(hasAuthoritativePlannerCriteria
                    ? $"{criterionIndex + 1}. [WAIVED] {normalizedCriterion}"
                    : $"- [WAIVED] {normalizedCriterion}");
                specLines.Add($"  Reason: {PromptContextFormatter.TrimPromptBlock(waiver.WaiverReason!)}");
                specLines.Add($"  Waived by {PromptContextFormatter.TrimPromptBlock(waiver.Actor)} at {waiver.RecordedAt:u}.");
            }
            if (refinedSpec.OperatorOwnedAcceptanceCriteria.Count > 0)
            {
                specLines.Add(string.Empty);
                specLines.Add("OPERATOR-OWNED / post-landing criteria (not part of worker acceptance):");
                foreach (var criterion in refinedSpec.OperatorOwnedAcceptanceCriteria)
                    specLines.Add($"- {criterion}");
            }
            var acceptanceGateOwnedCriteria = refinedSpec.AcceptanceGateOwnedAcceptanceCriteria
                .Where(criterion => !refinedSpec.OperatorOwnedAcceptanceCriteria.Contains(
                    criterion,
                    StringComparer.OrdinalIgnoreCase))
                .ToArray();
            if (acceptanceGateOwnedCriteria.Length > 0)
            {
                specLines.Add(string.Empty);
                specLines.Add("ACCEPTANCE-GATE-OWNED criteria (the acceptance gate, not the worker, produces this evidence):");
                foreach (var criterion in acceptanceGateOwnedCriteria)
                    specLines.Add($"- {criterion}");
            }
            if (refinedSpec.Decisions.Count > 0)
            {
                specLines.Add(string.Empty);
                specLines.Add("Decisions:");
                foreach (var decision in refinedSpec.Decisions)
                    specLines.Add($"- {decision.Question} → {decision.Choice} ({decision.Rationale})");
            }
            var authoritativeClarifications = refinedSpec.AuthoritativeClarificationAnswerHistory;
            if (authoritativeClarifications.Count > 0)
            {
                specLines.Add(string.Empty);
                specLines.Add("Clarification answer provenance:");
                foreach (var answer in authoritativeClarifications)
                {
                    var answeredUnder = answer.BriefVersion is { } briefVersion
                        ? $"brief v{briefVersion}"
                        : "an unknown brief version";
                    specLines.Add(
                        $"- {answer.Id} (answered under {answeredUnder}; " +
                        $"current brief v{goal.AuthoritativeBrief.Version}): {PromptContextFormatter.TrimPromptBlock(answer.Text)}");
                }
            }
            specLines.Add(string.Empty);
            segments.Add(TaskBriefSegment.Projected("goal/refined-spec.json", specLines));
        }

        var roleLines = new List<string>();
        roleLines.AddRange(RoleRequirementManifestPath.Apply(isScoutPlanner
            ? SdlcRolePromptRequirements.BuildScout(
                complexity,
                SdlcRolePromptRequirements.HasHighRiskOrComplexIntakeRiskLabel(goal))
            : SdlcRolePromptRequirements.Build(
                task.RequiredRole,
                complexity,
                SdlcRolePromptRequirements.HasHighRiskOrComplexIntakeRiskLabel(goal),
                hasDurableResearch), acceptanceManifestPromptPath));
        if (!string.IsNullOrWhiteSpace(contextDirectory))
            CriteriaSelfCheckPromptContext.Externalize(roleLines, task.RequiredRole, includeReference: false);
        roleLines.Add(string.Empty);
        segments.Add(TaskBriefSegment.Fixed(roleLines));

        if (usesFileAccessContext &&
            task.RequiredRole is AgentRole.Planner or AgentRole.Developer or AgentRole.Tester or AgentRole.Reviewer)
        {
            AddDurableArtifactBriefSegment(
                segments,
                contextDirectory!,
                "research-notes.md",
                "Durable Research Notes",
                collapsePriority: 10);
        }

        if (usesFileAccessContext &&
            task.RequiredRole is AgentRole.Developer or AgentRole.Tester or AgentRole.Reviewer)
        {
            AddDurableArtifactBriefSegment(
                segments,
                contextDirectory!,
                "planner-plan.md",
                "Durable Planner Plan",
                collapsePriority: 20);
        }

        var reviewerConvergenceScope = BuildReviewerConvergenceScopeBriefBlock(
            goal,
            task,
            reviewerScopeChangedFiles,
            reviewerRoundTouchedAnchors,
            reviewerRoundTouchProofDiagnostic);
        if (reviewerConvergenceScope.Count > 0)
        {
            segments.Add(TaskBriefSegment.Fixed(reviewerConvergenceScope));
        }

        var practiceLines = EngineeringPracticePromptRenderer.RenderBriefSection(
            task.RequiredRole,
            MatchEngineeringPractices(goal, task, reviewerScopeChangedFiles));
        if (practiceLines.Count > 0)
        {
            segments.Add(TaskBriefSegment.Fixed(practiceLines));
        }

        var standingRuleLines = WorkerStandingRules.RenderBriefSection(task.RequiredRole, contextDirectory);
        if (standingRuleLines.Count > 0)
        {
            segments.Add(TaskBriefSegment.Fixed(standingRuleLines));
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
            segments.Add(TaskBriefSegment.Projected(
                "task/verification-plan.md",
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
                collapsePriority: 50));
        }

        if (task.CriterionRetryFeedback.Count > 0)
        {
            var feedbackLines = new List<string>
            {
                "## Unmet acceptance criteria from the prior attempt - fix these:"
            };
            feedbackLines.AddRange(task.CriterionRetryFeedback.Select(item => $"- {item}"));
            feedbackLines.Add(string.Empty);
            segments.Add(TaskBriefSegment.Projected("task/criterion-retry-feedback.json", feedbackLines));
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

        if (resolvedInput.Count > 0)
        {
            var resolvedInputLines = new List<string>
            {
                "## Resolved Human Input",
                "Apply these operator decisions. Do not ask the same question again unless the underlying blocker has materially changed."
            };
            foreach (var request in resolvedInput)
            {
                var answer = request.AuthoritativeAnswer!;
                var answeredUnder = answer.BriefVersion is { } briefVersion
                    ? $"brief v{briefVersion}"
                    : "an unknown brief version";
                resolvedInputLines.Add(
                    $"- {request.Id}: {PromptContextFormatter.TrimPromptBlock(request.Question)} " +
                    $"→ {PromptContextFormatter.TrimPromptBlock(answer.Text)} " +
                    $"(answered under {answeredUnder}; current brief v{goal.AuthoritativeBrief.Version})");
            }
            resolvedInputLines.Add(string.Empty);
            segments.Add(TaskBriefSegment.Fixed(resolvedInputLines));
        }

        if (prerequisiteEvidenceSection.Lines.Count > 0)
        {
            // Fixed, never Projected: a collapsed pointer would drop below the required floor of
            // request id, summary, and evidence paths. Fixed segments are also never collapsed by
            // ApplyTaskBriefBudget, so this section can neither displace nor be displaced by a
            // required section.
            segments.Add(TaskBriefSegment.Fixed(prerequisiteEvidenceSection.Lines));
        }
        segments.AddRange(ClarificationsAndRulingsDigest.RenderSegments(GoalScopedClarificationAnswersQuery.Select(HumanInputRequests, goal, taskId), goal.AuthoritativeBrief.Version));
        var frozenFactRulingLines = FrozenFactRulingBriefSection.Render(HumanInputRequests, goalId, task.RequiredRole);
        if (frozenFactRulingLines.Count > 0)
            segments.Add(TaskBriefSegment.Fixed(frozenFactRulingLines));

        var changedExistingTestLines = ChangedExistingTestsBriefSection.Render(task.RequiredRole, changedExistingTests, changedExistingTestsDiagnostic);
        if (changedExistingTestLines.Count > 0)
            segments.Add(TaskBriefSegment.Fixed(changedExistingTestLines));

        if (task.LastExecution is not null)
        {
            segments.Add(TaskBriefSegment.Projected(
                "task/last-model-output.txt",
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
                collapsePriority: 30));
        }

        if (task.LastDispatch is not null)
        {
            segments.Add(TaskBriefSegment.Projected(
                "task/last-dispatch.json",
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
                collapsePriority: 20));
        }

        if (task.LastVerification is not null)
        {
            segments.Add(TaskBriefSegment.Fixed(
                [
                    "## Last Verification",
                    $"Command: {task.LastVerification.Command}",
                    $"Exit code: {task.LastVerification.ExitCode}",
                    $"Verification history count: {task.VerificationHistory.Count}"
                ]));
            segments.Add(TaskBriefSegment.Projected(
                "task/last-verification/stdout",
                [
                    $"Stdout: {PromptContextFormatter.TrimEvidenceBlock(task.LastVerification.StandardOutput, complexity)}",
                ],
                [
                    "Read current-task.md in the context directory for stdout; inline verification output collapsed to stay under the role file-access prompt budget.",
                ],
                collapsePriority: 40));
            var stderrLines = new[]
            {
                $"Stderr: {PromptContextFormatter.TrimEvidenceBlock(task.LastVerification.StandardError, complexity)}"
            };
            if (task.LastVerification.AuthoritativeStandardError is not null)
            {
                segments.Add(TaskBriefSegment.Projected(
                    "task/last-verification/stderr",
                    stderrLines,
                    [
                        "Read current-task.md in the context directory for stderr; inline verification error collapsed to stay under the role file-access prompt budget."
                    ],
                    collapsePriority: 40));
            }
            else
            {
                segments.Add(TaskBriefSegment.Fixed(stderrLines));
            }

            segments.Add(TaskBriefSegment.Fixed([string.Empty]));
        }

        segments.Add(TaskBriefSegment.Fixed(BuildNegativeControlRevertSetBriefBlock(goal, task)));
        var reviewerExecutedTestEvidence = ReviewerEvidenceBriefSection.BuildReviewerExecutedTestEvidenceBriefBlock(goal, task);
        if (reviewerExecutedTestEvidence.Count > 0)
        {
            segments.Add(TaskBriefSegment.Fixed(reviewerExecutedTestEvidence));
        }

        var sliceBatchParentReview = ReviewerEvidenceBriefSection.BuildSliceBatchParentReviewBriefBlock(goal, task, _goals.Values);
        if (sliceBatchParentReview.Count > 0)
        {
            segments.Add(TaskBriefSegment.Fixed(sliceBatchParentReview));
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
            if (task.RequiredRole is AgentRole.Developer or AgentRole.Tester or AgentRole.Reviewer)
            {
                segments.Add(TaskBriefSegment.Projected(
                    "context/prior-task-evidence.md",
                    priorEvidenceLines,
                    [
                        "## Prior Task Evidence",
                        "Read prior-task-summaries.md first for compact prior files, behavior, verification, risks, and model fit. " +
                        "When a completed Planner is present, read its complete Durable Planner Plan in prior-task-evidence.md before implementation; otherwise open fuller evidence only when needed.",
                        string.Empty
                    ],
                    collapsePriority: 10));
            }
            else
            {
                segments.Add(TaskBriefSegment.Fixed(priorEvidenceLines));
            }
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

            segments.Add(TaskBriefSegment.Projected(
                "goal/timeline.json",
                timelineLines,
                [
                    "## Recent Timeline",
                    "Read digest.md and current-task.md in the context directory for current status; inline timeline collapsed to stay under the role file-access prompt budget."
                ],
                collapsePriority: 0));
        }

        var humanInputRequests = HumanInputRequests
            .Where(request => request.GoalId == goalId)
            .ToArray();
        var clarificationAnswerHistory = goal.RefinedSpec?.ClarificationAnswerHistory ?? [];
        var roleVisibleSegments = segments
            .Select(segment => segment with { RoleVisibility = [task.RequiredRole] })
            .ToArray();
        var retractedSegments = ApplyHumanInputRetractions(
            roleVisibleSegments,
            humanInputRequests,
            clarificationAnswerHistory);
        var selection = ApplyTaskBriefBudget(
            retractedSegments,
            task.RequiredRole,
            usesFileAccessContext,
            measureWithTypedSourceBoundaries);

        return new TaskBriefSource(
            goal.Id,
            task.Id,
            task.RequiredRole,
            $"{task.RequiredRole}: {PromptContextFormatter.TrimPromptTitle(task.Description)}",
            selection.Segments,
            selection.Decisions,
            prerequisiteEvidenceSection.TrimmedRequestIds);
    }

    private static IReadOnlyList<ReviewFinding> ApplyHumanInputSupersedeFindingResolutions(
        Goal goal,
        IReadOnlyList<ReviewFinding> findings,
        IReadOnlyDictionary<string, DateTimeOffset> latestFindingOccurrences)
    {
        const string marker = "resolvedStableIds=";
        var resolvedIds = goal.Timeline
            .Where(evt => evt.Kind == ProgressKind.HumanInputSuperseded)
            .SelectMany(evt =>
            {
                if (evt.HumanInputSuperseded is { } payload)
                {
                    return payload.ResolvedStableIds
                        .Select(stableId => (StableId: stableId, evt.OccurredAt));
                }

                var start = evt.Message.IndexOf(marker, StringComparison.Ordinal);
                if (start < 0)
                {
                    return [];
                }

                start += marker.Length;
                var end = evt.Message.IndexOf(' ', start);
                var value = end < 0 ? evt.Message[start..] : evt.Message[start..end];
                return value.Equals("none", StringComparison.Ordinal)
                    ? []
                    : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(stableId => (StableId: stableId, evt.OccurredAt));
            })
            .Where(resolution =>
                !latestFindingOccurrences.TryGetValue(resolution.StableId, out var latestOccurrence) ||
                resolution.OccurredAt >= latestOccurrence)
            .Select(resolution => resolution.StableId)
            .ToHashSet(StringComparer.Ordinal);
        return resolvedIds.Count == 0
            ? findings
            : findings.Where(finding => !resolvedIds.Contains(finding.StableId)).ToArray();
    }

    private static IReadOnlyList<TaskBriefSegment> ApplyHumanInputRetractions(
        IReadOnlyList<TaskBriefSegment> segments,
        IReadOnlyList<HumanInputRequest> requests,
        IReadOnlyList<HumanInputAnswerRecord> clarificationAnswerHistory)
    {
        IReadOnlyList<string> Apply(IReadOnlyList<string> lines)
        {
            return lines
                .Select(line => HumanInputRetractionPolicy.Apply(
                    line,
                    requests,
                    clarificationAnswerHistory))
                .ToArray();
        }

        return segments
            .Select(segment => segment with
            {
                Lines = Apply(segment.Lines),
                CollapsedLines = segment.CollapsedLines is null
                    ? null
                    : Apply(segment.CollapsedLines)
            })
            .ToArray();
    }

    public static int TaskBriefCharacterBudget(AgentRole role, bool usesFileAccessContext) =>
        PromptContextFormatter.TaskBriefCharacterBudget(role, usesFileAccessContext);

    private static int TimelineEventBudget(TaskComplexity complexity)
    {
        return complexity == TaskComplexity.Complex ? 20 : 8;
    }

    private static IReadOnlyList<string> BuildTaskBriefInstructions(
        TaskComplexity complexity,
        string? modelFitTarget,
        AgentRole role,
        bool hasDurableResearch,
        string? contextDirectory)
    {
        var modelFitInstruction = BuildModelFitInstruction(modelFitTarget);
        if (complexity == TaskComplexity.Simple)
        {
            var simpleLines = new List<string>
            {
                "Complete this SDLC task. Report only changed files, verification evidence, blockers, or HUMAN_INPUT: <question>.",
                "Use repository-local verification when practical; do not claim completion without evidence.",
                "Do not stage or commit changes; the orchestrator commits verified Developer/Tester diffs.",
                "Host platform is Windows. Prefer PowerShell or cmd syntax for shell commands. POSIX-only utilities such as printf may be unavailable."
            };
            simpleLines.Insert(
                2,
                role == AgentRole.Planner && hasDurableResearch
                    ? "Use the complete Durable Research Notes supplied in the context package; synthesize from them and do not run another broad repository source survey."
                    : "When surveying files, start with source-survey.md in the context directory when present, or use rg excluding **/bin/**, **/obj/**, .scratch, and prototype state.");
            simpleLines.AddRange(AgentOutputDirectives.WorkerResultTemplateLinesForRole(role, contextDirectory));
            simpleLines.Add(modelFitInstruction);
            return simpleLines;
        }

        var complexLines = new List<string>
        {
            "Complete this task as the assigned SDLC role. Report concrete changes, verification evidence, blockers, and any human input required.",
            "If you cannot proceed without operator input, write a line that starts with HUMAN_INPUT: followed by the exact question.",
            "Use repository-local commands for evidence when possible. Do not mark work complete without verification.",
            "Avoid generic status summaries. Tie conclusions to repository files, command output, or cited source material.",
            "Do not stage or commit changes; the orchestrator commits verified Developer/Tester diffs.",
            "Host platform is Windows. Prefer PowerShell or cmd syntax for shell commands. POSIX-only utilities such as printf may be unavailable."
        };
        complexLines.InsertRange(
            4,
            role == AgentRole.Planner && hasDurableResearch
                ?
                [
                    "Use the complete Durable Research Notes supplied in the context package; synthesize from them and do not run another broad repository source survey."
                ]
                :
                [
                    "When surveying files, exclude generated output such as **/bin/**, **/obj/**, .scratch, and prototype state unless the task explicitly concerns those artifacts.",
                    "Prefer source-survey.md in the context directory as the starting repository map before broad recursive file reads."
                ]);
        complexLines.AddRange(AgentOutputDirectives.WorkerResultTemplateLinesForRole(role, contextDirectory));
        complexLines.Add(modelFitInstruction);
        return complexLines;
    }

    private static void AddDurableArtifactBriefSegment(
        ICollection<TaskBriefSegment> segments,
        string contextDirectory,
        string fileName,
        string heading,
        int collapsePriority)
    {
        var path = Path.Combine(contextDirectory, fileName);
        if (!File.Exists(path))
        {
            return;
        }

        var text = File.ReadAllText(path, Encoding.UTF8);
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        segments.Add(TaskBriefSegment.Projected(
            $"context/{fileName}",
            [
                $"## {heading}",
                $"Artifact identity: {fileName}; sha256:{hash}",
                text,
                string.Empty
            ],
            [
                $"## {heading}",
                $"Read {fileName} in the context directory for the complete artifact; sha256:{hash}. Inline copy collapsed to stay under the role file-access prompt budget.",
                string.Empty
            ],
            collapsePriority));
    }

    private static IReadOnlyList<string> BuildAcceptanceFailureBriefBlock(Goal goal, TaskSpec task, string? workingDirectory)
    {
        if (goal.RetainedAcceptanceFailure is not { } failure)
        {
            return [];
        }

        var retryEvent = goal.LatestTaskRetryAfterAcceptanceFailure(task.Id);
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
        };
        if (!string.IsNullOrWhiteSpace(failure.BaselineAttestation))
        {
            lines.Add(
                $"Clean-test baseline: main {FormatAcceptanceSha(failure.MainHeadSha)} {failure.BaselineAttestation}.");
            lines.Add(string.Empty);
        }

        lines.Add("Failing tests/checks:");
        lines.AddRange(failure.FailedChecks.Select(check =>
        {
            var attribution = failure.CheckAttributions?.FirstOrDefault(item =>
                item.CheckName.Equals(check, StringComparison.Ordinal));
            return attribution is null
                ? $"- {check}"
                : $"- {check} [{FormatAcceptanceFailureOrigin(attribution.Origin)}: {attribution.Evidence}]";
        }));
        var attributions = failure.CheckAttributions;
        if (attributions is { Count: > 0 } &&
            failure.FailedChecks.All(check => attributions.Any(item =>
                item.CheckName.Equals(check, StringComparison.Ordinal) &&
                item.Origin == AcceptanceFailureOrigin.Inherited &&
                item.Cause == AcceptanceFailureCause.EnvironmentalApparatus)))
        {
            lines.Add("Do NOT attempt to fix these; they are not attributable to your diff. Report them and address only the introduced/unattributed checks.");
        }
        else if (attributions is { Count: > 0 } && failure.FailedChecks.Any(check => attributions.Any(item =>
                     item.CheckName.Equals(check, StringComparison.Ordinal) &&
                     item.Origin == AcceptanceFailureOrigin.Unattributed &&
                     item.Cause == AcceptanceFailureCause.NotClassified)))
        {
            lines.Add("One or more failure origins remain unproven. Report them and request exact baseline/run evidence; do not assume they are introduced or inherited or make a blind fix.");
        }

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

    private static string FormatAcceptanceFailureOrigin(AcceptanceFailureOrigin origin) =>
        origin switch
        {
            AcceptanceFailureOrigin.Inherited => "inherited",
            AcceptanceFailureOrigin.Introduced => "introduced",
            _ => "unattributed"
        };

    private static string FormatAcceptanceSha(string? sha)
    {
        var normalized = sha?.Trim();
        return string.IsNullOrWhiteSpace(normalized)
            ? "unknown"
            : normalized[..Math.Min(8, normalized.Length)];
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

        var relevantRetryEvents = SelectUpstreamRetryEvents(goal, task, retryEvents);
        var recipientIndex = FindTaskIndex(goal, task.Id);
        var feedbackEvents = goal.Timeline
            .Where(evt =>
                IsAccumulatedRetryFeedbackEvent(evt) &&
                (evt.Kind != ProgressKind.FindingEvidenceRunRecorded || evt.TaskId == task.Id) &&
                (!IsAccumulatedRetryRoundEvent(evt) ||
                 IsUpstreamRetryEvent(goal, task.Id, recipientIndex, evt)))
            .OrderByDescending(evt => evt.OccurredAt)
            .ThenByDescending(evt => (int)evt.Kind)
            .ToList();
        if (feedbackEvents.Count == 0)
        {
            return [];
        }

        var latestRetry = relevantRetryEvents.LastOrDefault();
        var priorOutcomeEvent = latestRetry is not { TaskId: { } latestRetriedTaskId }
            ? null
            : goal.Timeline
                .Where(evt =>
                    evt.TaskId == latestRetriedTaskId &&
                    evt.OccurredAt <= latestRetry.OccurredAt &&
                    IsRetryPriorOutcomeEvent(evt))
                .OrderByDescending(evt => evt.OccurredAt)
                .FirstOrDefault();

        var lines = new List<string>
        {
            "<!-- ACCUMULATED_RETRY_FEEDBACK_START -->",
            "## Accumulated retry/review feedback",
            $"Operational entries are newest first and capped at {AccumulatedRetryFeedbackMaxEntries} entries and {AccumulatedRetryFeedbackMaxChars} chars. The latest canonical actionable findings and their evidence provenance are emitted once below for every retry-capable provider.",
            "Use this as the current correction context before relying on original task wording, prior task history, branch evidence, or context digests.",
        };

        if (latestRetry is not null)
        {
            lines.Add($"Most recent retry: Retry {relevantRetryEvents.Count} of {relevantRetryEvents.Count}; {latestRetry.OccurredAt:u}; {DescribeTimelineTask(goal, latestRetry)}.");
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

        var operationalSectionChars = string.Join(Environment.NewLine, lines).Length;
        var structuredFindings = goal.Tasks
            .Where(candidate => candidate.RequiredRole is AgentRole.Reviewer or AgentRole.Tester)
            .SelectMany(candidate => candidate.VerificationHistory.SelectMany(verification =>
                (verification.MergedReviewFindings ?? []).Select(finding => new
                {
                    candidate.RequiredRole,
                    OwningTask = candidate,
                    verification.CompletedAt,
                    Finding = finding
                })))
            .GroupBy(
                item => $"{item.RequiredRole}:{item.Finding.StableId}",
                StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(item => item.CompletedAt)
                .First())
            .Where(item => item.Finding.State == ReviewFindingState.Open)
            .OrderBy(item => item.RequiredRole)
            .ThenBy(item => item.Finding.StableId, StringComparer.Ordinal)
            .ToArray();
        if (structuredFindings.Length > 0)
        {
            lines.Add("## Structured actionable findings (not subject to operational retry caps)");
            lines.Add($"finding_count: {structuredFindings.Length}; latest canonical state is emitted once and operational retry entries are budgeted separately.");
            foreach (var item in structuredFindings)
            {
                lines.Add(
                    $"- role={item.RequiredRole}; stable_id={item.Finding.StableId}; severity={item.Finding.Severity}; " +
                    $"location={item.Finding.Location}; description={PromptContextFormatter.TrimPromptBlock(item.Finding.Description)}");
                if (item.Finding.EvidenceRequest is { } evidenceRequest)
                {
                    var selection = string.Join(",", (evidenceRequest.Selections ?? []).Select(value =>
                        $"{value.TestProject}:{value.TestClass}"));
                    var outcome = item.Finding.EvidenceOutcome;
                    var disposition = outcome is null
                        ? "pending"
                        : outcome.Honoured ? "honoured" : "not-honoured";
                    var reason = outcome?.Reason is { } reasonCode
                        ? $"; reason={FindingEvidenceNotHonouredReasonJsonConverter.ToWireValue(reasonCode)}"
                        : outcome?.ResultReason is { } resultReason
                            ? $"; reason={FindingEvidenceOutcomeReasonJsonConverter.ToWireValue(resultReason)}"
                            : string.Empty;
                    // state= and candidate_sha= are the point-of-decision distinction: verdict=
                    // alone cannot separate "no run has happened yet" from "this candidate was
                    // measured". A missing or old-candidate run reads pending-execution, never a pass.
                    var briefCandidateSha = string.IsNullOrWhiteSpace(targetHeadCommit)
                        ? null
                        : targetHeadCommit.Trim();
                    var executionState = FindingEvidenceExecutionClassifier.Classify(
                        item.OwningTask, item.Finding, briefCandidateSha);
                    lines.Add(
                        $"  evidence_index: selection={selection}; verdict={disposition}; " +
                        $"receipt={outcome?.ReceiptId ?? "none"}{reason}; " +
                        $"state={FindingEvidenceExecutionClassifier.ToWireValue(executionState)}; " +
                        $"candidate_sha={briefCandidateSha ?? FindingEvidenceExecutionClassifier.UnavailableCandidateSha}");

                    if (task.RequiredRole == item.RequiredRole && outcome?.ReceiptId is { } receiptId)
                    {
                        var receipt = goal.Tasks
                            .Where(candidate => candidate.RequiredRole == item.RequiredRole)
                            .SelectMany(candidate => candidate.VerificationHistory)
                            .OrderByDescending(verification => verification.CompletedAt)
                            .SelectMany(verification => verification.FindingEvidenceReceipts ?? [])
                            .FirstOrDefault(candidate => string.Equals(candidate.ReceiptId, receiptId, StringComparison.Ordinal));
                        if (receipt is not null)
                        {
                            lines.Add(
                                $"  evidence_receipt: id={receipt.ReceiptId}; candidate_sha={receipt.CandidateSha}; " +
                                $"accepted={receipt.Accepted}; passed={receipt.Passed}; summary={PromptContextFormatter.TrimPromptBlock(receipt.Summary)}");
                            foreach (var arm in receipt.Arms ?? [])
                            {
                                lines.Add(
                                    $"    evidence_arm: arm={arm.Arm.ToString().ToLowerInvariant()}; sha={arm.Sha}; " +
                                    $"disposition={arm.Disposition.ToString().ToLowerInvariant()}; accepted={arm.Accepted}; " +
                                    $"passed={arm.Passed}; summary={PromptContextFormatter.TrimPromptBlock(arm.Summary)}");
                                if (arm.FailingTestIdentities is { Count: > 0 })
                                {
                                    lines.Add($"      failing_tests: {string.Join(", ", arm.FailingTestIdentities)}");
                                }
                            }
                        }
                    }
                    else if (task.RequiredRole == item.RequiredRole && outcome is { Honoured: false })
                    {
                        lines.Add($"  evidence_not_honoured: detail={PromptContextFormatter.TrimPromptBlock(outcome.Detail ?? "none")}");
                    }
                }
            }
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

            var status = DescribeAccumulatedRetryFeedbackStatus(retryEvents, relevantRetryEvents, feedbackEvents, evt);
            var retryDescriptor = relevantRetryEvents.Count == 0
                ? "Retry n/a"
                : $"Retry {RetryOrdinalAt(relevantRetryEvents, evt.OccurredAt)} of {relevantRetryEvents.Count}";
            var line = $"- [{status}] {retryDescriptor}; {evt.OccurredAt:u}; {DescribeTimelineTask(goal, evt)}; {evt.Kind}: {PromptContextFormatter.TrimPromptBlock(message)}";
            if (emittedCount >= AccumulatedRetryFeedbackMaxEntries ||
                operationalSectionChars + line.Length + Environment.NewLine.Length > AccumulatedRetryFeedbackMaxChars)
            {
                omittedCount = feedbackEvents.Count - index;
                break;
            }

            lines.Add(line);
            operationalSectionChars += line.Length + Environment.NewLine.Length;
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
            lines.Add(correction.IsWaiver
                ? $"  Status: WAIVED — {PromptContextFormatter.TrimPromptBlock(correction.WaiverReason!)}"
                : $"  Effective criterion: {PromptContextFormatter.TrimPromptBlock(correction.Correction)}");
            if (correction.IsWaiver && correction.Dispositions is { Count: > 0 })
            {
                var criteria = goal.RefinedSpec?.AcceptanceCriteria ?? [];
                foreach (var item in correction.Dispositions
                    .Select((disposition, supplyIndex) => (disposition, supplyIndex))
                    .Where(item => !string.IsNullOrWhiteSpace(item.disposition.Disposition))
                    .OrderBy(item => FindCriterionIndex(criteria, item.disposition.Criterion))
                    .ThenBy(item => item.supplyIndex))
                {
                    var criterion = TrimDispositionField(item.disposition.Criterion);
                    var disposition = TrimDispositionField(item.disposition.Disposition);
                    lines.Add($"  Disposition: {criterion} -> {disposition}");
                }
            }
            lines.Add($"  Provenance: {correction.Actor}; {correction.RecordedAt:u}; {correction.SourceKind}; {taskReference}.");
        }

        lines.Add("<!-- EFFECTIVE_ACCEPTANCE_CRITERIA_CORRECTIONS_END -->");
        lines.Add(string.Empty);
        return lines;

        static int FindCriterionIndex(IReadOnlyList<string> criteria, string criterion)
        {
            for (var index = 0; index < criteria.Count; index++)
            {
                if (string.Equals(
                    criteria[index].Trim(),
                    criterion.Trim(),
                    StringComparison.OrdinalIgnoreCase))
                {
                    return index;
                }
            }

            return int.MaxValue;
        }

        static string TrimDispositionField(string value) =>
            PromptContextFormatter.TrimPromptBlock(value.ReplaceLineEndings(" ")).ReplaceLineEndings(" ");
    }

    private static string BuildModelFitInstruction(string? modelFitTarget)
    {
        var target = string.IsNullOrWhiteSpace(modelFitTarget)
            ? "<provider>/<model or launcher>"
            : modelFitTarget.Trim();
        return $"Include a final model-selection note: {ModelFitEvidence.BuildNoteTemplate(target)}.";
    }

    private static IReadOnlyList<string> BuildReviewerConvergenceScopeBriefBlock(
        Goal goal,
        TaskSpec task,
        IReadOnlyList<string>? changedFiles,
        IReadOnlyList<ReviewFindingLocation>? roundTouchedAnchors,
        string? roundTouchProofDiagnostic)
    {
        if (task.RequiredRole is not (AgentRole.Reviewer or AgentRole.Tester))
        {
            return [];
        }

        var state = ReplayStoredReviewFindingRounds(goal, task.RequiredRole, out _);

        if (state.Count == 0)
        {
            return [];
        }

        var open = state.Where(finding => finding.State == ReviewFindingState.Open).ToArray();
        var resolved = state.Where(finding => finding.State == ReviewFindingState.Resolved).ToArray();
        var lines = new List<string>
        {
            "## Review Convergence Scope (structured source of truth)",
            $"OPEN_ACTIVE_RECHECK count={open.Length}"
        };
        foreach (var finding in open)
        {
            lines.Add($"- {finding.StableId} | severity={finding.Severity.ToString().ToLowerInvariant()} | {finding.Location} | {finding.Description}");
        }

        lines.Add($"RESOLVED_CARRIED count={resolved.Length}");
        foreach (var finding in resolved)
        {
            lines.Add($"- {finding.StableId} | severity={finding.Severity.ToString().ToLowerInvariant()} | {finding.Location} | carry forward; do not re-review unless this exact anchor was touched.");
        }

        lines.Add("ROUND_DIFF_TOUCHED_ANCHORS (system-derived; authoritative for regression reopening and identity relocation):");
        foreach (var anchor in roundTouchedAnchors ?? [])
        {
            lines.Add($"- {anchor}");
        }

        if (!string.IsNullOrWhiteSpace(roundTouchProofDiagnostic))
        {
            lines.Add($"ROUND_DIFF_TOUCH_PROOF_UNAVAILABLE: {roundTouchProofDiagnostic}");
        }

        lines.Add("GOAL_DIFF_CHANGED_FILES (context only; file membership does not prove a structural anchor was touched):");
        foreach (var file in changedFiles ?? [])
        {
            lines.Add($"- {file}");
        }

        lines.Add("Actively check OPEN_ACTIVE_RECHECK, RESOLVED_CARRIED anchors listed in ROUND_DIFF_TOUCHED_ANCHORS, and net-new code. The touched-anchor set also authorizes a persistent open finding to keep its stable ID at the defect's current location. Carry every other resolved finding forward as resolved.");
        lines.Add("Output contract: emit exactly one findings entry for every OPEN_ACTIVE_RECHECK stable_id (`resolved` with concrete closure evidence if fixed, otherwise `open`). Narrative does not update the convergence ledger; omission leaves the finding open.");
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

    private static bool IsAccumulatedRetryFeedbackEvent(ProgressEvent evt)
    {
        return IsAccumulatedRetryRoundEvent(evt) ||
               evt.Kind is (
                   ProgressKind.TaskSubscriptionLimitReviewAcknowledged or
                   ProgressKind.ReviewerEvidenceRequestReceived or
                   ProgressKind.ReviewerEvidenceRunRecorded or
                   ProgressKind.FindingEvidenceRequestRecorded or
                    ProgressKind.FindingEvidenceRunRecorded or
                    ProgressKind.TaskRetryFeedbackUpdated or
                    ProgressKind.FindingEvidenceSuppressed) ||
               (evt.Kind is ProgressKind.TaskNote or ProgressKind.OperatorTaskNote &&
                   IsAccumulatedRetryFeedbackTaskNote(evt.Message));
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
        IReadOnlyList<ProgressEvent> relevantRetryEvents,
        IReadOnlyList<ProgressEvent> feedbackEvents,
        ProgressEvent feedbackEvent)
    {
        if (TryFindResolutionEvent(feedbackEvents, feedbackEvent, out var resolutionEvent))
        {
            return $"resolved-in-round-{RetryOrdinalAt(relevantRetryEvents, resolutionEvent.OccurredAt)}";
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

        return SharedJsonlFile.ReadAllLines(journalPath)
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

}
