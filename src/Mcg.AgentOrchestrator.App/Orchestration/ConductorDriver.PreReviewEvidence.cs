using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
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
                new ConductorAdvanceOutcome.Held(
                    fromState,
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
                new ConductorAdvanceOutcome.Held(
                    fromState,
                    _focusedEvidenceAttemptCoordinator.DescribeFocusedEvidenceHold(attemptDecision.Attempt),
                    $"pre-review-evidence:{attemptDecision.Attempt.AttemptId}") { Owner = ConductorHoldOwner.BackgroundAttempt });
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
                new ConductorAdvanceOutcome.Held(
                    fromState,
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

    private bool TryStopRepeatedPreReviewMappingRetry(
        Goal goal,
        TaskSpec reviewerTask,
        string goalPrefix,
        ConductorAutonomyPolicy policy,
        GoalLifecycleState fromState,
        PreReviewEvidenceContext context,
        PreReviewEvidenceReceipt? priorReceipt,
        out ConductorAdvanceResult result)
    {
        if (priorReceipt is not { Disposition: PreReviewEvidenceDisposition.MappingNeedsInput } ||
            !priorReceipt.MatchesCurrentCandidate(
                goal.Id.Value,
                context.CandidateSha!,
                context.SelectedFocusedTests))
        {
            result = default!;
            return false;
        }

        var requiredRole = context.RequiresSourceCleanup ? AgentRole.Developer : AgentRole.Tester;
        var repairTask = TasksBefore(goal, reviewerTask)
            .LastOrDefault(task => task.RequiredRole == requiredRole);
        if (repairTask is not null)
        {
            var remedy = context.RequiresSourceCleanup
                ? $" Remove and commit these paths: {FormatSourceCleanupPaths(context.SourceCleanupPaths)}."
                : " Correct the test-impact mapping or provide an explicit focused-test selection before retrying.";
            result = Escalate(
                goal,
                goalPrefix,
                policy,
                fromState,
                $"PRE_REVIEW_MAPPING_RETRY_DID_NOT_CONVERGE: automatic {requiredRole} retry left candidate " +
                $"{context.CandidateSha} with the same mapping-needed disposition; no further paid retry was started.{remedy}");
            return true;
        }

        var suppressedCount = 1 + goal.Timeline.Count(evt =>
            evt.Kind == ProgressKind.PreReviewMappingEscalationSuppressed &&
            evt.TaskId == reviewerTask.Id &&
            evt.Message.Contains($"candidate_sha={context.CandidateSha}", StringComparison.OrdinalIgnoreCase) &&
            evt.Message.Contains(
                $"disposition={PreReviewEvidenceDisposition.MappingNeedsInput}",
                StringComparison.Ordinal));
        _recordPreReviewMappingEscalationSuppressed(
            goal.Id,
            reviewerTask.Id,
            context.CandidateSha!,
            suppressedCount);
        result = MakeResult(
            goal.Id.Value,
            goalPrefix,
            policy,
            new ConductorAdvanceOutcome.Held(
                fromState,
                $"PRE_REVIEW_MAPPING_NEEDS_INPUT repeat suppressed for candidate {context.CandidateSha}; " +
                $"suppressed_count={suppressedCount}."));
        return true;
    }

    private bool TryRoutePreReviewEvidenceToDeveloper(
        Goal goal,
        TaskSpec reviewerTask,
        string goalPrefix,
        ConductorAutonomyPolicy policy,
        string reason,
        out ConductorAdvanceResult result)
    {
        var developerTask = TasksBefore(goal, reviewerTask)
            .LastOrDefault(task => task.RequiredRole == AgentRole.Developer);
        if (developerTask is null)
        {
            result = default!;
            return false;
        }

        _retryTask(
            goal.Id,
            developerTask.Id,
            reason,
            RetryRoundKind.Mechanical,
            RetryCause.NewSourceFinding);
        var refreshedGoal = GetCurrentGoal(goal);
        var retryState = GoalLifecycle.ResolveState(refreshedGoal, GetFacts(refreshedGoal));
        result = ExecuteDispatchAndStart(refreshedGoal, goalPrefix, policy, retryState);
        return true;
    }

    private bool TryRoutePreReviewEvidenceToTester(
        Goal goal,
        TaskSpec reviewerTask,
        string goalPrefix,
        ConductorAutonomyPolicy policy,
        string reason,
        out ConductorAdvanceResult result)
    {
        var testerTask = TasksBefore(goal, reviewerTask)
            .LastOrDefault(task => task.RequiredRole == AgentRole.Tester);
        if (testerTask is null)
        {
            result = default!;
            return false;
        }

        _retryTask(
            goal.Id,
            testerTask.Id,
            reason,
            RetryRoundKind.Mechanical,
            RetryCause.NewTestFinding);
        var refreshedGoal = GetCurrentGoal(goal);
        var retryState = GoalLifecycle.ResolveState(refreshedGoal, GetFacts(refreshedGoal));
        result = ExecuteDispatchAndStart(refreshedGoal, goalPrefix, policy, retryState);
        return true;
    }

    internal static PreReviewEvidenceContext BuildPreReviewEvidenceContext(Goal goal, string executionDirectory)
    {
        var worktreePath = GoalWorktrees.TryResolve(executionDirectory, goal.Id);
        var candidateSha = worktreePath is null ? null : TryResolveGitHead(worktreePath);
        var changedFiles = worktreePath is null
            ? Array.Empty<string>()
            : GoalAcceptanceEvidenceBundleBuilder.GetChangedFiles(worktreePath);
        return BuildPreReviewEvidenceContext(
            candidateSha,
            changedFiles,
            worktreePath);
    }

    internal PreReviewEvidenceContext GetPreReviewEvidenceContext(Goal goal) =>
        _getPreReviewEvidenceContext(goal);

    internal static PreReviewEvidenceContext BuildPreReviewEvidenceContext(
        string? candidateSha, IReadOnlyList<string> changedFiles, string? repositoryRoot = null)
    {
        var changeSummary = RepositoryChangeClassifier.Classify(changedFiles);
        var plan = string.IsNullOrWhiteSpace(repositoryRoot)
            ? RepositoryTestImpactPlanner.Plan(changeSummary)
            : RepositoryTestImpactPlanner.Plan(changeSummary, repositoryRoot);
        if (!plan.RequiresBuild &&
            plan.Checks.Count > 0 &&
            plan.Checks.All(check => check.Command.Count == 0))
        {
            var generatedArtifactsBlock = changeSummary.HasGeneratedArtifacts;
            return new PreReviewEvidenceContext(
                candidateSha,
                [],
                null,
                plan.Summary,
                NoApplicableTests: !generatedArtifactsBlock,
                MappingNeedsInput: generatedArtifactsBlock,
                SourceCleanupPaths: changeSummary.Files
                    .Where(file => file.IsGeneratedArtifact)
                    .Select(file => file.Path)
                    .ToArray());
        }

        var focusedChecks = plan.Checks
            .Where(check => FindArgument(check.Command, "--filter") >= 0)
            .ToArray();
        if (focusedChecks.Length == 0)
        {
            return new PreReviewEvidenceContext(
                candidateSha,
                [],
                null,
                $"{plan.Summary} No filtered test target mapped; project-wide checks are deferred to the acceptance gate.",
                NoApplicableTests: true,
                MappingNeedsInput: false);
        }

        var selected = focusedChecks.Select(check => check.CommandLine).ToArray();
        var requests = new List<string>();
        foreach (var check in focusedChecks)
        {
            var filterIndex = FindArgument(check.Command, "--filter");
            var project = check.Command.FirstOrDefault(argument =>
                argument.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(project))
            {
                return new PreReviewEvidenceContext(
                    candidateSha,
                    selected,
                    null,
                    plan.Summary,
                    NoApplicableTests: false,
                    MappingNeedsInput: true);
            }

            if (!PreReviewFocusedRequestSplitter.TryResolveBrokerAlias(project, out var alias))
            {
                return new PreReviewEvidenceContext(
                    candidateSha,
                    selected,
                    null,
                    $"Mapped project is not supported by the focused evidence broker: {project}",
                    NoApplicableTests: false,
                    MappingNeedsInput: true);
            }
            if (filterIndex >= 0 && filterIndex + 1 >= check.Command.Count)
            {
                return new PreReviewEvidenceContext(
                    candidateSha,
                    selected,
                    null,
                    $"Mapped test command has an empty --filter argument: {check.CommandLine}",
                    NoApplicableTests: false,
                    MappingNeedsInput: true);
            }

            if (!PreReviewFocusedRequestSplitter.TrySplitRequestItems(
                    alias!, check.Command[filterIndex + 1], out var requestItems))
            {
                return new PreReviewEvidenceContext(
                    candidateSha, selected, null,
                    "Mapped test filter cannot be split into broker-safe positive clauses.",
                    NoApplicableTests: false, MappingNeedsInput: true);
            }
            requests.AddRange(requestItems);
        }

        return new PreReviewEvidenceContext(
            candidateSha,
            requests,
            string.Join("; ", requests),
            plan.Summary,
            NoApplicableTests: false,
            MappingNeedsInput: requests.Count == 0);
    }

    private static string BuildAddTesterCommand(string goalPrefix, string candidateSha) =>
        $"add-task --goal {goalPrefix} Tester Resolve pre-review mapping for candidate {candidateSha} --before-role Reviewer";

    private static IReadOnlyList<TaskSpec> TasksBefore(Goal goal, TaskSpec task)
        => goal.Tasks.TakeWhile(candidate => candidate.Id != task.Id).ToArray();

    private static int GetCurrentReviewerRoundNumber(Goal goal, TaskSpec reviewerTask) =>
        1 + goal.Timeline.Count(evt =>
            evt.Kind == ProgressKind.TaskRetried &&
            evt.TaskId is not null &&
            (evt.TaskId != reviewerTask.Id ||
                !MechanicalReviewerRetryMessagePrefixes.Any(prefix =>
                    evt.Message.StartsWith(prefix, StringComparison.Ordinal))));

    private static int FindArgument(IReadOnlyList<string> arguments, string value)
    {
        for (var index = 0; index < arguments.Count; index++)
        {
            if (arguments[index].Equals(value, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private static IReadOnlyList<string> ExtractFailingTestIdentities(
        IEnumerable<AcceptanceCheckResult> checks) =>
        checks
            .SelectMany(check => check.FailingTestIdentities ?? [])
            .Where(identity => identity.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static string? BuildPreReviewEvidencePointer(FocusedEvidenceRunResult evidence) =>
        evidence.Checks
            .SelectMany(check =>
                (check.TestResultPaths ?? [])
                    .Concat(string.IsNullOrWhiteSpace(check.ArtifactsPath) ? [] : [check.ArtifactsPath!]))
            .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
}
