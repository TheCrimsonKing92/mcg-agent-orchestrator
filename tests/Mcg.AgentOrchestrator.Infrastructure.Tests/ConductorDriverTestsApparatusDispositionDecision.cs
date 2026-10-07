using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;

public sealed class ConductorDriverTestsApparatusDispositionDecision
{
    private const string HeartbeatIdentity =
        "Mcg.AgentOrchestrator.Infrastructure.Tests.WorkerDispatchHostTests.HeartbeatObserverTimingRace";
    private const string InheritedIdentity =
        "Mcg.AgentOrchestrator.Infrastructure.Tests.CliCommandTestsBacklogIntakeCommands.CliBacklogListSplitsLimitStatusAndTextFlags";
    private static readonly DateTimeOffset Now = new(2026, 9, 5, 14, 28, 0, TimeSpan.Zero);

    [Fact]
    public void ExcludedInheritedFailure_PreservesHoldAndRecordsRungOne()
    {
        // Arrangement from InheritedIdentityWithinImpactHoldsWithoutRetry at 719fa28a9.
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());
        var check = new AcceptanceCheckResult(
            "infrastructure tests: Remainder", false, 1, "inherited CLI failure",
            FailingTestIdentities: [InheritedIdentity],
            TestProjectPath: "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
            FailingTestAttributions:
            [
                new AcceptanceTestFailureAttribution(InheritedIdentity, AcceptanceTestFailureOrigin.Inherited,
                    "same focused identity failed at merge-base main-a")
            ]);
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptanceSummary: _ => Summary(check),
            retryTaskWithCause: (_, _, _, _, _) => throw new InvalidOperationException("Excluded failure must not retry."),
            getLandingFileScopes: _ => ["src/Mcg.AgentOrchestrator.App/Cli/CliCommandHandlers.Backlog.cs"]);

        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative).Outcome);

        Assert.Equal("Acceptance gate failures are all outside this goal's attributable scope: " +
            "Mcg.AgentOrchestrator.Infrastructure.Tests.CliCommandTestsBacklogIntakeCommands.CliBacklogListSplitsLimitStatusAndTextFlags (pre-existing/main-red). " +
            "The candidate remains held at Verified for operator/main-red routing; no worker was reopened.", held.Reason);
        Assert.Equal("acceptance-unattributable:candidate-a:main-a:59f0c423ba1bbbfa", held.StableIdentity);
        AssertDecision(goal, held, held.Decision, held.Reason, 1, "Hold", "excluded-outside-scope");
        var facts = AcceptanceApparatusDispositionFacts.FromRecordedFacts(held.Decision!.Facts);
        Assert.Equal(InheritedIdentity, facts.TestIdentities);
        Assert.Equal("candidate-a", facts.BranchHeadSha);
        Assert.Equal("main-a", facts.MainHeadSha);
        Assert.Null(facts.RegateCap);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CrossGoalApparatus_PreservesHoldOrBoundEscalationAndRecordsDecision(bool exhaustBound)
    {
        // Cross-goal arrangement and durable second-driver bound arrangement from ApparatusRedRegate.
        using var fixture = new TempRoot();
        var root = fixture.Path;
        const string source = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/WorkerDispatchHostTests.cs";
        var sourcePath = System.IO.Path.Combine(root, source);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(sourcePath)!);
        File.WriteAllText(sourcePath,
            $"namespace Mcg.AgentOrchestrator.Infrastructure.Tests;{Environment.NewLine}" +
            $"public sealed class WorkerDispatchHostTests{Environment.NewLine}{{{Environment.NewLine}}}{Environment.NewLine}");
        var index = CreateIndex(root);
        index.Append(
        [
            new AcceptanceFailingTestIndexRecord(AcceptanceFailingTestIndex.ContractVersion,
                AcceptanceFailingTestIndexKinds.GateFailure, "bf434fdbaaaa4a7987c4b843d1ea9c21", Now.AddHours(-6),
                CheckName: "infrastructure tests: dispatch host", TestIdentity: HeartbeatIdentity,
                ResolvedSourcePath: source, InsideChangedPaths: false,
                MessageFingerprint: AcceptanceFailingTestIndex.ComputeMessageFingerprint(
                    "heartbeat observer did not see the second beat before the 200ms deadline"))
        ], Now.AddHours(-6));
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());
        var check = new AcceptanceCheckResult("infrastructure tests: dispatch host", false, 1,
            "heartbeat observer did not see the second beat before the 200ms deadline",
            FailingTestIdentities: [HeartbeatIdentity],
            TestProjectPath: "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
            FailingTestAttributions:
            [
                new AcceptanceTestFailureAttribution(HeartbeatIdentity, AcceptanceTestFailureOrigin.Introduced,
                    "main is attested green for this identity")
            ]);
        ConductorDriver Driver() => MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptanceSummary: _ => Summary(check, introduced: true),
            retryTaskWithCause: (_, _, _, _, _) => throw new InvalidOperationException("Apparatus must not retry."),
            getLandingFileScopes: _ => ["src/Mcg.AgentOrchestrator.App/Providers/ProviderCommandBuilder.cs"],
            executionDirectory: root,
            apparatusRedGate: new ApparatusRedGate(CreateIndex(root), _ => root, () => Now, perGoalRegateCap: 1));

        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(Driver().AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative).Outcome);
        if (!exhaustBound)
        {
            Assert.Equal("Acceptance RED classified as apparatus (cross-goal-flake) for candidate " +
                "branch=candidate-a main=main-a: every failing test lies outside the candidate's changed paths " +
                "(Mcg.AgentOrchestrator.Infrastructure.Tests.WorkerDispatchHostTests.HeartbeatObserverTimingRace: " +
                "heartbeat observer did not see the second beat before the 200ms deadline). Re-gating on the next " +
                "conduct tick (1/1); no worker was reopened.", held.Reason);
            Assert.Equal("acceptance-apparatus-red:candidate-a:main-a:cross-goal-flake", held.StableIdentity);
            AssertDecision(goal, held, held.Decision, held.Reason, 2, "Hold", "cross-goal-flake");
            AssertRegateFacts(held.Decision!, HeartbeatIdentity);
            return;
        }

        var escalated = Assert.IsType<ConductorAdvanceOutcome.Escalated>(Driver().AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative).Outcome);
        Assert.Equal("apparatus-regate-bound-exhausted: this goal already re-gated 1/1 apparatus REDs without a green gate. " +
            "The current RED is apparatus again (cross-goal-flake) on " +
            "Mcg.AgentOrchestrator.Infrastructure.Tests.WorkerDispatchHostTests.HeartbeatObserverTimingRace: " +
            "heartbeat observer did not see the second beat before the 200ms deadline. " +
            "Repair the apparatus or confirm acceptance-retry; no worker was reopened.", escalated.Reason);
        Assert.Null(escalated.Kind);
        AssertDecision(goal, escalated, escalated.Decision, escalated.Reason, 3, "Escalate", "cross-goal-flake");
        var boundFacts = AcceptanceApparatusDispositionFacts.FromRecordedFacts(escalated.Decision!.Facts);
        Assert.Null(boundFacts.BranchHeadSha);
        Assert.Null(boundFacts.MainHeadSha);
        Assert.Equal(1, boundFacts.RegateCount);
        Assert.Equal(1, boundFacts.RegateCap);
        Assert.Equal(HeartbeatIdentity, boundFacts.TestIdentities);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WithinAttemptRerun_PreservesHoldOrBoundEscalationAndRecordsDecision(bool exhaustBound)
    {
        using var fixture = new TempRoot();
        var root = fixture.Path;
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());
        var partition = MakePartitionVerdict(root, goal.Id);
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptanceSummary: _ => Summary(partition),
            retryTaskWithCause: (_, _, _, _, _) => throw new InvalidOperationException("Apparatus must not retry."),
            getLandingFileScopes: _ => ["src/Unrelated.cs"],
            executionDirectory: root,
            apparatusRedGate: new ApparatusRedGate(CreateIndex(root), _ => root, () => Now, perGoalRegateCap: 1));

        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative).Outcome);
        if (!exhaustBound)
        {
            Assert.Equal("Acceptance apparatus (within-attempt-rerun-pass) for infrastructure tests: Process spawning: " +
                "the in-attempt rerun passed. Regating on the next conduct tick (1/1); no worker was reopened.", held.Reason);
            Assert.Equal("acceptance-apparatus-rerun-pass:candidate-a:main-a:1", held.StableIdentity);
            AssertDecision(goal, held, held.Decision, held.Reason, 4, "Hold", "within-attempt-rerun-pass");
            AssertRegateFacts(held.Decision!, "infrastructure tests: Process spawning");
            return;
        }

        var escalated = Assert.IsType<ConductorAdvanceOutcome.Escalated>(driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative).Outcome);
        Assert.Equal("apparatus-rerun-pass-regate-bound-exhausted: apparatus regate budget 1/1 exhausted for " +
            "infrastructure tests: Process spawning (missing-trx); each in-attempt rerun passed. " +
            "This is an infrastructure failure, not a criteria failure. " +
            "Repair the apparatus or confirm acceptance-retry; no worker was reopened.", escalated.Reason);
        Assert.Null(escalated.Kind);
        AssertDecision(goal, escalated, escalated.Decision, escalated.Reason, 5, "Escalate", "within-attempt-rerun-pass");
        var facts = AcceptanceApparatusDispositionFacts.FromRecordedFacts(escalated.Decision!.Facts);
        Assert.Equal("candidate-a", facts.BranchHeadSha);
        Assert.Equal("main-a", facts.MainHeadSha);
        Assert.Equal(1, facts.RegateCount);
        Assert.Equal(1, facts.RegateCap);
        Assert.Equal(partition.Name, facts.TestIdentities);
    }

    private static void AssertDecision(Goal goal, ConductorAdvanceOutcome outcome, PolicyDecisionRecord? record,
        string reason, int rung, string action, string evidence)
    {
        var decision = Assert.IsType<PolicyDecisionRecord>(record);
        Assert.Equal("acceptance-apparatus-disposition", decision.Stage);
        Assert.Equal(rung, decision.Rung);
        Assert.Equal(action, decision.Action);
        Assert.Equal(evidence, decision.DiscriminatingEvidence);
        Assert.Equal(reason, decision.Reason);
        Assert.Equal(goal.Id.Value, decision.Facts.Single(fact => fact.Name == "goalId").Value);
        Assert.Equal(decision, VerifiedAcceptanceEscalationDecision.BuildTickOutcomePayload(outcome).Decision);
        var state = outcome switch
        {
            ConductorAdvanceOutcome.Held held => held.State,
            ConductorAdvanceOutcome.Escalated escalated => escalated.State,
            _ => throw new InvalidOperationException("Expected hold or escalation.")
        };
        Assert.Equal(GoalLifecycleState.Verified, state);
        Assert.Equal(WorkTaskStatus.Completed, goal.Tasks.Single().Status);
        Assert.Equal(0, goal.AutomaticAcceptanceRetryCount);
    }

    private static void AssertRegateFacts(PolicyDecisionRecord decision, string identities)
    {
        var facts = AcceptanceApparatusDispositionFacts.FromRecordedFacts(decision.Facts);
        Assert.Equal("candidate-a", facts.BranchHeadSha);
        Assert.Equal("main-a", facts.MainHeadSha);
        Assert.Equal(1, facts.RegateOrdinal);
        Assert.Equal(1, facts.RegateCap);
        Assert.Equal(identities, facts.TestIdentities);
    }

    private static AcceptanceFailingTestIndex CreateIndex(string root) =>
        new(System.IO.Path.Combine(root, "acceptance-gate-attempts", AcceptanceFailingTestIndex.FileName));

    private static AcceptanceVerificationSummary Summary(AcceptanceCheckResult check, bool introduced = false) =>
        new(false, [check], FailedChecks: [check.Name], BranchHeadSha: "candidate-a", MainHeadSha: "main-a",
            CheckAttributions: introduced ? [new AcceptanceCheckAttribution(check.Name, AcceptanceFailureOrigin.Introduced, "main is attested green")] : null);

    // Copied from ConductorDriverTestsWithinAttemptRerunApparatus; no existing assertions are changed.
    private static AcceptanceCheckResult MakePartitionVerdict(string root, GoalId goalId)
    {
        var partition = new GoalAcceptanceVerifier.AcceptanceManifestCheck
        {
            Name = "infrastructure tests: Process spawning",
            Type = "dotnet-test",
            Project = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
            Arguments = ["--filter", "FullyQualifiedName~ProcessSpawning"]
        };
        var cache = Assert.IsType<AcceptancePartitionVerdictCache>(AcceptancePartitionVerdictCache.Create(
            new AcceptancePartitionVerdictCacheOptions(
                goalId, root, [partition], 5, true,
                _ => "candidate", _ => "main", _ => "commit", () => "attempt", () => "manifest",
                () => false)));
        var first = new AcceptanceCheckResult(
            partition.Name, false, 0, "first run crashed without TRX",
            FailureClassification: AcceptanceShardCompletionPredicates.MissingTrx,
            CompletionDecision: new AcceptanceShardCompletionDecision(
                false, AcceptanceShardCompletionPredicates.MissingTrx, false, 0, 351, 0, "missing"));
        cache.RecordWithinAttemptRetry(
            partition, first, "attempt:partition:0", "attempt:partition:1",
            new AcceptanceRetainedDiagnostic("first-run.err", "first-run-sha"));
        var rerun = new AcceptanceCheckResult(
            partition.Name, true, 0, null,
            TestResultPaths: ["rerun.trx"], ExecutedTestCount: 351,
            CompletionDecision: new AcceptanceShardCompletionDecision(true, null, false, 0, 351, 351, "parsed"));
        var verdict = cache.SelectPartitionVerdict(partition, first, rerun);
        Assert.False(verdict.Passed);
        Assert.NotNull(verdict.WithinAttemptRerun);
        return verdict;
    }

    private sealed class TempRoot : IDisposable
    {
        internal string Path { get; } = ConductorDriverTests.CreateTempDirectory();
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
