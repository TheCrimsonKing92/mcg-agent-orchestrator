using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    private bool TryRunDeferredNoChangeEvidence(
        Goal goal,
        string goalPrefix,
        ConductorAutonomyPolicy policy,
        GoalLifecycleState fromState,
        out ConductorAdvanceResult result)
    {
        result = default!;
        if (fromState is not (GoalLifecycleState.WorkspaceReady or
            GoalLifecycleState.Verifying or GoalLifecycleState.Verified)) return false;

        var developer = goal.Tasks.LastOrDefault(task =>
            task.RequiredRole == AgentRole.Developer && task.Status == WorkTaskStatus.Completed &&
            string.Equals(task.LastVerification?.CompletionVerdictRule,
                "deferred-no-change-round", StringComparison.Ordinal));
        if (developer?.LastVerification is null ||
            !DeferredNoChangeOutcome.TryParse(developer.LastVerification.StandardError, out var outcome))
            return false;

        var prior = DeferredNoChangeEvidenceIndexLines.Latest(goal, developer.Id, outcome.CandidateSha);
        if (prior is { Outcome: "green" })
        {
            if (prior.NotRun.Count == 0 && prior.Selections.Count == outcome.TestClasses.Count &&
                outcome.TestClasses.All(name => prior.Selections.Any(selection =>
                    selection.EndsWith(":" + name, StringComparison.OrdinalIgnoreCase))))
                return false;
            throw new InvalidDataException("Deferred no-change green receipt does not cover its declared classes.");
        }
        if (prior is { Outcome: "red" or "unusable" })
        {
            result = Escalate(goal, goalPrefix, policy, fromState,
                $"DEFERRED_NO_CHANGE_REPEAT_RED task={developer.Id.Value} candidate_sha={outcome.CandidateSha}; " +
                "the single candidate evidence run has already completed without green evidence");
            return true;
        }

        var currentSha = _getPreReviewEvidenceContext(goal).CandidateSha?.Trim();
        if (!_focusedEvidenceRunnerConfigured || _executionDirectory is null ||
            !string.Equals(currentSha, outcome.CandidateSha, StringComparison.OrdinalIgnoreCase))
        {
            result = MakeResult(goal.Id.Value, goalPrefix, policy,
                new ConductorAdvanceOutcome.Held(fromState,
                    $"Deferred no-change evidence awaits candidate {outcome.CandidateSha} and a focused runner."));
            return true;
        }

        var tester = goal.Tasks.FirstOrDefault(task => task.RequiredRole == AgentRole.Tester);
        if (tester is null)
            throw new InvalidDataException("Deferred no-change evidence has no Tester evidence owner.");
        var worktreePath = GoalWorktrees.WorktreePath(_executionDirectory, goal.Id);
        var declaration = DeveloperDeferredTestSelections.Resolve(
            worktreePath, "deferred - " + string.Join(", ", outcome.TestClasses));
        var settings = _getFindingEvidenceEngineSettings(goal);
        var selected = new List<FindingEvidenceSelection>();
        var notRun = new List<string>(declaration.NotRun);
        foreach (var selection in declaration.Selections)
        {
            if (TryNormalizeFindingEvidenceRequest(
                    new FindingEvidenceRequest([selection]), settings, (_, _) => [],
                    out var normalized, out _, out _, out _))
                selected.AddRange(normalized.Selections);
            else notRun.Add(selection.TestClass);
        }
        var distinct = selected.DistinctBy(FormatFindingEvidenceSelection).ToArray();
        var selectionNames = distinct.Select(FormatFindingEvidenceSelection).ToArray();
        if (prior is { Outcome: "started" } &&
            (!prior.Selections.SequenceEqual(selectionNames, StringComparer.OrdinalIgnoreCase) ||
             !prior.NotRun.SequenceEqual(notRun, StringComparer.OrdinalIgnoreCase)))
            throw new InvalidDataException("Deferred no-change selection changed during its candidate evidence run.");

        var identity = FindingEvidenceExecutionClassifier.BuildRequestIdentity(
            new FindingEvidenceRequest(distinct));
        var receiptId = CreateFindingEvidenceReceiptId(outcome.CandidateSha, "deferred-no-change", identity);
        if (distinct.Length == 0 || notRun.Count > 0)
        {
            RecordDeferredNoChangeEvidence("unusable", [], null);
            return RetryDeveloperForDeferredNoChange(
                goal, developer, outcome, receiptId, notRun, [], goalPrefix, policy, fromState, out result);
        }

        if (prior is null)
            _recordFindingEvidenceRequest(goal.Id, tester.Id,
                DeferredNoChangeEvidenceIndexLines.FormatMarker(new DeferredNoChangeEvidenceEntry(
                    "started", developer.Id, outcome.CandidateSha, receiptId,
                    selectionNames, notRun, null, [])));

        var requestContext = new ConductorFocusedEvidenceRequestContext(
            "deferred-no-change",
            CreateFindingEvidenceBatchId(outcome.CandidateSha, "deferred-no-change", policy.Name, identity),
            []);
        var completed = TryReconcileFocusedEvidenceAttempt(
            goal, policy, string.Join("; ", selectionNames), outcome.CandidateSha,
            "deferred-no-change", requestContext,
            out var evidence, out var attempt, out var decision, out var kind);
        var transient = attempt?.Outcome is
            ConductorParallelAcceptanceAttemptOutcome.BlockedBuildSlot or
            ConductorParallelAcceptanceAttemptOutcome.BlockedBuildLock or
            ConductorParallelAcceptanceAttemptOutcome.Cancelled or
            ConductorParallelAcceptanceAttemptOutcome.InfrastructureDeferred or
            ConductorParallelAcceptanceAttemptOutcome.StructuralCoveragePermitUnavailable or
            ConductorParallelAcceptanceAttemptOutcome.StaleCandidate;
        if (!completed &&
            (kind is not (ConductorParallelAcceptanceAttemptDecisionKind.Completed or
                          ConductorParallelAcceptanceAttemptDecisionKind.TerminalWithoutRun) ||
             transient ||
             kind == ConductorParallelAcceptanceAttemptDecisionKind.Completed &&
             decision.Kind == FailedGoalFindingObservationKind.FindingEvidencePending))
        {
            result = MakeResult(goal.Id.Value, goalPrefix, policy,
                new ConductorAdvanceOutcome.Held(fromState, decision.Evidence));
            return true;
        }

        var candidate = (evidence?.Arms ?? []).Select(CreateFindingEvidenceArmReceipt)
            .FirstOrDefault(arm => arm.Arm == FindingEvidenceArm.Candidate &&
                                   string.Equals(arm.Sha, outcome.CandidateSha, StringComparison.OrdinalIgnoreCase));
        var failingTests = candidate?.FailingTestIdentities ?? [];
        var green = completed && evidence.IsValidEvidence &&
            candidate is { Accepted: true, Passed: true, Disposition: FindingEvidenceArmDisposition.Green,
                ExecutedTestCount: > 0 } &&
            AcceptanceCohortGateEvidence.HasContentBoundGreenTrxEvidence(
                candidate.TestResultPaths, candidate.ReceiptArtifacts,
                candidate.ExecutedTestCount!.Value,
                distinct.Select(selection => selection.TestClass).ToHashSet(StringComparer.OrdinalIgnoreCase));
        var red = completed && evidence.Accepted &&
                  candidate is { Accepted: true, Disposition: FindingEvidenceArmDisposition.Red };
        RecordDeferredNoChangeEvidence(green ? "green" : red ? "red" : "unusable",
            failingTests, attempt?.ResultPath);
        if (green) return false;
        return RetryDeveloperForDeferredNoChange(
            goal, developer, outcome, receiptId, notRun, failingTests,
            goalPrefix, policy, fromState, out result);

        void RecordDeferredNoChangeEvidence(
            string verdict, IReadOnlyList<string> failures, string? resultPath) =>
            _recordFindingEvidenceRun(goal.Id, tester.Id,
                DeferredNoChangeEvidenceIndexLines.FormatMarker(new DeferredNoChangeEvidenceEntry(
                    verdict, developer.Id, outcome.CandidateSha, receiptId,
                    selectionNames, notRun, resultPath, failures)));
    }

    private bool RetryDeveloperForDeferredNoChange(
        Goal goal,
        TaskSpec developer,
        DeferredNoChangeOutcome outcome,
        string receiptId,
        IReadOnlyList<string> notRun,
        IReadOnlyList<string> failingTests,
        string goalPrefix,
        ConductorAutonomyPolicy policy,
        GoalLifecycleState fromState,
        out ConductorAdvanceResult result)
    {
        var feedback = failingTests.Count > 0
            ? AppendActionableCandidateRedFailureDetail(
                FormatActionableCandidateRedMessage(
                    outcome.CandidateSha, receiptId, "deferred-no-change", failingTests,
                    $"Originating deferred no-change task={developer.Id.Value}."),
                receiptId, failingTests, null)
            : $"DEFERRED_NO_CHANGE_EVIDENCE_UNUSABLE candidate_sha={outcome.CandidateSha}; " +
              $"receipt_id={receiptId}; not_run={string.Join(',', notRun)}; " +
              $"requested_classes={string.Join(',', outcome.TestClasses)}. Repair the class declaration or test evidence.";
        (_retryDeveloperAfterStructuralPreflight ?? _retryTask)(
            goal.Id, developer.Id, feedback, RetryRoundKind.Mechanical, RetryCause.NewTestFinding);
        var refreshed = GetCurrentGoal(goal);
        result = ExecuteDispatchAndStart(
            refreshed, goalPrefix, policy, GoalLifecycle.ResolveState(refreshed, GetFacts(refreshed)));
        return true;
    }
}
