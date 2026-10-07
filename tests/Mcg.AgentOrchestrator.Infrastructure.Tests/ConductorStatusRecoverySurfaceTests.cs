using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: fixture-owned databases and logs, injected lock probe and fixed clocks.
public sealed class ConductorStatusRecoverySurfaceTests
{
    private static readonly DateTimeOffset RecordedAt = new(2026, 10, 7, 1, 0, 0, TimeSpan.Zero);
    private const string GoalA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string GoalB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public async Task Status_TwoGenerations_ReportsLatestSummaryAndOnlyItsIdentityDeferrals()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var workspace = fixture.Workspace;
        await SeedStatusSources(workspace);
        var store = new SqliteRunEventStore(workspace.RunEventStorePath);
        var generations = new Queue<string>(["gen-old", "gen-new"]);
        var outcomes = new Queue<string[]>([
            ["deferred-identity-unproven"],
            ["adopted", "deferred-identity-unproven", "deferred-identity-unproven"]]);
        var row = new WorkerAdoptionCensusRow("goal:task", 101, RecordedAt,
            SpawnRegistryLifecycle.ConductorDetached, null, null, SpawnOwnerLiveness.Unknown,
            "identity-unproven", "worker identity unavailable");
        var recorder = new ConductorLifecycleRecorder(store, utcNow: () => RecordedAt,
            generationId: generations.Dequeue, readAdoptionCensus: () => [row],
            adoptInheritedWorkers: _ => outcomes.Dequeue()
                .Select(outcome => new WorkerAdoptionTransferResult(row, outcome)).ToArray());
        recorder.Start("Conservative", null, RecordedAt, null, null);
        // Reverse timestamps ensure sequence, rather than wall-clock order, selects the summary.
        recorder.Start("Conservative", null, RecordedAt.AddDays(-1), null, null);

        var lines = StatusWithoutMutatingSources(workspace);

