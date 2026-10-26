using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each case owns its workspace and repository; console capture is async-local.
public sealed class CliMonitorBareReadOnlyRouteTests : CliTaskQueryTestSupport
{
    [Xunit.Theory]
    [Xunit.InlineData("monitor")]
    [Xunit.InlineData("MONITOR")]
    [Xunit.InlineData("monitor", "abc10000")]
    public async Task MonitorForms_SelectReadOnlyRouteAndQueryOnlyComposition(params string[] args)
    {
        Xunit.Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
        Xunit.Assert.Equal(CliCommandCapability.QueryOnly, CliCommandCapabilities.Classify(args));
        var repository = new ProbeStateRepository(new AgentOrchestratorKernel());
        var startup = await CliReadOnlyStartupHydration.PrepareStartupAsync(args, repository);
        Xunit.Assert.False(startup.Hydrated);
        Xunit.Assert.Equal(0, repository.FullLoadAttempts);
    }

    [Xunit.Theory]
    [Xunit.InlineData(0)]
    [Xunit.InlineData(1)]
    [Xunit.InlineData(2)]
    public void Bare_SelectsCurrentGoalWithoutWritesAndMatchesPrefix(int session)
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            _ = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
            var created = new DateTimeOffset(2026, 9, 24, 0, 0, 0, TimeSpan.Zero);
            var clock = new FixedClock(created);
            var kernel = new AgentOrchestratorKernel(clock);
            var older = kernel.CreateGoal(new GoalId("abc10000aaaaaaaaaaaaaaaaaaaaaaaa"), "Session goal");
            var newer = kernel.CreateGoal(new GoalId("abc20000aaaaaaaaaaaaaaaaaaaaaaaa"), "Newest goal");
            var snapshot = kernel.ExportSnapshot();
            kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
            {
                Goals = snapshot.Goals.Select(goal =>
                {
                    var isOlder = goal.Id == older.Id.Value;
                    var creationTime = created.AddDays(isOlder ? 0 : 1);
                    return goal with
                    {
                        CreatedAt = creationTime,
                        Timeline = [goal.Timeline[0] with { OccurredAt = creationTime },
                            goal.Timeline[0] with
                            {
                                OccurredAt = created.AddDays(isOlder ? 3 : 2), Message = "Later update"
                            }]
                    };
                }).ToArray()
            }, clock);
            var repository = new ProbeStateRepository(kernel) { ThrowOnOutbox = true };
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = session switch
            {
                1 => kernel.GetGoal(older.Id),
                2 => new AgentOrchestratorKernel(clock).CreateGoal(
                    new GoalId("abc30000aaaaaaaaaaaaaaaaaaaaaaaa"), "Missing session goal"),
                _ => null
            };
            var initialSessionGoal = currentGoal;
            var expected = session == 1 ? older.Id : newer.Id;
            var before = JsonSerializer.Serialize(kernel.ExportSnapshot());

            var output = CaptureConsole(() => Xunit.Assert.False(CliPersistentStateRunner.ExecuteCommand(
                ["monitor"], repository, workspace, ref agents, new InMemoryModelProviderRegistry([]),
                ref profiles, ref currentGoal)));

            Xunit.Assert.Equal(expected, currentGoal!.Id);
            Xunit.Assert.Equal(1, repository.LoadGoalsCount);
            Xunit.Assert.Equal(new[] { expected.Value }, repository.LoadedGoalIds);
            AssertBareQueryHasNoWriterEffects(repository);
            Xunit.Assert.Equal(before, JsonSerializer.Serialize(kernel.ExportSnapshot()));
            Xunit.Assert.Contains($"Goal {expected.Value[..8]}", output);

            var bareOutput = ReadOutput(["monitor"], initialSessionGoal);
            var prefixOutput = ReadOutput(["monitor", expected.Value[..8]], null);
            Xunit.Assert.Equal(prefixOutput, bareOutput);
            Xunit.Assert.Contains($"Goal {expected.Value[..8]}", bareOutput);
            Xunit.Assert.Equal(before, JsonSerializer.Serialize(kernel.ExportSnapshot()));

            string ReadOutput(string[] command, Goal? selectedGoal)
            {
                var queryRepository = new ProbeStateRepository(kernel) { ThrowOnOutbox = true };
                var queryOutput = CaptureConsole(() =>
                {
                    Xunit.Assert.True(CliReadOnlyCommandRunner.TryExecute(
                        command, queryRepository, workspace, new InMemoryModelProviderRegistry([]), null,
                        ref agents, ref profiles, ref selectedGoal, out var changed, clock));
                    Xunit.Assert.False(changed);
                });
                Xunit.Assert.Equal(expected, selectedGoal!.Id);
                Xunit.Assert.Equal(1, queryRepository.LoadGoalsCount);
                Xunit.Assert.Equal(new[] { expected.Value }, queryRepository.LoadedGoalIds);
                AssertBareQueryHasNoWriterEffects(queryRepository);
                return queryOutput;
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void Bare_NoGoals_PreservesSelectorErrorWithoutDrain()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var repository = new ProbeStateRepository(new AgentOrchestratorKernel()) { ThrowOnOutbox = true };
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = null;
            var error = Xunit.Assert.Throws<InvalidOperationException>(() =>
                CliPersistentStateRunner.ExecuteCommand(["monitor"], repository, workspace, ref agents,
                    new InMemoryModelProviderRegistry([]), ref profiles, ref currentGoal));

            Xunit.Assert.Equal("Create a goal first with: goal <objective>", error.Message);
            Xunit.Assert.Equal(1, repository.ListGoalMetadataCount);
            Xunit.Assert.Equal(0, repository.LoadGoalsCount);
            AssertBareQueryHasNoWriterEffects(repository);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData("monitor", "--help")]
    [Xunit.InlineData("monitor", "-h")]
    [Xunit.InlineData("monitor", "-x")]
    [Xunit.InlineData("monitor", "abc10000", "extra")]
    [Xunit.InlineData("monitor", "abc10000", "-h")]
    [Xunit.InlineData("monitor", "")]
    [Xunit.InlineData("monitor", " ")]
    public void OtherShapes_KeepWriterPathWithQueryComposition(params string[] args)
    {
        Xunit.Assert.False(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
        Xunit.Assert.Equal(CliCommandCapability.QueryOnly, CliCommandCapabilities.Classify(args));
    }

    [Xunit.Fact]
    public void OtherShape_PersistentRunner_ObservesCliWriteOperationTag()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
            var clock = new FixedClock(new DateTimeOffset(2026, 9, 24, 0, 0, 0, TimeSpan.Zero));
            var kernel = new AgentOrchestratorKernel(clock);
            kernel.CreateGoal(new GoalId("abc10000aaaaaaaaaaaaaaaaaaaaaaaa"), "Monitor writer-path goal");
            var repository = new ProbeStateRepository(kernel);
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = null;
            string[] args = ["monitor", "abc10000", "extra"];
            Xunit.Assert.False(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
            CaptureConsole(() => Xunit.Assert.False(CliPersistentStateRunner.ExecuteCommand(
                args, repository, workspace, ref agents, new InMemoryModelProviderRegistry([]),
                ref profiles, ref currentGoal)));

            Xunit.Assert.True(repository.ListOutboxMessagesCount >= 1);
            Xunit.Assert.Equal(0, repository.MutationAttempts);
            Xunit.Assert.Contains(repository.ObservedWriteOperationTags,
                tag => tag is not null && tag.StartsWith("cli:", StringComparison.Ordinal));
            Xunit.Assert.Null(SqliteOrchestratorStateRepository.AmbientWriteOperationTag);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    private static void AssertBareQueryHasNoWriterEffects(ProbeStateRepository repository)
    {
        Xunit.Assert.Equal(0, repository.FullLoadAttempts);
        Xunit.Assert.Equal(0, repository.ListOutboxMessagesCount);
        Xunit.Assert.Equal(0, repository.OutboxClaimAttempts);
        Xunit.Assert.Equal(0, repository.MutationAttempts);
        Xunit.Assert.Equal(0, repository.SaveAttempts);
        Xunit.Assert.Equal(0, repository.MergeSaveAttempts);
        Xunit.Assert.NotEmpty(repository.ObservedWriteOperationTags);
        Xunit.Assert.All(repository.ObservedWriteOperationTags, tag => Xunit.Assert.Null(tag));
        Xunit.Assert.Null(SqliteOrchestratorStateRepository.AmbientWriteOperationTag);
    }
}
