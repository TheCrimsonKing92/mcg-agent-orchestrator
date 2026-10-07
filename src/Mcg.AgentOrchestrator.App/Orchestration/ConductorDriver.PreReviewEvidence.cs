using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    private readonly object _testImpactDegradedEventLock = new();
    private readonly HashSet<(string GoalId, string CandidateSha)> _testImpactDegradedCandidates = [];
    private ConductEventLogWriter? _testImpactDegradedEventWriter;

    internal void OverrideTestImpactDegradedEventWriterForTests(ConductEventLogWriter writer) =>
        _testImpactDegradedEventWriter = writer;

    private void TryRecordTestImpactDegradedEvent(
        Goal goal, string goalPrefix, PreReviewEvidenceContext context)
    {
        if (context.TestImpactDegradation is not
            { Kind: ReverseDependencyDegradationKind.IndexedSourceBound or ReverseDependencyDegradationKind.Unreadable } degradation)
        {
            return;
        }

        lock (_testImpactDegradedEventLock)
        {
            var key = (goal.Id.Value, context.CandidateSha!);
            if (_testImpactDegradedCandidates.Contains(key)) return;
            try
            {
                if (_testImpactDegradedEventWriter is null && _cohortWorkspace is not null)
                    _testImpactDegradedEventWriter = new ConductEventLogWriter(_cohortWorkspace.ConductEventsLogPath);
                if (_testImpactDegradedEventWriter is null) return;

                _testImpactDegradedEventWriter.Append(
                    "test-impact-degraded", goal.Id.Value,
                    $"TEST_IMPACT_DEGRADED goal={goalPrefix} candidate={context.CandidateSha} kind={degradation.Kind} reason={degradation.Reason}");
                _testImpactDegradedCandidates.Add(key);
            }
            catch
            {
                // Match other diagnostic events: logging cannot block review; failed writes may retry.
            }
        }
    }

    private bool TryRunPreReviewEvidenceStage(
        Goal goal,
        string goalPrefix,
        ConductorAutonomyPolicy policy,
        GoalLifecycleState fromState,
        out ConductorAdvanceResult result)
    {
        result = default!;
        var reviewerTask = goal.Tasks.FirstOrDefault(task => task.RequiredRole == AgentRole.Reviewer);
        if (reviewerTask is null ||
            reviewerTask.Status != WorkTaskStatus.Assigned ||
            !TasksBefore(goal, reviewerTask).All(task => task.Status == WorkTaskStatus.Completed))
        {
            return false;
        }

        var context = _getPreReviewEvidenceContext(goal);
        if (string.IsNullOrWhiteSpace(context.CandidateSha))
        {
            result = Escalate(
                goal,
                goalPrefix,
                policy,
                fromState,
                "PRE_REVIEW_MAPPING_NEEDS_INPUT: current candidate HEAD could not be resolved; Reviewer dispatch is blocked.");
            return true;
        }

        RecordTestImpactEvents(goal, goalPrefix, context);

        var round = GetCurrentReviewerRoundNumber(goal, reviewerTask);
        var currentReceipt = reviewerTask.PreReviewEvidenceReceipt;
        if (context.NoApplicableTests && !context.MappingNeedsInput &&
            currentReceipt is { Disposition: PreReviewEvidenceDisposition.NoApplicableTests } current &&
            current.MatchesCurrentCandidate(goal.Id.Value, context.CandidateSha, context.SelectedFocusedTests))
        {
            return false;
        }

        if (!context.MappingNeedsInput && !context.NoApplicableTests && !string.IsNullOrWhiteSpace(context.FocusedRequest) &&
            PreReviewEvidenceReceipts.TryReuse(
                reviewerTask,
                goal.Id.Value,
                context.CandidateSha,
                context.SelectedFocusedTests,
                out var constituentReceipts))
        {
            PreReviewEvidenceReceipts.RecordReuse(_recordPreReviewEvidence, goal, reviewerTask, context, round, constituentReceipts);
            return false;
        }

        if (context.NoApplicableTests)
        {
            PreReviewEvidenceReceipts.Record(_recordPreReviewEvidence,
                goal,
                reviewerTask,
                context,
                round,
                PreReviewEvidenceDisposition.NoApplicableTests,
                [],
                [],
                evidencePointer: null);
            return false;
        }

        if (context.MappingNeedsInput || string.IsNullOrWhiteSpace(context.FocusedRequest))
        {
            var receipt = PreReviewEvidenceReceipts.Record(_recordPreReviewEvidence,
                goal,
                reviewerTask,
                context,
                round,
                PreReviewEvidenceDisposition.MappingNeedsInput,
                [],
                [],
                evidencePointer: null);
            if (TryStopRepeatedPreReviewMappingRetry(
                    goal,
                    reviewerTask,
                    goalPrefix,
                    policy,
                    fromState,
                    context,
                    currentReceipt,
                    out result))
            {
                return true;
            }

            if (context.RequiresSourceCleanup && TryRoutePreReviewEvidenceToDeveloper(
                    goal,
                    reviewerTask,
                    goalPrefix,
                    policy,
                    $"pre-review source cleanup required for candidate {context.CandidateSha}: remove the generated " +
                    $"artifacts from the candidate and commit the cleanup before retrying; " +
                    $"paths={FormatSourceCleanupPaths(context.SourceCleanupPaths)}",
                    out result))
            {
                return true;
            }

            if (!context.RequiresSourceCleanup && TryRoutePreReviewEvidenceToTester(
                    goal,
                    reviewerTask,
                    goalPrefix,
                    policy,
                    $"pre-review mapping requires Tester selection for candidate {context.CandidateSha}: {context.MappingReason}",
                    out result))
            {
                return true;
            }

            var routingRequirement = context.RequiresSourceCleanup
                ? "source cleanup requires a writable Developer"
                : "deterministic test-impact mapping requires typed operator/Tester selection";
            result = Escalate(
                goal,
                goalPrefix,
                policy,
                fromState,
                $"PRE_REVIEW_MAPPING_NEEDS_INPUT: {routingRequirement}; " +
                $"Reviewer dispatch is blocked for candidate {context.CandidateSha}. Reason: {context.MappingReason}. " +
                (context.RequiresSourceCleanup
                    ? $"Remove and commit these paths before retrying: {FormatSourceCleanupPaths(context.SourceCleanupPaths)}. "
                    : string.Empty) +
                $"Receipt round={receipt.ReviewerRound}.");
            return true;
        }

        var candidate = ConductorParallelAcceptanceCandidate.Create(
            goal,
            slotIndex: 0,
            fileScopes: [],
            branchHeadSha: context.CandidateSha,
            mainHeadSha: null);
        ConductorParallelAcceptanceAttemptDecision attemptDecision;
        try
        {
            attemptDecision = _focusedEvidenceAttemptCoordinator.EvaluateFocusedEvidence(
                candidate,
                policy,
                context.FocusedRequest,
                _runFocusedEvidence);
        }
        catch (AcceptanceArtifactWriterLeaseBusyException ex)
        {
            result = MakeResult(
                goal.Id.Value,
                goalPrefix,
                policy,
                PreReviewEvidenceHold(
                    fromState, goal.Id.Value, "writer-busy", "", "",
                    $"Background pre-review evidence artifact writer is busy; retry on next conduct tick. {ex.Message}"));
            return true;
        }
        if (attemptDecision.Kind is
            ConductorParallelAcceptanceAttemptDecisionKind.Started or
            ConductorParallelAcceptanceAttemptDecisionKind.Running)
        {
            result = MakeResult(
                goal.Id.Value,
                goalPrefix,
                policy,
                PreReviewEvidenceHold(
                    fromState, goal.Id.Value, "attempt-running",
                    attemptDecision.Attempt.AttemptId, attemptDecision.Attempt.Outcome.ToString(),
                    _focusedEvidenceAttemptCoordinator.DescribeFocusedEvidenceHold(attemptDecision.Attempt),
                    $"pre-review-evidence:{attemptDecision.Attempt.AttemptId}", ConductorHoldOwner.BackgroundAttempt));
            return true;
        }

        if (attemptDecision.Kind == ConductorParallelAcceptanceAttemptDecisionKind.TerminalWithoutRun ||
            attemptDecision.Run?.Exception is
                DotnetBuildSlotsBusyException or
                BuildLockBlockedException or
                OperationCanceledException)
        {
            _focusedEvidenceAttemptCoordinator.MarkReconciled(attemptDecision.Attempt);
            result = MakeResult(
                goal.Id.Value,
                goalPrefix,
                policy,
                PreReviewEvidenceHold(
                    fromState, goal.Id.Value, "did-not-run",
                    attemptDecision.Attempt.AttemptId, attemptDecision.Attempt.Outcome.ToString(),
                    $"Background pre-review evidence did not run ({attemptDecision.Attempt.Outcome}); " +
                    $"retry on next conduct tick. attempt={attemptDecision.Attempt.AttemptId}: " +
                    (attemptDecision.Attempt.Detail ?? "no result artifact was produced")));
            return true;
        }

        _focusedEvidenceAttemptCoordinator.MarkReconciled(attemptDecision.Attempt);
        if (attemptDecision.Run?.Exception is { } backgroundFailure)
        {
            result = Escalate(
                goal,
                goalPrefix,
                policy,
                fromState,
                $"PRE_REVIEW_EVIDENCE_FAILED: background focused evidence run failed. " +
                $"attempt={attemptDecision.Attempt.AttemptId}: {backgroundFailure.Message}");
            return true;
        }

        var evidence = attemptDecision.Run?.FocusedEvidence ?? new FocusedEvidenceRunResult(
            context.FocusedRequest,
            Accepted: false,
            Passed: false,
            Summary: $"background pre-review evidence {attemptDecision.Attempt.Outcome}: " +
                (attemptDecision.Attempt.Detail ?? "no result artifact was produced"),
            Checks: []);
        var evidencePointer = BuildPreReviewEvidencePointer(evidence);
        if (!evidence.Accepted)
        {
            PreReviewEvidenceReceipts.Record(_recordPreReviewEvidence,
                goal,
                reviewerTask,
                context,
                round,
                PreReviewEvidenceDisposition.MappingNeedsInput,
                evidence.Checks,
                [],
                evidencePointer);
            if (TryStopRepeatedPreReviewMappingRetry(
                    goal,
                    reviewerTask,
                    goalPrefix,
                    policy,
                    fromState,
                    context,
                    currentReceipt,
                    out result))
            {
                return true;
            }

            if (TryRoutePreReviewEvidenceToTester(
                    goal,
                    reviewerTask,
                    goalPrefix,
                    policy,
                    $"pre-review focused-evidence request was rejected for candidate {context.CandidateSha}: {FormatFocusedEvidenceResult(evidence)}",
                    out result))
            {
                return true;
            }

            result = Escalate(
                goal,
                goalPrefix,
                policy,
                fromState,
                $"PRE_REVIEW_MAPPING_NEEDS_INPUT: mapped focused evidence request was rejected; Reviewer dispatch is blocked. " +
                $"{FormatFocusedEvidenceResult(evidence)}");
            return true;
        }

        if (evidence.Passed)
        {
            if (!PreReviewEvidenceReceipts.ValidateCoverage(context, evidence, out var mappingFailure))
            {
                PreReviewEvidenceReceipts.Record(_recordPreReviewEvidence,
                    goal,
                    reviewerTask,
                    context,
                    round,
                    PreReviewEvidenceDisposition.MappingNeedsInput,
                    evidence.Checks,
                    [],
                    evidencePointer);
                var mismatch = $"pre-review evidence mapping failure for candidate {context.CandidateSha}: {mappingFailure}";
                if (TryStopRepeatedPreReviewMappingRetry(
                        goal,
                        reviewerTask,
                        goalPrefix,
                        policy,
                        fromState,
                        context,
                        currentReceipt,
                        out result))
                {
                    return true;
                }

                if (TryRoutePreReviewEvidenceToTester(
                        goal,
                        reviewerTask,
                        goalPrefix,
                        policy,
                        mismatch,
                        out result))
                {
                    return true;
                }

                result = Escalate(
                    goal,
                    goalPrefix,
                    policy,
                    fromState,
                    $"PRE_REVIEW_MAPPING_NEEDS_INPUT: {mismatch}; no Tester task is available. " +
                    $"Add one with: {BuildAddTesterCommand(goalPrefix, context.CandidateSha)}");
                return true;
            }

            PreReviewEvidenceReceipts.Record(_recordPreReviewEvidence,
                goal,
                reviewerTask,
                context,
                round,
                PreReviewEvidenceDisposition.Green,
                evidence.Checks,
                [],
                evidencePointer);
            return false;
        }

        if (evidence.OutcomeReason == FindingEvidenceOutcomeReason.ApparatusFailure)
        {
            PreReviewEvidenceReceipts.Record(_recordPreReviewEvidence,
                goal,
                reviewerTask,
                context,
                round,
                PreReviewEvidenceDisposition.MappingNeedsInput,
                evidence.Checks,
                [],
                evidencePointer);
            var apparatusDetail =
                $"pre-review focused selection apparatus failure for candidate {context.CandidateSha}; " +
                $"the run executed zero tests and is not candidate-failure evidence; pointer={evidencePointer ?? "none"}";
            if (TryStopRepeatedPreReviewMappingRetry(
                    goal,
                    reviewerTask,
                    goalPrefix,
                    policy,
                    fromState,
                    context,
                    currentReceipt,
                    out result))
            {
                return true;
            }

            if (TryRoutePreReviewEvidenceToTester(
                    goal,
                    reviewerTask,
                    goalPrefix,
                    policy,
                    apparatusDetail,
                    out result))
            {
                return true;
            }

            result = Escalate(
                goal,
                goalPrefix,
                policy,
                fromState,
                $"PRE_REVIEW_SELECTION_APPARATUS_FAILURE: {apparatusDetail}; no Tester task is available.");
            return true;
        }

        var failingTests = ExtractFailingTestIdentities(evidence.Checks);
        if (TryHoldPreReviewEvidenceTimeout(goal, reviewerTask, goalPrefix, policy, fromState, context, round, evidence, failingTests, currentReceipt, evidencePointer, out result))
            return true;
        PreReviewEvidenceReceipts.Record(_recordPreReviewEvidence,
            goal, reviewerTask, context, round, PreReviewEvidenceDisposition.Red,
            evidence.Checks, failingTests, evidencePointer);
        var buildDiagnostic = failingTests.Count == 0 ? FormatPreReviewBuildDiagnostic(evidence.Checks) : null;
        if (buildDiagnostic is not null &&
            currentReceipt is { Disposition: PreReviewEvidenceDisposition.Red, EvidenceTimeoutChecks: null or [] } previousRed &&
            previousRed.FailingTestIdentities.Count == 0 &&
            previousRed.MatchesCurrentCandidate(goal.Id.Value, context.CandidateSha, context.SelectedFocusedTests))
        {
            result = Escalate(
                goal,
                goalPrefix,
                policy,
                fromState,
                $"PRE_REVIEW_RED_UNCHANGED_CANDIDATE: candidate {context.CandidateSha} failed again without typed test identities; " +
                $"diagnostic: {buildDiagnostic}.");
            return true;
        }

        var developerTask = TasksBefore(goal, reviewerTask)
            .LastOrDefault(task => task.RequiredRole == AgentRole.Developer);
        if (developerTask is null)
        {
            result = Escalate(
                goal,
                goalPrefix,
                policy,
                fromState,
                $"PRE_REVIEW_RED: no responsible Developer task exists. " +
                (buildDiagnostic is null ? $"Failing tests: {string.Join(", ", failingTests)}. " : $"Diagnostic: {buildDiagnostic}. ") +
                $"Pointer={evidencePointer ?? "none"}.");
            return true;
        }

        var repeatedStatement = string.Empty;
        if (failingTests.Count > 0 && TryHoldRepeatedPreReviewFailure(
                goal, reviewerTask, goalPrefix, policy, fromState,
                out repeatedStatement, out result))
            return true;

        var retryMessage = buildDiagnostic is null
            ? $"pre-review focused-test repair: candidate {context.CandidateSha}; exact failing tests: " +
                $"{string.Join(", ", failingTests)}; evidence pointer: {evidencePointer ?? "none"}" +
                (string.IsNullOrEmpty(repeatedStatement) ? string.Empty : Environment.NewLine + repeatedStatement)
            : $"pre-review build repair: candidate {context.CandidateSha}; diagnostic: {buildDiagnostic}; " +
                $"evidence pointer: {evidencePointer ?? "none"}";
        _retryTask(goal.Id, developerTask.Id, retryMessage, RetryRoundKind.Mechanical, RetryCause.NewSourceFinding);
        var refreshedGoal = GetCurrentGoal(goal);
        var retryState = GoalLifecycle.ResolveState(refreshedGoal, GetFacts(refreshedGoal));
        result = ExecuteDispatchAndStart(refreshedGoal, goalPrefix, policy, retryState);
        return true;
    }
}
