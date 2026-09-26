using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    private bool TryRouteCoveredPreTesterRequest(
        Goal goal,
        TaskSpec requestingTask,
        string candidateSha,
        IReadOnlyList<ReviewFinding> requestingFindings,
        out FailedGoalFindingObservation decision)
    {
        decision = FailedGoalFindingObservation.None;
        var tester = goal.Tasks.FirstOrDefault(task => task.RequiredRole == AgentRole.Tester);
        var receipt = PreTesterEvidenceIndexLines.Latest(
            goal, tester?.Id ?? requestingTask.Id, candidateSha);
        if (receipt is not { Outcome: "green" or "red" }) return false;

        var covered = receipt.Selections.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (requestingFindings.Any(finding =>
                finding.EvidenceRequest?.Selections is not { Count: > 0 } selections ||
                selections.Any(selection => !covered.Contains(FormatFindingEvidenceSelection(selection)))))
            return false;

        decision = BuildCappedFindingEvidenceDeliveryRetry(
            goal, requestingTask, candidateSha, requestingFindings,
            [receipt.ReceiptId],
            "The candidate-bound pre-Tester receipt covers every requested selection; " +
            "its result and any not-run classes are in the evidence index.");
        return true;
    }

    private bool TryRunPreTesterDeferredEvidence(
        Goal goal,
        TaskSpec developer,
        TaskSpec tester,
        string worktreePath,
        string? candidateSha,
        string goalPrefix,
        ConductorAutonomyPolicy policy,
        GoalLifecycleState fromState,
        out ConductorAdvanceResult result)
    {
        result = default!;
        var prior = PreTesterEvidenceIndexLines.Latest(goal, tester.Id, candidateSha);
        if (!_focusedEvidenceRunnerConfigured ||
            !ConductorGitRevisionReader.IsValid(candidateSha) ||
            !ConductorGitRevisionReader.IsValid(developer.LastDispatch?.BaseCommit) ||
            string.Equals(candidateSha, developer.LastDispatch.BaseCommit, StringComparison.OrdinalIgnoreCase) ||
            !WorkerResultBlockers.TryGetTestsStatus(developer.LastVerification, out var status) ||
            status != WorkerResultBlockers.TestsStatus.Deferred ||
            !WorkerResultBlockers.TryFindTests(developer.LastVerification, out var testsField))
        {
            if (prior?.Outcome == "started")
                throw new InvalidDataException("Started pre-Tester evidence lost its Developer declaration or candidate binding.");
            return false;
        }

        if (prior is not null && prior.Outcome != "started")
        {
            if (prior.Outcome == "actionable-red")
            {
                if (TryEscalatePreTesterRedLoop(goal, goalPrefix, policy, fromState, out result)) return true;
                throw new InvalidDataException("Actionable pre-Tester RED has no Developer retry or escalation.");
            }
            return false;
        }
        var declaration = DeveloperDeferredTestSelections.Resolve(worktreePath, testsField);
        if (declaration.Selections.Count == 0)
        {
            if (prior?.Outcome == "started")
                throw new InvalidDataException("Started pre-Tester evidence lost all selectable classes.");
            return false;
        }

        var settings = _getFindingEvidenceEngineSettings(goal);
        var selected = new List<FindingEvidenceSelection>();
        var notRun = new List<string>(declaration.NotRun);
        foreach (var selection in declaration.Selections)
        {
            if (TryNormalizeFindingEvidenceRequest(
                    new FindingEvidenceRequest([selection]), settings,
                    (_, _) => [],
                    out var normalized, out _, out _, out _))
                selected.AddRange(normalized.Selections);
            else
                notRun.Add(selection.TestClass);
        }
        if (selected.Count == 0)
        {
            if (prior?.Outcome == "started")
                throw new InvalidDataException("Started pre-Tester evidence lost its normalized selection.");
            return false;
        }

        // Open requests remain part of the same candidate run, with Developer declarations first.
        foreach (var finding in goal.Tasks
                     .Where(task => task.RequiredRole is AgentRole.Tester or AgentRole.Reviewer)
                     .SelectMany(task => task.LastVerification?.MergedReviewFindings ?? [])
                     .Where(finding => finding.State == ReviewFindingState.Open && finding.EvidenceRequest is not null))
        {
            foreach (var selection in finding.EvidenceRequest!.Selections)
            {
                if (TryNormalizeFindingEvidenceRequest(
                        new FindingEvidenceRequest([selection]), settings,
                        (project, name) => _resolveFindingEvidenceSiblingClasses(goal, project, name),
                        out var normalized, out _, out _, out _))
                    selected.AddRange(normalized.Selections);
            }
        }
        var distinct = selected.DistinctBy(FormatFindingEvidenceSelection).ToArray();
        var request = string.Join("; ", distinct.Select(FormatFindingEvidenceSelection));
        var selectionNames = distinct.Select(FormatFindingEvidenceSelection).ToArray();
        if (prior?.Outcome == "started" &&
            (!prior.Selections.SequenceEqual(selectionNames, StringComparer.OrdinalIgnoreCase) ||
             !prior.NotRun.SequenceEqual(notRun, StringComparer.Ordinal)))
            throw new InvalidDataException("Started pre-Tester evidence selection changed before reconciliation.");
        var identity = FindingEvidenceExecutionClassifier.BuildRequestIdentity(new FindingEvidenceRequest(distinct));
        var receiptId = CreateFindingEvidenceReceiptId(candidateSha!, "pre-tester-deferred", identity);
        var requestContext = new ConductorFocusedEvidenceRequestContext(
            "pre-tester-deferred",
            CreateFindingEvidenceBatchId(candidateSha!, "pre-tester-deferred", policy.Name, identity),
            []);
        var completed = TryReconcileFocusedEvidenceAttempt(
            goal, policy, request, candidateSha, "pre-tester-deferred", requestContext,
            out var evidence, out var attempt, out var decision, out var kind);
        var transientFailure = attempt?.Outcome is
            ConductorParallelAcceptanceAttemptOutcome.BlockedBuildSlot or
            ConductorParallelAcceptanceAttemptOutcome.BlockedBuildLock or
            ConductorParallelAcceptanceAttemptOutcome.Cancelled or
            ConductorParallelAcceptanceAttemptOutcome.InfrastructureDeferred or
            ConductorParallelAcceptanceAttemptOutcome.StructuralCoveragePermitUnavailable or
            ConductorParallelAcceptanceAttemptOutcome.StaleCandidate;
        if (!completed &&
            (kind is not (ConductorParallelAcceptanceAttemptDecisionKind.Completed or
                          ConductorParallelAcceptanceAttemptDecisionKind.TerminalWithoutRun) ||
             transientFailure ||
             kind == ConductorParallelAcceptanceAttemptDecisionKind.Completed &&
             decision.Kind == FailedGoalFindingObservationKind.FindingEvidencePending))
        {
            if (kind == ConductorParallelAcceptanceAttemptDecisionKind.Started && prior is null)
                _recordFindingEvidenceRequest(goal.Id, tester.Id,
                    PreTesterEvidenceIndexLines.FormatMarker(new PreTesterEvidenceEntry(
                        "started", candidateSha!, receiptId, selectionNames, notRun, null, [])));
            result = MakeResult(goal.Id.Value, goalPrefix, policy,
                new ConductorAdvanceOutcome.Held(fromState, decision.Evidence));
            return true;
        }

        var candidate = (evidence?.Arms ?? []).Select(CreateFindingEvidenceArmReceipt)
            .FirstOrDefault(arm => arm.Arm == FindingEvidenceArm.Candidate &&
                                   string.Equals(arm.Sha, candidateSha, StringComparison.OrdinalIgnoreCase));
        var baseline = (evidence?.Arms ?? []).Select(CreateFindingEvidenceArmReceipt)
            .FirstOrDefault(arm => arm.Arm == FindingEvidenceArm.Baseline);
        var failingTests = candidate?.FailingTestIdentities ?? [];
        var candidateRed = completed && evidence.Accepted &&
                           candidate is { Accepted: true, Disposition: FindingEvidenceArmDisposition.Red };
        var attributionBatch = new FindingEvidenceBatch(
            identity, request, new FindingEvidenceRequest(distinct), [], []);
        var actionableRed = candidateRed && failingTests.Count > 0 &&
                            (baseline is null
                                ? EveryFailingTestIsInsideCandidateChanges(goal, attributionBatch, failingTests)
                                : baseline.Disposition == FindingEvidenceArmDisposition.Green);
        var green = completed && evidence.IsValidEvidence &&
                    candidate is { Accepted: true, Passed: true, Disposition: FindingEvidenceArmDisposition.Green,
                        ExecutedTestCount: > 0 } &&
                    AcceptanceCohortGateEvidence.HasContentBoundGreenTrxEvidence(
                        candidate.TestResultPaths, candidate.ReceiptArtifacts,
                        candidate.ExecutedTestCount!.Value,
                        distinct.Select(selection => selection.TestClass).ToHashSet(StringComparer.OrdinalIgnoreCase));
        var outcome = green ? "green" : actionableRed ? "actionable-red" : candidateRed ? "red" : "unusable";
        _recordFindingEvidenceRun(goal.Id, tester.Id,
            PreTesterEvidenceIndexLines.FormatMarker(new PreTesterEvidenceEntry(
                outcome, candidateSha!, receiptId, selectionNames, notRun,
                attempt?.ResultPath, failingTests)));

        if (!actionableRed) return false;
        if (TryEscalatePreTesterRedLoop(GetCurrentGoal(goal), goalPrefix, policy, fromState, out result))
            return true;

        var feedback = FormatActionableCandidateRedMessage(
            candidateSha!, receiptId, "pre-tester-deferred", failingTests);
        (_retryDeveloperAfterStructuralPreflight ?? _retryTask)(
            goal.Id, developer.Id, feedback, RetryRoundKind.Mechanical, RetryCause.NewSourceFinding);
        var refreshed = GetCurrentGoal(goal);
        result = ExecuteDispatchAndStart(
            refreshed, goalPrefix, policy, GoalLifecycle.ResolveState(refreshed, GetFacts(refreshed)));
        return true;
    }

    private bool TryEscalatePreTesterRedLoop(
        Goal goal,
        string goalPrefix,
        ConductorAutonomyPolicy policy,
        GoalLifecycleState fromState,
        out ConductorAdvanceResult result)
    {
        result = default!;
        var tester = goal.Tasks.First(task => task.RequiredRole == AgentRole.Tester);
        var redHistory = goal.Timeline
            .Where(evt => evt.TaskId == tester.Id && evt.Kind == ProgressKind.FindingEvidenceRunRecorded &&
                          evt.Message.StartsWith("finding-evidence pre-tester outcome=actionable-red;", StringComparison.Ordinal))
            .ToArray();
        var lastTesterDispatch = goal.Timeline
            .Where(evt => evt.TaskId == tester.Id && evt.Kind == ProgressKind.TaskDispatchRecorded)
            .Select(evt => evt.OccurredAt).DefaultIfEmpty(DateTimeOffset.MinValue).Max();
        var consecutiveRed = redHistory.Count(evt => evt.OccurredAt > lastTesterDispatch);
        if (consecutiveRed < 3) return false;
        result = Escalate(goal, goalPrefix, policy, fromState,
            "PRE_TESTER_RED_LOOP: three consecutive candidate RED runs without Tester dispatch; " +
            $"failing_sets={string.Join(" | ", redHistory.Where(evt => evt.OccurredAt > lastTesterDispatch).TakeLast(3).Select(evt => evt.Message))}");
        return true;
    }
}
