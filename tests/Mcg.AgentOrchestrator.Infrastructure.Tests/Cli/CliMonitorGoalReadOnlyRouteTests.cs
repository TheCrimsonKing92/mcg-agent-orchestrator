using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: each case owns its SQLite database; console capture is async-local.
public sealed class CliMonitorGoalReadOnlyRouteTests : CliTaskQueryTestSupport
{
    [Fact]
    public void NdjsonOnce_PreservesFullKernelOutput()
    {
        WithWorkspace(workspace =>
        {
            string[] args = ["monitor-goal", "abc10000", "--once", "--format", "ndjson"];
            Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
            var expected = ExecuteFullKernel(args, workspace);
            var actual = Execute(args, workspace, skipReadOnlyRoute: false);
            Assert.Contains("goal.snapshot", expected);
            Assert.Equal(WithoutTimestamps(expected), WithoutTimestamps(actual));
        });
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("abc")]
    public void InvalidPrefix_PreservesFullKernelException(string prefix)
    {
        WithWorkspace(workspace =>
        {
            string[] args = ["monitor-goal", prefix, "--once", "--format", "ndjson"];
            Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
            var expected = Record.Exception(() => ExecuteFullKernel(args, workspace));
            var actual = Record.Exception(() => Execute(args, workspace, false));
            Assert.NotNull(expected);
            Assert.NotNull(actual);
            Assert.Equal(expected.GetType(), actual.GetType());
            Assert.Equal(expected.Message, actual.Message);
            if (prefix == "missing")
            {
                Assert.IsType<KeyNotFoundException>(actual);
                Assert.Equal("Goal 'missing' was not found.", actual.Message);
            }
            else
            {
                Assert.IsType<InvalidOperationException>(actual);
                Assert.Equal("Goal prefix 'abc' is ambiguous.", actual.Message);
            }
        });
    }

    [Fact]
    public void MissingPrefixSse_PreservesFullKernelErrorOutput()
    {
        WithWorkspace(workspace =>
        {
            string[] args = ["monitor-goal", "missing", "--once"];
            Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
            var expected = ExecuteFullKernel(args, workspace);
            var actual = Execute(args, workspace, false);
            Assert.Contains("monitor.error", expected);
            Assert.Equal(expected, actual);
        });
    }

    public static TheoryData<string[]> DeclinedForms => new()
    {
        new[] { "monitor-goal", "--help" },
        new[] { "goals", "subscribe", "--help" }
    };

    [Theory]
    [MemberData(nameof(DeclinedForms))]
    public void DeclinedForm_DoesNotReadRepository(string[] args)
    {
        var root = CreateTempDirectory();
        try
        {
            var repository = new ProbeStateRepository(new AgentOrchestratorKernel()) { ThrowOnOutbox = true };
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = null;
            Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(["monitor-goal", "abc10000", "--once"]));
            Assert.False(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
            Assert.False(CliReadOnlyCommandRunner.TryExecute(
                args, repository, OrchestratorWorkspace.ForDirectory(root), new InMemoryModelProviderRegistry([]),
                null, ref agents, ref profiles, ref currentGoal, out var changed));
            Assert.False(changed);
            Assert.Equal(0, repository.ListGoalMetadataCount);
            Assert.Equal(0, repository.LoadGoalsCount);
            Assert.Equal(0, repository.LoadGoalCount);
            Assert.Equal(0, repository.FullLoadAttempts);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void WithWorkspace(Action<OrchestratorWorkspace> action)
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
            new SqliteOrchestratorStateRepository(workspace.SqliteStatePath)
                .SaveAsync(CliTimelineReadOnlyRouteTests.CreateTimelineSeed(root)).GetAwaiter().GetResult();
            action(workspace);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string ExecuteFullKernel(string[] args, OrchestratorWorkspace workspace)
    {
        var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
        var kernel = repository.LoadAsync().GetAwaiter().GetResult();
        return CaptureConsole(() => GoalMonitoringSubscriptionCommand.RunAsync(
            args, Console.Out, kernel, workspace, AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(),
            reloadKernel: () => repository.LoadAsync().GetAwaiter().GetResult()).GetAwaiter().GetResult());
    }

    private static string Execute(string[] args, OrchestratorWorkspace workspace, bool skipReadOnlyRoute)
    {
        var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        return CaptureConsole(() => Assert.False(CliPersistentStateRunner.ExecuteCommand(
            args, repository, workspace, ref agents, new InMemoryModelProviderRegistry([]),
            ref profiles, ref currentGoal, skipReadOnlyRoute: skipReadOnlyRoute)));
    }

    private static string[] WithoutTimestamps(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line =>
            {
                var value = JsonNode.Parse(line)!.AsObject();
                value.Remove("timestamp");
                return value.ToJsonString();
            }).ToArray();
}
