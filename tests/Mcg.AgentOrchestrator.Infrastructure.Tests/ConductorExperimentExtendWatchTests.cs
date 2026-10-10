using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: fixtures own their stores, log, kernel, clock and AsyncLocal console captures.
public sealed class ConductorExperimentExtendWatchTests(ITestOutputHelper output) : ConductorBatchLoopTests(output)
{
    private static readonly DateTimeOffset AsOf = new(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);
    private const string Prefix = "experiment-reading-due:";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Run_ExtensionResolvesThenRearmsAtNewTarget(bool restart)
    {
        WithWorkspace(workspace =>
        {
            var fixture = new WatchFixture(workspace);
            var record = fixture.Add(Spec());
            fixture.Run(1);
            var first = Assert.Single(fixture.Attention());
            var key = $"{Prefix}{record.Id}:stop-rule";
            Assert.Equal(key, first.CorrelationKey);
            Assert.Equal(CollaborationItemStatus.Raised, first.Status);
            Assert.Single(fixture.Events());

            Assert.True(new ExperimentStore(workspace.ExperimentStorePath)
                .ExtendStopRuleAsync(record.Id, 3, "One more landed goal", AsOf).GetAwaiter().GetResult());
            fixture.Run(1);
            var resolved = Assert.Single(fixture.AllItems());
            Assert.Equal(first.Id, resolved.Id);
            Assert.Equal(CollaborationItemStatus.Resolved, resolved.Status);
            Assert.Equal($"experiment {record.Id} stop rule extended to 3", resolved.Resolution);
            Assert.Empty(fixture.Attention());
            Assert.Single(fixture.Events());

            if (restart) fixture.Restart();
            fixture.Run(1);
            Assert.Empty(fixture.Attention());
            Assert.Single(fixture.AllItems());
            AddLandedGoal(workspace);
            fixture.Run(1);
            var second = Assert.Single(fixture.Attention());
            Assert.Equal(CollaborationItemStatus.Raised, second.Status);
            Assert.Equal(key, second.CorrelationKey);
            Assert.NotEqual(first.Id, second.Id);
            Assert.Equal(2, fixture.AllItems().Count);
            Assert.Equal(2, fixture.Events().Count);
            fixture.Run(1);
            Assert.Equal(second.Id, Assert.Single(fixture.Attention()).Id);
            Assert.Equal(2, fixture.AllItems().Count);
            Assert.Equal(2, fixture.Events().Count);
        });
    }

