using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    internal Func<Goal, IReadOnlyList<string>, IReadOnlyDictionary<string, IReadOnlyList<string>>>? PreTesterFailureHistory { get; set; }

    private PreTesterOutOfDiffRerun OutOfDiffRerun(string worktree) => new(worktree, _executionDirectory,
        _getLandingFileScopes, PreTesterFailureHistory ?? ((goal, identities) => CrossGoalTestFailureHistory.Read(
            _cohortWorkspace, (_cohortKernel ?? _conductorTickKernel)?.Goals ?? [], goal, identities, _utcNow())),
        TryReconcileFocusedEvidenceAttempt, _recordFindingEvidenceRequest, _recordFindingEvidenceRun,
        CreateFindingEvidenceBatchId, CreateFindingEvidenceReceiptId);

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

        var covered = FindingEvidenceExecutedCoverage.ExecutedSelections(receipt.Selections, receipt.NotRun).ToHashSet(StringComparer.OrdinalIgnoreCase);
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
        var priorEvidence = prior is { Outcome: "unusable" } ? _focusedEvidenceAttemptCoordinator.ReadFocusedReceiptEvidence(goal.Id, prior.ReceiptId, candidateSha!) : null;
        var apparatusUnusable = PreTesterApparatusReceiptClassifier.IsApparatusUnusable(priorEvidence);
        var refreshCount = PreTesterEvidenceRefreshRule.CountForCandidate(goal, tester.Id, candidateSha!);
        var superseded = PreTesterEvidenceRefreshRule.Decide(prior, apparatusUnusable, tester.PendingRetryCause, refreshCount) == PreTesterEvidenceRefreshDecision.Refresh ||
            PreTesterEvidenceRefreshRule.DecideBuildPhaseRefresh(prior, apparatusUnusable, PreTesterApparatusReceiptClassifier.IsBuildPhaseOnly(priorEvidence), refreshCount) == PreTesterEvidenceRefreshDecision.Refresh ? prior!.ReceiptId : null;
        if (superseded is not null) prior = null;
        if (prior is not null && prior.Outcome != "started")
        {
            if (prior.Outcome == "actionable-red")
            {
                if (TryEscalatePreTesterRedLoop(goal, goalPrefix, policy, fromState, out result)) return true;
                throw new InvalidDataException("Actionable pre-Tester RED has no Developer retry or escalation.");
            }
            if (prior.Outcome == "red" && OutOfDiffRerun(worktreePath).TryRun(goal, tester, prior, policy, out var priorHold, out var priorHoldKind))
            {
                result = MakeResult(goal.Id.Value, goalPrefix, policy, FocusedEvidencePendingHeld(fromState, priorHold, priorHoldKind));
                return true;
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
        foreach (var selection in PreTesterAlwaysRunGuardTestClasses.Select(worktreePath).Concat(PreTesterTestSourceGuardClasses.Select(worktreePath, _getLandingFileScopes(goal))))
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
                !(PreTesterAlwaysRunGuardTestClasses.ContainsClass(selection.TestClass) || PreTesterTestSourceGuardClasses.ContainsClass(selection.TestClass)) ||
                prior.Selections.Contains(FormatFindingEvidenceSelection(selection), StringComparer.OrdinalIgnoreCase))
                .ToArray();
        var request = string.Join("; ", distinct.Select(FormatFindingEvidenceSelection));
        var selectionNames = distinct.Select(FormatFindingEvidenceSelection).ToArray();
        if (prior?.Outcome == "started" &&
            (!prior.Selections.SequenceEqual(selectionNames, StringComparer.OrdinalIgnoreCase) ||
             !prior.NotRun.SequenceEqual(notRun, StringComparer.Ordinal)))
            throw new InvalidDataException("Started pre-Tester evidence selection changed before reconciliation.");
        var identity = FindingEvidenceExecutionClassifier.BuildRequestIdentity(new FindingEvidenceRequest(distinct));
        var refresh = PreTesterEvidenceRefreshRule.LatestForCandidate(goal, tester.Id, candidateSha!);
        var refreshId = CreateFindingEvidenceReceiptId(candidateSha!, "pre-tester-deferred-refresh", identity);
        var salt = superseded is not null || prior?.ReceiptId == refresh?.FreshReceiptId && prior?.ReceiptId == refreshId
            ? "pre-tester-deferred-refresh" : "pre-tester-deferred";
        var receiptId = CreateFindingEvidenceReceiptId(candidateSha!, salt, identity);
        if (superseded is not null)
        {
            refresh = new(candidateSha!, superseded, receiptId);
            _recordFindingEvidenceRequest(goal.Id, tester.Id, PreTesterEvidenceRefreshRule.FormatMarker(refresh));
        }
        var requestContext = new ConductorFocusedEvidenceRequestContext(
            "pre-tester-deferred",
            CreateFindingEvidenceBatchId(candidateSha!, salt, policy.Name, identity),
            []);
        var completed = TryReconcileFocusedEvidenceAttempt(
            goal, policy, request, candidateSha, "pre-tester-deferred", requestContext,
            out var evidence, out var attempt, out var decision, out var kind);
        var transientFailure = PreTesterApparatusReceiptClassifier.IsTransientAttemptOutcome(attempt?.Outcome);
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
        var liveHead = _getPreReviewEvidenceContext(goal).CandidateSha?.Trim();
        if (PreTesterDeferredEvidenceReads.IsSupersededPreTesterCandidate(worktreePath, candidateSha, liveHead))
        {
            decision = FailedGoalFindingObservation.Observed(
                FailedGoalFindingObservationKind.FindingEvidencePending,
                $"Pre-Tester focused evidence for candidate {FormatShortSha(candidateSha)} " +
                $"superseded by live head {FormatShortSha(liveHead)}; re-running at the live head.");
            result = MakeResult(goal.Id.Value, goalPrefix, policy,
                FocusedEvidencePendingHeld(fromState, decision, kind));
            return true;
        }

        var attributionBatch = new FindingEvidenceBatch(
            identity, request, new FindingEvidenceRequest(distinct), [], []);
        var (outcome, candidate, failingTests) = PreTesterApparatusReceiptClassifier.ClassifyOutcome(evidence, completed, candidateSha, distinct,
            failures => EveryFailingTestIsInsideCandidateChanges(goal, attributionBatch, failures));
        if (outcome == "unusable" && attempt is not null)
            _focusedEvidenceAttemptCoordinator.RecordFocusedEvidenceRequestDispositions(attempt, requestContext.FindingRoundFingerprint, receiptId, []);
        _recordFindingEvidenceRun(goal.Id, tester.Id, PreTesterFailureSignatureReader.AppendTo(
            PreTesterEvidenceIndexLines.FormatMarker(new PreTesterEvidenceEntry(
                outcome, candidateSha!, receiptId, selectionNames, notRun,
                attempt?.ResultPath, failingTests)), outcome, failingTests, candidate?.TestResultPaths));
        if (PreTesterEvidenceRefreshRule.DecideAfterRun(refresh, receiptId, outcome) == PreTesterEvidenceRefreshDecision.EscalateRepeat)
        {
            result = Escalate(goal, goalPrefix, policy, fromState, PreTesterEvidenceRefreshRule.DescribeRepeat(candidateSha!, refresh!.SupersededReceiptId, receiptId));
            return true;
        }
        if (outcome == "unusable" && PreTesterApparatusReceiptClassifier.IsBuildPhaseOnly(evidence) &&
            PreTesterEvidenceRefreshRule.CountForCandidate(goal, tester.Id, candidateSha!) == 0)
        {
            result = MakeResult(goal.Id.Value, goalPrefix, policy, new ConductorAdvanceOutcome.Held(fromState,
                $"PRE_TESTER_BUILD_PHASE_REFRESH: candidate_sha={candidateSha}; receipt_id={receiptId}"));
            return true;
        }
        if (outcome == "red" && OutOfDiffRerun(worktreePath).TryRun(GetCurrentGoal(goal), tester,
                new(outcome, candidateSha!, receiptId, selectionNames, notRun, attempt?.ResultPath, failingTests), policy, out var hold, out var holdKind))
        {
            result = MakeResult(goal.Id.Value, goalPrefix, policy, FocusedEvidencePendingHeld(fromState, hold, holdKind));
            return true;
        }
        if (outcome != "actionable-red") return false;
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
        var lastOperatorAnswer = PreTesterDeferredEvidenceReads.LastOperatorRedLoopAnswerIndex(goal);
        var redHistory = goal.Timeline
            .Where((evt, index) => index > lastOperatorAnswer)
            .Where(evt => evt.TaskId == tester.Id && evt.Kind == ProgressKind.FindingEvidenceRunRecorded &&
                          evt.Message.StartsWith("finding-evidence pre-tester outcome=actionable-red;", StringComparison.Ordinal))
            .ToArray();
        var lastTesterDispatch = goal.Timeline
            .Where(evt => evt.TaskId == tester.Id && evt.Kind == ProgressKind.TaskDispatchRecorded)
            .Select(evt => evt.OccurredAt).DefaultIfEmpty(DateTimeOffset.MinValue).Max();
        var window = redHistory.Where(evt => evt.OccurredAt > lastTesterDispatch).ToArray();
        var decision = PreTesterRedLoopRule.EvaluateRuns(
            window.Select(evt => PreTesterFailureSignature.ReadRun(evt.Message)).ToArray());
        if (!decision.Trip) return false;
        result = Escalate(goal, goalPrefix, policy, fromState,
            "PRE_TESTER_RED_LOOP: three consecutive candidate RED runs without Tester dispatch; " +
            $"failing_sets={string.Join(" | ", window.Select(evt => evt.Message))}; " +
            $"failing_counts={string.Join(',', decision.FailingCounts)}; trip={decision.Kind}");
        return true;
    }

}
