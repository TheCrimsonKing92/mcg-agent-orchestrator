using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;

// Parallel-safe: scenario-owned files, kernel, clock and coordinator; process launch is seamed.
public sealed class ConductorDriverTestsPreTesterEvidenceRefresh
{
    private const string CandidateSha = "bbb2222";
    private const string ChangedPath = "tests/Mcg.AgentOrchestrator.Core.Tests/DeclaredCoreTests.cs";

    // The bound detects a hang; assertions measure cross-tick evidence and dispatch state.
    [Fact(Timeout = 30_000)]
    [Trait("Category", "CrossTick")]
    public void BuildOnlyUnusableRefreshesBeforeAnyTesterDispatch()
    {
        using var scenario = new Scenario(buildPhaseOnly: true);
        var first = Assert.IsType<ConductorAdvanceOutcome.Held>(scenario.Advance().Outcome);
        var old = scenario.Latest();
        Assert.StartsWith("PRE_TESTER_BUILD_PHASE_REFRESH", first.Reason, StringComparison.Ordinal);
        Assert.Contains(CandidateSha, first.Reason, StringComparison.Ordinal);
        Assert.Contains(old.ReceiptId, first.Reason, StringComparison.Ordinal);
        Assert.Equal("unusable", old.Outcome);
        Assert.Equal(1, scenario.Runs);
        Assert.Equal(0, scenario.Dispatches);
        Assert.Empty(scenario.DeliveredIndex);
        Assert.Empty(scenario.RefreshMarkers());

        Assert.IsType<ConductorAdvanceOutcome.Executed>(scenario.Advance().Outcome);

        var fresh = scenario.Latest();
        Assert.Equal("green", fresh.Outcome);
        Assert.NotEqual(old.ReceiptId, fresh.ReceiptId);
        var marker = Assert.Single(scenario.RefreshMarkers());
        Assert.Equal(CandidateSha, marker.CandidateSha);
        Assert.Equal(old.ReceiptId, marker.SupersededReceiptId);
        Assert.Equal(fresh.ReceiptId, marker.FreshReceiptId);
        Assert.Equal(1, PreTesterEvidenceRefreshRule.CountForCandidate(scenario.Goal, scenario.Tester.Id, CandidateSha));
        Assert.Equal(2, scenario.Runs);
        Assert.Equal(1, scenario.Dispatches);
        Assert.Contains(Assert.Single(scenario.DeliveredIndex), line => line.Contains("verdict=green", StringComparison.Ordinal) &&
            line.Contains("receipt=" + fresh.ReceiptId, StringComparison.Ordinal));
        Assert.Empty(scenario.Escalations);
        Assert.Empty(scenario.Recoveries);
    }

    // The bound detects a hang; assertions measure the persisted refresh budget and terminal outcome.
    [Fact(Timeout = 30_000)]
    [Trait("Category", "CrossTick")]
    public void BuildOnlyFreshUnusableEscalatesWithoutTesterDispatch()
    {
        using var scenario = new Scenario(secondUnusable: true, buildPhaseOnly: true);
        Assert.IsType<ConductorAdvanceOutcome.Held>(scenario.Advance().Outcome);
        var old = scenario.Latest();
        Assert.Equal(1, scenario.Runs);
        Assert.Equal(0, scenario.Dispatches);

        var escalated = Assert.IsType<ConductorAdvanceOutcome.Escalated>(scenario.Advance().Outcome);

        var fresh = scenario.Latest();
        Assert.Equal("unusable", fresh.Outcome);
        Assert.NotEqual(old.ReceiptId, fresh.ReceiptId);
        Assert.Equal(escalated.Reason, Assert.Single(scenario.Escalations));
        Assert.Contains("PRE_TESTER_APPARATUS_REFRESH_UNUSABLE", escalated.Reason, StringComparison.Ordinal);
        Assert.Contains(CandidateSha, escalated.Reason, StringComparison.Ordinal);
        Assert.Contains(old.ReceiptId, escalated.Reason, StringComparison.Ordinal);
        Assert.Contains(fresh.ReceiptId, escalated.Reason, StringComparison.Ordinal);
        var marker = Assert.Single(scenario.RefreshMarkers());
        Assert.Equal(old.ReceiptId, marker.SupersededReceiptId);
        Assert.Equal(fresh.ReceiptId, marker.FreshReceiptId);
        Assert.Equal(0, scenario.Dispatches);
        Assert.Empty(scenario.DeliveredIndex);
        Assert.Equal(2, scenario.Runs);
        Assert.Equal(1, PreTesterEvidenceRefreshRule.CountForCandidate(scenario.Goal, scenario.Tester.Id, CandidateSha));

        scenario.Advance();
        Assert.Equal(2, scenario.Runs);
        Assert.Single(scenario.RefreshMarkers());
    }

