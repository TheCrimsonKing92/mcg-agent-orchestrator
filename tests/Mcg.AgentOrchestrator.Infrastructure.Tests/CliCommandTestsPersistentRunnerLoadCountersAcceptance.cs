using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.CliProcessEnvironment)]
public sealed class CliCommandTestsPersistentRunnerLoadCountersAcceptance : CliCommandTestBase
{
    [Xunit.Fact(DisplayName = "Real tick reload hydrates three open goals while keeping two hundred terminal goals metadata only")]
    public void RealReloadCountsOpenAndTerminalPopulation()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = CreateRefinedWorkspace(root);
            var (repository, openIds) = Seed(workspace);
            var kernel = CliPersistentStateRunner.LoadConductLoopKernel(repository, executionDirectory: root);
            Assert.Equal(3, kernel.Goals.Count);
            AgentOrchestratorKernel? reloaded = null;
            var loop = new ConductorBatchLoop(measuredSweep: _ =>
            {
                reloaded = ConductorTickStepLedger.Measure("kernel-reload", () =>
                    CliPersistentStateRunner.LoadConductLoopKernel(repository, executionDirectory: root));
                return null;
            }, processCpuTime: () => TimeSpan.Zero);

            var tick = ConductorBatchLoopTestsStepLedger.OneTick(loop, kernel, ConductorBatchLoopTestsStepLedger.FixtureDriver());

            AssertPopulation(ConductorBatchLoopTestsStepLedger.Phase(tick, "sweep"));
            Assert.NotNull(reloaded);
            Assert.Equal(openIds.Order(StringComparer.Ordinal), reloaded.Goals.Select(goal => goal.Id.Value).Order(StringComparer.Ordinal));
            Assert.Equal(203, reloaded.KnownDependencyGoalStatuses.Count);
            Assert.Equal("Completed", Assert.Single(reloaded.KnownDependencyGoalStatuses.Values.Distinct(), status => status == "Completed"));
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    [Xunit.Fact(DisplayName = "Production conduct reload and sweep call sites emit current tick load counters")]
    public void ProductionConductEmitsLoadCounters()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = CreateRefinedWorkspace(root);
            var (repository, _) = Seed(workspace);
            var kernel = CliPersistentStateRunner.LoadConductLoopKernel(repository, executionDirectory: root);
            var reloadCalls = 0;
            var context = new CliExecutionContext(kernel, workspace, new InMemoryModelProviderRegistry([]),
                AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(), currentGoal: null,
                reloadKernelForGoals: ids =>
                {
                    reloadCalls++;
                    return CliPersistentStateRunner.LoadConductLoopKernel(repository, ids, root);
                }) { CleanupContext = CreateIsolatedCleanupContext(workspace) };

            // Manual policy keeps all dispatch/model boundaries closed in this store fixture.
            var text = CaptureConsole(() => CliCommandHandlers.Execute(
                ["conduct", "--loop", "--policy", "Manual", "--max-iterations", "1"], context));

            var sweep = Assert.Single(text.Split('\n'), line => line.StartsWith("PHASE_TIMING tick=1 phase=sweep ", StringComparison.Ordinal));
            AssertPopulation(sweep);
            Assert.Equal(1, reloadCalls);
            Assert.Matches(@"kernel-reload:\d+:1(?:,| )", sweep);
            Assert.Matches(@"refresh-dispatches:\d+:3(?:,| )", sweep);
            Assert.Matches(@"terminal-sweep-run:\d+:[12](?:,| )", sweep);
            Assert.Matches(@"owned-root-reap:\d+:[12](?:,| )", sweep);
            Assert.Matches(@" cpu_ms=\d+ ts=\S+\s*$", sweep);
            Assert.Equal(3, context.Kernel.Goals.Count);
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    private static void AssertPopulation(string line) => Assert.Contains(
        "load_counters=goals_hydrated=3,metadata_rows=203,terminal_journal_stats=200,", line);

    private static (SqliteOrchestratorStateRepository Repository, string[] OpenIds) Seed(OrchestratorWorkspace workspace)
    {
        var kernel = new AgentOrchestratorKernel();
        var terminalIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < 200; index++)
        {
            var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, AgentCatalog.Default().Agents, $"Terminal {index}");
            terminalIds.Add(goal.Id.Value);
            GoalOperationJournal.Completed(workspace.ExecutionDirectory, goal, "test:terminal", "Terminal load fixture.");
        }
        var openIds = Enumerable.Range(0, 3).Select(index => GoalLifecycleCommands.CreateAndActivateSimpleGoal(
            kernel, AgentCatalog.Default().Agents, $"Open {index}").Id.Value).ToArray();
        var snapshot = kernel.ExportSnapshot();
        kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            Goals = snapshot.Goals.Select(goal => terminalIds.Contains(goal.Id) ? goal with { Status = GoalStatus.Completed } : goal).ToArray()
        });
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        repository.SaveAsync(kernel).GetAwaiter().GetResult();
        Assert.Equal(203, repository.ListConductLoopGoalMetadataAsync().GetAwaiter().GetResult().Count);
        return (repository, openIds);
    }
}
