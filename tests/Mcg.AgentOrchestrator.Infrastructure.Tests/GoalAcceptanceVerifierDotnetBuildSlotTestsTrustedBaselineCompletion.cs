using System.Collections.Concurrent;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static GoalAcceptanceVerifierDotnetBuildSlotTestsTrustedBaselineDiscovery;

// Isolated: JobAccounting owns shared build-slot state; commands and workspace roots are fixtures.
[Xunit.Collection(TestCollections.JobAccounting)]
public sealed class GoalAcceptanceVerifierDotnetBuildSlotTestsTrustedBaselineCompletion : GoalAcceptanceVerifierDotnetBuildSlotTests
{
    [Theory]
    [InlineData(false, "passed")]
    [InlineData(true, "failed")]
    public async Task TrustedBaseline_ReportsCompletionBeforeReturning(bool failBuild, string outcome)
    {
        var (root, mainRoot) = CreateTrustedBaselineWorkspace();
        var goalId = new GoalId(Guid.NewGuid().ToString("N"));
        var storage = new DotnetBuildStorageRoot(Path.Combine(root, ".orchestrator", "test-dotnet"));
        TestOverrides.BuildStorageRootForTests = storage;
        TestOverrides.ResolveMainWorktreePathForTests = _ => mainRoot;
        TestOverrides.ResolveDeletedTestFilesForTests = _ => [];
        var progress = new ConcurrentQueue<AcceptanceGateProgress>();
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
            if (failBuild && worktree == mainRoot && args.Length >= 2 && args[0] == "dotnet" && args[1] == "build")
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(1, "error CS1002: ; expected"));
            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
        });
        try
        {
            try
            {
                var result = await verifier.RunOwnedAsync(root, goalId, changedFiles: ["src/Sample.cs"],
                    stableSlotIndex: null, stableSlotLease: null, cancellationToken: CancellationToken.None,
                    executionOptions: new AcceptanceRunExecutionOptions(ProgressSink: progress.Enqueue));
                if (!failBuild) Assert.True(result.Passed);
            }
            catch (AcceptanceInfrastructureDeferredException) when (failBuild) { }

            var completed = Assert.Single(progress, item => item.Phase == "check-complete" &&
                item.CurrentTarget.EndsWith(" [trusted main baseline]", StringComparison.Ordinal));
            Assert.Equal("sample tests [trusted main baseline]", completed.CurrentTarget);
            Assert.Equal(outcome, completed.Outcome);
        }
        finally
        {
            DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(goalId, storage);
            PerUserGoalRootLeakProbe.DrainRegistrationReports(storage);
            DeleteDirectoryWithRetry(root);
            DeleteDirectoryWithRetry(mainRoot);
        }
    }
}
