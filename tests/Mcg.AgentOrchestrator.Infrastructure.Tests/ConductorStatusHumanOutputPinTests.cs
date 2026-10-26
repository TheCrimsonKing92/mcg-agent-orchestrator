using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: private workspace files and databases, injected probe and clock, local writers.
public sealed class ConductorStatusHumanOutputPinTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 26, 8, 0, 0, TimeSpan.Zero);
    private const string GoalA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string GoalB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string GoalC = "cccccccccccccccccccccccccccccccc";

    [Fact]
    public async Task SeededStatus_PreservesEveryHumanByte()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        await Seed(fixture.Workspace);
        Assert.Equal(Lines(
            "Conductor: stopped",
            "Stopped since 2026-09-26T10:00:00.0000000+00:00; reason=operator-stop",
            "Supervisor build: abc123",
            "Generation: differs from main (running abc123, main def456)",
            "Latest tick: 2026-09-26T09:00:00.0000000+00:00",
            "Non-terminal goals: 2",
            "  aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa | WaitingForHuman | role=Researcher",
            "  cccccccccccccccccccccccccccccccc | WaitingForHuman | role=Researcher",
            "Pending clarifications: 1; human waits: 1",
            "Most recent landing: bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb at 2026-09-26T08:30:00.0000000+00:00",
            "Worker adoption: results=1 adopted=0 deferred-identity-unproven=1",
            "Pending operator intents: 1 goal(s)",
            "  aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa | intents=1 | latest=2026-09-26T08:15:00.0000000+00:00 | apply with: conductor apply-intents aaaaaaaa",
            "Owner directives: 0 active"),
            Status(fixture.Workspace, null));
    }

    [Fact]
    public void RunningOwner_PreservesEveryHumanByte()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        Directory.CreateDirectory(fixture.Workspace.LogDirectory);
        File.WriteAllText(fixture.Workspace.ConductEventsLogPath,
            "{\"timestamp\":\"2026-09-26T08:00:00+00:00\",\"eventKind\":\"loop-start\",\"detail\":\"LOOP_START\"}\r\n");
        Assert.Equal(Lines(
            "Conductor: running (pid 1234)",
            "Running since 2026-09-26T08:00:00.0000000+00:00",
            "Supervisor build: unavailable",
            "Generation: unavailable (no supervisor build recorded)",
            "Latest tick: unavailable",
            "Goals: unavailable (state database missing)",
            "Pending clarifications: unavailable; human waits: unavailable",
            "Most recent landing: unavailable",
            "Worker adoption: unavailable",
            "Pending operator intents: unavailable (intent database missing)",
            "Owner directives: 0 active"),
            Status(fixture.Workspace, 1234));
    }

    [Fact]
    public void EmptyWorkspace_PreservesEveryHumanByte()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        Assert.Equal(Lines(
            "Conductor: stopped",
            "Since: unavailable",
            "Supervisor build: unavailable",
            "Generation: unavailable (no supervisor build recorded)",
            "Latest tick: unavailable",
            "Goals: unavailable (state database missing)",
            "Pending clarifications: unavailable; human waits: unavailable",
            "Most recent landing: unavailable",
            "Worker adoption: unavailable",
            "Pending operator intents: unavailable (intent database missing)",
            "Owner directives: 0 active"),
            Status(fixture.Workspace, null));
    }

    private static string Lines(params string[] lines) => string.Join(Environment.NewLine, lines) + Environment.NewLine;

    private static string Status(OrchestratorWorkspace workspace, int? owner)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, CliConductorCommand.Run(["conductor", "status"], workspace,
            lockProbe: new ConductorVerbStartTests.FixedProbe(owner), bootTime: () => Start.AddDays(-1),
            mainCommit: () => "def456", output: output, error: error));
        Assert.Equal("", error.ToString());
        return output.ToString();
    }

    private static async Task Seed(OrchestratorWorkspace workspace)
    {
        var kernel = new AgentOrchestratorKernel(new FixedClock());
        var first = kernel.CreateGoal(new GoalId(GoalA), "Goal A");
        var second = kernel.CreateGoal(new GoalId(GoalC), "Goal C");
        kernel.RequestHumanInput(first.Id, first.Tasks[0].Id, "Which branch?");
        kernel.RequestHumanInput(second.Id, null, "Review risk?", HumanWaitKind.RiskReview);
        await InfrastructureTestSupport.CreateMigratedStateRepository(workspace.SqliteStatePath).SaveAsync(kernel);

        Directory.CreateDirectory(workspace.LogDirectory);
        var events = new[]
        {
            new { timestamp = Start, eventKind = "loop-start", detail = "LOOP_START" },
            new { timestamp = Start.AddMinutes(10), eventKind = "supervisor-build", detail = "SUPERVISOR_BUILD {\"commitSha\":\"abc123\"}" },
            new { timestamp = Start.AddHours(1), eventKind = "tick-load-recovered", detail = "TICK tick=4" },
            new { timestamp = Start.AddHours(2), eventKind = "loop-stop", detail = "LOOP_STOP tick=5 reason=operator-stop\r" }
        };
        File.WriteAllText(workspace.ConductEventsLogPath,
            string.Join("\r\n", events.Select(item => JsonSerializer.Serialize(item))) + "\r\n");
        File.WriteAllText(Path.Combine(workspace.OrchestratorDirectory, "conduct-loop.lock"), "old lock");
        new SqliteOrchestratorStateRepository(workspace.SqliteStatePath).RecordPromotionFacts([
            new GoalPromotionFact(GoalB, new string('a', 40), new string('b', 40), new string('c', 40),
                Start.AddMinutes(30), GoalPromotionFact.MergeTrainProvenance)]);

        var runs = new SqliteRunEventStore(workspace.RunEventStorePath);
        await runs.AppendAsync(new(RunEventTypes.ConductorTick, null, "tick", "complete", "tick=3", null, Start.AddMinutes(45)));
        await runs.AppendAsync(new(RunEventTypes.ConductorLifecycle, null, "adoption", "deferred-identity-unproven",
            "identity unavailable", "{\"generationId\":\"gen-current\"}", Start));
        await runs.AppendAsync(new(RunEventTypes.ConductorLifecycle, null, "adoption-summary", "complete",
            "results=1 adopted=0", "{\"generationId\":\"gen-current\"}", Start));

        var intents = SqliteOperatorIntentStore.ForDirectories(workspace.OrchestratorDirectory, workspace.LogDirectory);
        await intents.EnqueueAsync(new OperatorIntentRecord("intent-one", "key-one", "retry", GoalA, "task",
            "{}", [], "operator", "cli", "local", Start.AddMinutes(15)));
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Start;
    }
}
