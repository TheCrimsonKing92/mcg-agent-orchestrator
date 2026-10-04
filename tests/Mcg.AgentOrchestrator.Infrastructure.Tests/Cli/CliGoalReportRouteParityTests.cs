using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: both routes receive independent clones of the same seed.
public sealed class CliGoalReportRouteParityTests : CliTaskQueryTestSupport
{
    [Xunit.Fact]
    public void ExplicitForms_PreserveWriterPathOutputAndPrefixErrors()
    {
        var root = CreateTempDirectory();
        try
        {
            var kernel = CliGoalReportReadOnlyRouteTests.CreateGoalReportSeed(root);
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            foreach (var args in CliGoalReportReadOnlyRouteTests.ExplicitForms("abc10000"))
            {
                Xunit.Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
                var expected = Execute(args, kernel, workspace, skipReadOnlyRoute: true);
                var actual = Execute(args, kernel, workspace, skipReadOnlyRoute: false);
                Xunit.Assert.NotEmpty(expected);
                if (args[0] == "input-needed")
                    Xunit.Assert.Contains("seeded report question", expected);
                if (args[0] == "revise")
                {
                    Xunit.Assert.Contains("Initial report brief", expected);
                    Xunit.Assert.Contains("Revised report brief", expected);
                }
                Xunit.Assert.Equal(expected, actual);
            }

            foreach (var prefix in new[] { "missing", "abc" })
            foreach (var args in CliGoalReportReadOnlyRouteTests.ExplicitForms(prefix))
            {
                var expected = Xunit.Record.Exception(() => Execute(args, kernel, workspace, true));
                var actual = Xunit.Record.Exception(() => Execute(args, kernel, workspace, false));
                Xunit.Assert.NotNull(expected);
                Xunit.Assert.NotNull(actual);
                Xunit.Assert.Equal(expected.GetType(), actual.GetType());
                Xunit.Assert.Equal(expected.Message, actual.Message);
                if (prefix == "missing")
                    Xunit.Assert.IsType<KeyNotFoundException>(actual);
                else
                    Xunit.Assert.IsType<InvalidOperationException>(actual);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void ExplicitForms_MetadataGoalUnavailable_PreserveWriterPathError()
    {
        var root = CreateTempDirectory();
        try
        {
            var kernel = CliGoalReportReadOnlyRouteTests.CreateGoalReportSeed(root);
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var targetId = kernel.Goals.Single(goal => goal.Id.Value.StartsWith("abc10000")).Id.Value;
            foreach (var args in CliGoalReportReadOnlyRouteTests.ExplicitForms(targetId[..8]))
            {
                Xunit.Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
                var expected = Xunit.Record.Exception(() => Execute(
                    args, kernel, workspace, skipReadOnlyRoute: true, unavailableGoalId: targetId));
                var actual = Xunit.Record.Exception(() => Execute(
                    args, kernel, workspace, skipReadOnlyRoute: false, unavailableGoalId: targetId));
                var writerError = Xunit.Assert.IsType<KeyNotFoundException>(expected);
                var queryError = Xunit.Assert.IsType<KeyNotFoundException>(actual);
                Xunit.Assert.Equal($"Goal '{targetId}' was not found.", writerError.Message);
                Xunit.Assert.Equal(writerError.Message, queryError.Message);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string Execute(
        string[] args, AgentOrchestratorKernel kernel, OrchestratorWorkspace workspace,
        bool skipReadOnlyRoute, string? unavailableGoalId = null)
    {
        var repository = new ProbeStateRepository(kernel);
        if (unavailableGoalId is not null)
            repository.UnavailableGoalIds.Add(unavailableGoalId);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        return CaptureConsole(() => Xunit.Assert.False(CliPersistentStateRunner.ExecuteCommand(
            args, repository, workspace, ref agents, new InMemoryModelProviderRegistry([]),
            ref profiles, ref currentGoal, skipReadOnlyRoute: skipReadOnlyRoute)));
    }
}
