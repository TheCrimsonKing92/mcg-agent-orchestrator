using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Private roots, an injected clock and AsyncLocal console capture; safe to run in parallel.
public sealed class CliAgentReadHumanOutputTests : CliTaskQueryTestSupport
{
    [Fact]
    public void RemoteExecutors_SeededLedger_KeepsLiteralHumanOutput()
    {
        var root = CreateTempDirectory();
        try
        {
            var directory = Path.Combine(root, ".orchestrator");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "remote-lane-executors.json"),
                """{"executors":[{"id":"one","slots":2}],"lanes":[]}""");
            File.WriteAllLines(Path.Combine(directory, RemoteExecutorHealthLedger.FileName),
            [
                """{"observed_at":"2026-10-06T00:00:00Z","executor_id":"one","gate_attempt_id":"a","lane":"lane","outcome":"accepted","reason":null}"""
            ]);
            var output = Run(["remote-executors"], OrchestratorWorkspace.ForDirectory(root), new AgentOrchestratorKernel());
            Assert.Equal(string.Join(Environment.NewLine, new[]
            {
                "unreadable_outcome_lines=0\tunreadable_probe_lines=0",
                "executor\tattempts\toutcomes\taccepted_share\tlast_success\tlast_probe",
                "one\t1\t{\"accepted\":1}\t1\t2026-10-06T00:00:00.0000000+00:00\tnull",
                "attempt\tone\t2026-10-06T00:00:00.0000000+00:00\ta\tlane\taccepted\t",
                "slots\tone\t2\t0\t0",
                "admission\tone\tenabled"
            }) + Environment.NewLine, output);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Goals_SeededMetadata_KeepsLiteralHumanOutput()
    {
        var root = CreateTempDirectory();
        try
        {
            var clock = new FixedClock { UtcNow = new DateTimeOffset(2026, 10, 6, 0, 0, 0, TimeSpan.Zero) };
            var kernel = new AgentOrchestratorKernel(clock);
            kernel.CreateGoal(new GoalId("abc10000aaaaaaaaaaaaaaaaaaaaaaaa"), "First inspection goal");
            clock.UtcNow = clock.UtcNow.AddHours(1);
            kernel.CreateGoal(new GoalId("abc20000bbbbbbbbbbbbbbbbbbbbbbbb"), "Second inspection goal");

            var output = Run(["goals"], OrchestratorWorkspace.ForDirectory(root), kernel);
            Assert.Equal(string.Join(Environment.NewLine, new[]
            {
                "abc20000 Draft: Second inspection goal",
                "abc10000 Draft: First inspection goal"
            }) + Environment.NewLine, output);
            Assert.Empty(Directory.GetFiles(root, "*", SearchOption.AllDirectories));
        }
        finally { Directory.Delete(root, true); }
    }

    private static string Run(string[] args, OrchestratorWorkspace workspace, AgentOrchestratorKernel kernel)
    {
        var repository = new ProbeStateRepository(kernel) { ThrowOnOutbox = true };
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? goal = null;
        var output = CaptureConsole(() =>
        {
            Assert.True(CliReadOnlyCommandRunner.TryExecute(args, repository, workspace,
                new InMemoryModelProviderRegistry([]), null, ref agents, ref profiles, ref goal, out var changed));
            Assert.False(changed);
        });
        Assert.Null(goal);
        return output;
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; }
    }
}
