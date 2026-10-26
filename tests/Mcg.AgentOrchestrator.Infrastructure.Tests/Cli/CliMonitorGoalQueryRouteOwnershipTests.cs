using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: each case owns its workspace and kernel; console capture is async-local.
public sealed class CliMonitorGoalQueryRouteOwnershipTests : CliTaskQueryTestSupport
{
    public static TheoryData<string[]> MalformedForms => new()
    {
        new[] { "monitor-goal" },
        new[] { "goals", "subscribe" },
        new[] { "monitor-goal", "http://localhost:5087", "abc123" },
        new[] { "monitor-goal", "abc10000", "--bogus" },
        new[] { "monitor-goal", "abc10000", "--since", "x" }
    };

    [Theory]
    [MemberData(nameof(MalformedForms))]
    public void MalformedForms_QueryRouteFailsLikeRemovedHandlerWithoutStateReads(string[] args)
    {
        WithWorkspace(workspace =>
        {
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var profiles = WorkerProfileCatalog.Default();
            var baseline = Record.Exception(() =>
            {
                CliCommandHelp.ThrowIfInvalidFlags(args);
                GoalMonitoringSubscriptionCommand.RunAsync(
                    args, TextWriter.Null, new AgentOrchestratorKernel(), workspace, agents, profiles)
                    .GetAwaiter().GetResult();
            });
            Assert.NotNull(baseline);
            Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
            Assert.Equal(CliCommandCapability.QueryOnly, CliCommandCapabilities.Classify(args));

            var repository = new ProbeStateRepository(new AgentOrchestratorKernel()) { ThrowOnOutbox = true };
            Goal? currentGoal = null;
            var actual = Record.Exception(() => CliReadOnlyCommandRunner.TryExecute(
                args, repository, workspace, new InMemoryModelProviderRegistry([]),
                null, ref agents, ref profiles, ref currentGoal, out _));
            Assert.NotNull(actual);
            Assert.Equal(baseline.GetType(), actual.GetType());
            Assert.Equal(baseline.Message, actual.Message);
            AssertNoStateAccess(repository);

            var startupRepository = new ProbeStateRepository(new AgentOrchestratorKernel()) { ThrowOnOutbox = true };
            var startup = CliReadOnlyStartupHydration.PrepareStartupAsync(args, startupRepository)
                .GetAwaiter().GetResult();
            Assert.False(startup.Hydrated);
            Assert.Equal(0, startupRepository.FullLoadAttempts);
        });
    }

    public static TheoryData<string[]> BlankGoalForms => new()
    {
        new[] { "monitor-goal", "", "--once" },
        new[] { "monitor-goal", " ", "--once" },
        new[] { "goals", "subscribe", " ", "--once" }
    };

    [Theory]
    [MemberData(nameof(BlankGoalForms))]
    public void BlankGoalArgument_FailsWithMissingGoalUsage(string[] args)
    {
        WithWorkspace(workspace =>
        {
            var baseline = Assert.Throws<ArgumentException>(() =>
                GoalMonitoringSubscriptionCommand.Parse(["monitor-goal"]));
            var repository = new ProbeStateRepository(CreateKernel()) { ThrowOnOutbox = true };
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = null;
            Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));

            var output = CaptureConsole(() =>
            {
                var error = Assert.Throws<ArgumentException>(() => CliReadOnlyCommandRunner.TryExecute(
                    args, repository, workspace, new InMemoryModelProviderRegistry([]),
                    null, ref agents, ref profiles, ref currentGoal, out _));
                Assert.Equal(baseline.Message, error.Message);
            });
            Assert.Empty(output);
            AssertNoStateAccess(repository);
        });
    }

    public static TheoryData<string[]> WriterForms => new()
    {
        new[] { "monitor-goal", "abc10000", "--once" },
        new[] { "goals", "subscribe", "abc10000", "--once" }
    };

    [Theory]
    [MemberData(nameof(WriterForms))]
    public void WriterHandlers_RefuseWithoutOutputOrStateChange(string[] args)
    {
        WithWorkspace(workspace =>
        {
            var kernel = CreateKernel();
            var before = JsonSerializer.Serialize(kernel.ExportSnapshot());
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = null;
            var output = CaptureConsole(() =>
            {
                var error = Assert.Throws<InvalidOperationException>(() => CliCommandDispatcher.ExecuteCommand(
                    args, kernel, workspace, ref agents, new InMemoryModelProviderRegistry([]),
                    ref profiles, ref currentGoal));
                Assert.Equal(CliMonitorGoalQueryCommand.WriterPathRefusal, error.Message);
            });
            Assert.Empty(output);
            Assert.Equal(before, JsonSerializer.Serialize(kernel.ExportSnapshot()));
        });
    }

    [Fact]
    public void HelpAndBareGoals_KeepTheirRoutes()
    {
        Assert.False(CliReadOnlyCommandRunner.IsReadOnlyCommand(["monitor-goal", "--help"]));
        Assert.False(CliReadOnlyCommandRunner.IsReadOnlyCommand(["goals", "subscribe", "--help"]));
        Assert.False(CliReadOnlyCommandRunner.IsReadOnlyCommand(["goals", "subscribe", "-h"]));
        Assert.Equal(CliCommandCapability.QueryOnly,
            CliCommandCapabilities.Classify(["monitor-goal", "abc10000", "--once"]));

        WithWorkspace(workspace =>
        {
            var kernel = CreateKernel();
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = null;
            var output = CaptureConsole(() => Assert.False(CliCommandDispatcher.ExecuteCommand(
                ["goals"], kernel, workspace, ref agents, new InMemoryModelProviderRegistry([]),
                ref profiles, ref currentGoal)));
            Assert.Contains(kernel.Goals.Single().Id.Value[..8], output);
        });
    }

    private static void AssertNoStateAccess(ProbeStateRepository repository)
    {
        Assert.Equal(0, repository.ListGoalMetadataCount);
        Assert.Equal(0, repository.LoadGoalsCount);
        Assert.Equal(0, repository.LoadGoalCount);
        Assert.Equal(0, repository.FullLoadAttempts);
        Assert.Equal(0, repository.ListOutboxMessagesCount);
        Assert.Equal(0, repository.MutationAttempts);
        Assert.Equal(0, repository.SaveAttempts);
    }

    private static AgentOrchestratorKernel CreateKernel()
    {
        var kernel = new AgentOrchestratorKernel(
            new FixedClock(new DateTimeOffset(2026, 10, 25, 0, 0, 0, TimeSpan.Zero)));
        kernel.CreateGoal(new GoalId("abc10000aaaaaaaaaaaaaaaaaaaaaaaa"), "Query route ownership");
        return kernel;
    }

    private static void WithWorkspace(Action<OrchestratorWorkspace> action)
    {
        var root = CreateTempDirectory();
        try
        {
            action(OrchestratorWorkspace.ForDirectory(root));
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
}
