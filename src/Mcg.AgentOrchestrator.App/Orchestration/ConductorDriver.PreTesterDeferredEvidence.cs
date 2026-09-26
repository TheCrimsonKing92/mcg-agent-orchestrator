using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
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
        if (!_focusedEvidenceRunnerConfigured ||
            !ConductorGitRevisionReader.IsValid(candidateSha) ||
            !ConductorGitRevisionReader.IsValid(developer.LastDispatch?.BaseCommit) ||
            string.Equals(candidateSha, developer.LastDispatch.BaseCommit, StringComparison.OrdinalIgnoreCase) ||
            !WorkerResultBlockers.TryGetTestsStatus(developer.LastVerification, out var status) ||
            status != WorkerResultBlockers.TestsStatus.Deferred ||
            !WorkerResultBlockers.TryFindTests(developer.LastVerification, out var testsField))
        {
            return false;
        }

        var prior = PreTesterEvidenceIndexLines.Latest(goal, tester.Id, candidateSha);
        if (prior is not null && prior.Outcome != "started") return false;
        var declaration = DeveloperDeferredTestSelections.Resolve(worktreePath, testsField);
        if (declaration.Selections.Count == 0) return false;

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
        if (selected.Count == 0) return false;

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
        var identity = FindingEvidenceExecutionClassifier.BuildRequestIdentity(new FindingEvidenceRequest(distinct));
        var receiptId = CreateFindingEvidenceReceiptId(candidateSha!, "pre-tester-deferred", identity);
        var requestContext = new ConductorFocusedEvidenceRequestContext(
            "pre-tester-deferred",
            CreateFindingEvidenceBatchId(candidateSha!, "pre-tester-deferred", policy.Name, identity),
            []);
        var completed = TryReconcileFocusedEvidenceAttempt(
            goal, policy, request, candidateSha, "pre-tester-deferred", requestContext,
            out var evidence, out var attempt, out var decision, out var kind);
        if (!completed && kind is not (ConductorParallelAcceptanceAttemptDecisionKind.Completed or
                                        ConductorParallelAcceptanceAttemptDecisionKind.TerminalWithoutRun))
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
        var actionableRed = candidateRed &&
                            failingTests.Count > 0 &&
                            baseline?.Disposition != FindingEvidenceArmDisposition.Red;
        var green = completed && evidence.IsValidEvidence &&
                    candidate is { Accepted: true, Passed: true, Disposition: FindingEvidenceArmDisposition.Green,
                        ExecutedTestCount: > 0 } &&
                    AcceptanceCohortGateEvidence.HasContentBoundGreenTrxEvidence(
                        candidate.TestResultPaths, candidate.ReceiptArtifacts,
                        candidate.ExecutedTestCount!.Value,
                        distinct.Select(selection => selection.TestClass).ToHashSet(StringComparer.OrdinalIgnoreCase));
        var outcome = green ? "green" : candidateRed ? "red" : "unusable";
        _recordFindingEvidenceRun(goal.Id, tester.Id,
            PreTesterEvidenceIndexLines.FormatMarker(new PreTesterEvidenceEntry(
                outcome, candidateSha!, receiptId, selectionNames, notRun,
                attempt?.ResultPath, failingTests)));

        if (!actionableRed) return false;
        var redHistory = GetCurrentGoal(goal).Timeline
            .Where(evt => evt.TaskId == tester.Id && evt.Kind == ProgressKind.FindingEvidenceRunRecorded &&
                          evt.Message.StartsWith("finding-evidence pre-tester outcome=red;", StringComparison.Ordinal))
            .ToArray();
        var lastTesterDispatch = goal.Timeline
            .Where(evt => evt.TaskId == tester.Id && evt.Kind == ProgressKind.TaskDispatchRecorded)
            .Select(evt => evt.OccurredAt).DefaultIfEmpty(DateTimeOffset.MinValue).Max();
        var consecutiveRed = redHistory.Count(evt => evt.OccurredAt > lastTesterDispatch);
        if (consecutiveRed >= 3)
        {
            result = Escalate(goal, goalPrefix, policy, fromState,
                $"PRE_TESTER_RED_LOOP: three consecutive candidate RED runs without Tester dispatch; " +
                $"failing_sets={string.Join(" | ", redHistory.TakeLast(3).Select(evt => evt.Message))}");
            return true;
        }

        var feedback = FormatActionableCandidateRedMessage(
            candidateSha!, receiptId, "pre-tester-deferred", failingTests);
        (_retryDeveloperAfterStructuralPreflight ?? _retryTask)(
            goal.Id, developer.Id, feedback, RetryRoundKind.Mechanical, RetryCause.NewSourceFinding);
        var refreshed = GetCurrentGoal(goal);
        result = ExecuteDispatchAndStart(
            refreshed, goalPrefix, policy, GoalLifecycle.ResolveState(refreshed, GetFacts(refreshed)));
        return true;
    }
}
