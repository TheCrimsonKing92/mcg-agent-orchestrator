using System.Collections.Concurrent;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static GoalAcceptanceVerifierDotnetBuildSlotTestsTrustedBaselineDiscovery;

[Xunit.Collection(TestCollections.JobAccounting)]
public sealed class StructuralCoverageVerifierPermitWaitTestsRetryAfterLanes : GoalAcceptanceVerifierTestBase
{
    [Fact]
    public async Task PermitReleasedOnSecondAttempt_GreenLanesKeepPassingVerdict()
    {
        using var fixture = new RetryFixture(TestOverrides, holdPermit: true, releaseOnRetry: true);

        var result = await fixture.RunAsync();

        Assert.True(result.Passed);
        Assert.True(Assert.Single(result.Checks!.Where(check => check.Name == "structural test coverage")).Passed);
        Assert.Equal(2, fixture.DistinctHeartbeatPaths.Length);
        Assert.Equal(2, fixture.BaselineStarts);
        Assert.Equal(1, fixture.BaselineBuilds);
        var retry = Assert.Single(fixture.RetryProgress);
        Assert.Equal("retry=1/2", retry.CurrentTarget);
        Assert.Equal(1, Assert.Single(fixture.RetryLaneCounts));
        fixture.AssertPermitScope();
    }

    [Fact]
    public async Task PermitHeldForEveryAttempt_ThirdMissRaisesSameDeferral()
    {
        using var fixture = new RetryFixture(TestOverrides, holdPermit: true);

        var fault = await Assert.ThrowsAsync<AcceptanceInfrastructureDeferredException>(fixture.RunAsync);

        Assert.Equal(StructuralCoveragePermitWait.UnavailableReasonCode, fault.ReasonCode);
        Assert.Equal(3, fixture.DistinctHeartbeatPaths.Length);
        Assert.Equal(3, fixture.BaselineStarts);
        Assert.Equal(0, fixture.BaselineBuilds);
        Assert.Equal(["retry=1/2", "retry=2/2"], fixture.RetryProgress.Select(progress => progress.CurrentTarget));
        Assert.All(fixture.RetryLaneCounts, count => Assert.Equal(1, count));
        fixture.AssertPermitScope();
    }

    [Fact]
    public async Task TrustedMainBuildFails_NonPermitFaultIsNeverRetried()
    {
        using var fixture = new RetryFixture(TestOverrides, failBaselineBuild: true);

        var fault = await Assert.ThrowsAsync<AcceptanceInfrastructureDeferredException>(fixture.RunAsync);

        Assert.Equal("trusted-main-build-failed", fault.ReasonCode);
        Assert.Equal(1, fixture.BaselineStarts);
        Assert.Equal(1, fixture.BaselineBuilds);
        Assert.Empty(fixture.DistinctHeartbeatPaths);
        Assert.Empty(fixture.RetryProgress);
    }

    [Fact]
    public async Task PermitRetryHitsNonPermitFault_StopsAfterSecondAttempt()
    {
        using var fixture = new RetryFixture(TestOverrides,
            holdPermit: true, releaseOnRetry: true, failBaselineBuild: true);

        var fault = await Assert.ThrowsAsync<AcceptanceInfrastructureDeferredException>(fixture.RunAsync);

        Assert.Equal("trusted-main-build-failed", fault.ReasonCode);
        Assert.Equal(2, fixture.DistinctHeartbeatPaths.Length);
        Assert.Equal(2, fixture.BaselineStarts);
        Assert.Equal(1, fixture.BaselineBuilds);
        Assert.Equal("retry=1/2", Assert.Single(fixture.RetryProgress).CurrentTarget);
        Assert.Equal(1, Assert.Single(fixture.RetryLaneCounts));
        fixture.AssertPermitScope();
    }

    private sealed class RetryFixture : IDisposable
    {
        private readonly string _root;
        private readonly string _mainRoot;
        private readonly GoalId _goalId = GoalId.New();
        private readonly DotnetBuildStorageRoot _storage;
        private readonly PerUserGoalRootLeakProbe _leakProbe;
        private readonly GoalAcceptanceVerifier _verifier;
        private readonly ConcurrentQueue<AcceptanceGateProgress> _waitProgress = new();
        private DotnetBuildEnvironmentLease? _holder;
        private DotnetBuildEnvironment? _observedEnvironment;
        private string? _firstHeartbeatPath;
        private int _baselineStarts;
        private int _baselineBuilds;
        private int _lanesCompleted;