    [Fact]
    public void AutoRetryRefreshesUnusableReceiptAndDispatchesFreshGreen()
    {
        using var scenario = new Scenario();
        var old = scenario.FirstUnusable();
        scenario.CompleteTester();

        var result = scenario.Advance();

        Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
        Assert.Equal("verification-inconclusive", Assert.Single(scenario.Recoveries).DiscriminatingEvidence);
        Assert.Equal(RetryCause.EnvironmentApparatusFailure, scenario.Recoveries[0].RetryCause);
        AssertFreshGreen(scenario, old);
        Assert.DoesNotContain(scenario.Goal.Timeline, evt =>
            evt.Message.Contains("operator-intent", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void OperatorRouteRefreshesUnusableReceiptAndDispatchesFreshGreen()
    {
        using var scenario = new Scenario();
        var old = scenario.FirstUnusable();
        scenario.CompleteTester();
        scenario.Kernel.RetryTaskWithAuthoritativeFeedback(scenario.Goal.Id, scenario.Tester.Id,
            "Retry apparatus evidence", RetryCause.EnvironmentApparatusFailure, invalidateDownstream: true);

        Assert.IsType<ConductorAdvanceOutcome.Executed>(scenario.Advance().Outcome);

        AssertFreshGreen(scenario, old);
        Assert.Empty(scenario.Recoveries);
    }

    // The bound detects a hang; assertions measure attempt identity and launch counts.
    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    public void BackgroundRefreshRetainsFreshIdentityAcrossReconciliationTicks()
    {
        using var scenario = new Scenario();
        var old = scenario.FirstUnusable();
        scenario.CompleteTester();
        var launches = 0;
        ConductorParallelAcceptanceAttemptCoordinator Coordinator() => new(scenario.AttemptsRoot,
            TrunkBranchName.Default, isProcessAlive: pid => pid == 7105,
            launchOwnedProcess: _ => { launches++; return new(7105); }, acquireStableSlotLease: (_, _) => null);
        var coordinator = Coordinator();
        Assert.IsType<ConductorAdvanceOutcome.Held>(scenario.Advance(coordinator).Outcome);
        var attempt = Assert.Single(coordinator.GetUnreconciledAttempts([scenario.Goal.Id.Value]));
        var marker = Assert.Single(scenario.RefreshMarkers());
        Assert.Equal("started", scenario.Latest().Outcome);
        Assert.Equal(marker.FreshReceiptId, scenario.Latest().ReceiptId);
        Assert.Equal(old.ReceiptId, marker.SupersededReceiptId);
        Assert.Equal(1, launches);

        coordinator = Coordinator();
        Assert.IsType<ConductorAdvanceOutcome.Held>(scenario.Advance(coordinator).Outcome);
        Assert.Equal(attempt.AttemptId, Assert.Single(coordinator.GetUnreconciledAttempts([scenario.Goal.Id.Value])).AttemptId);
        Assert.Equal(1, launches);
        var request = string.Join("; ", scenario.Latest().Selections);
        coordinator.RunAttemptForTests(attempt,
            ConductorParallelAcceptanceCandidate.Create(scenario.Goal, 0, [], CandidateSha, null),
            ConductorAutonomyPolicy.Permissive,
            (current, _, _, _, _) => ConductorParallelAcceptanceRunResult.Focused(current, scenario.RunGreen(request)));

        Assert.IsType<ConductorAdvanceOutcome.Executed>(scenario.Advance(coordinator).Outcome);
        Assert.Equal(1, launches);
        AssertFreshGreen(scenario, old);
    }

    [Fact]
    public void SecondUnusableEscalatesAndNeverRunsAgainForCandidate()
    {
        using var scenario = new Scenario(secondUnusable: true);
        var old = scenario.FirstUnusable();
        scenario.CompleteTester();

        var result = scenario.Advance();

        Assert.IsType<ConductorAdvanceOutcome.Escalated>(result.Outcome);
        var fresh = scenario.Latest();
        Assert.Equal("unusable", fresh.Outcome);
        Assert.NotEqual(old.ReceiptId, fresh.ReceiptId);
        var reason = Assert.Single(scenario.Escalations);
        Assert.Contains(CandidateSha, reason, StringComparison.Ordinal);
        Assert.Contains(old.ReceiptId, reason, StringComparison.Ordinal);
        Assert.Contains(fresh.ReceiptId, reason, StringComparison.Ordinal);
        Assert.Equal(1, scenario.Dispatches);
        Assert.Equal(2, scenario.Runs);
        Assert.Equal(1, PreTesterEvidenceRefreshRule.CountForCandidate(scenario.Goal, scenario.Tester.Id, CandidateSha));

        scenario.Advance();
        Assert.Equal(2, scenario.Runs);
        scenario.CompleteTester();
        scenario.Kernel.RetryTaskWithAuthoritativeFeedback(scenario.Goal.Id, scenario.Tester.Id,
            "Retry same apparatus", RetryCause.EnvironmentApparatusFailure);
        scenario.Advance();
        Assert.Equal(2, scenario.Runs);
        Assert.Single(scenario.RefreshMarkers());
    }

    [Theory]
    [InlineData("red")]
    [InlineData("green")]
    public void UsableReceiptIsReusedAndUnchangedInputsStillEscalate(string outcome)
    {
        using var scenario = new Scenario();
        scenario.SeedUsable(outcome);
        scenario.Advance();
        scenario.CompleteTester();

        scenario.Advance();

        Assert.Equal(0, scenario.Runs);
        Assert.Equal(outcome, scenario.Latest().Outcome);
        Assert.Empty(scenario.RefreshMarkers());
        Assert.Contains(scenario.Goal.Timeline, evt =>
            evt.Message.Contains("prior-outcome-not-started", StringComparison.Ordinal));
        scenario.CompleteTester();
        Assert.True(TesterInconclusiveRoundInputsReader.Read(scenario.Goal, scenario.Tester)!.InputsUnchanged);
        var escalated = Assert.IsType<ConductorAdvanceOutcome.Escalated>(scenario.Advance().Outcome);
        var decision = Assert.IsType<PolicyDecisionRecord>(escalated.Decision);
        Assert.Equal("verification-inconclusive-unchanged-inputs", decision.DiscriminatingEvidence);
        Assert.Equal(0, scenario.Runs);
    }

    [Theory]
    [InlineData(RetryCause.NewSourceFinding)]
    [InlineData(RetryCause.Unknown)]
    public void OtherRetryCausesReuseApparatusReceipt(RetryCause cause)
    {
        using var scenario = new Scenario();
        var old = scenario.FirstUnusable();
        scenario.CompleteTester();
        scenario.Kernel.RetryTaskWithAuthoritativeFeedback(scenario.Goal.Id, scenario.Tester.Id, "Other retry", cause);

        scenario.Advance();

        Assert.Equal(1, scenario.Runs);
        Assert.Equal(old.ReceiptId, scenario.Latest().ReceiptId);
        Assert.Empty(scenario.RefreshMarkers());
    }

    [Fact]
    public void UnreadableReceiptCannotAuthorizeRefresh()
    {
        using var scenario = new Scenario();
        scenario.Kernel.RecordFindingEvidenceRun(scenario.Goal.Id, scenario.Tester.Id,
            PreTesterEvidenceIndexLines.FormatMarker(new("unusable", CandidateSha, "missing", [], [], null, [])));
        scenario.Advance();
        scenario.CompleteTester();
        scenario.Advance();
        Assert.Equal(0, scenario.Runs);
        Assert.Empty(scenario.RefreshMarkers());
        Assert.Equal("missing", scenario.Latest().ReceiptId);
    }

    private static void AssertFreshGreen(Scenario scenario, PreTesterEvidenceEntry old)
    {
        Assert.Equal(2, scenario.Runs);
        Assert.Equal(2, scenario.Dispatches);
        var fresh = scenario.Latest();
        Assert.Equal("green", fresh.Outcome);
        Assert.NotEqual(old.ReceiptId, fresh.ReceiptId);
        var marker = Assert.Single(scenario.RefreshMarkers());
        Assert.Equal(CandidateSha, marker.CandidateSha);
        Assert.Equal(old.ReceiptId, marker.SupersededReceiptId);
        Assert.Equal(fresh.ReceiptId, marker.FreshReceiptId);
        Assert.Contains(scenario.DeliveredIndex[^1], line => line.Contains("verdict=green", StringComparison.Ordinal) &&
            line.Contains("receipt=" + fresh.ReceiptId, StringComparison.Ordinal));
        Assert.Empty(scenario.Escalations);
    }

    private sealed class Scenario : IDisposable
    {
        private readonly string _root = ConductorDriverTests.CreateTempDirectory();
        private readonly FixtureClock _clock = new();
        private readonly bool _secondUnusable;
        private readonly bool _buildPhaseOnly;
        public AgentOrchestratorKernel Kernel { get; }
        public Goal Goal { get; }
        public TaskSpec Tester { get; }
        public int Runs { get; private set; }
        public int Dispatches { get; private set; }
        public List<IReadOnlyList<string>> DeliveredIndex { get; } = [];
        public List<string> Escalations { get; } = [];
        public List<FailedGoalRecoveryDecision> Recoveries { get; } = [];

        public Scenario(bool secondUnusable = false, bool buildPhaseOnly = false)
        {
            _secondUnusable = secondUnusable;
            _buildPhaseOnly = buildPhaseOnly;
            Kernel = new(_clock);
            Kernel.ConfigureCandidateIdentityResolver(_ => new CandidateIdentity("patch", "base", "manifest"));
            var developer = new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer);
            Tester = new(TaskId.New(), "Verify", AgentRole.Tester);
            Goal = Kernel.CreateGoal("Refresh pre-Tester apparatus evidence", [developer, Tester]);
            Kernel.ActivateGoal(Goal.Id, DefaultAgents());
            Kernel.RecordTaskDispatch(Goal.Id, developer.Id, new("fixture", "implement", _root, _clock.Next(), BaseCommit: "aaa1111"));
            var output = string.Join('\n', "WORKER_RESULT:", "files: " + ChangedPath, "commands: none",
                "tests: deferred - DeclaredCoreTests", "commit: none", "blockers: none",
                "assigned_scope_complete: true", "model_fit: test/model - adequate - fixture",
                "skills: none", "confidence: high", "END_WORKER_RESULT");
            Kernel.RecordTaskVerification(Goal.Id, developer.Id, new("implement", _root, 0, output, "", _clock.Next(),
                WorkerResultPresent: true, HasCommittedChanges: true));
            Assert.Equal(WorkTaskStatus.Completed, developer.Status);
            var worktree = GoalWorktrees.WorktreePath(_root, Goal.Id);
            Directory.CreateDirectory(worktree);
            File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: fixture");
            Declare(worktree, "DeclaredCoreTests", "Mcg.AgentOrchestrator.Core.Tests");
            foreach (var entry in PreTesterAlwaysRunGuardTestClasses.Entries)
                Declare(worktree, entry.TestClass, "Mcg.AgentOrchestrator.Infrastructure.Tests");
        }

        private static void Declare(string worktree, string name, string projectName)
        {
            var project = Path.Combine(worktree, "tests", projectName);
            Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project, projectName + ".csproj"), "<Project />");
            File.WriteAllText(Path.Combine(project, name + ".cs"), "public class " + name + " {}");
        }

        public string AttemptsRoot => Path.Combine(_root, "attempts");

        private ConductorParallelAcceptanceAttemptCoordinator Coordinator() => new(AttemptsRoot,
            TrunkBranchName.Default, runInline: true, acquireStableSlotLease: (_, _) => null);

        public PreTesterEvidenceEntry FirstUnusable()
        {
            Assert.IsType<ConductorAdvanceOutcome.Executed>(Advance().Outcome);
            Assert.Equal(1, Runs);
            Assert.Equal(1, Dispatches);
            var old = Latest();
            Assert.Equal("unusable", old.Outcome);
            Assert.True(PreTesterApparatusReceiptClassifier.IsApparatusUnusable(
                Coordinator().ReadFocusedReceiptEvidence(Goal.Id, old.ReceiptId, CandidateSha)));
            return old;
        }

        public PreTesterEvidenceEntry Latest() => Assert.IsType<PreTesterEvidenceEntry>(
            PreTesterEvidenceIndexLines.Latest(Goal, Tester.Id, CandidateSha));

        public PreTesterEvidenceRefreshMarker[] RefreshMarkers() => Goal.Timeline
            .Select(evt => PreTesterEvidenceRefreshRule.TryParse(evt.Message, out var marker) ? marker : null)
            .OfType<PreTesterEvidenceRefreshMarker>().ToArray();

        public void CompleteTester()
        {
            const string output = "WORKER_RESULT:\nfiles: none\ntests: inconclusive - inspected retained receipts\nblockers: none\nEND_WORKER_RESULT";
            Kernel.RecordDispatchExecutionResult(Goal.Id, Tester.Id, new("verify", _root, 0, output, "", _clock.Next(),
                WorkerResultPresent: true, DispatchStartedAt: Tester.LastDispatch!.DispatchedAt));
            Assert.Equal(DispatchOutcomeKind.VerificationInconclusive,
                DispatchFailureClassifier.Classify(Tester, Tester.LastVerification!).Kind);
            Assert.Equal(WorkTaskStatus.Failed, Tester.Status);
        }

        public void SeedUsable(string outcome)
        {
            const string request = "Core.Tests:DeclaredCoreTests";
            var evidence = outcome == "green" ? Green(request) : CandidateRedFindingEvidence(request, CandidateSha, "ExternalTests.Fails");
            if (outcome == "red") evidence = evidence with { Arms = evidence.Arms!.Select(arm =>
                arm with { Disposition = FindingEvidenceArmDisposition.Red,
                    Checks = arm.Checks.Select(check => check with { ExecutedTestCount = 1 }).ToArray() }).ToArray() };
            var coordinator = Coordinator();
            var run = coordinator.EvaluateFocusedEvidence(ConductorParallelAcceptanceCandidate.Create(Goal, 0, [], CandidateSha, null),
                ConductorAutonomyPolicy.Permissive, request, (_, _, _, _) => evidence, new("seed", "seed-batch", []));
            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Completed, run.Kind);
            Assert.True(coordinator.RecordFocusedEvidenceRequestDispositions(run.Attempt, "seed", "seed-receipt", []));
            coordinator.MarkReconciled(run.Attempt);
            Assert.False(PreTesterApparatusReceiptClassifier.IsApparatusUnusable(
                coordinator.ReadFocusedReceiptEvidence(Goal.Id, "seed-receipt", CandidateSha)));
            Kernel.RecordFindingEvidenceRun(Goal.Id, Tester.Id, PreTesterEvidenceIndexLines.FormatMarker(
                new(outcome, CandidateSha, "seed-receipt", [request], [], run.Attempt.ResultPath,
                    outcome == "red" ? ["ExternalTests.Fails"] : [])));
        }

