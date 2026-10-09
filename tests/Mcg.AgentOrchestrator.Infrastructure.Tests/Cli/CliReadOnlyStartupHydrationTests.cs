using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(CliTestCollections.ConsoleSerialized)]
public sealed class CliReadOnlyStartupHydrationTests : CliTaskQueryTestSupport
{
    [Xunit.Theory]
    [Xunit.InlineData("status")]
    [Xunit.InlineData("goal-events")]
    public async Task SingleGoalReadDoesNotHydrateOrTouchOutbox(string verb)
    {
        var root = CreateTempDirectory();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal(new GoalId("abc10000aaaaaaaaaaaaaaaaaaaaaaaa"), "Read route fixture");
            var repository = new ProbeStateRepository(kernel) { ThrowOnOutbox = true };
            var args = new[] { verb, goal.Id.Value[..8] };
            var startup = await CliReadOnlyStartupHydration.PrepareStartupAsync(args, repository);
            Xunit.Assert.False(startup.Hydrated);
            Xunit.Assert.Null(startup.CurrentGoal);

            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = startup.CurrentGoal;
            var changed = true;
            var output = CaptureConsole(() => changed = CliReadOnlyStartupHydration.ExecuteStartupCommand(
                args, repository, OrchestratorWorkspace.ForDirectory(root), ref agents,
                new InMemoryModelProviderRegistry([]), ref profiles, ref currentGoal, startup.Hydrated));

            Xunit.Assert.False(changed);
            if (verb == "status")
            {
                Xunit.Assert.Contains(goal.Id.Value, output, StringComparison.Ordinal);
                Xunit.Assert.Equal([goal.Id.Value], repository.LoadedGoalIds);
            }
            else
                Xunit.Assert.Equal(0, repository.LoadGoalsCount);
            Xunit.Assert.Equal(0, repository.FullLoadAttempts);
            Xunit.Assert.Equal(0, repository.ListOutboxMessagesCount);
            Xunit.Assert.Equal(0, repository.OutboxClaimAttempts);
            Xunit.Assert.Equal(0, repository.SaveAttempts);
            Xunit.Assert.Equal(0, repository.MergeSaveAttempts);
            Xunit.Assert.Equal(0, repository.MutationAttempts);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void DeclinedReadRouteHydratesBeforeNormalPath()
    {
        var root = CreateTempDirectory();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Fallback fixture");
            var repository = new ProbeStateRepository(kernel);
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = null;

            string[] args = ["status", goal.Id.Value[..8], "--tasks-only", "--tasks-only"];
            Xunit.Assert.False(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
            var error = Xunit.Assert.Throws<InvalidOperationException>(() =>
                CliReadOnlyStartupHydration.ExecuteStartupCommand(
                    args, repository,
                    OrchestratorWorkspace.ForDirectory(root), ref agents,
                    new InMemoryModelProviderRegistry([]), ref profiles, ref currentGoal,
                    hydrated: false));

            Xunit.Assert.Contains("Full-kernel hydration", error.Message, StringComparison.Ordinal);
            Xunit.Assert.Equal(1, repository.FullLoadAttempts);
            Xunit.Assert.Equal(0, repository.ListOutboxMessagesCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData("goals", "--board")]
    [Xunit.InlineData("task", "1")]
    [Xunit.InlineData("tasks", "")]
    [Xunit.InlineData("status", "abc10000")]
    [Xunit.InlineData("goal-events", "abc10000")]
    public void ServedCommandsSkipWholeStateHydration(string verb, string argument)
    {
        var args = argument.Length == 0 ? new[] { verb } : new[] { verb, argument };
        Xunit.Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
    }

    [Xunit.Theory]
    [Xunit.InlineData("status", "--help")]
    [Xunit.InlineData("goal-events", "--follow")]
    public void NonSingleGoalFormsKeepNormalPath(string verb, string argument) =>
        Xunit.Assert.False(CliReadOnlyCommandRunner.IsReadOnlyCommand([verb, "abc10000", argument]));
}
