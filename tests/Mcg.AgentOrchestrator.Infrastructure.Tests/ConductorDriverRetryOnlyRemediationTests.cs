using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;
using static ConductorDriverTests;

[Collection("IsolatedProcessSpawning")]
public sealed class ConductorDriverRetryOnlyRemediationTests
{
    [Fact]
    public void UnsetRemediationRetriesSpawnFailureOnceAndReportsRetryOnly()
    {
        var (_, goal) = SimpleGoal();
        var startCalls = 0;
        var phaseTimings = new List<string>();
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: _ => ++startCalls == 1
                ? DispatchStartOutcome.SpawnFailed("worker refused to launch")
                : DispatchStartOutcome.Started());
        driver.PhaseTimingSink = phaseTimings.Add;

        driver.BeginTick();
        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.Equal(2, startCalls);
        Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
        var remediationTiming = Assert.Single(phaseTimings,
            line => line.Contains("phase=dispatch-remediation", StringComparison.Ordinal));
        Assert.EndsWith("result=retry-only", remediationTiming, StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultCleanupShutdownIsGoalWorktreesNoOpAndPreservesDirectory()
    {
        var hook = new GoalWorktreeCleanupHooks().BuildServerShutdown;
        Assert.Equal(typeof(GoalWorktrees), hook.Method.DeclaringType);
        Assert.Equal("NoOpBuildServerShutdown", hook.Method.Name);

        var directory = ConductorDriverTests.CreateTempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, "nested"));
            File.WriteAllText(Path.Combine(directory, "nested", "receipt.txt"), "unchanged");
            var before = SnapshotDirectory(directory);

            hook(directory, 1_000);

            Assert.True(Directory.Exists(directory));
            Assert.Equal(before, SnapshotDirectory(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string[] SnapshotDirectory(string root) =>
        Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path) + ":" +
                (Directory.Exists(path) ? "directory" : Convert.ToBase64String(File.ReadAllBytes(path))))
            .Order(StringComparer.Ordinal)
            .ToArray();
}