        private FocusedEvidenceRunResult Green(string request) =>
            ConductorDriverTestsFindingEvidenceReuse.RetainedEvidenceWithExecutedClasses(_root, request, CandidateSha);

        public FocusedEvidenceRunResult RunGreen(string request)
        {
            Runs++;
            return Green(request);
        }

        private static FocusedEvidenceRunResult Unusable(string request)
        {
            var check = new AcceptanceCheckResult("build", false, -1, "no tests executed",
                FailureClassification: AcceptanceFailureClassifications.FocusedSelectionApparatusFailure, ExecutedTestCount: 0);
            return new(request, true, false, "apparatus timeout", [check],
                Arms: [new(FindingEvidenceArm.Candidate, CandidateSha, FindingEvidenceArmDisposition.Inconclusive,
                    true, false, "build timed out", [check])]);
        }

        private static FocusedEvidenceRunResult BuildOnlyUnusable(string request)
        {
            var check = new AcceptanceCheckResult("build", false, -1, "no tests executed",
                FailureClassification: AcceptanceFailureClassifications.BuildPhaseTimeout, ExecutedTestCount: 0);
            return new(request, true, false, "build timed out", [],
                Arms: [new(FindingEvidenceArm.Candidate, CandidateSha, FindingEvidenceArmDisposition.Inconclusive,
                    true, false, "build timed out", [check])]);
        }

