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
        if (requestingFindings.Any(finding => finding.EvidenceRequest?.NegativeControl is not null)) return false;
        var tester = goal.Tasks.FirstOrDefault(task => task.RequiredRole == AgentRole.Tester);
        var receipt = PreTesterEvidenceIndexLines.Latest(
            goal, tester?.Id ?? requestingTask.Id, candidateSha);
        if (receipt is not { Outcome: "green" }) return false;

        var covered = receipt.Selections.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var settings = _getFindingEvidenceEngineSettings(goal);
        var normalizedRequests = new List<FindingEvidenceRequest>();
        foreach (var finding in requestingFindings)
        {
            if (finding.EvidenceRequest is null ||
                !TryNormalizeFindingEvidenceRequest(
                    finding.EvidenceRequest, settings,
                    (project, name) => _resolveFindingEvidenceSiblingClasses(goal, project, name),
                    out var normalized, out _, out _, out _) ||
                normalized.Selections.Any(selection =>
                    !covered.Contains(FormatFindingEvidenceSelection(selection))))
                return false;
            normalizedRequests.Add(normalized);
        }

        var request = new FindingEvidenceRequest(normalizedRequests
            .SelectMany(normalized => normalized.Selections)
            .DistinctBy(FormatFindingEvidenceSelection)
            .ToArray());
        var attachedReceipt = new FindingEvidenceReceipt(
            receipt.ReceiptId, candidateSha, request, Accepted: true, Passed: true,
            Summary: $"Pre-Tester focused evidence {receipt.Outcome}; result_path={receipt.ResultPath ?? "none"}",
            RequestDispositions: requestingFindings.Select(finding =>
                new FindingEvidenceRequestDisposition(
                    finding.StableId,
                    FindingEvidenceExecutionClassifier.BuildRequestIdentity(finding.EvidenceRequest!),
                    "executed-pre-tester")).ToArray());
        foreach (var finding in requestingFindings)
        {
            var requestIdentity = FindingEvidenceExecutionClassifier.BuildRequestIdentity(finding.EvidenceRequest!);
            _recordFindingEvidenceOutcome(
                goal.Id, requestingTask.Id, finding.StableId,
                new FindingEvidenceOutcome(
                    Honoured: true, ReceiptId: receipt.ReceiptId,
                    ResultReason: FindingEvidenceOutcomeReason.ValidEvidence,
                    RequestedSelectionIdentity: requestIdentity,
                    DecisionReason: "pre-tester-covered"),
                attachedReceipt);
        }

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
        var decline = EvaluatePreTesterDeferredEvidenceGuard(developer, candidateSha, out var testsField);
        if (decline != PreTesterDeferredEvidenceDeclineClause.None)
        {
            RecordPreTesterDeferredEvidenceDecline(goal, developer, decline, candidateSha);
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
            RecordPreTesterDeferredEvidenceExit(goal, developer, testsField,
                PreTesterDeferredEvidenceExit.PriorOutcomeNotStarted, prior.Outcome,
                DeveloperDeferredTestClassNames.Parse(testsField));
            return false;
        }
        var declaration = DeveloperDeferredTestSelections.Resolve(worktreePath, testsField);
        if (declaration.Selections.Count == 0)
        {
            if (prior?.Outcome == "started")
                throw new InvalidDataException("Started pre-Tester evidence lost all selectable classes.");
            RecordPreTesterDeferredEvidenceExit(goal, developer, testsField,
                PreTesterDeferredEvidenceExit.NoResolvedClasses, prior?.Outcome, declaration.NotRun);
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
            RecordPreTesterDeferredEvidenceExit(goal, developer, testsField,
                PreTesterDeferredEvidenceExit.NoNormalizedClasses, prior?.Outcome, notRun);
            return false;
        }

        foreach (var selection in PreTesterNamedTestClasses.Select(goal, tester, worktreePath))
        {
            if (TryNormalizeFindingEvidenceRequest(
                    new FindingEvidenceRequest([selection]), settings,
                    (_, _) => [],
                    out var normalized, out _, out _, out _))
                selected.AddRange(normalized.Selections);
        }

        // Open requests follow Developer declarations, then brief- and Planner-named classes.
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
        foreach (var selection in PreTesterAlwaysRunGuardTestClasses.Select(worktreePath))
        {
            if (TryNormalizeFindingEvidenceRequest(
                    new FindingEvidenceRequest([selection]), settings,
                    (_, _) => [],
                    out var normalized, out _, out _, out _))
                selected.AddRange(normalized.Selections);
        }
        var distinct = selected.DistinctBy(FormatFindingEvidenceSelection).ToArray();
        // Pre-change attempts keep their original request identity and per-class TRX obligation.
        if (prior?.Outcome == "started")
            distinct = distinct.Where(selection =>
                !PreTesterAlwaysRunGuardTestClasses.ContainsClass(selection.TestClass) ||
                prior.Selections.Contains(FormatFindingEvidenceSelection(selection), StringComparer.OrdinalIgnoreCase))
                .ToArray();
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
                FocusedEvidencePendingHeld(fromState, decision, kind));
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
        var unlistedFailures = failingTests.Where(test => !PreTesterAlwaysRunGuardTestClasses.IsListed(test)).ToArray();
        var actionableRed = candidateRed && failingTests.Count > 0 &&
                            (baseline is null
                                ? unlistedFailures.Length == 0 ||
                                  EveryFailingTestIsInsideCandidateChanges(goal, attributionBatch, unlistedFailures)
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

        var feedback = AppendActionableCandidateRedFailureDetail(
            FormatActionableCandidateRedMessage(candidateSha!, receiptId, "pre-tester-deferred", failingTests),
            receiptId, failingTests, candidate?.TestResultPaths);
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
        var lastOperatorAnswer = LastOperatorRedLoopAnswerIndex(goal);
        var redHistory = goal.Timeline
            .Where((evt, index) => index > lastOperatorAnswer)
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

    private static int LastOperatorRedLoopAnswerIndex(Goal goal)
    {
        var lastEscalation = goal.Timeline
            .Select((evt, index) => (evt, index))
            .Where(item => item.evt.TaskId is null && item.evt.Kind == ProgressKind.GoalPolicyDecision &&
                           item.evt.Message.Contains("PRE_TESTER_RED_LOOP:", StringComparison.Ordinal))
            .Select(item => item.index).DefaultIfEmpty(-1).Max();
        if (lastEscalation < 0) return -1;

        for (var index = goal.Timeline.Count - 1; index > lastEscalation; index--)
        {
            var evt = goal.Timeline[index];
            if (evt.Kind == ProgressKind.TaskRetried &&
                !evt.Message.StartsWith("ACTIONABLE_CANDIDATE_RED ", StringComparison.Ordinal) &&
                goal.Tasks.Any(task => task.Id == evt.TaskId && task.RequiredRole == AgentRole.Developer))
                return index;
        }
        return -1;
    }
}
