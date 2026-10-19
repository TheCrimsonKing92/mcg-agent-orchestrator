using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;
using static AttributedFailureDeveloperRouteTests;
using static AttributedFailureAutoRouteScenarioTests;

// All state, stores and temporary roots are owned by the individual test.
public sealed class AttributedFailureAutoRouteControlTests
{
    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    public void InteractionOnlyNeverRecordsOrRoutesEitherMember()
    {
        var (kernel, first, second) = CohortGoals();
        Assert.False(ApplyCohort(kernel, first, second, interactionOnly: true).ShouldRecord);
        var harness = new RouteHarness(kernel);
        var driver = harness.Configure();
        for (var tick = 1; tick <= 3; tick++)
        {
            driver.BeginTick(kernel, tick);
            driver.AdvanceOnce(first, ConductorAutonomyPolicy.Permissive);
            driver.AdvanceOnce(second, ConductorAutonomyPolicy.Permissive);
        }
        Assert.All(new[] { first, second }, goal =>
        {
            Assert.Null(goal.LatestAcceptanceFailure);
            Assert.Empty(MarkerRetries(goal));
        });
        Assert.Empty(harness.Dispatches);
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    public void CohortRepeatSurvivesRestartAndNewHeadRoutesOnceMore()
    {
        var (kernel, first, second) = CohortGoals();
        ApplyCohort(kernel, first, second);
        var harness = new RouteHarness(kernel);
        var driver = harness.Configure();
        driver.BeginTick(kernel, 1);
        driver.AdvanceOnce(second, ConductorAutonomyPolicy.Permissive);
        driver.BeginTick(kernel, 2);
        driver.AdvanceOnce(second, ConductorAutonomyPolicy.Permissive);
        Assert.Single(MarkerRetries(second));
        CompleteWithoutNewCommit(kernel, second);
        RecordCohortFailure(kernel, second);
        var resumed = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot());
        var goal = resumed.GetGoal(second.Id);
        Assert.Contains(goal.Tasks.Single().DispatchHistory, dispatch => dispatch.BaseCommit == Main && dispatch.ResultCommit == Candidate);
        var replay = new RouteHarness(resumed);
        var replayDriver = replay.Configure();
        replayDriver.BeginTick(resumed, 3);
        var declined = replayDriver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
        AssertUnhandled(declined, goal);
        Assert.Single(MarkerRetries(goal));
        Assert.Empty(replay.Dispatches);

        RecordCohortFailure(resumed, goal, Main);
        replay.CurrentCandidate = Main;
        replayDriver.BeginTick(resumed, 4);
        replayDriver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
        Assert.Equal(2, MarkerRetries(goal).Length);
        Assert.Contains("candidate_sha=" + Main, MarkerRetries(goal)[1].Message);
        Assert.Single(replay.Dispatches);
        CompleteWithoutNewCommit(resumed, goal);
        RecordCohortFailure(resumed, goal, Main);
        replayDriver.BeginTick(resumed, 5);
        AssertUnhandled(replayDriver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive), goal);
        Assert.Equal(2, MarkerRetries(goal).Length);
        Assert.Single(replay.Dispatches);
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    public void TesterRepeatEscalatesWithoutSecondDeveloperRoute()
    {
        var (kernel, goal, tester, receipt) = DeliverTesterReceipt();
        var finding = Assert.Single(tester.VerificationHistory.Last().MergedReviewFindings!);
        RecordTesterResult(kernel, goal, tester, fail: true, reportFinding: false);
        var harness = new RouteHarness(kernel);
        var driver = harness.Configure();
        driver.BeginTick(kernel, 2);
        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
        Assert.Single(MarkerRetries(goal));
        CompleteWithoutNewCommit(kernel, goal);
        RecordTesterResult(kernel, goal, tester, fail: true, overrideFinding: finding, reportFinding: false);
        kernel.RecordFindingEvidenceOutcome(goal.Id, tester.Id, finding.StableId, finding.EvidenceOutcome!, receipt);
        driver.BeginTick(kernel, 3);
        AssertRungNine(driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive));
        Assert.Single(MarkerRetries(goal));
        Assert.Single(harness.Dispatches);
        AssertNoOperatorIntent(goal);
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    public void ResubmittedTesterRequestKeepsExistingEvidenceRecovery()
    {
        var (kernel, goal, tester, receipt) = DeliverTesterReceipt();
        RecordTesterResult(kernel, goal, tester, fail: true);
        Assert.Equal(receipt.ReceiptId,
            Assert.IsType<AttributedFailure>(AttributedFailureDeveloperRoute.ReadTesterReceipt(goal, tester, Candidate)).ReceiptId);
        var harness = new RouteHarness(kernel);
        var focusedRuns = 0;
        var driver = harness.Configure((_, request) =>
        {
            focusedRuns++;
            return CandidateRedFindingEvidence(request, Candidate, TesterIdentity);
        });
        driver.BeginTick(kernel, 2);
        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(1, focusedRuns);
        Assert.Equal(tester.Id, Assert.Single(harness.Dispatches));
        Assert.Empty(MarkerRetries(goal));
        Assert.False(result.WasEscalated);
        AssertNoOperatorIntent(goal);
    }

