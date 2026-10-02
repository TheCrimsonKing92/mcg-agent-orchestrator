using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

public sealed class AcceptanceCohortWorkflowTestsFocusedAttribution : AcceptanceCohortWorkflowTests
{
    private static readonly string[] Identities = ["Tests.First.Fails", "Tests.Second.Fails"];

    [Fact]
    public void SingleReproducer_SavesFocusedAttributionWithoutFullPartitions() =>
        RunInjected(Pass(Identities), Pass([]), (result, runner, store, first, second, databasePath) =>
        {
            var receipt = Assert.IsType<AcceptanceCohortReceipt>(result.Receipt);
            Assert.Equal(AcceptanceCohortAttributionOutcome.FirstMemberFailed, receipt.Attribution);
            Assert.Equal(AcceptanceCohortAttributionSources.FocusedPass, receipt.AttributionSource);
            var attributed = Assert.Single(receipt.AttributedMembers);
            Assert.Equal(first.Id, attributed.GoalId);
            Assert.Equal(0, attributed.MemberOrdinal);
            Assert.Equal(Identities, attributed.ReproducedFailingTests);
            Assert.Equal(1, store.ReadOvertakeCount(second.Id));
            Assert.Equal(new[] { 0, 1 }, runner.FocusedOrdinals);
            Assert.Empty(runner.FullOrdinals);
            var persisted = Assert.IsType<AcceptanceCohortReceipt>(store.TryReadReceipt(receipt.Identity.Value));
            Assert.Equal(receipt.AttributionSource, persisted.AttributionSource);
            Assert.Equal(Identities, Assert.Single(persisted.AttributedMembers).ReproducedFailingTests);
            Assert.All(store.ReadPartitionReceipts(receipt.Identity.Value), partition =>
                Assert.StartsWith("cohort-focused-partition-v1-", partition.ReceiptId));
        });

    [Fact]
    public void PartialReproduction_RunsBothFullPartitionsAndUsesTheirAttribution() =>
        RunInjected(Pass([Identities[0]]), Pass([Identities[1]]), AssertFullFallback);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BothOrNeitherReproduce_RunsBothFullPartitions(bool bothReproduce) =>
        RunInjected(Pass(bothReproduce ? Identities : []), Pass(bothReproduce ? Identities : []), AssertFullFallback);

