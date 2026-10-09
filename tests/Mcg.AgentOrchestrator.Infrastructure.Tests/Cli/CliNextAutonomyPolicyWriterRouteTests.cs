using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// The fact owns its workspace and stores; console capture is async-local.
public sealed class CliNextAutonomyPolicyWriterRouteTests : CliTaskQueryTestSupport
{
    private static readonly GoalId TargetId = new("abc10000aaaaaaaaaaaaaaaaaaaaaaaa");

    [Fact]
    public void BareNext_WithAutonomyPolicy_ResolvesCurrentGoal()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            _ = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
            var kernel = new AgentOrchestratorKernel();
            kernel.CreateGoal(TargetId, "Autonomy policy writer query");
            var repository = new ProbeStateRepository(kernel);
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = null;

            var output = CaptureConsole(() => Assert.False(CliPersistentStateRunner.ExecuteCommand(
                ["next", "--autonomy-policy", "observe"], repository, workspace, ref agents,
                new InMemoryModelProviderRegistry([]), ref profiles, ref currentGoal,
                skipReadOnlyRoute: true)));

            Assert.Equal(TargetId, currentGoal?.Id);
            Assert.Contains("Goal abc10000 ", output, StringComparison.Ordinal);
            Assert.DoesNotContain("Goal 'observe' was not found", output, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
