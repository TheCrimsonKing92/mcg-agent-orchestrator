using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: each case owns its SQLite store; console capture is async-local.
public sealed class CliBoardSnapshotReadOnlyRouteTests : CliTaskQueryTestSupport
{
    private const string ParentPrefix = "abc10000";
    private const string LiveChildId = "bbb10000bbbbbbbbbbbbbbbbbbbbbbbb";

    public static TheoryData<string[]> ServedForms => new()
    {
        new[] { "loop-health" },
        new[] { "loop-health", "--last", "5" },
        new[] { "operator-inbox" },
        new[] { "operator-inbox", ParentPrefix },
        new[] { "operator-inbox", ParentPrefix, "--show-acknowledged" },
        new[] { "durations" },
        new[] { "durations", "--by-model", "--since", "2026-10-01" },
        new[] { "dispatch-value" },
        new[] { "dispatch-value", "--since", "2026-10-01" },
        new[] { "brief", ParentPrefix, "2" },
        new[] { "brief", "--goal", ParentPrefix, "2" }
    };

    [Theory]
    [MemberData(nameof(ServedForms))]
    public void ServedForm_RealStoreMatchesWriterOutput(string[] args)
    {
        WithWorkspace(workspace =>
        {
            Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
            var expected = Execute(args, workspace, skipReadOnlyRoute: true);
            var actual = Execute(args, workspace, skipReadOnlyRoute: false);
            Assert.NotEmpty(expected);
            Assert.Equal(expected, actual);
            if (args[0] == "brief")
                Assert.Contains(LiveChildId, actual);
        });
    }

    [Theory]
    [InlineData("loop-health")]
    [InlineData("durations")]
    public void HydratingArchives_ChangesReportOutput(string verb)
    {
        WithWorkspace(workspace =>
        {
            var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
            var ids = repository.ListGoalIdStatusesAsync().GetAwaiter().GetResult()
                .Select(goal => new GoalId(goal.Id)).ToArray();
            var hydrated = repository.LoadGoalsAsync(ids).GetAwaiter().GetResult();
            Assert.Equal(4, hydrated.Goals.Count);
            Assert.All(hydrated.Goals, goal => Assert.False(goal.IsMetadataOnly));
            string[] args = [verb];
            Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
            var expected = Execute(args, workspace, skipReadOnlyRoute: true);
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = null;
            var actual = CaptureConsole(() => Assert.False(CliCommandDispatcher.ExecuteCommand(
                args, hydrated, workspace, ref agents, new InMemoryModelProviderRegistry([]),
                ref profiles, ref currentGoal)));
            Assert.NotEqual(expected, actual);
        });
    }

    public static TheoryData<string[]> ErrorForms => new()
    {
        new[] { "brief", "missing00", "2" },
        new[] { "brief", "--goal", "missing00", "2" },
        new[] { "brief", ParentPrefix, "99" },
        new[] { "durations", "--since", "bogus" }
    };

    [Theory]
    [MemberData(nameof(ErrorForms))]
    public void InvalidValue_MatchesWriterException(string[] args)
    {
        WithWorkspace(workspace =>
        {
            Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
            var expected = Record.Exception(() => Execute(args, workspace, true));
            var actual = Record.Exception(() => Execute(args, workspace, false));
            Assert.NotNull(expected);
            Assert.NotNull(actual);
            Assert.Equal(expected.GetType(), actual.GetType());
            Assert.Equal(expected.Message, actual.Message);
        });
    }

