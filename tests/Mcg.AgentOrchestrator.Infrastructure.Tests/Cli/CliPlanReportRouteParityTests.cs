using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: temporary roots, repository probes and console captures are local to each case.
public sealed class CliPlanReportRouteParityTests : CliTaskQueryTestSupport
{
    [Xunit.Theory]
    [Xunit.InlineData("retention-plan", "abc10000", "Retention plan goal: abc10000")]
    [Xunit.InlineData("supervisor", "abc10000", "Supervisor goal: abc10000")]
    [Xunit.InlineData("SUPERVISOR", "ABC10000", "Supervisor goal: abc10000")]
    public void ExplicitForms_PreserveWriterOutput(string verb, string prefix, string header)
    {
        var root = CreateTempDirectory();
        try
        {
            var kernel = CliSingleGoalReportReadOnlyRouteTests.CreateReportSeed(root, budgetHold: true);
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            string[] args = [verb, prefix];
            Xunit.Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
            var expected = Execute(args, kernel, workspace, skipReadOnlyRoute: true);
            var actual = Execute(args, kernel, workspace, skipReadOnlyRoute: false);
            Xunit.Assert.Contains(header, expected);
            Xunit.Assert.Equal(expected, actual);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData("retention-plan", "missing")]
    [Xunit.InlineData("supervisor", "missing")]
    [Xunit.InlineData("retention-plan", "abc")]
    [Xunit.InlineData("supervisor", "abc")]
    [Xunit.InlineData("retention-plan", "help")]
    [Xunit.InlineData("supervisor", "help")]
    public void UnresolvedPrefixes_PreserveWriterErrors(string verb, string prefix)
    {
        var root = CreateTempDirectory();
        try
        {
            var kernel = CliSingleGoalReportReadOnlyRouteTests.CreateReportSeed(root, budgetHold: true);
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            string[] args = [verb, prefix];
            Xunit.Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
            var expected = Xunit.Record.Exception(() => Execute(args, kernel, workspace, true));
            var actual = Xunit.Record.Exception(() => Execute(args, kernel, workspace, false));
            Xunit.Assert.NotNull(expected);
            Xunit.Assert.NotNull(actual);
            Xunit.Assert.Equal(expected.GetType(), actual.GetType());
            Xunit.Assert.Equal(expected.Message, actual.Message);
            if (prefix == "abc")
            {
                Xunit.Assert.IsType<InvalidOperationException>(actual);
                Xunit.Assert.Equal("Goal prefix 'abc' is ambiguous.", actual.Message);
            }
            else
            {
                Xunit.Assert.IsType<KeyNotFoundException>(actual);
                Xunit.Assert.Equal($"Goal '{prefix}' was not found.", actual.Message);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string Execute(
        string[] args, AgentOrchestratorKernel kernel, OrchestratorWorkspace workspace, bool skipReadOnlyRoute)
    {
        var repository = new ProbeStateRepository(kernel);
        IReadOnlyList<AgentDefinition> agents = CliSingleGoalReportReadOnlyRouteTests.ReportAgents();
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        return CaptureConsole(() => Xunit.Assert.False(CliPersistentStateRunner.ExecuteCommand(
            args, repository, workspace, ref agents, new InMemoryModelProviderRegistry([]),
            ref profiles, ref currentGoal, skipReadOnlyRoute: skipReadOnlyRoute)));
    }
}
