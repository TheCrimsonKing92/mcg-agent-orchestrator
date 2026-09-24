using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(CliTestCollections.ConsoleSerialized)]
public sealed class CliGoalEventsStatusGoldenOutputTests : CliTaskQueryTestSupport
{
    [Xunit.Theory]
    [Xunit.InlineData("status")]
    [Xunit.InlineData("goal-events")]
    public void ReadRouteOutputMatchesLegacyDispatcherBytes(string verb)
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal(new GoalId("abc10000aaaaaaaaaaaaaaaaaaaaaaaa"), "Golden query fixture");
            Directory.CreateDirectory(workspace.GoalLifecycleEventsDirectory);
            File.WriteAllText(Path.Combine(workspace.GoalLifecycleEventsDirectory, $"{goal.Id.Value}.jsonl"),
                "{\"eventKind\":\"golden\"}" + Environment.NewLine);
            var args = new[] { verb, goal.Id.Value[..8] };
            IReadOnlyList<AgentDefinition> legacyAgents = AgentCatalog.Default().Agents;
            var legacyProfiles = WorkerProfileCatalog.Default();
            Goal? legacyCurrentGoal = goal;
            var providers = new InMemoryModelProviderRegistry([]);
            var oldOutput = CaptureConsole(() =>
            {
                var changed = CliCommandDispatcher.ExecuteCommand(args, kernel, workspace,
                    ref legacyAgents, providers, ref legacyProfiles, ref legacyCurrentGoal);
                Xunit.Assert.False(changed);
            });

            var repository = new ProbeStateRepository(kernel) { ThrowOnOutbox = true };
            IReadOnlyList<AgentDefinition> readAgents = AgentCatalog.Default().Agents;
            var readProfiles = WorkerProfileCatalog.Default();
            Goal? readCurrentGoal = null;
            var newOutput = CaptureConsole(() =>
            {
                var claimed = CliReadOnlyCommandRunner.TryExecute(args, repository, workspace,
                    providers, null, ref readAgents, ref readProfiles, ref readCurrentGoal, out var changed);
                Xunit.Assert.True(claimed);
                Xunit.Assert.False(changed);
            });

            Xunit.Assert.Equal(oldOutput, newOutput);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void UnknownStatusGoalPreservesLegacyError()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var kernel = new AgentOrchestratorKernel();
            kernel.CreateGoal(new GoalId("abc10000aaaaaaaaaaaaaaaaaaaaaaaa"), "Known goal");
            var args = new[] { "status", "missing" };
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = null;
            var providers = new InMemoryModelProviderRegistry([]);
            var oldError = Xunit.Assert.Throws<KeyNotFoundException>(() =>
                CliCommandDispatcher.ExecuteCommand(args, kernel, workspace, ref agents,
                    providers, ref profiles, ref currentGoal));
            var repository = new ProbeStateRepository(kernel);
            var newError = Xunit.Assert.Throws<KeyNotFoundException>(() =>
                CliReadOnlyCommandRunner.TryExecute(args, repository, workspace, providers,
                    null, ref agents, ref profiles, ref currentGoal, out _));
            Xunit.Assert.Equal(oldError.Message, newError.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
