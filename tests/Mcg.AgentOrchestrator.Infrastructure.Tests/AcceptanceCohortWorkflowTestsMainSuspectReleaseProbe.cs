using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class AcceptanceCohortWorkflowTestsMainSuspectReleaseProbe : AcceptanceCohortWorkflowTests
{
    [Theory]
    [InlineData(true, 2, "Passed")]
    [InlineData(false, 2, "Failed")]
    [InlineData(true, 0, "CouldNotRun")]
    public async Task RealProbe_RunsRecordedTestsOnDetachedMainAndReleasesCheckoutAndLease(
        bool passed, int executed, string expected)
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        try
        {
            AddAcceptanceManifest(repo);
            var tip = RunGitOutput(repo, "rev-parse", "main").Trim();
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var verifier = new ProbeVerifier(tip, passed, executed, Path.Combine(repo, "probe.trx"));
            var driver = new ConductorDriver(new AgentOrchestratorKernel(), workspace, verifier,
                AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(),
                cleanupHooks: CreateIsolatedCleanupContext(repo).Hooks);
            DotnetBuildEnvironmentLease? acquired = null;
            var released = 0;
            driver.CohortPartitionStableSlotLeaseSource = (key, _) =>
            {
                Assert.Equal($"main-suspect-release:{tip}", key);
                acquired = AcceptanceStableSlotTestSupport.CreateFakeStableSlotLease(repo);
                acquired.RegisterExecutionLockReleaseObserver(() => released++);
                return acquired;
            };

            var result = await driver.CreateMainSuspectReleaseProbe().RunAsync(tip,
                AcceptanceEngineMainSuspectReleaseTests.Tests, CancellationToken.None);

            Assert.Equal(expected, result.Outcome.ToString());
            Assert.Equal(1, verifier.Calls);
            Assert.NotNull(verifier.Checkout);
            Assert.NotEqual(Path.GetFullPath(repo), Path.GetFullPath(verifier.Checkout));
            Assert.False(Directory.Exists(verifier.Checkout));
            Assert.DoesNotContain(verifier.Checkout.Replace('\\', '/'),
                RunGitOutput(repo, "worktree", "list", "--porcelain").Replace('\\', '/'));
            Assert.Equal(tip, RunGitOutput(repo, "rev-parse", "main").Trim());
            Assert.NotNull(acquired);
            Assert.False(acquired.IsExecutionLockHeld);
            Assert.Equal(1, released);
            if (executed > 0) Assert.Equal(Path.Combine(repo, "probe.trx"), result.ProbeReceipt);
        }
        finally { DeleteDirectory(repo); }
    }

    [Fact]
    public async Task RealProbe_UnselectableIdentitiesNeverRunVerifier()
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var tip = RunGitOutput(repo, "rev-parse", "main").Trim();
            var verifier = new ProbeVerifier(tip, true, 1, "receipt");
            var driver = new ConductorDriver(new AgentOrchestratorKernel(), workspace, verifier,
                AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(),
                cleanupHooks: CreateIsolatedCleanupContext(repo).Hooks);
            var result = await driver.CreateMainSuspectReleaseProbe().RunAsync(tip, ["Fixture.Tests.DataCase(1)"], CancellationToken.None);
            Assert.Equal(MainSuspectReleaseProbeOutcome.CouldNotRun, result.Outcome);
            Assert.Equal("selection-unavailable", result.Reason);
            Assert.Equal(0, verifier.Calls);
        }
        finally { DeleteDirectory(repo); }
    }

    private sealed class ProbeVerifier(string tip, bool passed, int executed, string receipt) : IGoalAcceptanceVerifier
    {
        internal int Calls { get; private set; }
        internal string? Checkout { get; private set; }
        public Task<AcceptanceVerificationResult> RunOwnedAsync(string worktreePath, GoalId? goalId,
            IReadOnlyList<string>? changedFiles, int? stableSlotIndex, DotnetBuildEnvironmentLease? stableSlotLease,
            IAcceptanceAttemptExecutionOwner executionOwner) => throw new InvalidOperationException("Release must run focused tests only.");

        public Task<FocusedEvidenceRunResult> RunFocusedEvidenceOwnedAsync(string worktreePath, GoalId? goalId,
            string request, IAcceptanceFocusedVerificationOwner executionOwner, int? stableSlotIndex = null,
            DotnetBuildEnvironmentLease? stableSlotLease = null, bool runBaselineArm = false)
        {
            Calls++;
            Checkout = worktreePath;
            Assert.Null(goalId);
            Assert.NotNull(stableSlotIndex);
            Assert.True(stableSlotLease!.IsExecutionLockHeld);
            Assert.False(runBaselineArm);
            Assert.Equal(tip, RunGitOutput(worktreePath, "rev-parse", "HEAD").Trim());
            Assert.NotEqual(0, GitCli.Run(worktreePath, "symbolic-ref", "-q", "HEAD").ExitCode);
            Assert.Equal("FullyQualifiedName~Fixture.Shared.FailsA; FullyQualifiedName~Fixture.Shared.FailsB", request);
            return Task.FromResult(new FocusedEvidenceRunResult(request, true, passed, "controlled focused probe",
                [new AcceptanceCheckResult("focused", passed, passed ? 0 : 1, "controlled",
                    ExecutedTestCount: executed, TestResultPaths: [receipt]) ]));
        }
    }
}
