using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static GoalAcceptanceVerifierDotnetBuildSlotTestsTrustedBaselineDiscovery;

[Xunit.Collection(TestCollections.JobAccounting)]
public sealed class GoalAcceptanceVerifierDotnetBuildSlotTestsStructuralCoveragePermitWait : GoalAcceptanceVerifierDotnetBuildSlotTests
{
    [Fact]
    public async Task StructuralCoverageIoRetryWaitsWhenAnotherHolderTakesReleasedPermit()
    {
        var (root, mainRoot) = CreateTrustedBaselineWorkspace();
        var goalId = GoalId.New();
        DotnetBuildEnvironmentLease? holder = null;
        var baselineBuilds = 0;
        var waitHeartbeats = 0;
        TestOverrides.ResolveMainWorktreePathForTests = _ => mainRoot;
        TestOverrides.ResolveDeletedTestFilesForTests = _ => [];
        TestOverrides.StructuralCoveragePermitWaitBound = TimeSpan.FromSeconds(1);
        TestOverrides.OnBuildArtifactIoRetryLeaseReleasedForTests = () =>
            holder = DotnetBuildEnvironmentManager.AcquireLeaseExecutionPermit(
                DotnetBuildEnvironmentManager.ResolveGoalEnvironment(goalId));
        TestOverrides.OnStructuralCoveragePermitWaitForTests = _ =>
        {
            waitHeartbeats++;
            holder?.Dispose();
        };
        LockAttribution.AttributeForTests = (path, _) => new BuildLockAttribution(
            path, [new BuildLockHolder(null, "foreign-csc", null, false)], "test");
        try
        {
            var verifier = new GoalAcceptanceVerifier(TestOverrides, (args, worktree, _) =>
            {
                if (IsVstestExecution(args))
                {
                    WriteVstestTrx(args, "Sample.Tests.Passes");
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                        0, "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."));
                }
                if (args.Contains("--list-tests", StringComparer.OrdinalIgnoreCase))
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                        0, "The following Tests are available:\n    Sample.Tests.Passes"));
                if (worktree == mainRoot && args.Length >= 2 && args[0] == "dotnet" && args[1] == "build" &&
                    ++baselineBuilds == 1)
                    return Task.FromException<GoalAcceptanceVerifier.CommandResult>(
                        new IOException("Build artifacts unavailable"));
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
            });

            var result = await verifier.RunAsync(root, goalId, changedFiles: ["src/Sample.cs"]);

            Assert.True(result.Passed);
            Assert.Equal(2, baselineBuilds);
            Assert.True(Assert.Single(result.Checks!.Where(check => check.Name == "structural test coverage")).Passed);
            Assert.True(waitHeartbeats > 0);
        }
        finally
        {
            holder?.Dispose();
            LockAttribution.AttributeForTests = null;
            DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(goalId);
            DeleteDirectoryWithRetry(root);
            DeleteDirectoryWithRetry(mainRoot);
        }
    }

    [Fact]
    public async Task GreenLanesKeepVerdictWhenStructuralCoveragePermitIsTemporarilyHeld()
    {
        var (root, mainRoot) = CreateTrustedBaselineWorkspace();
        var goalId = GoalId.New();
        DotnetBuildEnvironmentLease? holder = null;
        var waitProgress = new List<AcceptanceGateProgress>();
        var baselineBuilds = 0;
        TestOverrides.ResolveMainWorktreePathForTests = _ => mainRoot;
        TestOverrides.ResolveDeletedTestFilesForTests = _ => [];
        TestOverrides.StructuralCoveragePermitWaitBound = TimeSpan.FromSeconds(1);
        TestOverrides.OnStructuralCoverageStartedForTests = () =>
            holder = DotnetBuildEnvironmentManager.AcquireLeaseExecutionPermit(
                DotnetBuildEnvironmentManager.ResolveGoalEnvironment(goalId));
        TestOverrides.OnStructuralCoveragePermitWaitForTests = progress =>
        {
            waitProgress.Add(progress);
            holder?.Dispose();
        };
        try
        {
            var verifier = new GoalAcceptanceVerifier(TestOverrides, (args, worktree, _) =>
            {
                if (IsVstestExecution(args))
                {
                    WriteVstestTrx(args, "Sample.Tests.Passes");
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                        0, "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."));
                }
                if (args.Contains("--list-tests", StringComparer.OrdinalIgnoreCase))
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                        0, "The following Tests are available:\n    Sample.Tests.Passes"));
                if (worktree == mainRoot && args.Length >= 2 && args[0] == "dotnet" && args[1] == "build")
                    baselineBuilds++;
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
            });

            var result = await verifier.RunAsync(root, goalId, changedFiles: ["src/Sample.cs"]);

            Assert.True(result.Passed);
            Assert.True(Assert.Single(result.Checks!.Where(check => check.Name == "structural test coverage")).Passed);
            Assert.Equal(1, baselineBuilds);
            Assert.Contains(waitProgress, progress =>
                progress.Phase == StructuralCoveragePermitWait.PhaseName && progress.SlotIndex.HasValue);
        }
        finally
        {
            holder?.Dispose();
            DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(goalId);
            DeleteDirectoryWithRetry(root);
            DeleteDirectoryWithRetry(mainRoot);
        }
    }
}