        Assert.Equal("Worker adoption: results=3 adopted=1 deferred-identity-unproven=2",
            Assert.Single(lines.Where(line => line.StartsWith("Worker adoption:", StringComparison.Ordinal))));
        Assert.Empty(generations);
        Assert.Empty(outcomes);
        AssertRecoverySectionsFollowLanding(lines);
    }

    [Fact]
    public async Task Status_MissingDatabases_ReportsUnavailableAndCreatesNeitherStore()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var workspace = fixture.Workspace;
        await SeedStatusSources(workspace);
        var intentPath = IntentPath(workspace);
        Assert.False(File.Exists(workspace.RunEventStorePath));
        Assert.False(File.Exists(intentPath));

        var lines = StatusWithoutMutatingSources(workspace);

        Assert.Contains("Worker adoption: unavailable", lines);
        Assert.Contains("Pending operator intents: unavailable (intent database missing)", lines);
        Assert.False(File.Exists(workspace.RunEventStorePath));
        Assert.False(File.Exists(intentPath));
        AssertRecoverySectionsFollowLanding(lines);
    }

    [Fact]
    public async Task Status_NoAdoptionSummary_ReportsAdoptionUnavailable()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var workspace = fixture.Workspace;
        await SeedStatusSources(workspace);
        var store = new SqliteRunEventStore(workspace.RunEventStorePath);
        new ConductorLifecycleRecorder(store, generationId: () => "gen-no-adoption")
            .Start("Conservative", null, RecordedAt, null, null);

        Assert.Contains("Worker adoption: unavailable", StatusWithoutMutatingSources(workspace));
    }

    [Fact]
    public async Task Status_AppliedIntent_ReportsNoneAndPreservesIntent()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var workspace = fixture.Workspace;
        await SeedStatusSources(workspace);
        var store = SqliteOperatorIntentStore.ForDirectories(workspace.OrchestratorDirectory, workspace.LogDirectory);
        var intent = await store.EnqueueAsync(Intent("applied", GoalA, RecordedAt));
        var claimed = await store.ClaimNextAsync(GoalA, "fixture");
        Assert.NotNull(claimed);
        Assert.Equal(intent.Id, claimed.Id);
        await store.CompleteAsync(intent.Id, "fixture", OperatorIntentStatus.Applied, "done", RecordedAt);
        var before = JsonSerializer.Serialize(await store.GetAsync(intent.Id));

        Assert.Contains("Pending operator intents: none", StatusWithoutMutatingSources(workspace));

        Assert.Equal(before, JsonSerializer.Serialize(await store.GetAsync(intent.Id)));
        Assert.Equal(OperatorIntentStatus.Applied, (await store.GetAsync(intent.Id))!.Status);
    }

    [Fact]
    public async Task Status_TwoPendingGoals_OrdersCountsAndHintsByOwnerWithoutChangingIntents()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var workspace = fixture.Workspace;
        await SeedStatusSources(workspace);
        var store = SqliteOperatorIntentStore.ForDirectories(workspace.OrchestratorDirectory, workspace.LogDirectory);
        // Insert in reverse goal order so ordering is not an artifact of submission order.
        var intents = new[]
        {
            await store.EnqueueAsync(Intent("b", GoalB, RecordedAt.AddMinutes(2))),
            await store.EnqueueAsync(Intent("a-first", GoalA, RecordedAt)),
            await store.EnqueueAsync(Intent("a-latest", GoalA, RecordedAt.AddMinutes(1)))
        };
        var before = await SnapshotIntents(store, intents);
        var expected = new[]
        {
            $"  {GoalA} | intents=2 | latest={RoundTrip(RecordedAt.AddMinutes(1))}",
            $"  {GoalB} | intents=1 | latest={RoundTrip(RecordedAt.AddMinutes(2))}"
        };

        var stopped = StatusWithoutMutatingSources(workspace);
        Assert.Contains("Pending operator intents: 2 goal(s)", stopped);
        Assert.Equal(expected.Select((line, index) => line +
                $" | apply with: conductor apply-intents {(index == 0 ? GoalA : GoalB)[..8]}"),
            IntentLines(stopped));
        Assert.Equal(before, await SnapshotIntents(store, intents));
        foreach (var intent in intents)
            Assert.Equal(OperatorIntentStatus.Pending, (await store.GetAsync(intent.Id))!.Status);

        var running = StatusWithoutMutatingSources(workspace, owner: 4242);
        Assert.Contains("Conductor: running (pid 4242)", running);
        Assert.Contains("Pending operator intents: 2 goal(s)", running);
        Assert.Equal(expected, IntentLines(running));
        Assert.DoesNotContain(running, line => line.Contains("apply with:", StringComparison.Ordinal));
        Assert.Equal(before, await SnapshotIntents(store, intents));
        foreach (var intent in intents)
            Assert.Equal(OperatorIntentStatus.Pending, (await store.GetAsync(intent.Id))!.Status);
    }

    [Fact]
    public async Task Status_ClaimedAndWorkspaceIntents_ReportsOnlyGoalAndPreservesClaim()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var workspace = fixture.Workspace;
        await SeedStatusSources(workspace);
        var store = SqliteOperatorIntentStore.ForDirectories(workspace.OrchestratorDirectory, workspace.LogDirectory);
        var goalIntent = await store.EnqueueAsync(Intent("claimed", GoalA, RecordedAt));
        var workspaceIntent = await store.EnqueueAsync(Intent("workspace", OperatorIntentScopes.Workspace, RecordedAt));
        var claimed = await store.ClaimNextAsync(GoalA, "fixture");
        Assert.NotNull(claimed);
        Assert.Equal(OperatorIntentStatus.Claimed, claimed.Status);
        var intents = new[] { goalIntent, workspaceIntent };
        var before = await SnapshotIntents(store, intents);

        var lines = StatusWithoutMutatingSources(workspace);

        Assert.Contains("Pending operator intents: 1 goal(s)", lines);
        Assert.Equal($"  {GoalA} | intents=1 | latest={RoundTrip(claimed.ClaimedAt!.Value)}" +
            " | apply with: conductor apply-intents aaaaaaaa", Assert.Single(IntentLines(lines)));
        Assert.Equal(before, await SnapshotIntents(store, intents));
    }

    [Fact]
    public async Task Status_UnreadableIntentDatabase_ReportsUnavailableWithoutRewritingFile()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var workspace = fixture.Workspace;
        await SeedStatusSources(workspace);
        File.WriteAllText(IntentPath(workspace), "not a SQLite database");
        var before = Hash(IntentPath(workspace));

        Assert.Contains("Pending operator intents: unavailable", StatusWithoutMutatingSources(workspace));

        Assert.Equal(before, Hash(IntentPath(workspace)));
    }

    private static async Task SeedStatusSources(OrchestratorWorkspace workspace)
    {
        await InfrastructureTestSupport.CreateMigratedStateRepository(workspace.SqliteStatePath)
            .SaveAsync(new AgentOrchestratorKernel());
        Directory.CreateDirectory(workspace.LogDirectory);
        File.WriteAllText(workspace.ConductEventsLogPath, JsonSerializer.Serialize(new
        {
            timestamp = RecordedAt, eventKind = "loop-stop", detail = "reason=operator-stop"
        }) + "\r\n");
    }

    private static string[] StatusWithoutMutatingSources(OrchestratorWorkspace workspace, int? owner = null)
    {
        var original = new[] { workspace.SqliteStatePath, workspace.ConductEventsLogPath }
            .ToDictionary(path => path, Hash);
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = CliConductorCommand.Run(["conductor", "status"], workspace,
            lockProbe: new ConductorVerbStartTests.FixedProbe(owner), bootTime: () => RecordedAt.AddDays(-2),
            mainCommit: () => null, output: output, error: error);
        Assert.Equal(0, exit);
        Assert.Equal("", error.ToString());
        foreach (var (path, hash) in original) Assert.Equal(hash, Hash(path));
        return output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
    }

    private static void AssertRecoverySectionsFollowLanding(string[] lines)
    {
        var landing = Array.FindIndex(lines, line => line.StartsWith("Most recent landing:", StringComparison.Ordinal));
        Assert.True(landing >= 0);
        Assert.StartsWith("Worker adoption:", lines[landing + 1]);
        Assert.StartsWith("Pending operator intents:", lines[landing + 2]);
    }

    private static string[] IntentLines(string[] lines) =>
        lines.Where(line => line.Contains(" | intents=", StringComparison.Ordinal)).ToArray();

    private static OperatorIntentRecord Intent(string id, string goalId, DateTimeOffset at) =>
        new(id, "key-" + id, "retry", goalId, "task", "{}", [], "operator", "cli", "local", at);

    private static async Task<string[]> SnapshotIntents(SqliteOperatorIntentStore store,
        IEnumerable<OperatorIntentRecord> intents)
    {
        var result = new List<string>();
        foreach (var intent in intents) result.Add(JsonSerializer.Serialize(await store.GetAsync(intent.Id)));
        return result.ToArray();
    }

    private static string IntentPath(OrchestratorWorkspace workspace) =>
        Path.Combine(workspace.OrchestratorDirectory, SqliteOperatorIntentStore.DatabaseFileName);

    private static string RoundTrip(DateTimeOffset at) => at.ToString("O", CultureInfo.InvariantCulture);
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