    [Fact]
    public void SecondMemberReproduces_SavesSecondMemberAndRequeuesFirst() =>
        RunInjected(Pass([]), Pass(Identities), (result, runner, store, first, second, databasePath) =>
        {
            var receipt = Assert.IsType<AcceptanceCohortReceipt>(result.Receipt);
            Assert.Equal(AcceptanceCohortAttributionOutcome.SecondMemberFailed, receipt.Attribution);
            Assert.Equal(AcceptanceCohortAttributionSources.FocusedPass, receipt.AttributionSource);
            var attributed = Assert.Single(receipt.AttributedMembers);
            Assert.Equal(second.Id, attributed.GoalId);
            Assert.Equal(1, attributed.MemberOrdinal);
            Assert.Equal(Identities, attributed.ReproducedFailingTests);
            Assert.Equal(1, store.ReadOvertakeCount(first.Id));
            Assert.Empty(runner.FullOrdinals);
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FocusedInfrastructureFailure_RunsBothFullPartitions(bool throwException) =>
        RunInjected(Pass(Identities), Pass([]) with { Kind = ConductorCohortFocusedPassKind.InfrastructureFailure },
            AssertFullFallback, throwSecondFocused: throwException);

    [Fact]
    public void InnocentMember_IsHeldForOrdinaryAcceptanceWithoutLandingOrPassReceipt() =>
        RunInjected(Pass(Identities), Pass([]), (result, runner, store, first, second, databasePath) =>
        {
            var receipt = Assert.IsType<AcceptanceCohortReceipt>(result.Receipt);
            Assert.IsType<ConductorAdvanceOutcome.Held>(result.MemberResults[second.Id.Value].Outcome);
            Assert.False(receipt.ValidForLanding);
            Assert.Empty(store.ReadPassedReceiptsForGoal(second.Id));
            Assert.Equal(1, store.ReadOvertakeCount(second.Id));
            Assert.Empty(runner.FullOrdinals);
            using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM cohort_landing_intents WHERE cohort_id=$cohort;";
            command.Parameters.AddWithValue("$cohort", receipt.Identity.Value);
            Assert.Equal(0, Convert.ToInt32(command.ExecuteScalar()));
        }, assertInnocentUnchanged: true);

    [Fact]
    public void DefaultRunner_UsesMemberPartitionsOwnedFocusedPathAndReleasesLeases()
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        try
        {
            AddAcceptanceManifest(repo);
            var kernel = new AgentOrchestratorKernel();
            var first = CreateCompletedGoal(kernel, "Focused first member", repo);
            var second = CreateCompletedGoal(kernel, "Focused second member", repo);
            var firstRevision = CreateWorktreeCandidate(repo, first.Id,
                "src/Mcg.AgentOrchestrator.Infrastructure/First.cs", "first");
            var secondRevision = CreateWorktreeCandidate(repo, second.Id, "tests/Second.cs", "second");
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var verifier = new FocusedVerifier(repo, first.Id);
            var driver = new ConductorDriver(kernel, workspace, verifier,
                AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(),
                cleanupHooks: CreateIsolatedCleanupContext(workspace.ExecutionDirectory).Hooks);
            var leases = new List<(DotnetBuildEnvironmentLease Lease, int[] Releases)>();
            driver.CohortPartitionStableSlotLeaseSource = (_, _) =>
            {
                Assert.All(leases, item => Assert.False(item.Lease.IsExecutionLockHeld));
                var lease = AcceptanceStableSlotTestSupport.CreateFakeStableSlotLease(repo);
                var releases = new int[1];
                lease.RegisterExecutionLockReleaseObserver(() => releases[0]++);
                leases.Add((lease, releases));
                return lease;
            };
            var result = driver.RunAcceptanceCohort(ProjectSelection(driver, first, second),
                [first, second], ConductorAutonomyPolicy.Permissive);
            Assert.Equal(1, verifier.FullRuns);
            Assert.Equal(new[] { first.Id, second.Id }, verifier.FocusedGoalIds);
            Assert.All(verifier.Requests, request => Assert.Equal(
                "Infrastructure: FullyQualifiedName~Tests.First.Fails; Infrastructure: FullyQualifiedName~Tests.Second.Fails", request));
            Assert.Equal(AcceptanceCohortAttributionSources.FocusedPass, result.Receipt?.AttributionSource);
            Assert.Equal(AcceptanceCohortAttributionOutcome.FirstMemberFailed, result.Receipt?.Attribution);
            Assert.Equal(2, leases.Count);
            Assert.All(leases, item =>
            {
                Assert.Equal(1, item.Releases[0]);
                Assert.False(item.Lease.IsExecutionLockHeld);
            });
            Assert.Equal(firstRevision, RunGitOutput(GoalWorktrees.TryResolve(repo, first.Id)!, "rev-parse", "HEAD").Trim());
            Assert.Equal(secondRevision, RunGitOutput(GoalWorktrees.TryResolve(repo, second.Id)!, "rev-parse", "HEAD").Trim());
            AssertNoCohortWorkspaces(repo);
        }
        finally { DeleteDirectory(repo); }
    }

    private void RunInjected(ConductorCohortFocusedPassResult firstPass, ConductorCohortFocusedPassResult secondPass,
        Action<ConductorAcceptanceCohortRunResult, RecordingRunner, CohortAcceptanceStore, Goal, Goal, string> assert,
        bool throwSecondFocused = false, bool assertInnocentUnchanged = false)
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        try
        {
            AddAcceptanceManifest(repo);
            var kernel = new AgentOrchestratorKernel();
            var first = CreateCompletedGoal(kernel, "Focused failed member", repo);
            var second = CreateCompletedGoal(kernel, "Focused innocent member", repo);
            CreateWorktreeCandidate(repo, first.Id, "src/Mcg.AgentOrchestrator.Infrastructure/First.cs", "first");
            CreateWorktreeCandidate(repo, second.Id, "tests/Second.cs", "second");
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var verifier = new SequenceAcceptanceVerifier([CohortFailure(repo)]);
            var runner = new RecordingRunner(firstPass, secondPass, throwSecondFocused);
            var driver = new ConductorDriver(kernel, workspace, verifier,
                AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(),
                cleanupHooks: CreateIsolatedCleanupContext(workspace.ExecutionDirectory).Hooks)
            { CohortPartitionRunner = runner };
            var selection = ProjectSelection(driver, first, second);
            var statusBefore = second.Status;
            var mainBefore = RunGitOutput(repo, "rev-parse", "main").Trim();
            var result = driver.RunAcceptanceCohort(selection, [first, second], ConductorAutonomyPolicy.Permissive);
            var databasePath = Path.Combine(workspace.OrchestratorDirectory, "cohort-acceptance.db");
            var store = new CohortAcceptanceStore(databasePath);
            Assert.Equal(1, verifier.RunCount);
            assert(result, runner, store, first, second, databasePath);
            if (assertInnocentUnchanged)
            {
                Assert.Equal(statusBefore, kernel.GetGoal(second.Id).Status);
                Assert.Null(kernel.GetGoal(second.Id).LatestAcceptanceFailure);
                Assert.Equal(mainBefore, RunGitOutput(repo, "rev-parse", "main").Trim());
            }
            AssertNoCohortWorkspaces(repo);
        }
        finally { DeleteDirectory(repo); }
    }

