using System.Reflection;
using System.Runtime.ExceptionServices;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Each case owns its databases. The collection protects CLI console/environment use.
// Interleavings occur at explicit load/save callbacks, never by timing or real workers.
[Xunit.Collection(TestCollections.CliProcessEnvironment)]
public sealed class GraphRepairPersistenceTests : CliCommandTestBase
{
    private readonly List<string> _ownedRoots = [];

    private OrchestratorWorkspace CreateWorkspace()
    {
        var root = CreateTempDirectory();
        _ownedRoots.Add(root);
        return CreateRefinedWorkspace(root);
    }

    public override async ValueTask DisposeAsync()
    {
        try
        {
            foreach (var root in _ownedRoots)
                SharedTestSupport.RemoveTempDirectory(root);
        }
        finally { await base.DisposeAsync(); }
    }

    [Xunit.Theory]
    [Xunit.InlineData("backlog-intake")]
    [Xunit.InlineData("acceptance-queue")]
    public async Task Command_StaleHolder_PreservesCommittedGraph(string command)
    {
        var workspace = CreateWorkspace();
        var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
        var (goal, unrelated) = await Seed(repository);
        var item = await new BacklogStore(workspace.BacklogStorePath).AddAsync("Create isolated backlog slice");
        var proxy = RepositoryProbe.Wrap(repository, out var probe);
        IReadOnlyList<TaskId> inserted = [];
        probe.AfterLoad = () => inserted = CommitRepair(repository, goal, unrelated);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        var args = command == "backlog-intake"
            ? new[] { command, item.Id, "--create-simple-goal", "--backlog-coverage", "slice" }
            : new[] { command, "--apply", "--confirm-acceptance-queue" };

        Assert.True(AcceptanceStableSlotTestSupport.ExecuteWithIsolatedStableSlot(
            args, proxy, workspace, ref agents, new InMemoryModelProviderRegistry([]),
            ref profiles, ref currentGoal));

        Assert.Equal(1, probe.LoadInterleavings);
        var restarted = await new SqliteOrchestratorStateRepository(workspace.SqliteStatePath).LoadAsync();
        AssertRepair(restarted, goal, unrelated);
        Assert.Equal(3, inserted.Count);
        Assert.All(inserted, id => Assert.Contains(restarted.GetGoal(goal.Id).Tasks, task => task.Id == id));
        Assert.Equal(0, probe.RawSaves);
        Assert.Equal(command == "backlog-intake" ? 1 : 0, probe.Creations);
        if (command == "backlog-intake")
        {
            var created = Assert.Single(restarted.Goals.Where(candidate => candidate.SourceBacklogItemId == item.Id));
            Assert.Equal(created.Id.Value, new BacklogIntakeRecordStore(workspace.SqliteStatePath).Get(item.Id)!.GoalId);
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData(1)]
    [Xunit.InlineData(2)]
    public async Task BacklogIntake_CommitFailure_RetainsOnlyCommittedGoals(int failAt)
    {
        var workspace = CreateWorkspace();
        var store = new BacklogStore(workspace.BacklogStorePath);
        var items = new[] { await store.AddAsync("First batch slice"), await store.AddAsync("Second batch slice") };
        var commits = 0;
        var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath,
            statementObserver: null, telemetryOptions: null, beforeOutboxCommit: () =>
            {
                var record = new BacklogIntakeRecordStore(workspace.SqliteStatePath).Get(items[commits].Id);
                Assert.NotNull(record);
                Assert.Null(record.GoalId);
                if (++commits == failAt)
                    throw new InvalidOperationException("injected creation commit failure");
            });
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        var error = Assert.Throws<InvalidOperationException>(() => CliPersistentStateRunner.ExecuteCommand(
            ["backlog-intake", items[0].Id, items[1].Id, "--create-simple-goal", "--backlog-coverage", "slice"],
            repository, workspace, ref agents, new InMemoryModelProviderRegistry([]), ref profiles, ref currentGoal));

        Assert.Equal(failAt, commits);
        Assert.Contains("injected creation commit failure", error.Message);
        var restarted = await repository.LoadAsync();
        Assert.Equal(failAt - 1, restarted.Goals.Count);
        foreach (var item in items)
        {
            var record = new BacklogIntakeRecordStore(workspace.SqliteStatePath).Get(item.Id);
            var durable = restarted.Goals.SingleOrDefault(goal => goal.SourceBacklogItemId == item.Id);
            Assert.Equal(durable?.Id.Value, record?.GoalId);
            if (durable is not null)
            {
                Assert.Equal("GoalCreated", record!.Status);
                Assert.Contains(durable.Id.Value, (string)error.Data["CommittedGoalIds"]!);
                Assert.Equal(durable.Id.Value,
                    new SourceBacklogClaimStore(workspace.SqliteStatePath).ResolveClaim(restarted, item.Id)!.OwnerGoalId);
            }
            else
            {
                if (record is not null) Assert.NotEqual("GoalCreated", record.Status);
                Assert.Null(new SourceBacklogClaimStore(workspace.SqliteStatePath).ResolveClaim(restarted, item.Id));
            }
        }
        Assert.Empty(await repository.ListOutboxMessagesAsync(GoalRefinementWorkCoordinator.OutboxKind));
    }

