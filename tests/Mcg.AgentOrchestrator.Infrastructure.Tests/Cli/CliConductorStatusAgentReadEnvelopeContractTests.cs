using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: fixture-owned databases/logs, injected clock and probe, local writers.
public sealed class CliConductorStatusAgentReadEnvelopeContractTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 26, 8, 0, 0, TimeSpan.Zero);
    private const string GoalA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string GoalB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string GoalC = "cccccccccccccccccccccccccccccccc";

    private static readonly DateTimeOffset GeneratedAt = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(null, "def456", "differs")]
    [InlineData(1234, "def456", "differs")]
    [InlineData(null, "abc123", "current")]
    [InlineData(1234, "abc123", "current")]
    public async Task SeededStatus_PinsOrderedContractValuesAndHumanParity(int? owner, string main, string generation)
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        await Seed(fixture.Workspace);
        using var json = JsonDocument.Parse(Status(fixture.Workspace, true, owner, main));
        var payload = AssertContract(json.RootElement);
        var conductor = payload.GetProperty("conductor");
        Assert.Equal(owner is null ? "stopped" : "running", Text(conductor, "state"));
        if (owner is null) Assert.Null(Text(conductor, "pid"));
        else Assert.Equal(owner.Value, conductor.GetProperty("pid").GetInt32());

        var since = payload.GetProperty("since");
        Assert.Equal("stopped", Text(since, "kind"));
        Assert.Equal("2026-09-26T10:00:00.0000000+00:00", Text(since, "at"));
        Assert.Equal("operator-stop", Text(since, "reason"));
        Assert.Equal("abc123", Text(payload, "supervisorBuild"));
        var build = payload.GetProperty("generation");
        Assert.Equal(generation, Text(build, "state"));
        Assert.Equal("abc123", Text(build, "build"));
        Assert.Equal(main, Text(build, "main"));
        Assert.Equal("2026-09-26T09:00:00.0000000+00:00", Text(payload, "latestTick"));

        var goals = payload.GetProperty("goals");
        Assert.True(goals.GetProperty("available").GetBoolean());
        var nonTerminal = goals.GetProperty("nonTerminal").EnumerateArray().ToArray();
        Assert.Equal(new[] { GoalA, GoalC }, nonTerminal.Select(goal => Text(goal, "id")));
        Assert.All(nonTerminal, goal =>
        {
            Assert.Equal("WaitingForHuman", Text(goal, "status"));
            Assert.Equal("Researcher", Text(goal, "role"));
        });
        Assert.Equal(1, goals.GetProperty("pendingClarifications").GetInt32());
        Assert.Equal(1, goals.GetProperty("humanWaits").GetInt32());
        var landing = payload.GetProperty("mostRecentLanding");
        Assert.Equal(GoalB, Text(landing, "goal"));
        Assert.Equal("2026-09-26T08:30:00.0000000+00:00", Text(landing, "at"));
        var adoption = payload.GetProperty("workerAdoption");
        Assert.Equal("results=1 adopted=0", Text(adoption, "summary"));
        Assert.Equal(1, adoption.GetProperty("deferredIdentityUnproven").GetInt32());
        var intents = payload.GetProperty("pendingIntents");
        Assert.Equal("pending", Text(intents, "state"));
        Assert.Null(Text(intents, "reason"));
        var intent = Assert.Single(intents.GetProperty("goals").EnumerateArray());
        Assert.Equal(GoalA, Text(intent, "goal"));
        Assert.Equal(1, intent.GetProperty("intents").GetInt32());
        Assert.Equal("2026-09-26T08:15:00.0000000+00:00", Text(intent, "latest"));

        var human = Status(fixture.Workspace, false, owner, main);
        AssertHumanParity(payload, human);
    }

    [Fact]
    public void EmptyWorkspace_PinsUnavailableContractAndNulls()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        using var json = JsonDocument.Parse(Status(fixture.Workspace, true, null, null));
        var payload = AssertContract(json.RootElement);
        Assert.Equal("stopped", Text(payload.GetProperty("conductor"), "state"));
        Assert.Null(Text(payload.GetProperty("conductor"), "pid"));
        var since = payload.GetProperty("since");
        Assert.Equal("unavailable", Text(since, "kind"));
        Assert.Null(Text(since, "at"));
        Assert.Null(Text(since, "reason"));
        Assert.Null(Text(payload, "supervisorBuild"));
        var generation = payload.GetProperty("generation");
        Assert.Equal("no-build", Text(generation, "state"));
        Assert.Null(Text(generation, "build"));
        Assert.Null(Text(generation, "main"));
        Assert.Null(Text(payload, "latestTick"));
        var goals = payload.GetProperty("goals");
        Assert.False(goals.GetProperty("available").GetBoolean());
        foreach (var name in new[] { "nonTerminal", "pendingClarifications", "humanWaits" })
            Assert.Equal(JsonValueKind.Null, goals.GetProperty(name).ValueKind);
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("mostRecentLanding").ValueKind);
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("workerAdoption").ValueKind);
        var intents = payload.GetProperty("pendingIntents");
        Assert.Equal("unavailable", Text(intents, "state"));
        Assert.Equal("intent database missing", Text(intents, "reason"));
        Assert.Empty(intents.GetProperty("goals").EnumerateArray());
    }

    [Fact]
    public async Task HostRestart_PreservesIndependentOwnerAndLifecycle()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        await Seed(fixture.Workspace);
        using var json = JsonDocument.Parse(Status(fixture.Workspace, true, null, "abc123", Start.AddDays(1)));
        var payload = AssertContract(json.RootElement);
        Assert.Equal("stopped", Text(payload.GetProperty("conductor"), "state"));
        Assert.Null(Text(payload.GetProperty("conductor"), "pid"));
        Assert.Equal("host-restart", Text(payload.GetProperty("since"), "kind"));
        Assert.Equal("2026-09-27T08:00:00.0000000+00:00", Text(payload.GetProperty("since"), "at"));
        Assert.Null(Text(payload.GetProperty("since"), "reason"));
        Assert.Contains("stopped since host restart at 2026-09-27T08:00:00.0000000+00:00",
            Status(fixture.Workspace, false, null, "abc123", Start.AddDays(1)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1234)]
    public void MissingLifecycle_ReportsOwnerIndependently(int? owner)
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        using var json = JsonDocument.Parse(Status(fixture.Workspace, true, owner, null));
        var payload = AssertContract(json.RootElement);
        Assert.Equal(owner is null ? "stopped" : "running", Text(payload.GetProperty("conductor"), "state"));
        Assert.Equal("unavailable", Text(payload.GetProperty("since"), "kind"));
        if (owner is not null) Assert.Equal(owner.Value, payload.GetProperty("conductor").GetProperty("pid").GetInt32());
    }

    [Fact]
    public async Task StoreTickLaterThanLog_UsesStoreInstant()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        await Seed(fixture.Workspace);
        var runs = new SqliteRunEventStore(fixture.Workspace.RunEventStorePath);
        await runs.AppendAsync(new(RunEventTypes.ConductorTick, null, "tick", "complete", "tick=6", null, Start.AddMinutes(75)));
        using var json = JsonDocument.Parse(Status(fixture.Workspace, true, null, "abc123"));
        var payload = AssertContract(json.RootElement);
        Assert.Equal("2026-09-26T09:15:00.0000000+00:00", Text(payload, "latestTick"));
        Assert.Contains($"Latest tick: {Text(payload, "latestTick")}", Status(fixture.Workspace, false, null, "abc123"));
    }

    private static JsonElement AssertContract(JsonElement root)
    {
        Names(root, "schema", "schemaVersion", "generatedAt", "payload");
        Assert.Equal("mcg.conductor-status", Text(root, "schema"));
        Assert.Equal(AgentReadEnvelope.ConductorStatusSchema, Text(root, "schema"));
        Assert.Equal(JsonValueKind.Number, root.GetProperty("schemaVersion").ValueKind);
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(AgentReadEnvelope.ConductorStatusSchemaVersion, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("2026-10-01T12:00:00.0000000Z", Text(root, "generatedAt"));
        var payload = root.GetProperty("payload");
        Names(payload, "conductor", "since", "supervisorBuild", "generation", "latestTick", "goals",
            "mostRecentLanding", "workerAdoption", "pendingIntents");
        Names(payload.GetProperty("conductor"), "state", "pid");
        Names(payload.GetProperty("since"), "kind", "at", "reason");
        Names(payload.GetProperty("generation"), "state", "build", "main");
        var goals = payload.GetProperty("goals");
        Names(goals, "available", "nonTerminal", "pendingClarifications", "humanWaits");
        if (goals.GetProperty("nonTerminal").ValueKind != JsonValueKind.Null)
            foreach (var goal in goals.GetProperty("nonTerminal").EnumerateArray()) Names(goal, "id", "status", "role");
        if (payload.GetProperty("mostRecentLanding").ValueKind != JsonValueKind.Null)
            Names(payload.GetProperty("mostRecentLanding"), "goal", "at");
        if (payload.GetProperty("workerAdoption").ValueKind != JsonValueKind.Null)
            Names(payload.GetProperty("workerAdoption"), "summary", "deferredIdentityUnproven");
        var intents = payload.GetProperty("pendingIntents");
        Names(intents, "state", "reason", "goals");
        foreach (var goal in intents.GetProperty("goals").EnumerateArray()) Names(goal, "goal", "intents", "latest");
        return payload;
    }

    private static void AssertHumanParity(JsonElement payload, string human)
    {
        var conductor = payload.GetProperty("conductor");
        var pid = conductor.GetProperty("pid");
        Assert.Contains(pid.ValueKind == JsonValueKind.Null ? "Conductor: stopped"
            : $"Conductor: running (pid {pid.GetInt32()})", human);
        var since = payload.GetProperty("since");
        Assert.Contains($"Stopped since {Text(since, "at")}; reason={Text(since, "reason")}", human);
        Assert.Contains($"Supervisor build: {Text(payload, "supervisorBuild")}", human);
        var generation = payload.GetProperty("generation");
        Assert.Contains(Text(generation, "state") == "current" ? $"Generation: current with main {Text(generation, "main")}"
            : $"Generation: differs from main (running {Text(generation, "build")}, main {Text(generation, "main")})", human);
        Assert.Contains($"Latest tick: {Text(payload, "latestTick")}", human);
        var goals = payload.GetProperty("goals");
        var nonTerminal = goals.GetProperty("nonTerminal").EnumerateArray().ToArray();
        Assert.Contains($"Non-terminal goals: {nonTerminal.Length}", human);
        foreach (var goal in nonTerminal)
            Assert.Contains($"  {Text(goal, "id")} | {Text(goal, "status")} | role={Text(goal, "role")}{Environment.NewLine}", human);
        Assert.Contains($"Pending clarifications: {goals.GetProperty("pendingClarifications").GetInt32()}; human waits: {goals.GetProperty("humanWaits").GetInt32()}", human);
        var landing = payload.GetProperty("mostRecentLanding");
        Assert.Contains($"Most recent landing: {Text(landing, "goal")} at {Text(landing, "at")}", human);
        var adoption = payload.GetProperty("workerAdoption");
        Assert.Contains($"Worker adoption: {Text(adoption, "summary")} deferred-identity-unproven={adoption.GetProperty("deferredIdentityUnproven").GetInt32()}", human);
        var intents = payload.GetProperty("pendingIntents").GetProperty("goals").EnumerateArray().ToArray();
        Assert.Contains($"Pending operator intents: {intents.Length} goal(s)", human);
        foreach (var intent in intents)
        {
            var goal = Text(intent, "goal")!;
            var hint = pid.ValueKind == JsonValueKind.Null ? $" | apply with: conductor apply-intents {goal[..8]}" : "";
            Assert.Contains($"  {goal} | intents={intent.GetProperty("intents").GetInt32()} | latest={Text(intent, "latest")}{hint}{Environment.NewLine}", human);
        }
    }

    private static void Names(JsonElement element, params string[] names) =>
        Assert.Equal(names, element.EnumerateObject().Select(property => property.Name).ToArray());

    private static string? Text(JsonElement element, string property) =>
        element.GetProperty(property).ValueKind == JsonValueKind.Null ? null : element.GetProperty(property).GetString();

    private static string Status(OrchestratorWorkspace workspace, bool json, int? owner, string? main,
        DateTimeOffset? boot = null)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, CliConductorCommand.Run(json ? ["conductor", "status", "--json"] : ["conductor", "status"],
            workspace, lockProbe: new ConductorVerbStartTests.FixedProbe(owner), bootTime: () => boot ?? Start.AddDays(-1),
            mainCommit: () => main, output: output, error: error, time: new FixedTimeProvider()));
        Assert.Equal("", error.ToString());
        if (json) Assert.Single(output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        return output.ToString();
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => GeneratedAt;
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
        Directory.CreateDirectory(workspace.GoalLifecycleEventsDirectory);
        File.WriteAllText(Path.Combine(workspace.GoalLifecycleEventsDirectory, "landings.jsonl"),
            JsonSerializer.Serialize(new { eventType = "GoalLanded", goalId = GoalB, timestamp = Start.AddMinutes(30) }) + "\r\n");

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