    private static void AssertFullFallback(ConductorAcceptanceCohortRunResult result, RecordingRunner runner,
        CohortAcceptanceStore store, Goal first, Goal second, string databasePath)
    {
        Assert.Equal(new[] { 0, 1 }, runner.FullOrdinals);
        var receipt = Assert.IsType<AcceptanceCohortReceipt>(result.Receipt);
        var expected = ConductorAcceptanceCohortFailingTestAttribution.Classify(
            Identities, runner.FullReceipts[0], runner.FullReceipts[1]);
        Assert.Equal(AcceptanceCohortAttributionOutcome.BothMembersFailed, receipt.Attribution);
        Assert.Equal(expected.Outcome, receipt.Attribution);
        Assert.Null(receipt.AttributionSource);
        Assert.Equal(new[] { first.Id, second.Id }, receipt.AttributedMembers.Select(member => member.GoalId));
        Assert.All(receipt.AttributedMembers, member => Assert.Equal(Identities, member.ReproducedFailingTests));
        Assert.Null(store.TryReadReceipt(receipt.Identity.Value)?.AttributionSource);
        Assert.Equal(runner.FullReceipts.Select(partition => partition.ReceiptId),
            store.ReadPartitionReceipts(receipt.Identity.Value).Select(partition => partition.ReceiptId));
    }

    private static AcceptanceVerificationResult CohortFailure(string repo) =>
        new(false, false, 1, "cohort red", Checks:
        [new AcceptanceCheckResult("cohort", false, 1, "cohort red", FailingTestIdentities: Identities,
            TestProjectPath: "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj")],
            TestResultPaths: [WriteFailingTrx(repo, "cohort-focused-red.trx")]);

    private static ConductorCohortFocusedPassResult Pass(IReadOnlyList<string> failures) =>
        new(ConductorCohortFocusedPassKind.Executed, failures, failures.Count == 0 ? [] : ["focused"],
            2, failures.Count == 0, []);

    private sealed class RecordingRunner(ConductorCohortFocusedPassResult first,
        ConductorCohortFocusedPassResult second, bool throwSecondFocused) : IConductorCohortPartitionRunner
    {
        internal List<int> FocusedOrdinals { get; } = [];
        internal List<int> FullOrdinals { get; } = [];
        internal List<AcceptanceCohortPartitionReceipt> FullReceipts { get; } = [];

        public ConductorCohortFocusedPassResult RunFocused(AcceptanceCohortMemberBinding member, int ordinal,
            AcceptanceCohortIdentity identity, ConductorCohortFocusedSelection selection,
            ConductEventLogWriter writer, CancellationToken cancellationToken)
        {
            FocusedOrdinals.Add(ordinal);
            Assert.Equal(Identities, selection.Identities);
            if (ordinal == 1 && throwSecondFocused) throw new NotSupportedException("controlled focused infrastructure failure");
            return ordinal == 0 ? first : second;
        }

        public AcceptanceCohortPartitionReceipt RunFull(AcceptanceCohortMemberBinding member, int ordinal,
            AcceptanceCohortIdentity identity, ConductEventLogWriter writer, CancellationToken cancellationToken)
        {
            FullOrdinals.Add(ordinal);
            var receipt = new AcceptanceCohortPartitionReceipt($"full-fixture-{ordinal}", member.GoalId, ordinal,
                member.CandidateRevision, identity.ObservedMainRevision, member.CandidateRevision,
                identity.ManifestIdentity, AcceptanceCohortGateOutcome.Failed, 0, [])
            { FailingTestIdentities = Identities, FailedChecks = ["full partition"] };
            FullReceipts.Add(receipt);
            return receipt;
        }
    }

    private sealed class FocusedVerifier(string repo, GoalId reproducer) : IGoalAcceptanceVerifier
    {
        internal int FullRuns { get; private set; }
        internal List<GoalId> FocusedGoalIds { get; } = [];
        internal List<string> Requests { get; } = [];

        public Task<AcceptanceVerificationResult> RunOwnedAsync(string worktreePath, GoalId? goalId,
            IReadOnlyList<string>? changedFiles, int? stableSlotIndex, DotnetBuildEnvironmentLease? stableSlotLease,
            IAcceptanceAttemptExecutionOwner executionOwner)
        {
            FullRuns++;
            Assert.Null(goalId);
            return Task.FromResult(CohortFailure(repo));
        }

        public Task<FocusedEvidenceRunResult> RunFocusedEvidenceOwnedAsync(string worktreePath, GoalId? goalId,
            string request, IAcceptanceFocusedVerificationOwner executionOwner, int? stableSlotIndex = null,
            DotnetBuildEnvironmentLease? stableSlotLease = null, bool runBaselineArm = false)
        {
            Assert.NotNull(goalId);
            Assert.NotNull(stableSlotIndex);
            Assert.True(stableSlotLease!.IsExecutionLockHeld);
            Assert.False(runBaselineArm);
            FocusedGoalIds.Add(goalId);
            Requests.Add(request);
            Assert.Equal(goalId == reproducer, File.Exists(Path.Combine(worktreePath,
                "src/Mcg.AgentOrchestrator.Infrastructure/First.cs")));
            Assert.Equal(goalId != reproducer, File.Exists(Path.Combine(worktreePath, "tests/Second.cs")));
            var passed = goalId != reproducer;
            var check = new AcceptanceCheckResult("focused", passed, passed ? 0 : 1, null,
                FailingTestIdentities: passed ? [] : Identities, ExecutedTestCount: 2);
            return Task.FromResult(new FocusedEvidenceRunResult(request, true, passed, "controlled focused result", [check]));
        }
    }
}