        public ConductorAdvanceResult Advance(ConductorParallelAcceptanceAttemptCoordinator? coordinator = null)
        {
            var driver = MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                getPreReviewEvidenceContext: _ => NoPreReviewContext(CandidateSha),
                getLandingFileScopes: _ => [ChangedPath], executionDirectory: _root,
                focusedEvidenceAttemptCoordinator: coordinator ?? Coordinator(),
                runFocusedEvidence: (_, request) => ++Runs == 1 || _secondUnusable
                    ? _buildPhaseOnly ? BuildOnlyUnusable(request) : Unusable(request) : Green(request),
                dispatchAndStart: _ =>
                {
                    Assert.Equal(WorkTaskStatus.Assigned, Tester.Status);
                    DeliveredIndex.Add(PreTesterEvidenceIndexLines.ForTester(Goal, Tester, CandidateSha));
                    Kernel.RecordTaskDispatch(Goal.Id, Tester.Id, new("fixture", "verify", _root, _clock.Next()));
                    Dispatches++;
                    return DispatchStartOutcome.Started();
                },
                retryTaskWithCause: (gid, tid, message, round, cause) =>
                    Kernel.RetryTask(gid, tid, message, retryRoundKind: round, retryCause: cause),
                recordTaskNote: (gid, tid, message) => Kernel.RecordTaskNote(gid, tid, message),
                recordFindingEvidenceRequest: (gid, tid, message) => Kernel.RecordFindingEvidenceRequest(gid, tid, message),
                recordFindingEvidenceRun: (gid, tid, message) => Kernel.RecordFindingEvidenceRun(gid, tid, message),
                beforeFailedGoalRecoveryEffect: (_, decision) => Recoveries.Add(decision),
                emptyOutputBackoffDelay: _ => { },
                writeEscalation: (_, _, reason) => Escalations.Add(reason));
            driver.OverrideDeveloperCompletionStructuralPreflightForTests(
                _ => new DeveloperCompletionStructuralFindings(false, "clean"), TimeSpan.FromSeconds(5));
            return driver.AdvanceOnce(Goal, ConductorAutonomyPolicy.Permissive);
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }

    private sealed class FixtureClock : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = new(2026, 10, 17, 0, 0, 0, TimeSpan.Zero);
        public DateTimeOffset Next() => UtcNow = UtcNow.AddMinutes(1);
    }
}
