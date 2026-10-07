using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Each fixture owns its workspace; console capture uses the existing async-local router.
public sealed class CliGoalTimingReadOnlyRouteTests : CliTaskQueryTestSupport
{
    [Xunit.Theory]
    [Xunit.InlineData(true, "goal-timing", "abc10000")]
    [Xunit.InlineData(true, "GOAL-TIMING", "ABC10000")]
    [Xunit.InlineData(false, "goal-timing")]
    [Xunit.InlineData(false, "goal-timing", "--all")]
    [Xunit.InlineData(false, "goal-timing", "--help")]
    [Xunit.InlineData(false, "goal-timing", "-h")]
    [Xunit.InlineData(false, "goal-timing", "-x")]
    [Xunit.InlineData(false, "goal-timing", "abc10000", "extra")]
    [Xunit.InlineData(false, "goal-timing", " ")]
    [Xunit.InlineData(false, "goal-timing", "")]
    [Xunit.InlineData(false, "failure-triage", "abc10000")]
    [Xunit.InlineData(false, "failure-triage")]
    [Xunit.InlineData(false, "failure-triage", "--all")]
    public void CommandForms_ClassifyOnlyOneExplicitTimingPrefix(bool expected, params string[] args)
    {
        Xunit.Assert.Equal(expected, CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
    }

    [Xunit.Theory]
    [Xunit.InlineData("goal-timing", "abc10000")]
    [Xunit.InlineData("GOAL-TIMING", "ABC10000")]
    public void ExplicitPrefix_ReadsOnlyTargetWithoutOutboxOrWrites(params string[] args)
    {
        var root = CreateTempDirectory();
        try
        {
            var kernel = CliSingleGoalReportReadOnlyRouteTests.CreateReportSeed(root);
            var target = kernel.Goals.Single(goal => goal.Id.Value.StartsWith("abc10000", StringComparison.Ordinal));
            var other = kernel.Goals.Single(goal => goal.Id != target.Id);
            var repository = new ProbeStateRepository(kernel) { ThrowOnOutbox = true };
            IReadOnlyList<AgentDefinition> agents = CliSingleGoalReportReadOnlyRouteTests.ReportAgents();
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = other;
            var output = CaptureConsole(() =>
            {
                Xunit.Assert.True(CliReadOnlyCommandRunner.TryExecute(
                    args, repository, OrchestratorWorkspace.ForDirectory(root),
                    new InMemoryModelProviderRegistry([]), null,
                    ref agents, ref profiles, ref currentGoal, out var changed, new FixedClock()));
                Xunit.Assert.False(changed);
            });

            Xunit.Assert.Contains(output.Split(Environment.NewLine),
                line => line.StartsWith("Goal timing abc10000", StringComparison.Ordinal));
            Xunit.Assert.Equal(target.Id, currentGoal!.Id);
            Xunit.Assert.Equal(1, repository.ListGoalMetadataCount);
            Xunit.Assert.Equal(1, repository.LoadGoalsCount);
            Xunit.Assert.Equal([target.Id.Value], repository.LoadedGoalIds);
            Xunit.Assert.Equal(0, repository.FullLoadAttempts);
            Xunit.Assert.Equal(0, repository.ListOutboxMessagesCount);
            Xunit.Assert.Equal(0, repository.OutboxClaimAttempts);
            Xunit.Assert.Equal(0, repository.MutationAttempts);
            Xunit.Assert.Equal(0, repository.SaveAttempts);
            Xunit.Assert.Equal(0, repository.MergeSaveAttempts);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 9, 24, 1, 0, 0, TimeSpan.Zero);
    }
}