    [Fact]
    public void Run_OrdinaryAcknowledgementStaysSuppressedAfterRestart()
    {
        WithWorkspace(workspace =>
        {
            var fixture = new WatchFixture(workspace);
            var record = fixture.Add(Spec());
            fixture.Run(1);
            var first = Assert.Single(fixture.Attention());
            Assert.Equal($"{Prefix}{record.Id}:stop-rule", first.CorrelationKey);
            Assert.True(CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory)
                .TryResolveAsync(first.CorrelationKey!, "acknowledged").GetAwaiter().GetResult());
            fixture.Restart();
            fixture.Run(2);
            Assert.Empty(fixture.Attention());
            var resolved = Assert.Single(fixture.AllItems());
            Assert.Equal(first.Id, resolved.Id);
            Assert.Equal(CollaborationItemStatus.Resolved, resolved.Status);
            Assert.Equal("acknowledged", resolved.Resolution);
            Assert.Single(fixture.Events());
        });
    }

    private static void AddLandedGoal(OrchestratorWorkspace workspace)
    {
        var id = new string('e', 32);
        var task = new TaskSnapshot($"{id}-0", "Developer", AgentRole.Developer, WorkTaskStatus.Completed,
            null, null, null, [], null, null, DispatchHistory:
            [new TaskDispatchSnapshot("worker", "command", workspace.RootDirectory, AsOf.AddMinutes(-5))]);
        var goal = new GoalSnapshot(id, id, GoalStatus.Completed, [task],
            [new ProgressEventSnapshot(id, task.Id, ProgressKind.TaskCompleted, "done", AsOf.AddMinutes(-4))]);
        new SqliteOrchestratorStateRepository(workspace.SqliteStatePath).SaveGoalSnapshotsAsync([goal])
            .GetAwaiter().GetResult();
        File.AppendAllLines(Path.Combine(workspace.GoalLifecycleEventsDirectory, "seed.jsonl"),
            [JsonSerializer.Serialize(new { eventType = "GoalLanded", goalId = id, timestamp = AsOf.AddMinutes(-4) })]);
    }
    private static ExperimentSpec Spec() => new("A shorter brief reduces rounds per landing",
        new(ExperimentInterventionKind.BriefOrPromptChange, "Remove repeated preamble"),
        new(ExperimentBaselineKind.BeforeAfterWindow, AsOf.AddDays(-2), AsOf.AddDays(-1)),
        ["rounds-per-landing", "landings-per-hour"], new("productive-rounds", new("productive-rounds", "<", -10)),
        new(2, ExperimentStopUnit.Goals),
        new([new("rounds-per-landing", "<", 0)], [new("rounds-per-landing", ">", 0)]));

    private static void WithWorkspace(Action<OrchestratorWorkspace> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "experiment-watch-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        try { action(OrchestratorWorkspace.ForDirectory(root)); }
        finally { Directory.Delete(root, true); }
    }

    private static string Execute(string[] args, OrchestratorWorkspace workspace)
    {
        var context = new CliExecutionContext(new AgentOrchestratorKernel(), workspace, new InMemoryModelProviderRegistry([]),
            DefaultAgents(), WorkerProfileCatalog.Default(), null);
        return CaptureConsole(() => Assert.Equal(false, CliExperimentCommands.TryExecute(args[0], args, context)));
    }

    private static ExperimentRecord AddExperiment(OrchestratorWorkspace workspace, ExperimentSpec spec)
    {
        var path = Path.Combine(workspace.RootDirectory, "spec.json");
        File.WriteAllText(path, JsonSerializer.Serialize(spec, ExperimentStore.JsonOptions));
        var output = Execute(["experiment-add", "--spec", path], workspace);
        var line = output.TrimEnd().Split(Environment.NewLine)[^1];
        Assert.StartsWith("experiment: ", line);
        var id = line["experiment: ".Length..];
        File.Delete(path);
        return new ExperimentStore(workspace.ExperimentStorePath).ResolveAsync(id).GetAwaiter().GetResult()!;
    }

    private static AgentOrchestratorKernel Seed(OrchestratorWorkspace workspace)
    {
        GoalSnapshot Completed(string id, DateTimeOffset at, GoalStatus status, params AgentRole[] roles)
        {
            var tasks = roles.Select((role, i) => new TaskSnapshot($"{id}-{i}", role.ToString(), role, WorkTaskStatus.Completed,
                null, null, null, [], null, null, DispatchHistory:
                [new TaskDispatchSnapshot("worker", "command", workspace.RootDirectory, at.AddMinutes(i))])).ToArray();
            var events = tasks.Select((task, i) => new ProgressEventSnapshot(id, task.Id, ProgressKind.TaskCompleted,
                "done", at.AddMinutes(i).AddSeconds(30))).ToArray();
            return new(id, id, status, tasks, events);
        }
        var snapshots = new[]
        {
            Completed(new string('a', 32), AsOf.AddDays(-2).AddHours(1), GoalStatus.Completed,
                AgentRole.Planner, AgentRole.Developer, AgentRole.Tester, AgentRole.Reviewer),
            Completed(new string('b', 32), AsOf.AddDays(-2).AddHours(2), GoalStatus.Cancelled, AgentRole.Developer, AgentRole.Tester),
            Completed(new string('c', 32), AsOf.AddDays(-1).AddHours(1), GoalStatus.Completed,
                AgentRole.Developer, AgentRole.Tester, AgentRole.Reviewer),
            Completed(new string('d', 32), AsOf.AddDays(-1).AddHours(2), GoalStatus.Completed,
                AgentRole.Developer, AgentRole.Tester, AgentRole.Reviewer)
        };
        var kernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot(snapshots, []));
        StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
        new SqliteOrchestratorStateRepository(workspace.SqliteStatePath).SaveAsync(kernel).GetAwaiter().GetResult();
        Directory.CreateDirectory(workspace.GoalLifecycleEventsDirectory);
        File.WriteAllLines(Path.Combine(workspace.GoalLifecycleEventsDirectory, "seed.jsonl"),
            snapshots.Where(goal => goal.Status == GoalStatus.Completed).Select(goal => JsonSerializer.Serialize(new
            {
                eventType = "GoalLanded", goalId = goal.Id,
                timestamp = goal.Id[0] == 'a' ? AsOf.AddDays(-2).AddHours(12) : AsOf.AddDays(-1).AddHours(12)
            })));
        return kernel;
    }

    private static ExperimentSpec BreachedSpec(int count) => Spec() with
    {
        StopRule = new(count, ExperimentStopUnit.Goals),
        Guardrail = new("productive-rounds", new("productive-rounds", ">", 20))
    };

    private sealed class WatchFixture
    {
        private readonly OrchestratorWorkspace _workspace;
        private readonly AgentOrchestratorKernel _kernel;
        private readonly Goal _held;
        private readonly ConductorDriver _driver;
        internal ConductEventLogWriter Writer { get; }
        internal ConductorBatchLoop Loop { get; private set; }

        internal WatchFixture(OrchestratorWorkspace workspace)
        {
            _workspace = workspace;
            var seed = Seed(workspace);
            var held = GoalLifecycleCommands.CreateAndActivateSimpleGoal(seed, DefaultAgents(), "Hold while experiments are observed");
            var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
            repository.SaveAsync(seed).GetAwaiter().GetResult();
            // Historical terminal goals stay in state.db; production conduct hydrates only active work.
            Assert.Contains(seed.Goals, goal => goal.Status == GoalStatus.Completed);
            Assert.Contains(seed.Goals, goal => goal.Status == GoalStatus.Cancelled);
            _kernel = CliPersistentStateRunner.LoadConductLoopKernel(repository, executionDirectory: workspace.RootDirectory);
            _held = Assert.Single(_kernel.Goals);
            Assert.Equal(held.Id, _held.Id);
            Assert.Equal(GoalStatus.Active, _held.Status);
            _driver = MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true), getRunningCount: () => 2,
                dispatchAndStart: _ => throw new InvalidOperationException("The worker cap must prevent dispatch."),
                hasGateReadyGoal: () => false, utcNow: () => AsOf);
            Writer = new ConductEventLogWriter(workspace.ConductEventsLogPath, utcNow: () => AsOf);
            Loop = NewLoop();
        }

        internal ExperimentRecord Add(ExperimentSpec spec)
        {
            var record = AddExperiment(_workspace, spec);
            Assert.True(File.Exists(_workspace.ExperimentStorePath));
            return record;
        }

        internal void Restart() => Loop = NewLoop();
        private ConductorBatchLoop NewLoop() => new(conductEventLogWriter: Writer, utcNow: () => AsOf, workspace: _workspace);

        internal void Run(int ticks) => CaptureConsole(() =>
        {
            Assert.Equal(_held.Id, Assert.Single(_kernel.Goals).Id);
            Assert.Equal(GoalStatus.Active, _held.Status);
            var summary = Loop.Run(_kernel, _driver, ConductorAutonomyPolicy.Conservative with { MaxConcurrentPaidWorkers = 2 },
                Path.Combine(_workspace.RootDirectory, "stop"), onlyGoalId: _held.Id.Value, maxIterations: ticks,
                watchInterval: TimeSpan.FromSeconds(1), sleepFunc: _ => false);
            Assert.Equal(ticks, summary.Ticks);
            Assert.Equal(ticks, summary.Held);
        });

        internal List<JsonElement> Events() => !File.Exists(Writer.CurrentPath) ? [] :
            File.ReadAllLines(Writer.CurrentPath).Select(line => JsonSerializer.Deserialize<JsonElement>(line))
                .Where(entry => entry.GetProperty("eventKind").GetString() == "experiment-reading-due").ToList();

        internal IReadOnlyList<CollaborationItem> Attention() => CollaborationItemStore.ForDirectory(_workspace.OrchestratorDirectory)
            .GetAttentionQueueAsync().GetAwaiter().GetResult().Where(item => item.CorrelationKey?.StartsWith(Prefix, StringComparison.Ordinal) == true).ToArray();

        internal IReadOnlyList<CollaborationItem> AllItems() => CollaborationItemStore.ForDirectory(_workspace.OrchestratorDirectory)
            .ListAsync().GetAwaiter().GetResult().Where(item => item.CorrelationKey?.StartsWith(Prefix, StringComparison.Ordinal) == true).ToArray();
    }
}