    [Xunit.Fact]
    public async Task AcceptanceQueue_MultipleCheckpoints_PreservesRepairAndVerdicts()
    {
        var root = CreateShortAcceptanceRepository();
        var workspace = CreateRefinedWorkspace(root);
        var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
        var (repairTarget, unrelated) = await Seed(repository);
        var holder = await repository.LoadAsync();
        var ready = new List<Goal>();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        try
        {
            for (var index = 0; index < 2; index++)
            {
                var task = new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer);
                var goal = holder.CreateGoal($"Ready queue goal {index}", [task]);
                ready.Add(goal);
                holder.ActivateGoal(goal.Id, agents);
                holder.RecordTaskVerification(goal.Id, task.Id,
                    ManualVerificationRecorder.Create(true, "Passed.", root,
                        DateTimeOffset.Parse("2026-10-21T12:00:00Z")));
                CommitGoalWork(root, goal.Id, $"feature-{index}.txt", $"queue work {index}");
            }
            await repository.SaveAsync(holder);
            var proxy = RepositoryProbe.Wrap(repository, out var probe);
            var verifier = new ProbeAcceptanceVerifier(() =>
            {
                // Gate execution occurs after queue load and before its first checkpoint.
                if (probe.MergeSaves == 0)
                    CommitRepair(repository, repairTarget, unrelated);
            });
            Goal? currentGoal = null;

            Assert.True(AcceptanceStableSlotTestSupport.ExecuteWithIsolatedStableSlot(
                ["acceptance-queue", "--apply", "--confirm-acceptance-queue"],
                proxy, workspace, ref agents, new InMemoryModelProviderRegistry([]),
                ref profiles, ref currentGoal, acceptanceVerifier: verifier,
                acceptanceCleanupContext: CreateIsolatedCleanupContext(workspace)));

            Assert.Equal(2, verifier.RunCount);
            Assert.True(probe.MergeSaves >= 2, "Both queue goals must checkpoint through real persistence.");
            var restarted = await repository.LoadAsync();
            AssertRepair(restarted, repairTarget, unrelated);
            Assert.All(ready, goal =>
            {
                Assert.Equal(GoalStatus.Completed, restarted.GetGoal(goal.Id).Status);
                Assert.Contains(GoalOperationJournal.Read(root, goal.Id).Entries,
                    entry => entry.AcceptanceOutcome == "gate-passed" && entry.HasCandidatePair);
            });
        }
        finally
        {
            foreach (var goal in ready)
                _ = GoalWorktrees.Remove(root, goal.Id);
            CleanupAcceptanceRepository(root, null);
        }
    }

    [Xunit.Fact]
    public async Task Checkpoint_RepeatedMerge_RebasesAndCarriesHumanRequests()
    {
        var workspace = CreateWorkspace();
        var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
        var (goal, unrelated) = await Seed(repository);
        var holder = await repository.LoadAsync();
        var baselines = holder.ExportSnapshot().Goals.ToDictionary(item => item.Id);
        var proxy = RepositoryProbe.Wrap(repository, out var probe);
        var persist = CliPersistentStateRunner.CreateCommandCheckpoint(proxy, workspace, holder, baselines);
        await repository.TransactAsync((current, _) =>
        {
            current.RecordTaskNote(goal.Id, goal.Tasks[0].Id, "concurrent-target-progress");
            current.RecordTaskNote(unrelated.Id, unrelated.Tasks[0].Id, "independent-committed-progress");
            return Task.FromResult((true, true));
        });
        holder.RecordTaskNote(goal.Id, goal.Tasks[0].Id, "first-checkpoint");
        persist(holder);
        Assert.Contains(holder.GetGoal(goal.Id).Timeline, item => item.Message == "concurrent-target-progress");
        Assert.Equal(1, probe.MergeSaves);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(await repository.LoadGoalAsync(goal.Id)),
            System.Text.Json.JsonSerializer.Serialize(baselines[goal.Id.Value]));
        var restoredBaseline = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot(
            [baselines[goal.Id.Value]], []));
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(restoredBaseline.ExportGoalSnapshot(goal.Id)),
            System.Text.Json.JsonSerializer.Serialize(holder.ExportGoalSnapshot(goal.Id)));
        persist(holder);
        Assert.Equal(1, probe.MergeSaves); // No write for the restored empty-obligation representation.
        var question = holder.RequestHumanInput(goal.Id, goal.Tasks[0].Id, "Confirm checkpoint evidence");
        holder.RecordTaskNote(goal.Id, goal.Tasks[0].Id, "second-checkpoint");
        persist(holder);
        persist(holder);
        Assert.Equal(2, probe.MergeSaves);
        var restarted = await repository.LoadAsync();
        foreach (var note in new[] { "first-checkpoint", "second-checkpoint", "concurrent-target-progress" })
            Assert.Contains(restarted.GetGoal(goal.Id).Timeline, item => item.Message == note);
        Assert.Contains(restarted.HumanInputRequests, item => item.Id == question.Id);
        Assert.Contains(restarted.GetGoal(unrelated.Id).Timeline, item => item.Message == "independent-committed-progress");
    }

    [Xunit.Fact]
    public async Task Checkpoint_GraphConflict_FailsWithBothStatesAndRebases()
    {
        var workspace = CreateWorkspace();
        var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
        var (goal, unrelated) = await Seed(repository);
        var holder = await repository.LoadAsync();
        var baselines = holder.ExportSnapshot().Goals.ToDictionary(item => item.Id);
        var persist = CliPersistentStateRunner.CreateCommandCheckpoint(repository, workspace, holder, baselines);
        CommitRepair(repository, goal, unrelated);
        holder.RecordTaskNote(goal.Id, goal.Tasks[0].Id, "rejected-stale-note");

        var error = Assert.Throws<InvalidOperationException>(() => persist(holder));

        Assert.Contains("COMMAND_CHECKPOINT_REJECTED", error.Message);
        Assert.Contains("rejected-stale-note", (string)error.Data["SubmittedGoalState"]!);
        Assert.Contains("Establish evidence", (string)error.Data["StoredGoalState"]!);
        Assert.Equal(ExpectedRoles, holder.GetGoal(goal.Id).Tasks.Select(task => task.RequiredRole));
        AssertRepair(await repository.LoadAsync(), goal, unrelated);
    }

    [Xunit.Fact]
    public async Task BacklogIntake_ExistingGoal_PreservesLegacyNoOp()
    {
        var workspace = CreateWorkspace();
        var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
        var item = await new BacklogStore(workspace.BacklogStorePath).AddAsync("Legacy existing goal slice");
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        string[] args = ["backlog-intake", item.Id, "--create-simple-goal", "--backlog-coverage", "slice"];
        Assert.True(CliPersistentStateRunner.ExecuteCommand(args, repository, workspace, ref agents,
            new InMemoryModelProviderRegistry([]), ref profiles, ref currentGoal));
        var createdId = Assert.Single((await repository.LoadAsync()).Goals).Id;

        Assert.False(CliPersistentStateRunner.ExecuteCommand(args, repository, workspace, ref agents,
            new InMemoryModelProviderRegistry([]), ref profiles, ref currentGoal));

        Assert.Equal(createdId, Assert.Single((await repository.LoadAsync()).Goals).Id);
        Assert.Equal(createdId, currentGoal!.Id);
    }

    [Xunit.Fact]
    public async Task Checkpoint_NewGoal_CommitsItsHumanRequests()
    {
        var workspace = CreateWorkspace();
        var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
        var holder = await repository.LoadAsync();
        var persist = CliPersistentStateRunner.CreateCommandCheckpoint(repository, workspace, holder,
            new Dictionary<string, GoalSnapshot>());
        var goal = holder.CreateGoal("New goal with a question", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
        var request = holder.RequestHumanInput(goal.Id, goal.Tasks[0].Id, "Confirm new goal");
        persist(holder);
        var restarted = await repository.LoadAsync();
        Assert.Single(restarted.Goals);
        Assert.Contains(restarted.HumanInputRequests, item => item.Id == request.Id);
    }

    private static async Task<(Goal Goal, Goal Unrelated)> Seed(SqliteOrchestratorStateRepository repository)
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Repair target", [
            new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer),
            new TaskSpec(TaskId.New(), "Review", AgentRole.Reviewer)]);
        var unrelated = kernel.CreateGoal("Independent progress", [new TaskSpec(TaskId.New(), "Independent implementation", AgentRole.Developer)]);
        await repository.SaveAsync(kernel);
        return (goal, unrelated);
    }

    private static IReadOnlyList<TaskId> CommitRepair(SqliteOrchestratorStateRepository repository, Goal goal, Goal unrelated)
    {
        var inserted = repository.TransactAsync((current, _) =>
        {
            var researcher = current.AddTask(goal.Id, AgentRole.Researcher, "Establish evidence", beforeRole: AgentRole.Developer);
            var planner = current.AddTask(goal.Id, AgentRole.Planner, "Plan change", beforeRole: AgentRole.Developer);
            var tester = current.AddTask(goal.Id, AgentRole.Tester, "Verify change", beforeRole: AgentRole.Reviewer);
            current.RecordTaskNote(unrelated.Id, unrelated.Tasks[0].Id, "independent-committed-progress");
            return Task.FromResult((true, new[] { researcher.Id, planner.Id, tester.Id }));
        }).GetAwaiter().GetResult();
        AssertRepair(repository.LoadAsync().GetAwaiter().GetResult(), goal, unrelated);
        return inserted;
    }

    private static void AssertRepair(AgentOrchestratorKernel kernel, Goal goal, Goal unrelated)
    {
        Assert.True(ExpectedRoles.SequenceEqual(kernel.GetGoal(goal.Id).Tasks.Select(task => task.RequiredRole)),
            "Committed five-role graph must survive the stale-holder checkpoint and reload.");
        Assert.Contains(kernel.GetGoal(unrelated.Id).Timeline, item => item.Message == "independent-committed-progress");
    }

    private static readonly AgentRole[] ExpectedRoles = [
        AgentRole.Researcher, AgentRole.Planner, AgentRole.Developer, AgentRole.Tester, AgentRole.Reviewer];

    // Forward every call to real SQLite; pause the explicitly armed first load after it completes.
    public class RepositoryProbe : DispatchProxy
    {
        private IOrchestratorStateOutboxRepository _repository = null!;
        public Action? AfterLoad { get; set; }
        public int LoadInterleavings { get; private set; }
        public int RawSaves { get; private set; }
        public int Creations { get; private set; }
        public int MergeSaves { get; private set; }

        public static IOrchestratorStateOutboxRepository Wrap(IOrchestratorStateOutboxRepository repository, out RepositoryProbe probe)
        {
            var wrapped = Create<IOrchestratorStateOutboxRepository, RepositoryProbe>();
            probe = (RepositoryProbe)wrapped;
            probe._repository = repository;
            return wrapped;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var method = targetMethod!;
            if (method.Name == nameof(IOrchestratorStateRepository.SaveAsync)) RawSaves++;
            if (method.Name == nameof(IOrchestratorStateOutboxRepository.TransactGoalCreationWithOutboxAsync)) Creations++;
            if (method.Name == nameof(ITransactionalOrchestratorStateRepository.SaveGoalSnapshotsWithMergeAsync)) MergeSaves++;
            object? result;
            try { result = method.Invoke(_repository, args); }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
            if (method.Name == nameof(IOrchestratorStateRepository.LoadAsync) && AfterLoad is { } interleave)
            {
                AfterLoad = null;
                return Interleave((Task<AgentOrchestratorKernel>)result!, interleave);
            }
            return result;
        }

        private async Task<AgentOrchestratorKernel> Interleave(Task<AgentOrchestratorKernel> load, Action interleave)
        {
            var holder = await load;
            LoadInterleavings++;
            interleave();
            return holder;
        }
    }
}