        internal int BaselineStarts => Volatile.Read(ref _baselineStarts);
        internal int BaselineBuilds => Volatile.Read(ref _baselineBuilds);
        internal string[] DistinctHeartbeatPaths => _waitProgress.Select(progress => progress.HeartbeatPath).Distinct().ToArray();
        internal ConcurrentQueue<AcceptanceGateProgress> RetryProgress { get; } = new();
        internal ConcurrentQueue<int> RetryLaneCounts { get; } = new();

        internal RetryFixture(GoalAcceptanceVerifierTestOverrides testOverrides,
            bool holdPermit = false, bool releaseOnRetry = false, bool failBaselineBuild = false)
        {
            (_root, _mainRoot) = CreateTrustedBaselineWorkspace();
            _storage = new DotnetBuildStorageRoot(Path.Combine(_root, ".orchestrator", "test-dotnet"));
            _leakProbe = new PerUserGoalRootLeakProbe(_goalId);
            testOverrides.BuildStorageRootForTests = _storage;
            testOverrides.ResolveMainWorktreePathForTests = _ => _mainRoot;
            testOverrides.ResolveDeletedTestFilesForTests = _ => [];
            testOverrides.StructuralCoveragePermitWaitBound = TimeSpan.FromSeconds(1);
            testOverrides.OnTrustedMainBaselineBuildStartingForTests = _ =>
            {
                if (Interlocked.Increment(ref _baselineStarts) == 1 && holdPermit)
                    _holder = DotnetBuildEnvironmentManager.AcquireLeaseExecutionPermit(
                        _observedEnvironment = DotnetBuildEnvironmentManager.ResolveGoalEnvironment(_goalId, _storage));
            };
            testOverrides.OnStructuralCoveragePermitWaitForTests = progress =>
            {
                _waitProgress.Enqueue(progress);
                _firstHeartbeatPath ??= progress.HeartbeatPath;
                if (releaseOnRetry && progress.HeartbeatPath != _firstHeartbeatPath)
                {
                    _holder?.Dispose();
                    _holder = null;
                }
            };
            _verifier = new GoalAcceptanceVerifier(testOverrides, (args, worktree, _) =>
            {
                if (IsVstestExecution(args))
                {
                    WriteVstestTrx(args, "Sample.Tests.Passes");
                    Interlocked.Increment(ref _lanesCompleted);
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                        0, "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."));
                }
                if (args.Contains("--list-tests", StringComparer.OrdinalIgnoreCase))
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                        0, "The following Tests are available:\n    Sample.Tests.Passes"));
                if (worktree == _mainRoot && args.Length >= 2 && args[0] == "dotnet" && args[1] == "build")
                {
                    Interlocked.Increment(ref _baselineBuilds);
                    if (failBaselineBuild)
                        return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(1, "error CS1002: ; expected"));
                }
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
            });
        }

        internal Task<AcceptanceVerificationResult> RunAsync() => _verifier.RunOwnedAsync(
            _root, _goalId, changedFiles: ["src/Sample.cs"], stableSlotIndex: null, stableSlotLease: null,
            cancellationToken: CancellationToken.None,
            executionOptions: new AcceptanceRunExecutionOptions(ProgressSink: progress =>
            {
                if (progress.Phase == GoalAcceptanceVerifier.StructuralCoveragePermitRetryPhaseName)
                {
                    RetryProgress.Enqueue(progress);
                    RetryLaneCounts.Enqueue(Volatile.Read(ref _lanesCompleted));
                }
            }));

        internal void AssertPermitScope()
        {
            Assert.All(_waitProgress, progress =>
            {
                Assert.Equal(StructuralCoveragePermitWait.PhaseName, progress.Phase);
                Assert.True(progress.SlotIndex.HasValue);
                Assert.False(string.IsNullOrWhiteSpace(progress.HeartbeatPath));
            });
            _leakProbe.AssertScopedTo(Assert.IsType<DotnetBuildEnvironment>(_observedEnvironment).RootPath, _root);
        }

        public void Dispose()
        {
            _holder?.Dispose();
            DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(_goalId, _storage);
            PerUserGoalRootLeakProbe.DrainRegistrationReports(_storage);
            DeleteDirectoryWithRetry(_root);
            DeleteDirectoryWithRetry(_mainRoot);
        }
    }
}