    [Theory]
    [InlineData(AcceptanceFailureCause.EnvironmentalApparatus)]
    [InlineData(AcceptanceFailureCause.FixturePublication)]
    public void ClassifiedAcceptanceFailureKeepsEscalation(AcceptanceFailureCause cause)
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CompletedGoal(kernel);
        RecordCohortFailure(kernel, goal, cause: cause);
        var harness = new RouteHarness(kernel);
        var driver = harness.Configure();
        driver.BeginTick(kernel, 1);
        AssertUnhandled(driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive), goal);
        Assert.Empty(MarkerRetries(goal));
        Assert.Empty(harness.Dispatches);
    }

    [Fact]
    public void EmptyCohortReproductionKeepsEscalation()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CompletedGoal(kernel);
        RecordCohortFailure(kernel, goal, evidence: "cohort-attribution=combined-red cohort-partition=partition-1 reproduced=");
        var harness = new RouteHarness(kernel);
        var driver = harness.Configure();
        driver.BeginTick(kernel, 1);
        AssertUnhandled(driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive), goal);
        Assert.Empty(MarkerRetries(goal));
        Assert.Empty(harness.Dispatches);
    }

    [Fact]
    public void ConservativeCohortKeepsLifecycleEntryEscalation()
    {
        var (kernel, first, second) = CohortGoals();
        ApplyCohort(kernel, first, second);
        RecordCohortFailure(kernel, second);
        var harness = new RouteHarness(kernel);
        var driver = harness.Configure();
        driver.BeginTick(kernel, 1);
        var outcome = Assert.IsType<ConductorAdvanceOutcome.Escalated>(driver.AdvanceOnce(second, ConductorAutonomyPolicy.Conservative).Outcome);
        Assert.Equal("policy-manual-review", outcome.Decision!.DiscriminatingEvidence);
        Assert.Empty(MarkerRetries(second));
        Assert.Empty(harness.Dispatches);
    }

    [Fact]
    public void ImpactTrainKeepsUnhandledEscalation()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CompletedGoal(kernel);
        var root = ConductorDriverTests.CreateTempDirectory();
        try
        {
            ApplyTrain(kernel, goal, root, "impact");
            var harness = new RouteHarness(kernel);
            var driver = harness.Configure();
            driver.BeginTick(kernel, 1);
            AssertUnhandled(driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive), goal);
            Assert.Empty(MarkerRetries(goal));
            Assert.Empty(harness.Dispatches);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("apparatus")]
    [InlineData("apparatus-request")]
    [InlineData("inconclusive")]
    [InlineData("baseline-red")]
    [InlineData("empty")]
    [InlineData("stale")]
    [InlineData("unaccepted")]
    public void UnsafeTesterReceiptKeepsBaselineRecovery(string fault)
    {
        var (kernel, goal, tester, receipt) = DeliverTesterReceipt();
        var arms = receipt.Arms!.ToArray();
        var red = Array.FindIndex(arms, arm => arm.Arm == FindingEvidenceArm.Candidate);
        if (fault == "empty") arms[red] = arms[red] with { FailingTestIdentities = [] };
        if (fault.StartsWith("apparatus", StringComparison.Ordinal)) arms = [.. arms, new(FindingEvidenceArm.SourceReverted, Candidate,
            FindingEvidenceArmDisposition.ApparatusFailure, false, false, "apparatus failed")];
        if (fault == "inconclusive") arms = [.. arms, new(FindingEvidenceArm.SourceReverted, Candidate,
            FindingEvidenceArmDisposition.Inconclusive, false, false, "inconclusive")];
        if (fault == "baseline-red") arms = arms.Select(arm => arm.Arm == FindingEvidenceArm.Baseline
            ? arm with { Disposition = FindingEvidenceArmDisposition.Red } : arm).ToArray();
        // Use a distinct receipt identity so the safe original cannot satisfy the new finding citation.
        var altered = receipt with { ReceiptId = "control-" + fault, Arms = arms,
            CandidateSha = fault == "stale" ? Main : Candidate, Accepted = fault != "unaccepted" };
        var finding = Assert.Single(tester.VerificationHistory.Last().MergedReviewFindings!) with
        { EvidenceOutcome = new(true, altered.ReceiptId, ResultReason: FindingEvidenceOutcomeReason.CandidateRed) };
        // Carry the apparatus finding through the kernel merge without a fresh request;
        // the other cases exercise recovery with their current request still present.
        RecordTesterResult(kernel, goal, tester, fail: true, overrideFinding: finding, reportFinding: fault != "apparatus");
        kernel.RecordFindingEvidenceOutcome(goal.Id, tester.Id, finding.StableId, finding.EvidenceOutcome!, altered);
        Assert.Equal(altered.ReceiptId, Assert.Single(tester.LastVerification!.MergedReviewFindings!).EvidenceOutcome!.ReceiptId);
        Assert.Null(AttributedFailureDeveloperRoute.ReadTesterReceipt(goal, tester, Candidate));

        // Evaluate the original observation flow and policy on an identical snapshot.
        // No new guard or route executes; candidate and receipt facts remain identical.
        var baselineKernel = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot());
        var baselineGoal = baselineKernel.GetGoal(goal.Id);
        var baselineHarness = new RouteHarness(baselineKernel);
        var baselineDriver = baselineHarness.Configure();
        baselineDriver.BeginTick(baselineKernel, 2);
        var baseline = BaselineRecovery(baselineDriver, baselineGoal);
        var harness = new RouteHarness(kernel);
        var driver = harness.Configure();
        driver.BeginTick(kernel, 2);
        var actual = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
        var expectedOutcome = baseline.Action switch
        {
            FailedGoalRecoveryAction.Escalate => typeof(ConductorAdvanceOutcome.Escalated),
            FailedGoalRecoveryAction.Hold => typeof(ConductorAdvanceOutcome.Held),
            FailedGoalRecoveryAction.CriterionRetry or FailedGoalRecoveryAction.FindingRetry or
                FailedGoalRecoveryAction.RetryTransient or FailedGoalRecoveryAction.RetryStale => typeof(ConductorAdvanceOutcome.Executed),
            _ => throw new InvalidOperationException($"Unexpected baseline action {baseline.Action}")
        };
        Assert.Equal(expectedOutcome, actual.Outcome.GetType());
        var baselineDecision = FailedGoalRecoveryDecisionRecords.FromPolicy(baseline);
        // Successful retry outcomes carry the later dispatch-start decision; compare
        // the recovery decision captured before that effect, including earlier rungs.
        var actualDecision = harness.RecoveryEffectDecision is { } recovery
            ? FailedGoalRecoveryDecisionRecords.FromPolicy(recovery) : RecoveryDecision(actual, harness);
        Assert.Equal(FailedGoalRecoveryDecisionRecords.StageName, baselineDecision.Stage);
        Assert.Equal(baselineDecision.Stage, actualDecision.Stage);
        Assert.Equal(baselineDecision.Rung, actualDecision.Rung);
        Assert.Equal(baselineDecision.DiscriminatingEvidence, actualDecision.DiscriminatingEvidence);
        if (fault == "apparatus") AssertRungNine(actual);
        Assert.Equal(baseline.RetryCause, harness.RecoveryEffectDecision?.RetryCause);
        Assert.Equal(baseline.Identity.TaskId is { } target ? new[] { target } : [], harness.Dispatches);
        Assert.All(new[] { baselineGoal, goal }, target =>
        {
            Assert.Empty(MarkerRetries(target));
            Assert.DoesNotContain(target.Tasks, task => task.RequiredRole == AgentRole.Developer &&
                task.PendingRetryCause == RetryCause.NewTestFinding);
            AssertNoOperatorIntent(target);
        });
    }

    private static FailedGoalRecoveryDecision BaselineRecovery(ConductorDriver driver, Goal goal)
    {
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var policy = ConductorAutonomyPolicy.Permissive;
        // These unchanged methods are the original executor's seams. Invoking them directly
        // excludes both additions in ExecuteFailedGoalRecovery, including the discovery guard.
        object Invoke(string name, object?[] arguments) =>
            typeof(ConductorDriver).GetMethod(name, flags)!.Invoke(driver, arguments)!;
        FailedGoalRecoveryFacts Facts() => (FailedGoalRecoveryFacts)Invoke("BuildFailedGoalRecoveryFacts",
            [goal, policy, GoalLifecycleState.Failed]);
        var landingScopes = (Func<Goal, IReadOnlyList<string>>)typeof(ConductorDriver)
            .GetField("_getLandingFileScopes", flags)!.GetValue(driver)!;
        var facts = Facts();
        var decision = FailedGoalRecoveryPolicy.Evaluate(facts);
        FailedGoalFindingObservation? contract = null, finding = null;
        var contractCompleted = false;
        var findingCompleted = false;
        while (decision.Action is FailedGoalRecoveryAction.ObserveReviewContract or FailedGoalRecoveryAction.ObserveVerifyingFinding)
        {
            if (decision.Action == FailedGoalRecoveryAction.ObserveReviewContract)
            {
                Assert.False(contractCompleted);
                object?[] arguments = [goal, FailedGoalFindingObservation.None];
                if ((bool)Invoke("TryObserveReviewContractRecovery", arguments))
                    contract = (FailedGoalFindingObservation)arguments[1]!;
                contractCompleted = true;
            }
            else
            {
                Assert.False(findingCompleted);
                object?[] arguments = [goal, policy, landingScopes(goal), FailedGoalFindingObservation.None];
                if ((bool)Invoke("TryObserveVerifyingFindingRecovery", arguments))
                    finding = (FailedGoalFindingObservation)arguments[3]!;
                findingCompleted = true;
            }
            Assert.Equal(GoalLifecycleState.Failed, GoalLifecycle.ResolveState(goal));
            facts = Facts();
            if (contractCompleted) facts = facts.WithReviewContractObservation(contract);
            if (findingCompleted) facts = facts.WithVerifyingFindingObservation(finding);
            decision = FailedGoalRecoveryPolicy.Evaluate(facts);
        }
        return decision;
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    public void ApparatusCandidateRerunRestoresVerifiedAndRunsGateOnce()
    {
        var root = ConductorDriverTests.CreateTempDirectory();
        try
        {
            var directory = Path.Combine(root, "tests", "Sample.Tests");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "UnchangedTests.cs"), "namespace Sample.Tests; public sealed class UnchangedTests { }");
            var kernel = new AgentOrchestratorKernel();
            var goal = CompletedGoal(kernel);
            const string identity = "Sample.Tests.UnchangedTests.FailsOnce";
            var check = new AcceptanceCheckResult(identity, false, 1, "candidate failure", FailingTestIdentities: [identity],
                TestProjectPath: "tests/Sample.Tests/Sample.Tests.csproj", FailingTestAttributions:
                [new(identity, AcceptanceTestFailureOrigin.UnconfirmedIntroduced, "merge-base attribution", new("Passed", "candidate-rerun.trx"))]);
            var summary = new AcceptanceVerificationSummary(false, [check], FailedChecks: [identity], BranchHeadSha: Candidate, MainHeadSha: Main,
                CheckAttributions: [new(identity, AcceptanceFailureOrigin.Introduced, "main green")]);
            var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, ["src/Changed.cs"], Candidate, Main);
            var run = ConductorParallelAcceptanceRunResult.Accepted(candidate, summary);
            var attempt = new ConductorParallelAcceptanceAttempt("attempt-a", goal.Id.Value, goal.Id.Value[..8], 0, Candidate, Main,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1234, ConductorParallelAcceptanceAttemptOutcome.Failed,
                "stdout", "stderr", "exit", "heartbeat", "result", "metadata");
            var gates = 0;
            var driver = MakeDriver(getFacts: _ => GoalLifecycleFacts.None,
                runAcceptanceSummary: _ => { gates++; return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria; },
                retryTaskWithCause: (_, _, _, _, _) => throw new InvalidOperationException("Unexpected worker retry"),
                recordCriterionRetryFeedback: kernel.RecordCriterionRetryFeedback,
                recordAcceptanceFailure: (target, checks, branch, main, attributions, baseline) =>
                    kernel.RecordAcceptanceFailure(target.Id, checks, branch, main, attributions, baseline),
                getLandingFileScopes: _ => ["src/Changed.cs"],
                apparatusRedGate: new ApparatusRedGate(Path.Combine(root, "acceptance-index.jsonl"), _ => root, perGoalRegateCap: 2));
            driver.BeginTick(kernel, 1);
            ConductorBatchLoop.ReconcileParallelAcceptanceTerminalState(kernel, goal, run, attempt);
            Assert.Equal(GoalStatus.AcceptanceFailed, goal.Status);
            var result = ConductorBatchLoop.CompleteParallelAcceptanceRun(driver, ConductorAutonomyPolicy.Conservative, run, attempt, out var held);
            Assert.False(held);
            Assert.Equal(GoalLifecycleState.Verified, Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome).State);
            Assert.Equal(GoalStatus.Verified, goal.Status);
            Assert.Null(goal.LatestAcceptanceFailure);
            driver.BeginTick(kernel, 2);
            Assert.False(driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative).WasEscalated);
            Assert.Equal(1, gates);
            Assert.Empty(MarkerRetries(goal));
        }
        finally { Directory.Delete(root, true); }
    }

    private static void CompleteWithoutNewCommit(AgentOrchestratorKernel kernel, Goal goal)
    {
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        CompleteRecordedProcess(kernel, goal, developer);
        kernel.RecordTaskVerification(goal.Id, developer.Id,
            new("fixture", Path.GetTempPath(), 0, "No new commit", "", DateTimeOffset.UtcNow, HasCommittedChanges: false));
    }

    private static void AssertUnhandled(ConductorAdvanceResult result, Goal goal)
    {
        var outcome = Assert.IsType<ConductorAdvanceOutcome.Escalated>(result.Outcome);
        Assert.Equal("Unhandled lifecycle state AcceptanceFailed", outcome.Reason);
        AssertNoOperatorIntent(goal);
    }

    private static void AssertRungNine(ConductorAdvanceResult result)
    {
        var outcome = Assert.IsType<ConductorAdvanceOutcome.Escalated>(result.Outcome);
        Assert.Equal("failed-terminal-fallthrough", outcome.Decision!.DiscriminatingEvidence);
    }

    private static PolicyDecisionRecord RecoveryDecision(ConductorAdvanceResult result, RouteHarness harness) =>
        Assert.IsType<PolicyDecisionRecord>((result.Outcome switch
        {
            ConductorAdvanceOutcome.Executed executed => executed.Decision,
            ConductorAdvanceOutcome.Held held => held.Decision,
            ConductorAdvanceOutcome.Escalated escalated => escalated.Decision,
            ConductorAdvanceOutcome.Done done => done.Decision,
            _ => throw new InvalidOperationException("Unexpected conductor outcome")
        }) ?? (harness.RecoveryEffectDecision is { } decision
            ? FailedGoalRecoveryDecisionRecords.FromPolicy(decision) : null));
}
