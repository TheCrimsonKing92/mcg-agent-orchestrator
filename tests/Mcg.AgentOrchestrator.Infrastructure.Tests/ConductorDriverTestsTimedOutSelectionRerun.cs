using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;

// Unique artifact root, injected clock and process/runner seams; parallel-safe.
public sealed class ConductorDriverTestsTimedOutSelectionRerun
{
    private const string CandidateSha = "abc1234";
    private const string Selection = "reviewer-focused-evidence-infrastructure-tests-fullyqualifiedname-mtptestrunnerscripttests";
    private const string Request = "Infrastructure.Tests:MtpTestRunnerScriptTests";
    private const string Reason = FailedGoalTimedOutSelectionRerunRule.ReasonSlug;

    [Fact]
    public void PassingRerun_AttachesFreshReceiptDispatchesSameTesterOnceAndSurvivesRelaunch()
    {
        using var scenario = new Scenario();
        var result = scenario.Driver().AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Permissive);

        Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
        Assert.Equal(new[] { Request }, scenario.RunRequests);
        Assert.Equal(new[] { scenario.Tester.Id }, scenario.Retries);
        Assert.Equal(new[] { scenario.Tester.Id }, scenario.Dispatches);
        Assert.Equal(WorkTaskStatus.Completed, scenario.Owner.Status);
        var receipt = Assert.Single(scenario.Owner.LastVerification!.FindingEvidenceReceipts!
            .Where(item => item.ReceiptId != "original-receipt"));
        Assert.True(receipt.Passed);
        Assert.Equal(CandidateSha, receipt.CandidateSha);
        Assert.Equal("MtpTestRunnerScriptTests", Assert.Single(receipt.Request.Selections).TestClass);
        var inputs = scenario.Tester.LastDispatch!.InconclusiveRoundInputs!;
        Assert.Contains(receipt.ReceiptId, inputs.ReceiptIds);
        Assert.NotEqual(scenario.OriginalInputs, inputs);
        AssertTimelinePair(scenario.Goal, receipt.ReceiptId, "passed");

