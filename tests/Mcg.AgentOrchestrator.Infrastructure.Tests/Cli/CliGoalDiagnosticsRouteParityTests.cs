using System.Text;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(CliTestCollections.ConsoleSerialized)]
public sealed class CliGoalDiagnosticsRouteParityTests : CliTaskQueryTestSupport
{
    [Xunit.Fact]
    public void FixedClock_OutputMatchesWriterAndNextFullBytes()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            _ = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
            var clock = new FixedClock();
            var kernel = new AgentOrchestratorKernel(clock);
            var goal = kernel.CreateGoal(new GoalId("abc10000aaaaaaaaaaaaaaaaaaaaaaaa"), "Golden next fixture");
            var args = new[] { "goal-diagnostics", "abc10000" };
            var providers = new InMemoryModelProviderRegistry([]);
            IReadOnlyList<AgentDefinition> writerAgents = AgentCatalog.Default().Agents;
            var writerProfiles = WorkerProfileCatalog.Default();
            Goal? writerGoal = goal;
            var writer = CaptureConsole(() => Xunit.Assert.False(CliCommandDispatcher.ExecuteCommand(
                args, kernel, workspace, ref writerAgents, providers, ref writerProfiles, ref writerGoal,
                diagnosticsClock: clock)));
            var read = ReadOutput(args);
            var next = ReadOutput(["next", "--full", "abc10000"]);

            Xunit.Assert.Equal(Encoding.UTF8.GetBytes(writer), Encoding.UTF8.GetBytes(read));
            Xunit.Assert.Equal(Encoding.UTF8.GetBytes(next), Encoding.UTF8.GetBytes(read));
            Xunit.Assert.Equal(Encoding.UTF8.GetBytes(CliAttentionNextGoldenFixtures.NextFull),
                Encoding.UTF8.GetBytes(read));

            string ReadOutput(string[] command)
            {
                var repository = new ProbeStateRepository(kernel) { ThrowOnOutbox = true };
                IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
                var profiles = WorkerProfileCatalog.Default();
                Goal? currentGoal = null;
                var output = CaptureConsole(() =>
                {
                    Xunit.Assert.True(CliReadOnlyCommandRunner.TryExecute(command, repository, workspace,
                        providers, null, ref agents, ref profiles, ref currentGoal, out var changed, clock));
                    Xunit.Assert.False(changed);
                });
                Xunit.Assert.Equal(goal.Id, currentGoal!.Id);
                Xunit.Assert.Equal(0, repository.FullLoadAttempts);
                Xunit.Assert.Equal(0, repository.ListOutboxMessagesCount);
                return output;
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData("missing", false)]
    [Xunit.InlineData("abc10000", true)]
    public async Task UnresolvedPrefix_CliErrorsMatchAcrossRoutes(string prefix, bool ambiguous)
    {
        // Real-process coverage uses only this test's private database and workspace.
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

            var itemsDb = Path.Combine(workspace.OrchestratorDirectory, "collaboration-items.db");
            Xunit.Assert.False(File.Exists(itemsDb));
            var args = new[] { "goal-diagnostics", prefix };
            var writer = await CliAttentionNextReadOnlyWriterHeldTests.RunQueryAsync(root, args);
            _ = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
            Xunit.Assert.True(File.Exists(itemsDb));
            var read = await CliAttentionNextReadOnlyWriterHeldTests.RunQueryAsync(root, args);

            var fixture = ambiguous
                ? CliAttentionNextGoldenFixtures.AmbiguousNextFullGoal
                : CliAttentionNextGoldenFixtures.UnknownNextFullGoal;
            Xunit.Assert.Equal(fixture.ExitCode, writer.ExitCode);
            Xunit.Assert.Equal(Encoding.UTF8.GetBytes(fixture.Stdout), Encoding.UTF8.GetBytes(writer.Output));
            Xunit.Assert.Equal(Encoding.UTF8.GetBytes(fixture.Stderr), Encoding.UTF8.GetBytes(writer.Error));
            Xunit.Assert.Equal(writer.ExitCode, read.ExitCode);
            Xunit.Assert.Equal(Encoding.UTF8.GetBytes(writer.Output), Encoding.UTF8.GetBytes(read.Output));
            Xunit.Assert.Equal(Encoding.UTF8.GetBytes(writer.Error), Encoding.UTF8.GetBytes(read.Error));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 9, 24, 0, 0, 0, TimeSpan.Zero);
    }
}
