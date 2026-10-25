using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each case owns its workspace and repositories; console capture is async-local.
public sealed class CliProcessLogsReadOnlyRouteTests : CliTaskQueryTestSupport
{
    internal const string FirstGoalId = "abc10000aaaaaaaaaaaaaaaaaaaaaaaa";
    private const string SecondGoalId = "abc20000bbbbbbbbbbbbbbbbbbbbbbbb";

    [Theory]
    [InlineData("logs", "abc10000", "1")]
    [InlineData("logs", "--goal", "abc10000", "1")]
    [InlineData("logs", "1", "--goal", "abc10000")]
    [InlineData("logs", "abc10000", "1", "stdout")]
    [InlineData("LOGS", "ABC10000", "1", "out")]
    [InlineData("logs", "--GOAL", "abc10000", "1", "stderr")]
    [InlineData("logs", "1", "--goal", "abc10000", "exit")]
    public async Task ExplicitGoal_PreservesWriterOutputAndLoadsOnlyMatchingGoal(params string[] args)
    {
        var root = CreateTempDirectory();
        try
        {
            var kernel = CreateSeed(root);
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
            var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
            await repository.SaveAsync(kernel);
            var probe = new ProbeStateRepository(kernel) { ThrowOnOutbox = true };

            Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
            var expected = Execute(args, repository, workspace, skipReadOnlyRoute: true);
            var actual = Execute(args, probe, workspace, skipReadOnlyRoute: false);
            Assert.Equal(expected, actual);
            if (args[^1] is not ("stderr" or "exit"))
                Assert.Contains("hello stdout", actual);
            Assert.Equal(new[] { FirstGoalId }, probe.LoadedGoalIds);
            Assert.Equal(1, probe.ListGoalMetadataCount);
            Assert.Equal(1, probe.LoadGoalsCount);
            Assert.Equal(0, probe.FullLoadAttempts);
            Assert.Equal(0, probe.ListOutboxMessagesCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("missing0", "1", 0)]
    [InlineData("abc", "1", 2)]
    [InlineData("abc10000", "9", 1)]
    [InlineData("abc20000", "1", 1)]
    public async Task InvalidTarget_PreservesWriterException(string prefix, string task, int loads)
    {
        var root = CreateTempDirectory();
        try
        {
            var kernel = CreateSeed(root);
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
            var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
            await repository.SaveAsync(kernel);
            var probe = new ProbeStateRepository(kernel) { ThrowOnOutbox = true };
            string[] args = ["logs", prefix, task];

            Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
            var expected = Record.Exception(() => Execute(args, repository, workspace, true));
            var actual = Record.Exception(() => Execute(args, probe, workspace, false));
            Assert.NotNull(expected);
            Assert.NotNull(actual);
            Assert.Equal(expected.GetType(), actual.GetType());
            Assert.Equal(expected.Message, actual.Message);
            if (prefix == "abc20000")
            {
                Assert.IsType<InvalidOperationException>(actual);
                Assert.Equal("Task has no background process logs.", actual.Message);
            }
            Assert.Equal(loads, probe.LoadGoalsCount);
            Assert.Equal(0, probe.FullLoadAttempts);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("logs")]
    [InlineData("logs", "1")]
    [InlineData("logs", "1", "stdout")]
    [InlineData("logs", "--help")]
    [InlineData("logs", "abc10000", "1", "extra")]
    [InlineData("logs", "abc10000", "1", "stdout", "extra")]
    [InlineData("logs", "abc10000", "1", "STDOUT")]
    [InlineData("logs", "abc10000", "-h")]
    [InlineData("logs", "--goal", "--bad", "1")]
    [InlineData("logs", "1", "--goal", "--bad")]
    [InlineData("logs", "abc10000", "--bad")]
    [InlineData("logs", " ", "1")]
    public void UnsupportedShape_DeclinesWithoutReadingState(params string[] args)
    {
        Assert.False(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
        var root = CreateTempDirectory();
        try
        {
            var probe = new ProbeStateRepository(new AgentOrchestratorKernel()) { ThrowOnOutbox = true };
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = null;
            CaptureConsole(() => Assert.False(CliReadOnlyCommandRunner.TryExecute(
                args, probe, OrchestratorWorkspace.ForDirectory(root), new InMemoryModelProviderRegistry([]),
                null, ref agents, ref profiles, ref currentGoal, out _)));
            Assert.Equal(0, probe.ListGoalMetadataCount);
            Assert.Equal(0, probe.LoadGoalsCount);
            Assert.Equal(0, probe.FullLoadAttempts);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    internal static AgentOrchestratorKernel CreateSeed(string root)
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(new GoalId(FirstGoalId), "Logs goal",
            [new TaskSpec(new TaskId("abc10000dddddddddddddddddddddddd"), "Log task", AgentRole.Developer)]);
        kernel.CreateGoal(new GoalId(SecondGoalId), "Other goal",
            [new TaskSpec(new TaskId("abc20000dddddddddddddddddddddddd"), "No process", AgentRole.Developer)]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var process = new TaskProcessRecord(777, "worker", root,
            Path.Combine(root, "worker.out.log"), Path.Combine(root, "worker.err.log"),
            Path.Combine(root, "worker.exit.txt"), DateTimeOffset.Parse("2026-10-07T00:00:00Z"),
            DateTimeOffset.Parse("2026-10-07T00:01:00Z"), 0);
        File.WriteAllText(process.StandardOutputPath, "hello stdout");
        File.WriteAllText(process.StandardErrorPath, "hello stderr");
        File.WriteAllText(process.ExitCodePath, "0");
        kernel.RecordTaskDispatch(goal.Id, goal.Tasks.Single().Id,
            new TaskDispatchRecord("codex-cli", process.Command, root, process.StartedAt));
        kernel.RecordTaskProcessStarted(goal.Id, goal.Tasks.Single().Id, process);
        return kernel;
    }

    private static string Execute(string[] args, ITransactionalOrchestratorStateRepository repository,
        OrchestratorWorkspace workspace, bool skipReadOnlyRoute)
    {
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        return CaptureConsole(() => Assert.False(CliPersistentStateRunner.ExecuteCommand(
            args, repository, workspace, ref agents, new InMemoryModelProviderRegistry([]),
            ref profiles, ref currentGoal, skipReadOnlyRoute: skipReadOnlyRoute)));
    }
}
