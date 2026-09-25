using System.Text;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(CliTestCollections.ConsoleSerialized)]
public sealed class CliAttentionNextGoldenOutputTests : CliTaskQueryTestSupport
{
    [Xunit.Theory]
    [Xunit.InlineData("attention")]
    [Xunit.InlineData("attention", "list")]
    [Xunit.InlineData("attention", "show")]
    [Xunit.InlineData("attention", "show", "--all")]
    [Xunit.InlineData("attention", "show", "abc10000")]
    public void AttentionOutputMatchesNormalHandlerBytes(params string[] args)
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            _ = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal(new GoalId("abc10000aaaaaaaaaaaaaaaaaaaaaaaa"), "Golden attention fixture");
            var providers = new InMemoryModelProviderRegistry([]);
            IReadOnlyList<AgentDefinition> legacyAgents = AgentCatalog.Default().Agents;
            var legacyProfiles = WorkerProfileCatalog.Default();
            Goal? legacyGoal = goal;
            var legacy = CaptureConsole(() => Xunit.Assert.False(CliCommandDispatcher.ExecuteCommand(
                args, kernel, workspace, ref legacyAgents, providers, ref legacyProfiles, ref legacyGoal)));
            var fixture = args.Length == 3 && args[2] == "--all"
                ? CliAttentionNextGoldenFixtures.EmptyAttentionHistory
                : args.Length == 3
                    ? CliAttentionNextGoldenFixtures.EmptyGoalAttention(goal.Id.Value)
                    : CliAttentionNextGoldenFixtures.EmptyAttention;
            Xunit.Assert.Equal(Encoding.UTF8.GetBytes(fixture), Encoding.UTF8.GetBytes(legacy));

            var repository = new ProbeStateRepository(kernel) { ThrowOnOutbox = true };
            IReadOnlyList<AgentDefinition> readAgents = AgentCatalog.Default().Agents;
            var readProfiles = WorkerProfileCatalog.Default();
            Goal? readGoal = null;
            var read = CaptureConsole(() =>
            {
                Xunit.Assert.True(CliReadOnlyCommandRunner.TryExecute(args, repository, workspace,
                    providers, null, ref readAgents, ref readProfiles, ref readGoal, out var changed));
                Xunit.Assert.False(changed);
            });
            Xunit.Assert.Equal(Encoding.UTF8.GetBytes(fixture), Encoding.UTF8.GetBytes(read));
            Xunit.Assert.Equal(0, repository.FullLoadAttempts);
            Xunit.Assert.Equal(0, repository.ListOutboxMessagesCount);
            Xunit.Assert.Equal(0, repository.OutboxClaimAttempts);
            Xunit.Assert.Equal(0, repository.MutationAttempts);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void NextFullOutputMatchesNormalHandlerBytes()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            _ = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
            var clock = new FixedClock();
            var kernel = new AgentOrchestratorKernel(clock);
            var goal = kernel.CreateGoal(new GoalId("abc10000aaaaaaaaaaaaaaaaaaaaaaaa"), "Golden next fixture");
            var args = new[] { "next", "--full", goal.Id.Value[..8] };
            var providers = new InMemoryModelProviderRegistry([]);
            IReadOnlyList<AgentDefinition> legacyAgents = AgentCatalog.Default().Agents;
            var legacyProfiles = WorkerProfileCatalog.Default();
            Goal? legacyGoal = goal;
            var legacy = CaptureConsole(() => Xunit.Assert.False(CliCommandDispatcher.ExecuteCommand(
                args, kernel, workspace, ref legacyAgents, providers, ref legacyProfiles, ref legacyGoal,
                diagnosticsClock: clock)));
            Xunit.Assert.Equal(Encoding.UTF8.GetBytes(CliAttentionNextGoldenFixtures.NextFull),
                Encoding.UTF8.GetBytes(legacy));

            var repository = new ProbeStateRepository(kernel) { ThrowOnOutbox = true };
            IReadOnlyList<AgentDefinition> readAgents = AgentCatalog.Default().Agents;
            var readProfiles = WorkerProfileCatalog.Default();
            Goal? readGoal = null;
            var read = CaptureConsole(() =>
            {
                Xunit.Assert.True(CliReadOnlyCommandRunner.TryExecute(args, repository, workspace,
                    providers, null, ref readAgents, ref readProfiles, ref readGoal, out var changed, clock));
                Xunit.Assert.False(changed);
            });
            Xunit.Assert.Equal(Encoding.UTF8.GetBytes(CliAttentionNextGoldenFixtures.NextFull),
                Encoding.UTF8.GetBytes(read));
            Xunit.Assert.Equal(0, repository.FullLoadAttempts);
            Xunit.Assert.Equal(0, repository.ListOutboxMessagesCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData("attention", "show", "missing")]
    [Xunit.InlineData("next", "--full", "missing")]
    public async Task UnknownGoalErrorMatchesCommittedCliBytes(params string[] args)
    {
        var fixture = args[0] == "attention"
            ? CliAttentionNextGoldenFixtures.UnknownAttentionGoal
            : CliAttentionNextGoldenFixtures.UnknownNextFullGoal;
        await AssertWriterAndReadRouteErrorAsync(args, fixture, ambiguous: false);
    }

    [Xunit.Fact]
    public async Task AmbiguousNextFullGoalErrorMatchesCommittedCliBytes()
    {
        await AssertWriterAndReadRouteErrorAsync(["next", "--full", "abc10000"],
            CliAttentionNextGoldenFixtures.AmbiguousNextFullGoal, ambiguous: true);
    }

    private static async Task AssertWriterAndReadRouteErrorAsync(
        string[] args, CliAttentionNextGoldenFixtures.ErrorResult fixture, bool ambiguous)
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
            var kernel = new AgentOrchestratorKernel();
            kernel.CreateGoal(new GoalId("abc10000aaaaaaaaaaaaaaaaaaaaaaaa"), "First goal");
            if (ambiguous)
                kernel.CreateGoal(new GoalId("abc10000bbbbbbbbbbbbbbbbbbbbbbbb"), "Second goal");
            await new SqliteOrchestratorStateRepository(workspace.SqliteStatePath).SaveAsync(kernel);

            // Without collaboration-items.db the read-only command declines, so this uses the writer path.
            var writer = await CliAttentionNextReadOnlyWriterHeldTests.RunQueryAsync(root, args);
            AssertErrorFixture(fixture, writer);

            _ = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
            var read = await CliAttentionNextReadOnlyWriterHeldTests.RunQueryAsync(root, args);
            AssertErrorFixture(fixture, read);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void AssertErrorFixture(CliAttentionNextGoldenFixtures.ErrorResult fixture,
        (int ExitCode, string Output, string Error) actual)
    {
        Xunit.Assert.Equal(fixture.ExitCode, actual.ExitCode);
        Xunit.Assert.Equal(Encoding.UTF8.GetBytes(fixture.Stdout), Encoding.UTF8.GetBytes(actual.Output));
        Xunit.Assert.Equal(Encoding.UTF8.GetBytes(fixture.Stderr), Encoding.UTF8.GetBytes(actual.Error));
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 9, 24, 0, 0, 0, TimeSpan.Zero);
    }
}
