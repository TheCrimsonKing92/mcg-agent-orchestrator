namespace Mcg.AgentOrchestrator.Core;

public sealed partial class AgentOrchestratorKernel
{
    private const int ProviderConnectivityRetryLimit = 3;

    public void RecordTaskVerification(GoalId goalId, TaskId taskId, TaskVerificationRecord verification)
    {
        var goal = GetGoal(goalId);
        var task = goal.FindTask(taskId);
        verification = PrepareReviewFindingRecord(
            goal,
            task,
            AttachAuthoritativeReviewFindingContext(task, verification));
        task.RecordVerification(verification);

        var status = verification.Succeeded ? "passed" : "failed";
        Append(goal, taskId, ProgressKind.TaskVerificationRecorded, $"Verification {status} ({verification.ExitCode}): {verification.Command}");
        var outcome = DispatchFailureClassifier.Classify(task, verification);
        if (!string.IsNullOrWhiteSpace(outcome.ClassifierReceipt))
        {
            Append(goal, taskId, ProgressKind.TaskNote, outcome.ClassifierReceipt);
        }

        if (outcome.Kind == DispatchOutcomeKind.VerificationInconclusive)
        {
            ReportTaskProgress(
                goalId,
                taskId,
                WorkTaskStatus.Failed,
                $"Tester verification inconclusive; same Tester retry or operator escalation required. {outcome.EvidenceSummary}");
            return;
        }

        if (TryFailWorkerResultBlocker(goalId, task, verification, enforceFailureEvidenceRule: false))
        {
            return;
        }

        TryCompleteTaskWithPassingVerification(
            goal,
            task,
            BuildCompletionMessageWithAdvisoryBlocker(
                $"Task completed with passing verification: {verification.Command}",
                task,
                verification));
        RefreshGoalStatus(goal);
    }

