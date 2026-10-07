using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each case owns its workspace and repositories; console capture is async-local.
public sealed class CliMonitorRouteParityTests : CliTaskQueryTestSupport
{
    [Xunit.Fact]
    public void ExplicitPrefix_PreservesWriterPathOutput()
    {
        var root = CreateTempDirectory();
        try
        {
            var kernel = CliTimelineReadOnlyRouteTests.CreateTimelineSeed(root);
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            string[] args = ["monitor", "abc10000"];
            Xunit.Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
            var expected = Execute(args, kernel, workspace, skipReadOnlyRoute: true);
            var actual = Execute(args, kernel, workspace, skipReadOnlyRoute: false);
            Xunit.Assert.Contains("Goal abc10000", expected);
            Xunit.Assert.Contains("Tasks: 1", expected);
            Xunit.Assert.Equal(expected, actual);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData("missing")]
    [Xunit.InlineData("abc")]
    public void InvalidPrefix_PreservesWriterPathError(string prefix)
    {
        var root = CreateTempDirectory();
        try
        {
            var kernel = CliTimelineReadOnlyRouteTests.CreateTimelineSeed(root);
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            string[] args = ["monitor", prefix];
            Xunit.Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
            var expected = Xunit.Record.Exception(() => Execute(args, kernel, workspace, true));
            var actual = Xunit.Record.Exception(() => Execute(args, kernel, workspace, false));
            Xunit.Assert.NotNull(expected);
            Xunit.Assert.NotNull(actual);
            Xunit.Assert.Equal(expected.GetType(), actual.GetType());
            Xunit.Assert.Equal(expected.Message, actual.Message);
            if (prefix == "missing")
            {
                Xunit.Assert.IsType<KeyNotFoundException>(actual);
                Xunit.Assert.Equal("Goal 'missing' was not found.", actual.Message);
            }
            else
            {
                Xunit.Assert.IsType<InvalidOperationException>(actual);
                Xunit.Assert.Equal("Goal prefix 'abc' is ambiguous.", actual.Message);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string Execute(
        string[] args, AgentOrchestratorKernel kernel, OrchestratorWorkspace workspace,
        bool skipReadOnlyRoute)
    {
        var repository = new ProbeStateRepository(kernel);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        return CaptureConsole(() => Xunit.Assert.False(CliPersistentStateRunner.ExecuteCommand(
            args, repository, workspace, ref agents, new InMemoryModelProviderRegistry([]),
            ref profiles, ref currentGoal, skipReadOnlyRoute: skipReadOnlyRoute)));
    }
}