    [Theory]
    [MemberData(nameof(ServedForms))]
    public void ServedForm_ReadsExactlyOneSnapshot(string[] args)
    {
        WithWorkspace(workspace =>
        {
            var kernel = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath)
                .LoadBoardSnapshotAsync().GetAwaiter().GetResult();
            var repository = new ProbeStateRepository(kernel) { ThrowOnOutbox = true };
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = kernel.Goals.Single(goal => goal.Id.Value.StartsWith(ParentPrefix));
            var callerGoal = currentGoal;
            CaptureConsole(() =>
            {
                Assert.True(CliReadOnlyCommandRunner.TryExecute(args, repository, workspace,
                    new InMemoryModelProviderRegistry([]), null, ref agents, ref profiles, ref currentGoal,
                    out var changed));
                Assert.False(changed);
            });
            Assert.Same(callerGoal, currentGoal);
            Assert.Equal(1, repository.BoardSnapshotLoads);
            Assert.Equal(0, repository.LoadGoalsCount);
            Assert.Equal(0, repository.FullLoadAttempts);
        });
    }

    public static TheoryData<string[]> DeclinedForms => new()
    {
        new[] { "brief", "2" },
        new[] { "brief", "2", "extra" },
        new[] { "brief", "--help" },
        new[] { "loop-health", "--last" },
        new[] { "loop-health", "--last", "5", "--last", "6" },
        new[] { "durations", "--bogus" },
        new[] { "operator-inbox", ParentPrefix, "extra" },
        new[] { "operator-inbox-ack", "item-1" }
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
            Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(["loop-health"]));
            Assert.False(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
            Assert.False(CliReadOnlyCommandRunner.TryExecute(args, repository,
                OrchestratorWorkspace.ForDirectory(root), new InMemoryModelProviderRegistry([]), null,
                ref agents, ref profiles, ref currentGoal, out var changed));
            Assert.False(changed);
            Assert.Equal(0, repository.BoardSnapshotLoads);
            Assert.Equal(0, repository.LoadGoalsCount);
            Assert.Equal(0, repository.ListGoalMetadataCount);
            Assert.Equal(0, repository.FullLoadAttempts);
            if (args[0] == "operator-inbox-ack")
                Assert.Equal(CliCommandCapability.Execution, CliCommandCapabilities.Classify(args));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    public static TheoryData<string[]> AdditionalAcceptedForms => new()
    {
        new[] { "LOOP-HEALTH", "--LAST", "bogus" },
        new[] { "durations", "--by-model" },
        new[] { "durations", "--since", "2026-10-01" },
        new[] { "DURATIONS", "--SINCE", "2026-10-01", "--BY-MODEL" },
        new[] { "operator-inbox", "--show-acknowledged" },
        new[] { "BRIEF", "--GOAL", "2", "1" },
        new[] { "brief", "12345678", "1" }
    };

    [Theory]
    [MemberData(nameof(AdditionalAcceptedForms))]
    public void SupportedShape_IsEligibleWithoutSemanticValidation(string[] args) =>
        Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));

    private static void WithWorkspace(Action<OrchestratorWorkspace> action)
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
            var kernel = new AgentOrchestratorKernel(new FixedClock());
            var parent = kernel.CreateGoal(new GoalId(ParentPrefix + new string('a', 24)), "Parent review",
            [
                new TaskSpec(TaskId.New(), "Implement parent", AgentRole.Developer),
                new TaskSpec(TaskId.New(), "Review parent", AgentRole.Reviewer)
            ]);
            kernel.ActivateGoal(parent.Id, AgentCatalog.Default().Agents);
            kernel.CreateGoal(new GoalId(LiveChildId), "Live child",
                [new TaskSpec(TaskId.New(), "Implement live child", AgentRole.Developer)], parent.Id);
            var archivedChild = kernel.CreateGoal(new GoalId("ccc10000cccccccccccccccccccccccc"), "Archived child",
                [new TaskSpec(TaskId.New(), "Completed child", AgentRole.Developer)], parent.Id);
            kernel.RequestHumanInput(parent.Id, null, "Choose the parent behavior");
            var snapshot = kernel.ExportSnapshot();
            var goals = snapshot.Goals.Select(goal => goal.Id == archivedChild.Id.Value ? goal with
            {
                Status = GoalStatus.Completed,
                Tasks = goal.Tasks.Select(task => task with { Status = WorkTaskStatus.Completed }).ToArray()
            } : goal).Append(CompletedSnapshot(
                "ddd10000dddddddddddddddddddddddd")).ToArray();
            var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
            repository.SaveAsync(AgentOrchestratorKernel.FromSnapshot(snapshot with { Goals = goals }, new FixedClock()))
                .GetAwaiter().GetResult();
            Assert.Equal(2, repository.ArchiveTerminalGoalSnapshotsAsync(
                new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.Zero)).GetAwaiter().GetResult());
            _ = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
            action(workspace);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static GoalSnapshot CompletedSnapshot(string id)
    {
        var clock = new FixedClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var task = new TaskSpec(TaskId.New(), "Completed implementation", AgentRole.Developer);
        var goal = kernel.CreateGoal(new GoalId(id), "Archived completed goal", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
            "codex-cli", "fixture-dispatch", "fixture", clock.UtcNow,
            ProviderName: "OpenAI", ModelName: AgentCatalog.OpenAiSubscriptionModelAlias));
        clock.UtcNow = clock.UtcNow.AddMinutes(3);
        kernel.RecordTaskVerification(goal.Id, task.Id,
            new TaskVerificationRecord("fixture-verify", "fixture", 0, "verified", "", clock.UtcNow));
        var snapshot = kernel.ExportGoalSnapshot(goal.Id);
        return snapshot with
        {
            Status = GoalStatus.Completed,
            Tasks = snapshot.Tasks.Select(item => item with { Status = WorkTaskStatus.Completed }).ToArray()
        };
    }

    private static string Execute(string[] args, OrchestratorWorkspace workspace, bool skipReadOnlyRoute)
    {
        var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        return CaptureConsole(() => Assert.False(CliPersistentStateRunner.ExecuteCommand(args, repository,
            workspace, ref agents, new InMemoryModelProviderRegistry([]), ref profiles, ref currentGoal,
            skipReadOnlyRoute: skipReadOnlyRoute)));
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
    }
}