    public void RecordDispatchExecutionResult(
        GoalId goalId,
        TaskId taskId,
        TaskVerificationRecord verification,
        ProviderFailureKind providerFailureKind = ProviderFailureKind.Unknown)
    {
        var goal = GetGoal(goalId);
        var task = goal.FindTask(taskId);

        if (task.LastDispatch is null)
        {
            throw new InvalidOperationException($"Task '{taskId}' has no dispatch to execute.");
        }

        if (!string.Equals(task.LastDispatch.Command, verification.Command, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Dispatch execution evidence command does not match the recorded dispatch command.");
        }

        if (!string.Equals(task.LastDispatch.WorkingDirectory, verification.WorkingDirectory, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Dispatch execution evidence working directory does not match the recorded dispatch working directory.");
        }

        if (task.LatestRetryAt is { } latestRetryAt &&
            (task.LastDispatch.DispatchedAt < latestRetryAt ||
             (task.LastDispatch.DispatchedAt == latestRetryAt &&
              task.LastVerification is null &&
              task.Status != WorkTaskStatus.Running)))
        {
            var staleDispatchAt = task.LastDispatch.DispatchedAt;
            task.ClearLastDispatch();
            task.ClearLastProcess();
            task.ClearLatestVerification();
            task.SetStatus(task.AssignedAgentId is null ? WorkTaskStatus.Pending : WorkTaskStatus.Assigned);
            Append(
                goal,
                taskId,
                ProgressKind.TaskNote,
                $"Ignored stale dispatch execution evidence from {staleDispatchAt:u}; latest retry was {latestRetryAt:u}.");
            RefreshGoalStatus(goal);
            return;
        }

        if (providerFailureKind != ProviderFailureKind.Unknown &&
            verification.ProviderFailureKind == ProviderFailureKind.Unknown)
        {
            verification = verification with { ProviderFailureKind = providerFailureKind };
        }

        if (task.LastVerification is { } latestVerification &&
            IsDuplicateDispatchExecutionResult(latestVerification, verification))
        {
            Append(
                goal,
                taskId,
                ProgressKind.TaskNote,
                $"Ignored duplicate dispatch execution evidence for already settled dispatch: {verification.Command}");
            RefreshGoalStatus(goal);
            return;
        }

        verification = PrepareReviewFindingRecord(
            goal,
            task,
            AttachAuthoritativeReviewFindingContext(task, verification));
        task.RecordVerification(verification);
        var completedRound = task.VerificationHistory.Count;

        var status = verification.Succeeded ? "passed" : "failed";
        Append(goal, taskId, ProgressKind.TaskVerificationRecorded, $"Dispatch execution {status} ({verification.ExitCode}): {verification.Command}");

        if (!verification.WorkerResultPresent)
        {
            Append(
                goal,
                taskId,
                ProgressKind.HumanInputRoundEvaluationInconclusive,
                $"kind=human-input-round-evaluation-inconclusive round={completedRound} " +
                $"worker_result_log={verification.StandardOutputPath ?? "unavailable"} reason=missing-or-unparseable-worker-result");
        }

        var parsedHumanInput = AgentOutputDirectives.ParseHumanInputRequest(verification.StandardOutput, task.RequiredRole);
        var humanInputQuestion = verification.HumanInputQuestion ?? parsedHumanInput.Directive?.Question;
        if (humanInputQuestion is not null)
        {
            var rawQuestion = humanInputQuestion;
            string? accompanyingBlocker = null;
            if (WorkerResultBlockers.TryFindBlocker(verification, out accompanyingBlocker))
            {
                humanInputQuestion += $"{Environment.NewLine}Accompanying WORKER_RESULT blocker evidence: {accompanyingBlocker}";
            }

            var requestResult = RequestHumanInputDeduplicated(
                goal.Id,
                task.Id,
                humanInputQuestion,
                questionFingerprint: verification.HumanInputQuestionFingerprint
                    ?? parsedHumanInput.Directive?.QuestionFingerprint
                    ?? HumanInputRequest.BuildQuestionFingerprint(rawQuestion),
                blockerFingerprint: verification.HumanInputBlockerFingerprint
                    ?? parsedHumanInput.Directive?.BlockerFingerprint
                    ?? HumanInputRequest.BuildWorkerResultBlockerFingerprint(
                        task.Id,
                        task.RequiredRole,
                        rawQuestion,
                        accompanyingBlocker),
                completedRound: verification.WorkerResultPresent ? completedRound : null,
                workerResultLogReference: verification.StandardOutputPath,
                recordDuplicateSuppression: verification.WorkerResultPresent);
            if (verification.WorkerResultPresent &&
                requestResult.WasSuppressedByAnswer &&
                WorkerResultBlockers.TryGetBlockersStatus(verification, out var blockersStatus) &&
                blockersStatus == WorkerResultBlockers.BlockersStatus.None)
            {
                RecordHumanInputWorkerResultContradiction(
                    goal.Id,
                    taskId,
                    requestResult.Request.Id,
                    completedRound,
                    verification.StandardOutputPath);
            }
            if (verification.WorkerResultPresent || !requestResult.WasSuppressedByAnswer)
            {
                return;
            }
        }

        var effectiveProviderFailureKind = verification.ProviderFailureKind;
        var outcome = DispatchFailureClassifier.Classify(task, verification, effectiveProviderFailureKind);
        if (verification.WorkerResultPresent &&
            task.RequiredRole is AgentRole.Planner or AgentRole.Researcher &&
            WorkerResultBlockers.TryFindPremiseInvalidEvidence(verification, out var premiseEvidence))
        {
            var question =
                $"{task.RequiredRole} reported premise-invalid: {premiseEvidence}. " +
                "Clarify, supersede, or abandon the goal before Developer dispatch.";
            RequestHumanInputDeduplicated(
                goal.Id,
                task.Id,
                question,
                blockerFingerprint: HumanInputRequest.BuildWorkerResultBlockerFingerprint(
                    task.Id,
                    task.RequiredRole,
                    question,
                    premiseEvidence),
                completedRound: completedRound,
                workerResultLogReference: verification.StandardOutputPath);
            return;
        }

        if (verification.WorkerResultPresent)
        {
            ResetAnsweredHumanInputSuppressionStreaks(goal.Id, task.Id);
        }

        if (outcome.Kind == DispatchOutcomeKind.VerificationInconclusive)
        {
            if (!string.IsNullOrWhiteSpace(outcome.ClassifierReceipt))
            {
                Append(goal, taskId, ProgressKind.TaskNote, outcome.ClassifierReceipt);
            }

            ReportTaskProgress(
                goalId,
                taskId,
                WorkTaskStatus.Failed,
                $"Tester verification inconclusive; same Tester retry or operator escalation required. {outcome.EvidenceSummary}");
            return;
        }

        if (!string.IsNullOrWhiteSpace(outcome.ClassifierReceipt))
        {
            Append(goal, taskId, ProgressKind.TaskNote, outcome.ClassifierReceipt);
        }

        if (TryFailWorkerResultBlocker(goalId, task, verification, enforceFailureEvidenceRule: true))
        {
            return;
        }

        if (!verification.Succeeded &&
            outcome.Kind == DispatchOutcomeKind.RecoverableSubscriptionLimit)
        {
            if (DispatchFailureClassifier.TryGetSubscriptionLimitRetryAfter(verification, out var retryAfter))
            {
                task.SetSubscriptionRetryAfter(retryAfter);
            }

            var recoverableLimitFailures = DispatchFailureClassifier.CountRecoverableSubscriptionLimitFailures(task);
            task.ClearLatestVerification();
            task.SetStatus(task.AssignedAgentId is null ? WorkTaskStatus.Pending : WorkTaskStatus.Assigned);
            var message = recoverableLimitFailures >= DispatchFailureClassifier.RecoverableSubscriptionLimitReviewThreshold
                ? $"Dispatch hit a recoverable subscription usage limit {recoverableLimitFailures} time(s); review model, profile, or timing before redispatch: {task.LastDispatch.Command}"
                : $"Dispatch hit a recoverable subscription usage limit; task is ready to retry later: {task.LastDispatch.Command}";
            Append(goal, taskId, ProgressKind.TaskRetried, message);
            RefreshGoalStatus(goal);
            return;
        }

        if (!verification.Succeeded &&
            outcome.Kind == DispatchOutcomeKind.ProviderConnectivity)
        {
            var connectivityFailures = DispatchFailureClassifier.CountRecoverableProviderConnectivityFailures(task);
            if (connectivityFailures <= ProviderConnectivityRetryLimit)
            {
                task.SetSubscriptionRetryAfter(_clock.UtcNow + BuildProviderConnectivityBackoff(connectivityFailures));
                task.RecordRetry(_clock.UtcNow);
                task.ClearLatestVerification();
                task.SetStatus(task.AssignedAgentId is null ? WorkTaskStatus.Pending : WorkTaskStatus.Assigned);
                Append(
                    goal,
                    taskId,
                    ProgressKind.TaskRetried,
                    $"Dispatch hit provider connectivity failure; task is ready to retry after bounded backoff (attempt {connectivityFailures}/{ProviderConnectivityRetryLimit}): {task.LastDispatch.Command}");
                RefreshGoalStatus(goal);
                return;
            }

            ReportTaskProgress(
                goalId,
                taskId,
                WorkTaskStatus.Failed,
                $"Dispatch provider connectivity failed after {ProviderConnectivityRetryLimit} automatic retry attempt(s); evidence: {outcome.EvidenceSummary}: {task.LastDispatch.Command}");
            return;
        }

        if (outcome.Kind == DispatchOutcomeKind.EmptyOutputFlake &&
            verification.ExitCode == 0 &&
            string.IsNullOrWhiteSpace(verification.StandardOutput) &&
            string.IsNullOrWhiteSpace(verification.StandardError))
        {
            ReportTaskProgress(
                goalId,
                taskId,
                WorkTaskStatus.Failed,
                $"Dispatch exited 0 with no output; verification cannot be confirmed: {task.LastDispatch.Command}");
            return;
        }

        if (!verification.Succeeded &&
            outcome.Kind == DispatchOutcomeKind.VerifiedSuccess)
        {
            Append(
                goal,
                taskId,
                ProgressKind.TaskNote,
                "Reconciled failed dispatch verification to Completed from structured WORKER_RESULT evidence and commit provenance.");
        }

        ReportTaskProgress(
            goalId,
            taskId,
            outcome.Kind == DispatchOutcomeKind.VerifiedSuccess ? WorkTaskStatus.Completed : WorkTaskStatus.Failed,
            outcome.Kind == DispatchOutcomeKind.VerifiedSuccess
                 ? BuildCompletionMessageWithAdvisoryBlocker(
                      $"Dispatch completed successfully: {task.LastDispatch.Command}",
                      task,
                      verification)
                : BuildDispatchFailureMessage(outcome, task, verification));
    }

    private static string BuildDispatchFailureMessage(
        DispatchOutcome outcome,
        TaskSpec task,
        TaskVerificationRecord verification)
    {
        var rule = TaskOutcomeClassifier.TryExtractRule(outcome.ClassifierReceipt) ??
            TaskOutcomeRules.UnknownFailure.Token;
        if (!string.Equals(
                rule,
                TaskOutcomeRules.RequiredFileChangeEvidenceMissing.Token,
                StringComparison.Ordinal))
        {
            var exitCode = outcome.ExitCode != 0 &&
                (string.Equals(rule, TaskOutcomeRules.SilentLaunchFailure.Token, StringComparison.Ordinal) ||
                 string.Equals(rule, TaskOutcomeRules.UnknownFailure.Token, StringComparison.Ordinal))
                    ? $"; exit code {outcome.ExitCode}"
                    : string.Empty;
            return $"Dispatch failed: rule={rule}{exitCode}: {task.LastDispatch!.Command}";
        }

        var dispatch = task.LastDispatch;
        var testsStatus = WorkerResultBlockers.TryGetTestsStatus(verification, out var parsedTestsStatus)
            ? parsedTestsStatus.ToString()
            : WorkerResultBlockers.TestsStatus.Unknown.ToString();
        var blockersStatus = WorkerResultBlockers.TryGetBlockersStatus(verification, out var parsedBlockersStatus)
            ? parsedBlockersStatus.ToString()
            : WorkerResultBlockers.BlockersStatus.Unknown.ToString();
        var missingChangeEvidence = string.Equals(
            rule,
            TaskOutcomeRules.RequiredFileChangeEvidenceMissing.Token,
            StringComparison.Ordinal);
        var hasRejectionDiagnostic = DispatchRejectionDiagnosticMarker.TryParse(
            verification.StandardError,
            out var verificationRecognized,
            out var rejectionReason,
            out var postDispatchCommits,
            out var changedPaths);
        var reason = hasRejectionDiagnostic ? rejectionReason : rule;
        var detail = BuildDispatchRejectionDetail(outcome, verification, missingChangeEvidence, verificationRecognized);
        var baselineHead = string.IsNullOrWhiteSpace(dispatch?.BaseCommit) ? "unknown" : dispatch.BaseCommit;

        return $"DISPATCH_REJECTED role={task.RequiredRole} task={task.Id.Value} " +
            $"worktree={QuoteDispatchDiagnosticValue(verification.WorkingDirectory)} " +
            $"dispatched_at={(dispatch?.DispatchedAt ?? verification.DispatchStartedAt ?? verification.CompletedAt):O} " +
            $"baseline_head={FormatDispatchDiagnosticAtom(baselineHead)} " +
            $"post_dispatch_commits={(hasRejectionDiagnostic ? postDispatchCommits.ToString() : "unknown")} " +
            $"changed_paths={FormatDispatchDiagnosticAtom(hasRejectionDiagnostic ? changedPaths : "unknown")} " +
            $"tests_status={testsStatus} blockers={blockersStatus} " +
            $"verification_recognized={(hasRejectionDiagnostic ? verificationRecognized.ToString().ToLowerInvariant() : "unknown")} " +
            $"reason={reason} detail={QuoteDispatchDiagnosticValue(detail)}";
    }

    private static string BuildDispatchRejectionDetail(
        DispatchOutcome outcome,
        TaskVerificationRecord verification,
        bool missingChangeEvidence,
        bool verificationRecognized)
    {
        if (!missingChangeEvidence)
        {
            return string.IsNullOrWhiteSpace(outcome.EvidenceSummary)
                ? "dispatch outcome was rejected"
                : outcome.EvidenceSummary;
        }

        if (verificationRecognized)
        {
            return "recognized verification evidence but no relevant post-dispatch file change was recorded";
        }

        return WorkerResultBlockers.TryFindTests(verification, out var tests)
            ? $"{tests} matched no accepted verification pattern"
            : "no structured tests value or accepted verification pattern was present";
    }

    private static string FormatDispatchDiagnosticAtom(string value) =>
        value.All(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.' or '/' or ',')
            ? value
            : QuoteDispatchDiagnosticValue(value);

    private static string QuoteDispatchDiagnosticValue(string value) =>
        $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal)}\"";

    private static TimeSpan BuildProviderConnectivityBackoff(int attempt)
    {
        return TimeSpan.FromMinutes(Math.Clamp(attempt, 1, ProviderConnectivityRetryLimit));
    }

    private static bool IsDuplicateDispatchExecutionResult(
        TaskVerificationRecord latestVerification,
        TaskVerificationRecord verification)
    {
        return string.Equals(latestVerification.Command, verification.Command, StringComparison.Ordinal) &&
            string.Equals(latestVerification.WorkingDirectory, verification.WorkingDirectory, StringComparison.OrdinalIgnoreCase) &&
            latestVerification.ExitCode == verification.ExitCode &&
            string.Equals(latestVerification.StandardOutput, verification.StandardOutput, StringComparison.Ordinal) &&
            string.Equals(latestVerification.StandardError, verification.StandardError, StringComparison.Ordinal) &&
            latestVerification.CompletedAt == verification.CompletedAt &&
            latestVerification.ProviderFailureKind == verification.ProviderFailureKind;
    }

    private bool TryFailWorkerResultBlocker(
        GoalId goalId,
        TaskSpec task,
        TaskVerificationRecord verification,
        bool enforceFailureEvidenceRule)
    {
        var goal = GetGoal(goalId);
        string? nonPassingCriteriaDiagnostic = null;
        if (verification.WorkerResultPresent &&
            task.RequiredRole == AgentRole.Reviewer &&
            WorkerResultBlockers.TryFindBlockedAtCapVerdict(verification, out _) &&
            task.LastDispatch?.ReviewRetryCap is not { IsAtCap: true })
        {
            ReportTaskProgress(
                goalId,
                task.Id,
                WorkTaskStatus.Failed,
                "Reviewer WORKER_RESULT verdict rejected: blocked-at-cap is only valid when the system-owned dispatch receipt is at the configured review-retry cap.");
            return true;
        }

        if (verification.ReviewFindingContractViolation is { } violation)
        {
            var startedAt = task.LastProcess?.StartedAt ??
                task.LastDispatch?.DispatchedAt ??
                verification.CompletedAt;
            var reviewerWallMilliseconds = Math.Max(
                0,
                (long)(verification.CompletedAt - startedAt).TotalMilliseconds);
            Append(
                goal,
                task.Id,
                ProgressKind.ReviewFindingContractViolationRecorded,
                $"Review finding contract violation recorded: code={violation.Code}; " +
                $"prior_stable_id={violation.PriorStableId ?? "none"}; " +
                $"submitted_stable_id={violation.SubmittedStableId ?? "none"}; " +
                $"prior_location={violation.PriorLocation?.ToString() ?? "none"}; " +
                $"submitted_location={violation.SubmittedLocation?.ToString() ?? "none"}; " +
                $"reviewer_wall_ms={reviewerWallMilliseconds}.");

            if (task.RequiredRole == AgentRole.Tester &&
                !(ReviewFindingConvergence.IsRejectedIdentityTransitionRound(violation) &&
                  verification.MergedReviewFindings is not null))
            {
                ReportTaskProgress(
                    goalId,
                    task.Id,
                    WorkTaskStatus.Failed,
                    $"Tester WORKER_RESULT structured findings invalid: {violation.Message}");
                return true;
            }
        }

        if (verification.WorkerResultPresent &&
            task.RequiredRole == AgentRole.Reviewer &&
            goal.RefinedSpec is { AcceptanceCriteria.Count: > 0 } refinedSpec)
        {
            if (!WorkerResultBlockers.TryFindCriteriaVerdicts(
                    verification,
                    out var criterionVerdicts,
                    out var criteriaDiagnostic))
            {
                ReportTaskProgress(
                    goalId,
                    task.Id,
                    WorkTaskStatus.Failed,
                    $"Reviewer WORKER_RESULT criteria attestation invalid: {criteriaDiagnostic}");
                return true;
            }

            var registeredVerdicts = criterionVerdicts
                .Where(item => item.CriterionIndex < refinedSpec.AcceptanceCriteria.Count)
                .ToArray();
            var actualIndices = registeredVerdicts
                .Select(item => item.CriterionIndex)
                .OrderBy(index => index)
                .ToArray();
            var expectedIndices = Enumerable.Range(0, refinedSpec.AcceptanceCriteria.Count).ToArray();
            if (!actualIndices.SequenceEqual(expectedIndices))
            {
                ReportTaskProgress(
                    goalId,
                    task.Id,
                    WorkTaskStatus.Failed,
                    "Reviewer WORKER_RESULT criteria attestation invalid: " +
                    $"expected every registered criterion_index {string.Join(", ", expectedIndices)} exactly once; " +
                    $"received registered indices {string.Join(", ", actualIndices)}.");
                return true;
            }

            var nonPassingVerdicts = registeredVerdicts
                .Where(item =>
                    !item.Verdict.Equals("met", StringComparison.Ordinal) &&
                    !goal.EffectiveAcceptanceCriteriaCorrections.Any(correction =>
                        correction.IsWaiver &&
                        string.Equals(
                            correction.SupersededCriterion,
                            refinedSpec.AcceptanceCriteria[item.CriterionIndex].Trim(),
                            StringComparison.OrdinalIgnoreCase)))
                .ToArray();
            if (nonPassingVerdicts.Length > 0 &&
                WorkerResultBlockers.TryFindPassVerdict(verification))
            {
                var details = string.Join(
                    "; ",
                    nonPassingVerdicts.Select(item =>
                        $"criterion_index={item.CriterionIndex} verdict={item.Verdict} evidence={item.Evidence}"));
                nonPassingCriteriaDiagnostic =
                    $"Reviewer WORKER_RESULT criteria attestation rejected: non-waived criteria are not passing: {details}.";
            }

            foreach (var extra in criterionVerdicts.Where(item =>
                         item.CriterionIndex >= refinedSpec.AcceptanceCriteria.Count))
            {
                Append(
                    goal,
                    task.Id,
                    ProgressKind.TaskNote,
                    $"Reviewer informational extra criterion attestation recorded: " +
                    $"criterion_index={extra.CriterionIndex}; verdict={extra.Verdict}; evidence={extra.Evidence}");
            }
        }

        IReadOnlyList<ReviewFinding> mergedFindings = verification.MergedReviewFindings ?? [];
        if (verification.WorkerResultPresent &&
            task.RequiredRole == AgentRole.Reviewer &&
            verification.MergedReviewFindings is null &&
            !TryBuildMergedReviewFindingState(
                goal,
                AgentRole.Reviewer,
                verification,
                task.LastDispatch?.ReviewRetryCap,
                out mergedFindings,
                out var findingDiagnostic,
                out _,
                out _,
                out _))
        {
            ReportTaskProgress(
                goalId,
                task.Id,
                WorkTaskStatus.Failed,
                $"Reviewer WORKER_RESULT structured findings invalid: {findingDiagnostic}");
            return true;
        }

        ReviewFindings.TryGetEffectiveOpenFindings(
            mergedFindings,
            goal.EffectiveAcceptanceCriteriaCorrections,
            out _,
            out var suppressedFindings);
        if (verification.WorkerResultPresent &&
            task.RequiredRole == AgentRole.Reviewer)
        {
            foreach (var item in suppressedFindings)
            {
                var capAudit = task.LastDispatch?.ReviewRetryCap is { IsAtCap: true } cap
                    ? $" review_cap={cap.Round}/{cap.StopRound}; candidate_sha={verification.ReviewedCommit ?? "missing"}; override=operator-waiver."
                    : string.Empty;
                Append(
                    goal,
                    task.Id,
                    ProgressKind.TaskNote,
                    $"Suppressed Reviewer structured finding matching operator criteria correction: stable_id={item.Finding.StableId}; finding: {item.Finding.Description}; superseded criterion: {item.Correction.SupersededCriterion}; correction recorded {item.Correction.RecordedAt:u} by {item.Correction.Actor}.{capAudit}");
            }
        }

        var openBlockingFindings = ReviewFindings.GetOpenBlockingFindings(
            mergedFindings,
            goal.EffectiveAcceptanceCriteriaCorrections);
        if (verification.WorkerResultPresent &&
            task.RequiredRole == AgentRole.Reviewer &&
            openBlockingFindings.Count > 0)
        {
            var openIds = string.Join(
                ", ",
                openBlockingFindings.Select(finding => finding.StableId));
            ReportTaskProgress(
                goalId,
                task.Id,
                WorkTaskStatus.Failed,
                $"Reviewer WORKER_RESULT verdict rejected: merged structured finding state still has open blocking stable_id(s): {openIds}.");
            return true;
        }

        if (nonPassingCriteriaDiagnostic is not null)
        {
            ReportTaskProgress(
                goalId,
                task.Id,
                WorkTaskStatus.Failed,
                nonPassingCriteriaDiagnostic);
            return true;
        }

        if (verification.WorkerResultPresent &&
            task.RequiredRole == AgentRole.Reviewer &&
            !WorkerResultBlockers.TryFindPassVerdict(verification))
        {
            ReportTaskProgress(
                goalId,
                task.Id,
                WorkTaskStatus.Failed,
                "Reviewer WORKER_RESULT verdict rejected: zero open blocking structured findings requires verdict: pass.");
            return true;
        }

        if (WorkerResultBlockers.TryFindTesterWorkerResultBlocker(task, verification, out var testerBlocker))
        {
            ReportTaskProgress(
                goalId,
                task.Id,
                WorkTaskStatus.Failed,
                $"Tester WORKER_RESULT reported blocker: {testerBlocker}; same Tester retry or operator action required.");
            return true;
        }

        if (verification.Succeeded &&
            task.RequiredRole == AgentRole.Reviewer &&
            !WorkerResultBlockers.IsAdvisoryNoChangeContractBlocker(task, verification) &&
            WorkerResultBlockers.TryFindHardFailureBlocker(verification, out var blocker))
        {
            if (TrySuppressSupersededReviewerBlocker(goal, task, blocker, out var effectiveBlocker))
            {
                if (string.IsNullOrWhiteSpace(effectiveBlocker))
                {
                    return false;
                }

                blocker = effectiveBlocker;
            }

            ReportTaskProgress(
                goalId,
                task.Id,
                WorkTaskStatus.Failed,
                $"Reviewer WORKER_RESULT reported blocker: {blocker}");
            return true;
        }

        if (enforceFailureEvidenceRule &&
            task.RequiredRole != AgentRole.Reviewer &&
            !WorkerResultBlockers.IsAdvisoryNoChangeContractBlocker(task, verification) &&
            WorkerResultBlockers.TryFindHardFailureBlocker(verification, out blocker))
        {
            ReportTaskProgress(
                goalId,
                task.Id,
                WorkTaskStatus.Failed,
                $"WORKER_RESULT reported blocker: {blocker}");
            return true;
        }

        return false;
    }

    public IReadOnlyList<ReviewFinding> GetOpenAdvisoryReviewFindings(GoalId goalId)
    {
        var goal = GetGoal(goalId);
        var finalReviewRecord = goal.Tasks
            .Where(task =>
                task.RequiredRole == AgentRole.Reviewer &&
                task.Status == WorkTaskStatus.Completed)
            .Select(task => task.LastVerification)
            .OfType<TaskVerificationRecord>()
            .OrderBy(verification => verification.CompletedAt)
            .LastOrDefault();

        return finalReviewRecord?.GetOpenAdvisoryFindings(goal.EffectiveAcceptanceCriteriaCorrections) ?? [];
    }

    private static TaskVerificationRecord AttachAuthoritativeReviewFindingContext(
        TaskSpec task,
        TaskVerificationRecord verification)
    {
        if (task.RequiredRole is not (AgentRole.Reviewer or AgentRole.Tester) ||
            !verification.WorkerResultPresent)
        {
            return verification;
        }

        return verification with
        {
            ReviewFindingTouchedAnchors = task.LastDispatch?.ReviewFindingTouchedAnchors ?? [],
            ReviewFindingTouchProofDiagnostic = task.LastDispatch?.ReviewFindingTouchProofDiagnostic,
            ReviewedCommit = task.LastDispatch?.BaseCommit
        };
    }

    private TaskVerificationRecord PrepareReviewFindingRecord(
        Goal goal,
        TaskSpec task,
        TaskVerificationRecord verification)
    {
        if (!verification.WorkerResultPresent ||
            task.RequiredRole is not (AgentRole.Reviewer or AgentRole.Tester))
        {
            return verification;
        }

        if (task.RequiredRole == AgentRole.Tester &&
            !WorkerResultBlockers.TryFindReviewFindingRound(verification, out _, out _))
        {
            // Structured Tester findings are opt-in so existing Tester workers remain compatible.
            // When present they use the same stable-identity ledger as Reviewer findings.
            return verification;
        }

        if (!TryBuildMergedReviewFindingState(
                goal,
                task.RequiredRole,
                verification,
                task.LastDispatch?.ReviewRetryCap,
                out var mergedFindings,
                out _,
                out var violation,
                out var identityTransitionSalvaged,
                out var canonicalizations))
        {
            return verification with
            {
                ReviewFindingContractViolation = violation,
                MergedReviewFindings = violation is not null &&
                    (ReviewFindingConvergence.IsRejectedCapResolutionRound(violation) ||
                     identityTransitionSalvaged)
                    ? mergedFindings
                    : null
            };
        }

        foreach (var canonicalization in canonicalizations)
        {
            Append(
                goal,
                task.Id,
                ProgressKind.TaskNote,
                $"Canonicalized {task.RequiredRole} finding identity at exact anchor {canonicalization.Anchor}: " +
                $"submitted_stable_id={canonicalization.SubmittedStableId}; " +
                $"canonical_stable_id={canonicalization.PriorStableId}.");
        }

        return verification with { MergedReviewFindings = mergedFindings };
    }

    private static bool TryBuildMergedReviewFindingState(
        Goal goal,
        AgentRole role,
        TaskVerificationRecord currentVerification,
        ReviewRetryCapReceipt? reviewRetryCap,
        out IReadOnlyList<ReviewFinding> state,
        out string diagnostic,
        out ReviewFindingContractViolation? violation,
        out bool identityTransitionSalvaged,
        out IReadOnlyList<ReviewFindingIdentityCanonicalization> canonicalizations)
    {
        state = [];
        diagnostic = string.Empty;
        violation = null;
        identityTransitionSalvaged = false;
        canonicalizations = [];
        var latestFindingOccurrences = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        string? lastAcceptedReviewedCommit = null;
        var historicalVerifications = goal.Tasks
            .Where(candidate => candidate.RequiredRole == role)
            .SelectMany(candidate => candidate.VerificationHistory)
            .Where(candidate => !ReferenceEquals(candidate, currentVerification));
        var evidenceReceipts = historicalVerifications
            .Append(currentVerification)
            .SelectMany(candidate => candidate.FindingEvidenceReceipts ?? [])
            .ToArray();
        foreach (var verification in historicalVerifications
            .Append(currentVerification)
            .OrderBy(candidate => candidate.CompletedAt))
        {
            var isCurrentRound = ReferenceEquals(verification, currentVerification);
            if (!isCurrentRound &&
                verification.ReviewFindingContractViolation is { } historicalViolation &&
                !ReviewFindingConvergence.IsRejectedCapResolutionRound(historicalViolation) &&
                !(ReviewFindingConvergence.IsRejectedIdentityTransitionRound(historicalViolation) &&
                  verification.MergedReviewFindings is not null))
            {
                // The durable violation marks this worker-authored round as rejected. Replaying it would
                // let an invalid structural transition mutate the accepted ledger.
                continue;
            }

            if (!WorkerResultBlockers.TryFindReviewFindingRound(verification, out var round, out var parseDiagnostic))
            {
                if (isCurrentRound)
                {
                    diagnostic = parseDiagnostic;
                    return false;
                }

                continue;
            }

            try
            {
                if (isCurrentRound)
                {
                    var nextState = ReviewFindingConvergence.ApplyRound(state, round, out canonicalizations);
                    if (role == AgentRole.Reviewer && reviewRetryCap is { IsAtCap: true })
                    {
                        ReviewFindingConvergence.ValidateResolutionAtCap(
                            state,
                            round,
                            goal.EffectiveAcceptanceCriteriaCorrections,
                            currentVerification.ReviewedCommit,
                            evidenceReceipts);
                    }
                    else if (role == AgentRole.Reviewer && reviewRetryCap is null)
                    {
                        ReviewFindingConvergence.ValidateResolutionWithoutCapReceipt(
                            state,
                            round,
                            goal.EffectiveAcceptanceCriteriaCorrections,
                            currentVerification.ReviewedCommit,
                            evidenceReceipts);
                    }

                    state = nextState;
                }
                else
                {
                    state = verification.ReviewFindingContractViolation switch
                    {
                        { } rejected when ReviewFindingConvergence.IsRejectedCapResolutionRound(rejected) =>
                            ReviewFindingConvergence.ApplyRejectedCapResolutionRound(state, round, rejected),
                        { } rejected when ReviewFindingConvergence.IsRejectedIdentityTransitionRound(rejected) =>
                            ReviewFindingConvergence.ApplyRejectedIdentityTransitionRound(state, round, rejected),
                        _ => ReviewFindingConvergence.ApplyRound(state, round)
                    };
                }
                var conductorOutcomes = (verification.MergedReviewFindings ?? [])
                    .Where(finding => finding.EvidenceOutcome is not null)
                    .ToDictionary(finding => finding.StableId, StringComparer.Ordinal);
                state = state
                    .Select(finding => conductorOutcomes.TryGetValue(finding.StableId, out var authoritative)
                        ? finding with
                        {
                            EvidenceRequest = authoritative.EvidenceRequest ?? finding.EvidenceRequest,
                            EvidenceOutcome = authoritative.EvidenceOutcome
                        }
                        : finding)
                    .ToArray();
                foreach (var finding in round.Findings.Where(finding =>
                             verification.ReviewFindingContractViolation is not { } rejectedTransition ||
                             (!ReviewFindingConvergence.IsRejectedCapResolutionTransition(rejectedTransition, finding.StableId) &&
                              !ReviewFindingConvergence.IsRejectedIdentityTransition(rejectedTransition, finding.StableId))))
                {
                    latestFindingOccurrences[finding.StableId] = verification.CompletedAt;
                }
                state = ApplyHumanInputSupersedeFindingResolutions(goal, state, latestFindingOccurrences);
                if (!string.IsNullOrWhiteSpace(verification.ReviewedCommit))
                {
                    lastAcceptedReviewedCommit = verification.ReviewedCommit.Trim();
                }
            }
            catch (ReviewFindingConvergenceException ex)
            {
                // Reject only the round being recorded. A HISTORICAL round that cannot be folded was already
                // rejected when it was recorded; replaying it must not block every later review from being
                // evaluated, and must not report a stale violation as though it described the new submission.
                if (ReviewFindingConvergence.CanCanonicalizeIdentityTransitions(ex.Violation) &&
                    SameNonEmptyReviewedCommit(lastAcceptedReviewedCommit, verification.ReviewedCommit))
                {
                    var priorState = state;
                    if (isCurrentRound && role == AgentRole.Reviewer)
                    {
                        try
                        {
                            if (reviewRetryCap is { IsAtCap: true })
                            {
                                ReviewFindingConvergence.ValidateResolutionAtCap(
                                    priorState,
                                    round,
                                    goal.EffectiveAcceptanceCriteriaCorrections,
                                    currentVerification.ReviewedCommit,
                                    evidenceReceipts);
                            }
                            else if (reviewRetryCap is null)
                            {
                                ReviewFindingConvergence.ValidateResolutionWithoutCapReceipt(
                                    priorState,
                                    round,
                                    goal.EffectiveAcceptanceCriteriaCorrections,
                                    currentVerification.ReviewedCommit,
                                    evidenceReceipts);
                            }
                        }
                        catch (ReviewFindingConvergenceException validationException)
                        {
                            if (ReviewFindingConvergence.IsRejectedCapResolutionRound(validationException.Violation))
                            {
                                try
                                {
                                    state = ReviewFindingConvergence.ApplyRejectedCapResolutionRound(
                                        priorState,
                                        round,
                                        validationException.Violation);
                                }
                                catch (ReviewFindingConvergenceException)
                                {
                                    // The filtered round can still contain the identity transition that
                                    // brought execution here. Preserve the accepted ledger and record the
                                    // original cap violation instead of discarding the whole verification.
                                    state = priorState;
                                }
                            }

                            diagnostic = $"{validationException.Code}: {validationException.Message}";
                            violation = validationException.Violation;
                            return false;
                        }
                    }

                    IReadOnlyList<ReviewFindingIdentityCanonicalization> identityCanonicalizations;
                    try
                    {
                        state = ReviewFindingConvergence.ApplyCanonicalizedIdentityTransitionRound(
                            priorState,
                            round,
                            ex.Violation,
                            out identityCanonicalizations);
                    }
                    catch (ReviewFindingConvergenceException canonicalizedRoundException)
                    {
                        // Canonicalizing the identity can expose a different invalid transition that the
                        // initial identity check intentionally evaluated first (for example, an untouched
                        // resolved-to-open transition). Retain the pre-round ledger and record that typed
                        // violation instead of letting the second exception discard the verification.
                        state = priorState;
                        if (isCurrentRound)
                        {
                            diagnostic = $"{canonicalizedRoundException.Code}: {canonicalizedRoundException.Message}";
                            violation = canonicalizedRoundException.Violation;
                            identityTransitionSalvaged = true;
                            return false;
                        }

                        continue;
                    }
                    var canonicalizedOutcomes = (verification.MergedReviewFindings ?? [])
                        .Where(finding => finding.EvidenceOutcome is not null)
                        .ToDictionary(finding => finding.StableId, StringComparer.Ordinal);
                    state = state
                        .Select(finding => canonicalizedOutcomes.TryGetValue(finding.StableId, out var authoritative)
                            ? finding with
                            {
                                EvidenceRequest = authoritative.EvidenceRequest ?? finding.EvidenceRequest,
                                EvidenceOutcome = authoritative.EvidenceOutcome
                            }
                            : finding)
                        .ToArray();
                    foreach (var finding in round.Findings)
                    {
                        latestFindingOccurrences[finding.StableId] = verification.CompletedAt;
                    }
                    state = ApplyHumanInputSupersedeFindingResolutions(goal, state, latestFindingOccurrences);
                    if (isCurrentRound)
                    {
                        canonicalizations = identityCanonicalizations;
                        return true;
                    }

                    lastAcceptedReviewedCommit = verification.ReviewedCommit!.Trim();
                    continue;
                }

                if (isCurrentRound)
                {
                    if (ReviewFindingConvergence.IsRejectedCapResolutionRound(ex.Violation))
                    {
                        var priorState = state;
                        try
                        {
                            state = ReviewFindingConvergence.ApplyRejectedCapResolutionRound(
                                priorState,
                                round,
                                ex.Violation);
                        }
                        catch (ReviewFindingConvergenceException)
                        {
                            // Keep the original typed cap violation authoritative if applying the valid
                            // subset exposes another invalid transition.
                            state = priorState;
                        }
                    }

                    if (ReviewFindingConvergence.IsRejectedIdentityTransitionRound(ex.Violation) &&
                        !string.IsNullOrWhiteSpace(lastAcceptedReviewedCommit) &&
                        !string.IsNullOrWhiteSpace(verification.ReviewedCommit))
                    {
                        var priorState = state;
                        try
                        {
                            state = ReviewFindingConvergence.ApplyRejectedIdentityTransitionRound(
                                priorState,
                                round,
                                ex.Violation);
                        }
                        catch (ReviewFindingConvergenceException salvagedRoundException)
                        {
                            // Applying the non-identity portion of the round can expose another invalid
                            // transition. Preserve the accepted ledger while still recording the more
                            // specific typed violation on this verification.
                            state = priorState;
                            diagnostic = $"{salvagedRoundException.Code}: {salvagedRoundException.Message}";
                            violation = salvagedRoundException.Violation;
                            identityTransitionSalvaged = true;
                            return false;
                        }

                        identityTransitionSalvaged = true;
                    }

                    diagnostic = $"{ex.Code}: {ex.Message}";
                    violation = ex.Violation;
                    return false;
                }
            }
        }

        return true;
    }

    private static bool SameNonEmptyReviewedCommit(string? prior, string? current) =>
        !string.IsNullOrWhiteSpace(prior) &&
        !string.IsNullOrWhiteSpace(current) &&
        string.Equals(prior.Trim(), current.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string BuildCompletionMessageWithAdvisoryBlocker(
        string message,
        TaskSpec task,
        TaskVerificationRecord verification)
    {
        return (WorkerResultBlockers.TryFindAdvisoryBlocker(verification, out var blocker) ||
                (WorkerResultBlockers.IsAdvisoryNoChangeContractBlocker(task, verification) &&
                 WorkerResultBlockers.TryFindBlocker(verification, out blocker)))
            ? $"{message}; advisory WORKER_RESULT blocker: {blocker}"
            : message;
    }

    public void RecordDispatchBaseCommit(GoalId goalId, TaskId taskId, string baseCommit)
    {
        var goal = GetGoal(goalId);
        var task = goal.FindTask(taskId);
        task.SetDispatchBaseCommit(baseCommit);
    }

    public void RecordDispatchResultCommit(GoalId goalId, TaskId taskId, string resultCommit)
    {
        var goal = GetGoal(goalId);
        var task = goal.FindTask(taskId);
        task.SetDispatchResultCommit(resultCommit);
    }

    public void RecordDispatchSandboxLowIntegrity(GoalId goalId, TaskId taskId, bool sandboxLowIntegrity)
    {
        var goal = GetGoal(goalId);
        var task = goal.FindTask(taskId);
        task.SetDispatchSandboxLowIntegrity(sandboxLowIntegrity);
    }

    public void RecordDispatchSpawnReceipt(
        GoalId goalId,
        TaskId taskId,
        string command,
        string? providerSessionId,
        string? worktreeHeadSha,
        string? dirtyStateHash)
    {
        var goal = GetGoal(goalId);
        var task = goal.FindTask(taskId);
        task.SetDispatchSpawnReceipt(command, providerSessionId, worktreeHeadSha, dirtyStateHash);
    }

    public void RecordDispatchProviderSessionId(GoalId goalId, TaskId taskId, string providerSessionId)
    {
        var goal = GetGoal(goalId);
        var task = goal.FindTask(taskId);
        task.SetDispatchProviderSessionId(providerSessionId);
    }

    public void RetireDispatchProviderSession(GoalId goalId, TaskId taskId, DateTimeOffset retiredAt)
    {
        var goal = GetGoal(goalId);
        var task = goal.FindTask(taskId);
        task.RetireDispatchProviderSession(retiredAt);
    }

    public void RecordDispatchContextPackageReceipt(
        GoalId goalId,
        TaskId taskId,
        DateTimeOffset dispatchedAt,
        WorkerContextPackageReceipt receipt)
    {
        var goal = GetGoal(goalId);
        goal.FindTask(taskId).SetDispatchContextPackageReceipt(dispatchedAt, receipt);
    }

    public void RecordTaskDispatch(
        GoalId goalId,
        TaskId taskId,
        TaskDispatchRecord dispatch,
        bool allowPendingRecordedDispatchRefresh = false)
    {
        var goal = GetGoal(goalId);
        var task = goal.FindTask(taskId);

        if (task.LastVerification?.Succeeded is true)
        {
            throw new InvalidOperationException($"Task '{taskId}' already has passing verification; retry the task before dispatching it again.");
        }

        if (task.Status != WorkTaskStatus.Assigned &&
            !(allowPendingRecordedDispatchRefresh &&
                task.Status == WorkTaskStatus.Running &&
                task.LastDispatch is not null &&
                task.LastProcess is null))
        {
            throw new InvalidOperationException($"Task '{taskId}' status is {task.Status}; retry or assign it before dispatching it again.");
        }

        dispatch.BriefVersion = goal.AuthoritativeBrief.Version;
        dispatch.BriefSnapshot = goal.Objective;
        task.RecordDispatch(dispatch);
        task.SetStatus(WorkTaskStatus.Running);
        goal.SetStatus(GoalStatus.Active);
        Append(goal, taskId, ProgressKind.TaskDispatchRecorded, $"Dispatched to {dispatch.WorkerName}{FormatDispatchTimelineModelSelection(dispatch)}: {dispatch.Command}");
    }

    public void ReplacePreparedTaskDispatch(GoalId goalId, TaskId taskId, TaskDispatchRecord dispatch)
    {
        var goal = GetGoal(goalId);
        var task = goal.FindTask(taskId);
        dispatch.BriefVersion = task.LastDispatch?.BriefVersion ?? goal.AuthoritativeBrief.Version;
        dispatch.BriefSnapshot = task.LastDispatch?.BriefSnapshot ?? goal.Objective;
        task.ReplacePreparedDispatch(dispatch);
    }

    private static string FormatDispatchTimelineModelSelection(TaskDispatchRecord dispatch)
    {
        if (string.IsNullOrWhiteSpace(dispatch.ProviderName) || string.IsNullOrWhiteSpace(dispatch.ModelName))
        {
            return string.Empty;
        }

        var lane = string.IsNullOrWhiteSpace(dispatch.DispatchLane)
            ? string.Empty
            : $" lane={dispatch.DispatchLane}";
        var reason = string.IsNullOrWhiteSpace(dispatch.ModelSelectionReason)
            ? string.Empty
            : $" ({dispatch.ModelSelectionReason})";
        return $" using {dispatch.ProviderName}/{dispatch.ModelName}{lane}{reason}";
    }

    public void RecordTaskProcessStarted(GoalId goalId, TaskId taskId, TaskProcessRecord process)
    {
        var goal = GetGoal(goalId);
        var task = goal.FindTask(taskId);

        if (task.LastDispatch is null)
        {
            throw new InvalidOperationException($"Task '{taskId}' has no dispatch to start.");
        }

        if (!string.Equals(task.LastDispatch.Command, process.Command, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Process command does not match the recorded dispatch command.");
        }

        if (!string.Equals(task.LastDispatch.WorkingDirectory, process.WorkingDirectory, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Process working directory does not match the recorded dispatch working directory.");
        }

        task.RecordProcess(process);
        task.SetStatus(WorkTaskStatus.Running);
        goal.SetStatus(GoalStatus.Active);
        Append(goal, taskId, ProgressKind.TaskProcessStarted, $"Started process {process.ProcessId}: {process.Command}");
    }

    public void RecordTaskProcessRefreshed(
        GoalId goalId,
        TaskId taskId,
        TaskProcessRecord process,
        TaskVerificationRecord? verification,
        ProviderFailureKind providerFailureKind = ProviderFailureKind.Unknown)
    {
        var goal = GetGoal(goalId);
        var task = goal.FindTask(taskId);
        task.RecordProcess(process);

        if (verification is not null)
        {
            RecordDispatchExecutionResult(goalId, taskId, verification, providerFailureKind);
        }
    }

    internal void RecordHumanInputWorkerResultContradiction(
        GoalId goalId,
        TaskId taskId,
        HumanInputRequestId requestId,
        int completedRound,
        string? workerResultLogReference)
    {
        Append(
            GetGoal(goalId),
            taskId,
            ProgressKind.HumanInputWorkerResultContradiction,
            $"kind=human-input-worker-result-contradiction request={requestId.Value[..8]} " +
            $"round={completedRound} worker_result_log={workerResultLogReference ?? "unavailable"} " +
            "human_input=emitted blockers=none");
    }

    public void RecordTaskProcessGracefullyDetached(
        GoalId goalId,
        TaskId taskId,
        TaskProcessRecord process)
    {
        var goal = GetGoal(goalId);
        var task = goal.FindTask(taskId);
        if (task.Status != WorkTaskStatus.Running ||
            task.LastProcess is not { IsRunning: true } current ||
            current.ProcessId != process.ProcessId ||
            current.StartedAt != process.StartedAt)
        {
            throw new InvalidOperationException("Only the current running process attempt can be gracefully detached.");
        }

        if (!process.WasGracefullyDetachedByConductor || !process.IsRunning)
        {
            throw new InvalidOperationException("Gracefully detached process record must remain running and carry its detach marker.");
        }

        task.RecordProcess(process);
        Append(
            goal,
            taskId,
            ProgressKind.TaskNote,
            $"Gracefully detached process {process.ProcessId}; a successor conductor may reconcile it.");
    }

    public void RecordTaskProcessCancelled(GoalId goalId, TaskId taskId, TaskProcessRecord process)
    {
        var goal = GetGoal(goalId);
        var task = goal.FindTask(taskId);

        if (task.LastProcess is null)
        {
            throw new InvalidOperationException($"Task '{taskId}' has no background process to cancel.");
        }

        if (process.WasCancelled is false)
        {
            throw new InvalidOperationException("Cancelled process record must have WasCancelled set.");
        }

        task.RecordProcess(process);
        if (process.WasCancelledByConductor)
        {
            task.SetConductorCancelledStatus();
        }
        else
        {
            task.SetStatus(WorkTaskStatus.Cancelled);
        }
        Append(goal, taskId, ProgressKind.TaskCancelled, $"Cancelled process {process.ProcessId}: {process.Command}");
    }

    private bool TryCompleteTaskWithPassingVerification(Goal goal, TaskSpec task, string message)
    {
        if (task.Status == WorkTaskStatus.Completed || task.LastVerification is not { Succeeded: true })
        {
            return false;
        }

        if (_humanInputRequests.Values.Any(request => request.GoalId == goal.Id && request.TaskId == task.Id && !request.IsCompleted))
        {
            return false;
        }

        task.SetStatus(WorkTaskStatus.Completed);
        Append(goal, task.Id, ProgressKind.TaskCompleted, message);
        return true;
    }
}
