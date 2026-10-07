using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.CliProcessEnvironment)]
public sealed class CliCommandTestsPersistentRunnerCommandsNextLiveDisposition : CliCommandTestBase
{
    [Xunit.Fact]
    public async Task Next_GoalPrefixWithLegacyTick_PrintsLiveDisposition()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = CreateRefinedWorkspace(root);
            var kernel = new AgentOrchestratorKernel();
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var goal = kernel.CreateGoal("Live next disposition", [
                new TaskSpec(TaskId.New(), "Implement feature", AgentRole.Developer)
            ]);
            kernel.ActivateGoal(goal.Id, agents);
            goal = kernel.GetGoal(goal.Id);
            var payload = JsonSerializer.Serialize(new
            {
                OperatorDispositions = new[]
                {
                    new ConductorOperatorDispositionSnapshot(
                        goal.Id.Value,
                        OperatorDispositionState.ProductBug,
                        OperatorDispositionConfidence.High,
                        "stale-run-event-disposition",
                        "stale-run-event-command",
                        new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
                        [], [], [])
                }
            });
            await new SqliteRunEventStore(workspace.RunEventStorePath).AppendAsync(new RunEventAppend(
                RunEventTypes.ConductorTick, goal.Id.Value, "conduct", "completed", null, payload));
            var repository = new InMemoryTransactionalStateRepository(kernel);
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = null;

            var output = CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
                ["next", goal.Id.Value[..8]],
                repository,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal));

            Xunit.Assert.Equal(GoalStatus.Active, goal.Status);
            Xunit.Assert.Contains(output.Split('\n'),
                line => line.StartsWith("Disposition: state='", StringComparison.Ordinal));
            Xunit.Assert.DoesNotContain("stale-run-event-disposition", output);
            Xunit.Assert.DoesNotContain("stale-run-event-command", output);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
