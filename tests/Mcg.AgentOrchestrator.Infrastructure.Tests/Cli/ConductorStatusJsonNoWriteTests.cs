using System.Security.Cryptography;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: unique fixture root and private SQLite stores; no machine-wide state or console access.
public sealed class ConductorStatusJsonNoWriteTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 26, 8, 0, 0, TimeSpan.Zero);
    private const string GoalA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string GoalB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string GoalC = "cccccccccccccccccccccccccccccccc";

    [Fact]
    public async Task SeededStatus_PreservesEveryExistingFileAndCreatesOnlyStoreSidecars()
    {
        using var fixture = new Fixture();
        var workspace = fixture.Workspace;
        await Seed(workspace);
        var before = Inventory(workspace.RootDirectory);
        var stores = new[] { workspace.SqliteStatePath, workspace.RunEventStorePath,
            Path.Combine(workspace.OrchestratorDirectory, SqliteOperatorIntentStore.DatabaseFileName) };
        Assert.Contains(workspace.SqliteStatePath, before.Keys);
        Assert.Contains(workspace.ConductEventsLogPath, before.Keys);
        Assert.Contains(Path.Combine(workspace.OrchestratorDirectory, "conduct-loop.lock"), before.Keys);
        Assert.Contains(workspace.RunEventStorePath, before.Keys);
        Assert.Contains(Path.Combine(workspace.OrchestratorDirectory, SqliteOperatorIntentStore.DatabaseFileName), before.Keys);

        ReadJson(workspace);

        var after = Inventory(workspace.RootDirectory);
        foreach (var (path, hash) in before)
        {
            Assert.True(after.TryGetValue(path, out var current), $"Status deleted {path}");
            Assert.Equal(hash, current);
        }
        foreach (var path in after.Keys.Except(before.Keys, StringComparer.OrdinalIgnoreCase))
        {
            Assert.True(path.EndsWith("-wal", StringComparison.Ordinal) || path.EndsWith("-shm", StringComparison.Ordinal),
                $"Status created a non-sidecar file: {path}");
            var store = path[..^4];
            Assert.Contains(store, stores);
            Assert.Contains(store, before.Keys);
        }
    }

    [Fact]
    public void EmptyWorkspace_CreatesNoFile()
    {
        using var fixture = new Fixture();
        Assert.Empty(Inventory(fixture.Workspace.RootDirectory));
        ReadJson(fixture.Workspace);
        Assert.Empty(Inventory(fixture.Workspace.RootDirectory));
    }

    private static Dictionary<string, string> Inventory(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToDictionary(path => path,
            path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), StringComparer.OrdinalIgnoreCase);

    private static void ReadJson(OrchestratorWorkspace workspace)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, CliConductorCommand.Run(["conductor", "status", "--json"], workspace,
            lockProbe: new FixedProbe(null), bootTime: () => Start.AddDays(-1),
            mainCommit: () => "def456", output: output, error: error, time: new FixedTimeProvider()));
        Assert.Equal("", error.ToString());
        Assert.Single(output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal("mcg.conductor-status", json.RootElement.GetProperty("schema").GetString());
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "conductor-status-" + Guid.NewGuid().ToString("N"));
        internal OrchestratorWorkspace Workspace { get; }

        internal Fixture()
        {
            Directory.CreateDirectory(_root);
            Workspace = OrchestratorWorkspace.ForDirectory(_root);
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }

    private sealed class FixedProbe(int? owner) : IConductorLockProbe
    {
        public int? ActiveOwnerPid(OrchestratorWorkspace workspace) => owner;
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Start;
    }

    private static async Task Seed(OrchestratorWorkspace workspace)
    {
        var kernel = new AgentOrchestratorKernel(new FixedClock());
        var first = kernel.CreateGoal(new GoalId(GoalA), "Goal A");
        var second = kernel.CreateGoal(new GoalId(GoalC), "Goal C");
        kernel.RequestHumanInput(first.Id, first.Tasks[0].Id, "Which branch?");
        kernel.RequestHumanInput(second.Id, null, "Review risk?", HumanWaitKind.RiskReview);
        _ = StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
        await new SqliteOrchestratorStateRepository(workspace.SqliteStatePath).SaveAsync(kernel);

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