        // Finish this dispatch inconclusively, then finish another on its new inputs.
        // Recreating both kernel and coordinator exercises persisted state, not driver memory.
        scenario.CompleteTester();
        scenario.RetryAndCompleteTester();
        Assert.True(TesterInconclusiveRoundInputsReader.Read(scenario.Goal, scenario.Tester)!.InputsUnchanged);
        scenario.Reload();
        var afterRelaunch = scenario.Driver().AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Permissive);

        Assert.IsType<ConductorAdvanceOutcome.Escalated>(afterRelaunch.Outcome);
        Assert.Single(scenario.RunRequests);
        Assert.Single(scenario.Dispatches);
        Assert.Single(scenario.Retries);
        Assert.Contains("stayed verification-inconclusive on unchanged inputs", scenario.Escalations.Single());
        AssertTimelinePair(scenario.Goal, receipt.ReceiptId, "passed");
    }

    [Fact]
    public void RenewedTimeout_RecordsReceiptAndEscalatesWithoutTesterDispatch()
    {
        using var scenario = new Scenario();
        var result = scenario.Driver(timedOut: true).AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Permissive);

        Assert.IsType<ConductorAdvanceOutcome.Escalated>(result.Outcome);
        Assert.Equal(new[] { Request }, scenario.RunRequests);
        Assert.Empty(scenario.Dispatches);
        Assert.Empty(scenario.Retries);
        var receipt = Assert.Single(scenario.Owner.LastVerification!.FindingEvidenceReceipts!
            .Where(item => item.ReceiptId != "original-receipt"));
        Assert.False(receipt.Passed);
        AssertTimelinePair(scenario.Goal, receipt.ReceiptId, "timed-out");
        scenario.Reload();
        scenario.Driver().AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Permissive);
        Assert.Single(scenario.RunRequests);
        Assert.Empty(scenario.Dispatches);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnyRealFailureInCurrentReceipt_PreventsRerunIncludingBaselineFailure(bool baselineFailure)
    {
        using var scenario = new Scenario(realFailure: true, baselineFailure: baselineFailure);
        var result = scenario.Driver().AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Permissive);

        Assert.IsType<ConductorAdvanceOutcome.Escalated>(result.Outcome);
        Assert.Empty(scenario.RunRequests);
        Assert.Empty(scenario.Dispatches);
        Assert.DoesNotContain(scenario.Goal.Timeline, item => item.Message.StartsWith(Reason, StringComparison.Ordinal));
    }

    [Fact]
    public void RealFailureFromFreshRerun_IsAttachedAndReturnedToTheSameTester()
    {
        using var scenario = new Scenario();
        var result = scenario.Driver(realFailure: true).AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Permissive);
        Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
        Assert.Equal(new[] { Request }, scenario.RunRequests);
        Assert.Equal(new[] { scenario.Tester.Id }, scenario.Dispatches);
        var receipt = Assert.Single(scenario.Owner.LastVerification!.FindingEvidenceReceipts!
            .Where(item => item.ReceiptId != "original-receipt"));
        Assert.False(receipt.Passed);
        Assert.Contains(receipt.ReceiptId, scenario.Tester.LastDispatch!.InconclusiveRoundInputs!.ReceiptIds);
        AssertTimelinePair(scenario.Goal, receipt.ReceiptId, "failed");
    }

    [Fact]
    public void RequestMarkerWithoutItsAttempt_CannotAuthorizeReplacementLaunch()
    {
        using var scenario = new Scenario();
        var key = FailedGoalTimedOutSelectionRerunRule.BuildKey(Scenario.Candidate.Canonical, [Selection]);
        scenario.Kernel.RecordFindingEvidenceRequest(scenario.Goal.Id, scenario.Tester.Id,
            $"{Reason}; phase=request; key={key}; candidate={Scenario.Candidate.Canonical}; selections={Selection}");
        scenario.Reload();
        Assert.IsType<ConductorAdvanceOutcome.Escalated>(
            scenario.Driver().AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Permissive).Outcome);
        Assert.Empty(scenario.RunRequests);
        Assert.Empty(scenario.Dispatches);
    }

    [Fact]
    public void ReceiptPersistedBeforeRetry_RelaunchDeliversItWithoutRepeatingTheRun()
    {
        using var scenario = new Scenario();
        var interruption = Assert.Throws<InvalidOperationException>(() => scenario.Driver(interruptAfterReceipt: true)
            .AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Permissive));
        Assert.Equal("fixture interruption after durable receipt", interruption.Message);
        Assert.Single(scenario.RunRequests);
        Assert.Empty(scenario.Retries);
        scenario.Reload();
        Assert.IsType<ConductorAdvanceOutcome.Executed>(
            scenario.Driver().AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Permissive).Outcome);
        Assert.Single(scenario.RunRequests);
        Assert.Equal(new[] { scenario.Tester.Id }, scenario.Retries);
        Assert.Equal(new[] { scenario.Tester.Id }, scenario.Dispatches);
    }

    [Fact(Timeout = 30000)]
    [Trait("Category", "CrossTick")]
    public void RequestedBackgroundRerun_RelaunchObservesSameAttemptWithoutAnotherLaunch()
    {
        using var scenario = new Scenario();
        var launches = 0;
        ConductorParallelAcceptanceAttemptCoordinator Coordinator() => new(scenario.AttemptsRoot,
            isProcessAlive: pid => pid == 7103,
            launchOwnedProcess: _ => { launches++; return new(7103); },
            acquireStableSlotLease: (_, _) => null);
        var coordinator = Coordinator();
        var first = scenario.Driver(coordinator: coordinator).AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Permissive);
        Assert.IsType<ConductorAdvanceOutcome.Held>(first.Outcome);
        var attempt = Assert.Single(coordinator.GetUnreconciledAttempts([scenario.Goal.Id.Value]));
        Assert.Single(scenario.Goal.Timeline.Where(item => item.Kind == ProgressKind.FindingEvidenceRequestRecorded &&
            item.Message.StartsWith(Reason, StringComparison.Ordinal)));
        Assert.Equal(1, launches);

        scenario.Reload();
        coordinator = Coordinator();
        var driver = scenario.Driver(coordinator: coordinator);
        Assert.IsType<ConductorAdvanceOutcome.Held>(driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Permissive).Outcome);
        Assert.Equal(attempt.AttemptId, Assert.Single(coordinator.GetUnreconciledAttempts([scenario.Goal.Id.Value])).AttemptId);
        Assert.Equal(1, launches);
        Assert.Empty(scenario.RunRequests);

        var candidate = ConductorParallelAcceptanceCandidate.Create(scenario.Goal, 0, [], CandidateSha, null);
        coordinator.RunAttemptForTests(attempt, candidate, ConductorAutonomyPolicy.Permissive,
            (current, _, _, _, _) =>
            {
                scenario.RunRequests.Add(Request);
                return ConductorParallelAcceptanceRunResult.Focused(current, Evidence(Request));
            });
        Assert.IsType<ConductorAdvanceOutcome.Executed>(driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Permissive).Outcome);
        Assert.Equal(1, launches);
        Assert.Single(scenario.RunRequests);
        Assert.Equal(new[] { scenario.Tester.Id }, scenario.Dispatches);
        var receipt = Assert.Single(scenario.Owner.LastVerification!.FindingEvidenceReceipts!
            .Where(item => item.ReceiptId != "original-receipt"));
        AssertTimelinePair(scenario.Goal, receipt.ReceiptId, "passed");
    }

    private static void AssertTimelinePair(Goal goal, string receiptId, string outcome)
    {
        var request = Assert.Single(goal.Timeline.Where(item => item.Kind == ProgressKind.FindingEvidenceRequestRecorded &&
            item.Message.StartsWith(Reason, StringComparison.Ordinal)));
        var receipt = Assert.Single(goal.Timeline.Where(item => item.Kind == ProgressKind.FindingEvidenceRunRecorded &&
            item.Message.StartsWith(Reason, StringComparison.Ordinal)));
        Assert.Contains("selections=" + Selection, request.Message, StringComparison.Ordinal);
        Assert.Contains("receipt_id=" + receiptId, receipt.Message, StringComparison.Ordinal);
        Assert.Contains("outcome=" + outcome, receipt.Message, StringComparison.Ordinal);
        var key = FailedGoalTimedOutSelectionRerunRule.BuildKey(Scenario.Candidate.Canonical, [Selection]);
        Assert.Contains("key=" + key, request.Message, StringComparison.Ordinal);
        Assert.Contains("key=" + key, receipt.Message, StringComparison.Ordinal);
    }

    private static FocusedEvidenceRunResult Evidence(string request, bool timedOut = false,
        bool includePassedSelection = false, bool realFailure = false, bool baselineFailure = false,
        bool batchedSelection = false)
    {
        var checkName = "reviewer focused evidence: Infrastructure.Tests FullyQualifiedName~MtpTestRunnerScriptTests" +
            (batchedSelection ? "|FullyQualifiedName~SecondTimedOutTests" : "");
        var selection = Selection + (batchedSelection ? "-fullyqualifiedname-secondtimedouttests" : "");
        var checks = new List<AcceptanceCheckResult>
        {
            timedOut
                ? new($"acceptance-check-timeout: {selection} elapsed=40m budget=40m", false, -1, "no TRX",
                    FailureClassification: "timed-out")
                : new(checkName, true, 0, "passed")
        };
        var coverage = new List<FocusedEvidenceTargetCoverage> { new(Request, [checkName]) };
        if (batchedSelection)
            coverage.Add(new("Infrastructure.Tests:SecondTimedOutTests", [checkName]));
        if (includePassedSelection)
        {
            checks.Add(new("already passed selection", true, 0, "passed"));
            coverage.Add(new("Infrastructure.Tests:AlreadyPassedTests", ["already passed selection"]));
        }
        var failure = new AcceptanceCheckResult("real test failure", false, 2, "assertion failed",
            FailureClassification: "failing-trx");
        if (realFailure && !baselineFailure) checks.Add(failure);
        return new(request, true, !timedOut && !realFailure, "fake focused evidence", checks,
            Coverage: new(coverage),
            Arms: baselineFailure ? [
                new(FindingEvidenceArm.Candidate, CandidateSha, FindingEvidenceArmDisposition.Inconclusive,
                    true, false, "candidate timeout", checks),
                new(FindingEvidenceArm.Baseline, "aaa1111", FindingEvidenceArmDisposition.Red,
                    true, false, "baseline assertion failure", [failure])] : null);
    }

    internal sealed class Scenario : IDisposable
    {
        internal static readonly CandidateIdentity Candidate = new("patch", "base", "manifest");
        private readonly FixtureClock _clock = new();
        private readonly string _root = ConductorDriverTests.CreateTempDirectory();
        private readonly TaskId _testerId;
        private readonly TaskId _ownerId;
        private readonly GoalId _goalId;
        private readonly bool _batchedSelection;
        private readonly ReviewFinding? _testerFinding;
        private readonly FindingEvidenceReceipt[]? _testerReceipts;
        internal AgentOrchestratorKernel Kernel { get; private set; }
        internal Goal Goal => Kernel.GetGoal(_goalId);
        internal TaskSpec Tester => Goal.Tasks.Single(item => item.Id == _testerId);
        internal TaskSpec Owner => Goal.Tasks.Single(item => item.Id == _ownerId);
        internal string AttemptsRoot => Path.Combine(_root, "attempts");
        internal List<string> RunRequests { get; } = [];
        internal List<TaskId> Retries { get; } = [];
        internal List<TaskId> Dispatches { get; } = [];
        internal List<string> Escalations { get; } = [];
        internal FailedGoalInconclusiveRoundInputs OriginalInputs { get; }

        internal Scenario(bool realFailure = false, bool baselineFailure = false, bool batchedSelection = false,
            bool earlierCandidateReceipt = false, bool testerOwnsReceipt = false, bool missingSelectionMapping = false)
        {
            _batchedSelection = batchedSelection;
            Kernel = new(_clock);
            Kernel.ConfigureCandidateIdentityResolver(_ => Candidate);
            var owner = new TaskSpec(TaskId.New(), "Supply focused evidence", AgentRole.Developer);
            var tester = new TaskSpec(TaskId.New(), "Inspect focused evidence", AgentRole.Tester);
            var goal = Kernel.CreateGoal("Retry only the timed-out selection", [owner, tester]);
            _goalId = goal.Id;
            _ownerId = testerOwnsReceipt ? tester.Id : owner.Id;
            _testerId = tester.Id;
            Kernel.ActivateGoal(goal.Id, DefaultAgents());
            var finding = EvidenceFindingWithRequest("Inspect focused receipts", classes:
                batchedSelection ? ["MtpTestRunnerScriptTests", "SecondTimedOutTests", "AlreadyPassedTests"] :
                ["MtpTestRunnerScriptTests", "AlreadyPassedTests"]) with { Severity = FindingSeverity.Advisory };
            Kernel.RecordTaskDispatch(goal.Id, owner.Id, new("fixture", "evidence", _root, _clock.Next()));
            Kernel.RecordTaskVerification(goal.Id, owner.Id, new("evidence", _root, 0, "ok", "", _clock.Next(),
                MergedReviewFindings: [finding]));
            Assert.Equal(WorkTaskStatus.Completed, owner.Status);
            var originalRequest = Request + (batchedSelection ? "; Infrastructure.Tests:SecondTimedOutTests" : "") +
                "; Infrastructure.Tests:AlreadyPassedTests";
            var coordinator = InlineCoordinator();
            var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, [], CandidateSha, null);
            var dispositions = new[] { new FindingEvidenceRequestDisposition(finding.StableId, "initial-request", "executed", "initial") };
            if (earlierCandidateReceipt)
            {
                const string earlierSha = "aaa1111";
                var earlier = coordinator.EvaluateFocusedEvidence(
                    ConductorParallelAcceptanceCandidate.Create(goal, 0, [], earlierSha, null),
                    ConductorAutonomyPolicy.Permissive, originalRequest,
                    (_, request, _, _) => Evidence(request, includePassedSelection: true),
                    new("earlier-round", "earlier-batch", dispositions));
                Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Completed, earlier.Kind);
                Assert.True(coordinator.RecordFocusedEvidenceRequestDispositions(
                    earlier.Attempt, "earlier-round", "earlier-receipt", dispositions));
                coordinator.MarkReconciled(earlier.Attempt);
                Kernel.RecordFindingEvidenceOutcome(goal.Id, owner.Id, finding.StableId, new(true, "earlier-receipt"),
                    new("earlier-receipt", earlierSha, finding.EvidenceRequest!, true, true, "earlier passed evidence",
                        RequestDispositions: dispositions));
            }
            var seed = coordinator.EvaluateFocusedEvidence(candidate, ConductorAutonomyPolicy.Permissive,
                originalRequest, (_, request, _, _) => missingSelectionMapping
                    ? Evidence(request, true, true) with { Coverage = null }
                    : Evidence(request, true, true, realFailure, baselineFailure, batchedSelection),
                new("initial-round", "initial-batch", dispositions));
            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Completed, seed.Kind);
            Assert.True(coordinator.RecordFocusedEvidenceRequestDispositions(seed.Attempt, "initial-round", "original-receipt", dispositions));
            coordinator.MarkReconciled(seed.Attempt);
            var receipt = new FindingEvidenceReceipt("original-receipt", CandidateSha, finding.EvidenceRequest!,
                true, false, "original timeout", RequestDispositions: dispositions);
            if (testerOwnsReceipt)
            {
                _testerFinding = finding with { EvidenceOutcome = new(false, receipt.ReceiptId) };
                _testerReceipts = [receipt];
            }
            else
                Kernel.RecordFindingEvidenceOutcome(goal.Id, owner.Id, finding.StableId, new(false, receipt.ReceiptId), receipt);
            DispatchTester();
            CompleteTester();
            RetryAndCompleteTester();
            if (testerOwnsReceipt) RetryAndCompleteTester();
            Assert.True(TesterInconclusiveRoundInputsReader.Read(Goal, Tester)!.InputsUnchanged);
            Assert.Equal(GoalLifecycleState.Failed, GoalLifecycle.ResolveState(Goal, GoalLifecycleFacts.None));
            OriginalInputs = Tester.LastVerification!.InconclusiveRoundInputs!;
        }

        private ConductorParallelAcceptanceAttemptCoordinator InlineCoordinator() => new(AttemptsRoot,
            runInline: true, acquireStableSlotLease: (_, _) => null);

        internal ConductorDriver Driver(bool timedOut = false, ConductorParallelAcceptanceAttemptCoordinator? coordinator = null,
            bool realFailure = false, bool interruptAfterReceipt = false) =>
            MakeDriver(getPreReviewEvidenceContext: _ => NoPreReviewContext(CandidateSha),
                focusedEvidenceAttemptCoordinator: coordinator ?? InlineCoordinator(),
                runFocusedEvidence: (_, request) => { RunRequests.Add(request); return Evidence(request, timedOut,
                    realFailure: realFailure, batchedSelection: _batchedSelection); },
                retryTaskWithCause: (gid, tid, message, round, cause) =>
                {
                    Retries.Add(tid);
                    return Kernel.RetryTask(gid, tid, message, cause, retryRoundKind: round);
                },
                dispatchAndStart: current =>
                {
                    var task = Assert.Single(current.Tasks.Where(item => item.Status == WorkTaskStatus.Assigned));
                    Dispatches.Add(task.Id);
                    Assert.Equal(_testerId, task.Id);
                    DispatchTester();
                    return DispatchStartOutcome.Started();
                },
                writeEscalation: (_, _, message) => Escalations.Add(message),
                recordTaskNote: (gid, tid, message) => Kernel.RecordTaskNote(gid, tid, message),
                recordFindingEvidenceRequest: (gid, tid, message) => Kernel.RecordFindingEvidenceRequest(gid, tid, message),
                recordFindingEvidenceRun: (gid, tid, message) =>
                {
                    Kernel.RecordFindingEvidenceRun(gid, tid, message);
                    if (interruptAfterReceipt) throw new InvalidOperationException("fixture interruption after durable receipt");
                },
                recordFindingEvidenceOutcome: (gid, tid, id, outcome, receipt) =>
                    Kernel.RecordFindingEvidenceOutcome(gid, tid, id, outcome, receipt));

        private void DispatchTester() => Kernel.RecordTaskDispatch(_goalId, _testerId,
            new("fixture", "verify", _root, _clock.Next()));

        internal void CompleteTester()
        {
            const string output = "WORKER_RESULT:\nfiles: none\ntests: inconclusive - inspected focused receipts\nblockers: none\nEND_WORKER_RESULT";
            Kernel.RecordDispatchExecutionResult(_goalId, _testerId,
                new("verify", _root, 0, output, "", _clock.Next(), WorkerResultPresent: true,
                    MergedReviewFindings: _testerFinding is null ? null : [_testerFinding],
                    FindingEvidenceReceipts: _testerReceipts,
                    DispatchStartedAt: Tester.LastDispatch!.DispatchedAt));
            Assert.Equal(WorkTaskStatus.Failed, Tester.Status);
        }

        internal void RetryAndCompleteTester()
        {
            _clock.Next();
            Kernel.RetryTask(_goalId, _testerId, "fixture round", RetryCause.EnvironmentApparatusFailure);
            DispatchTester();
            CompleteTester();
        }

        internal void Reload()
        {
            Kernel = AgentOrchestratorKernel.FromSnapshot(JsonSerializer.Deserialize<OrchestratorSnapshot>(
                JsonSerializer.Serialize(Kernel.ExportSnapshot()))!, _clock);
            Kernel.ConfigureCandidateIdentityResolver(_ => Candidate);
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }

    private sealed class FixtureClock : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = new(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
        internal DateTimeOffset Next() => UtcNow = UtcNow.AddMinutes(1);
    }
}
