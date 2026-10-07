using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each case owns its workspace and repository; console capture is async-local.
public sealed class CliMonitorReadOnlyRouteTests : CliTaskQueryTestSupport
{
    [Xunit.Theory]
    [Xunit.InlineData("monitor", "abc10000")]
    [Xunit.InlineData("MONITOR", "ABC10000")]
    public void ExplicitPrefix_ClassifiesAndUsesOnlyTargetedReads(params string[] args)
    {
        Xunit.Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
        var root = CreateTempDirectory();
        try
        {
            var kernel = CliTimelineReadOnlyRouteTests.CreateTimelineSeed(root);
            var target = kernel.Goals.Single(goal => goal.Id.Value.StartsWith("abc10000"));
            var other = kernel.Goals.Single(goal => goal.Id != target.Id);
            var repository = new ProbeStateRepository(kernel) { ThrowOnOutbox = true };
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = other;
            var output = CaptureConsole(() =>
            {
                Xunit.Assert.True(CliReadOnlyCommandRunner.TryExecute(
                    args, repository, OrchestratorWorkspace.ForDirectory(root),
                    new InMemoryModelProviderRegistry([]), null,
                    ref agents, ref profiles, ref currentGoal, out var changed));
                Xunit.Assert.False(changed);
            });

            Xunit.Assert.Contains("Goal abc10000", output);
            Xunit.Assert.Equal(target.Id, currentGoal!.Id);
            Xunit.Assert.Equal(1, repository.ListGoalMetadataCount);
            Xunit.Assert.Equal(1, repository.LoadGoalsCount);
            Xunit.Assert.Equal([target.Id.Value], repository.LoadedGoalIds);
            Xunit.Assert.DoesNotContain(other.Id.Value, repository.LoadedGoalIds);
            Xunit.Assert.Equal(0, repository.MutationAttempts);
            Xunit.Assert.Equal(0, repository.SaveAttempts);
            Xunit.Assert.Equal(0, repository.MergeSaveAttempts);
            Xunit.Assert.Equal(0, repository.ListOutboxMessagesCount);
            Xunit.Assert.Equal(0, repository.OutboxClaimAttempts);
            Xunit.Assert.Equal(0, repository.FullLoadAttempts);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData("monitor")]
    [Xunit.InlineData("monitor", "--help")]
    [Xunit.InlineData("monitor", "-h")]
    [Xunit.InlineData("monitor", "-x")]
    [Xunit.InlineData("monitor", "abc10000", "extra")]
    [Xunit.InlineData("monitor", "abc10000", "-h")]
    [Xunit.InlineData("monitor", "")]
    [Xunit.InlineData("monitor", " ")]
    public void OtherShapes_KeepTheirExistingRoute(params string[] args) =>
        Xunit.Assert.False(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));

    [Xunit.Theory]
    [Xunit.InlineData("status", "abc10000")]
    [Xunit.InlineData("STATUS", "ABC10000")]
    public void StatusPrefix_KeepsReadOnlyRoute(params string[] args) =>
        Xunit.Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));

    [Xunit.Theory]
    [Xunit.InlineData("status", "")]
    [Xunit.InlineData("status", " ")]
    [Xunit.InlineData("status", "--help")]
    [Xunit.InlineData("status", "-x")]
    [Xunit.InlineData("status", "abc10000", "extra")]
    public void StatusOtherShapes_KeepTheirExistingRoute(params string[] args) =>
        Xunit.Assert.False(CliStatusQueryCommand.IsStatusQueryCommand(args));
}
