using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: each fixture owns a unique temporary root and database, disposes its
// connections, and does not mutate environment variables or other process-global state.
namespace Mcg.AgentOrchestrator.Tests;

public sealed class SqliteAttentionProjectionTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions Options = new() { Converters = { new JsonStringEnumConverter() } };

    [Theory]
    [InlineData("operator", false)]
    [InlineData("completed", true)]
    [InlineData("inflight", true)]
    [InlineData("invalidated", false)]
    [InlineData("recovered", true)]
    [InlineData("success", false)]
    [InlineData("equal-time", true)]
    public async Task CrossGoalArchivedHistory_ReusesBindingAndSchedulePolicy(string scenario, bool compress)
    {
        using var fixture = await Fixture.CreateAsync(7);
        var goals = fixture.Goals;
        var exhaustion = Verification(At.AddMinutes(1), ProviderFailureKind.BudgetExhausted) with
        { StandardError = "You've hit your usage limit. resets at 2026-10-05T04:00:00Z", StandardErrorPath = "exhaustion-receipt" };
        var a = Dispatch(At, "account-a");
        var b = Dispatch(At, "account-b");
        var tasks = goals[0].Tasks.Select((task, index) => task with
        {
            LastDispatch = index == 0 ? a : b,
            LastVerification = exhaustion,
            DispatchHistory = null, VerificationHistory = null,
            LatestProviderBudgetRecoveryAt = scenario == "recovered" && index == 0 ? At.AddMinutes(10) : null
        }).ToArray();
        // Only two credentials, regardless of the default task count.
        await fixture.WriteAsync(goals[0] with { Status = GoalStatus.Completed, Tasks = tasks.Take(2).ToArray() }, archive: true, compress: compress);
        var resetAt = At.AddHours(3);
        const string binding = "claude::account-a";
        var reset = Note(goals[1], null, ProviderBudgetHoldSchedules.FormatReset(new(binding, resetAt, "reset-intent")), At.AddMinutes(2));
        await fixture.WriteAsync(goals[1] with { Status = GoalStatus.Cancelled, Timeline = [reset] }, archive: true, compress: !compress);
        var admittedTask = goals[2].Tasks[0] with { Status = WorkTaskStatus.Assigned, AssignedAgentId = "probe-agent" };
        var probe = Note(goals[2], admittedTask.Id,
            ProviderBudgetHoldSchedules.FormatProbe(new(binding, resetAt, 1, "operator")), At.AddMinutes(3));
        var invalidation = Note(goals[2], admittedTask.Id, "Reassigned", At.AddMinutes(4)) with { Kind = ProgressKind.TaskRedelegated };
        await fixture.WriteAsync(goals[2] with { Status = GoalStatus.Active, Tasks = [admittedTask],
            Timeline = scenario == "invalidated" ? [probe, invalidation] : [probe] });
        // Success for B must not release A; success for A must release an archived exhaustion.
        var success = goals[3].Tasks[0] with { LastDispatch = Dispatch(At.AddMinutes(4), "account-b"),
            LastVerification = Verification(At.AddMinutes(5), ProviderFailureKind.Unknown) with { ExitCode = 0 } };
        var successes = scenario == "success"
            ? new[] { success, success with { Id = goals[3].Tasks[1].Id, LastDispatch = Dispatch(At.AddMinutes(4), "account-a") } }
            : [success];
        await fixture.WriteAsync(goals[3] with { Status = GoalStatus.Superseded, Tasks = successes }, archive: true, compress: compress);
        if (scenario is "completed" or "inflight")
        {
            var task = goals[4].Tasks[0] with { LastDispatch = Dispatch(At.AddMinutes(6), "account-a"),
                LastVerification = scenario == "completed" ? Verification(At.AddMinutes(7), ProviderFailureKind.Unknown) : null };
            await fixture.WriteAsync(goals[4] with { Status = GoalStatus.Completed, Tasks = [task],
                Timeline = [Note(goals[4], task.Id,
                    ProviderBudgetHoldSchedules.FormatProbe(new(binding, resetAt, 1, "operator")), At.AddMinutes(5))] },
                archive: true, compress: compress);
        }
        if (scenario == "equal-time")
            await fixture.WriteAsync(goals[4] with { Status = GoalStatus.Completed, Tasks = [goals[4].Tasks[0] with
                { LastDispatch = a, LastVerification = exhaustion with { StandardErrorPath = "tied-later-goal" } }] },
                archive: true, compress: compress);

        var ids = goals.Select(goal => new GoalId(goal.Id)).ToArray();
        var baseline = await fixture.Queries.LoadGoalsAsync(ids);
        var expected = ProviderUsageHoldView.Read(baseline.Goals, At.AddHours(1));
        var projection = Assert.IsType<AttentionReadProjection>(await fixture.Queries.ReadAttentionProjectionAsync());
        var actual = ProviderUsageHoldView.ReadEvidence(projection.ProviderEvidence, At.AddHours(1));
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));
        if (scenario is "recovered" or "success") Assert.Empty(actual);
        else
        {
            var hold = Assert.Single(actual);
            Assert.Equal(goals[0].Id, hold.Hold.SourceGoalId.Value);
            Assert.Equal("exhaustion-receipt", hold.Hold.EvidenceReceipt);
            Assert.Equal("operator", hold.Schedule!.ResetSource);
            Assert.Equal(resetAt, hold.Schedule.ResetAt);
            if (scenario == "operator") Assert.Equal(new GoalId(goals[2].Id), hold.Schedule.AdmittedGoalId);
            if (scenario == "invalidated") Assert.Null(hold.Schedule.AdmittedGoalId);
            if (scenario == "completed") { Assert.Equal(2, hold.Schedule.Attempt); Assert.Equal("inconclusive", hold.Schedule.LastProbeOutcome); }
            if (scenario == "inflight") Assert.True(hold.Schedule.ProbeInFlight);
        }
        Assert.Equal(scenario is "completed" or "inflight" or "equal-time" ? 4 : 3, projection.Reads.ArchivePayloadsRead);
        Assert.Equal(projection.Reads.ArchivePayloadsRead, projection.Reads.ArchivePayloadsDecoded);

        // Unused fields cannot support a full GoalSnapshot/TaskSnapshot decode. Policy output
        // must still agree with the baseline captured before poisoning those unrelated fields.
        await fixture.PoisonUnusedArchiveFieldsAsync(goals[0].Id, compress);
        var narrow = Assert.IsType<AttentionReadProjection>(await fixture.Queries.ReadAttentionProjectionAsync());
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(
            ProviderUsageHoldView.ReadEvidence(narrow.ProviderEvidence, At.AddHours(1))));
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(60, true)]
    public async Task UnrelatedArchivePopulation_DecodesPayloads_WithoutFullGraphMaterialization(int count, bool compress)
    {
        using var fixture = await Fixture.CreateAsync(count);
        foreach (var goal in fixture.Goals)
        {
            await fixture.WriteAsync(goal with { Status = GoalStatus.Completed }, archive: true, compress: compress);
            await fixture.PoisonUnusedArchiveFieldsAsync(goal.Id, compress);
        }
        var projection = Assert.IsType<AttentionReadProjection>(await fixture.Queries.ReadAttentionProjectionAsync());
        Assert.Equal(count, projection.Goals.Count);
        Assert.Equal(new AttentionProjectionReadCounts(count, count, count, 0, 0), projection.Reads);
        Assert.Empty(projection.ProviderEvidence);
        Assert.Empty(projection.OwnerQuestions);
        Assert.Empty(projection.GateEvidence);
    }

    [Fact]
    public async Task MissingArchiveTableOrPayload_RetainsMetadataOnlyBehavior()
    {
        using var fixture = await Fixture.CreateAsync(1);
        await fixture.WriteAsync(fixture.Goals[0] with { Status = GoalStatus.Completed, IsMetadataOnly = true, Tasks = [], Timeline = [] });
        var missingPayload = Assert.IsType<AttentionReadProjection>(await fixture.Queries.ReadAttentionProjectionAsync());
        Assert.Equal(0, missingPayload.Reads.ArchivePayloadsRead);
        await using var connection = fixture.Connection();
        await connection.OpenAsync();
        await using var drop = connection.CreateCommand();
        drop.CommandText = "DROP TABLE goal_snapshot_archive";
        await drop.ExecuteNonQueryAsync();
        var missingTable = Assert.IsType<AttentionReadProjection>(await fixture.Queries.ReadAttentionProjectionAsync());
        Assert.Equal(missingPayload.Goals, missingTable.Goals);
        Assert.Empty(missingTable.ProviderEvidence);
    }

    [Fact]
    public async Task CancellationAndCorruptArchive_PropagateInsteadOfReturningUnsupported()
    {
        using var fixture = await Fixture.CreateAsync(1);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Queries.ReadAttentionProjectionAsync(cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Queries.ReadAttentionProjectionAsync(new AttentionProjectionReadOptions(false), cancellation.Token));
        await fixture.WriteAsync(fixture.Goals[0] with { Status = GoalStatus.Completed }, archive: true);
        await using var connection = fixture.Connection();
        await connection.OpenAsync();
        await using var update = connection.CreateCommand();
        update.CommandText = "UPDATE goal_snapshot_archive SET snapshot_json = X'010203'";
        Assert.Equal(1, await update.ExecuteNonQueryAsync());
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Queries.ReadAttentionProjectionAsync());
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Queries.ReadAttentionProjectionAsync(new AttentionProjectionReadOptions(false), default));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SummarySkipsGateDeserialization_FullModeStillValidates(bool archive, bool compress)
    {
        using var fixture = await Fixture.CreateAsync(1);
        var goal = fixture.Goals[0] with
        {
            CurrentHold = new("question", "steward-owner-question", "Owner must choose", At),
            GateTestObligations = [new("gate", 1, ["Selector"], null, "Pending", At)],
            Tasks = [fixture.Goals[0].Tasks[0] with
            {
                LastDispatch = Dispatch(At, "account-a"),
                LastVerification = Verification(At.AddMinutes(1), ProviderFailureKind.BudgetExhausted)
            }]
        };
        await fixture.WriteAsync(goal, archive, compress);
        var full = Assert.IsType<AttentionReadProjection>(await fixture.Queries.ReadAttentionProjectionAsync());
        Assert.Single(full.GateEvidence);
        Assert.Single(full.OwnerQuestions);
        Assert.Single(ProviderUsageHoldView.ReadEvidence(full.ProviderEvidence, At.AddHours(1)));

        await using var connection = fixture.Connection();
        await connection.OpenAsync();
        var table = archive ? "goal_snapshot_archive" : "goals";
        await using var read = connection.CreateCommand();
        read.CommandText = $"SELECT snapshot_json FROM {table}";
        var root = JsonNode.Parse(GoalSnapshotArchiveCodec.Decode(await read.ExecuteScalarAsync())!)!;
        // The document remains valid JSON; only typed gate decoding is impossible. Restoring
        // gate deserialization in summary mode must therefore fail this control.
        root["GateTestObligations"] = JsonNode.Parse("[\"not-a-gate-obligation\"]");
        var json = root.ToJsonString();
        await using var update = connection.CreateCommand();
        update.CommandText = $"UPDATE {table} SET snapshot_json = $json";
        update.Parameters.AddWithValue("$json", compress ? (object)GoalSnapshotArchiveCodec.Encode(json) : json);
        Assert.Equal(1, await update.ExecuteNonQueryAsync());
        await Assert.ThrowsAsync<JsonException>(() => fixture.Queries.ReadAttentionProjectionAsync());
        var summary = Assert.IsType<AttentionReadProjection>(await fixture.Queries.ReadAttentionProjectionAsync(new AttentionProjectionReadOptions(false), default));
        Assert.Empty(summary.GateEvidence);
        Assert.Equal(JsonSerializer.Serialize(full.Goals), JsonSerializer.Serialize(summary.Goals));
        Assert.Equal(JsonSerializer.Serialize(full.OwnerQuestions), JsonSerializer.Serialize(summary.OwnerQuestions));
        Assert.Equal(JsonSerializer.Serialize(full.ProviderEvidence), JsonSerializer.Serialize(summary.ProviderEvidence));
        Assert.Equal(full.Reads, summary.Reads);

        // Corruption in a retained field must still fail the selected read path.
        root["CurrentHold"] = "not-a-hold";
        json = root.ToJsonString();
        update.Parameters["$json"].Value = compress ? (object)GoalSnapshotArchiveCodec.Encode(json) : json;
        Assert.Equal(1, await update.ExecuteNonQueryAsync());
        await Assert.ThrowsAsync<JsonException>(() => fixture.Queries.ReadAttentionProjectionAsync(new AttentionProjectionReadOptions(false), default));
    }

    [Fact]
    public async Task LegacyProjectionImplementation_ReceivesAdditiveCallsAndOriginalDefaultLiteral()
    {
        using var fixture = await Fixture.CreateAsync(1);
        var projection = Assert.IsType<AttentionReadProjection>(await fixture.Queries.ReadAttentionProjectionAsync());
        var legacy = new LegacyAttentionQueries(projection);
        IOrchestratorStateQueries queries = legacy;
        // Keep this default literal: it proves source compatibility of existing callers.
        Assert.Same(projection, await queries.ReadAttentionProjectionAsync(default));
        Assert.Equal(1, legacy.Calls);
        using var cancellation = new CancellationTokenSource();
        Assert.Same(projection, await queries.ReadAttentionProjectionAsync(new AttentionProjectionReadOptions(false), cancellation.Token));
        Assert.Equal(2, legacy.Calls);
        Assert.Equal(cancellation.Token, legacy.LastToken);
    }

    private sealed class LegacyAttentionQueries(AttentionReadProjection projection) : IOrchestratorStateQueries
    {
        public int Calls { get; private set; }
        public CancellationToken LastToken { get; private set; }
        public Task<AttentionReadProjection?> ReadAttentionProjectionAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            LastToken = cancellationToken;
            return Task.FromResult<AttentionReadProjection?>(projection);
        }
        public Task<AgentOrchestratorKernel> LoadGoalsAsync(IReadOnlyCollection<GoalId> goalIds,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("Unexpected kernel load");
        public Task<IReadOnlyList<GoalSummary>> ListGoalMetadataAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Unexpected metadata load");
    }

    private static TaskDispatchSnapshot Dispatch(DateTimeOffset at, string credential) =>
        new("claude-cli", "claude", "fixture", at, ProviderName: "Claude", ClaudeCredentialSourceDirectory: credential);
    private static TaskVerificationSnapshot Verification(DateTimeOffset at, ProviderFailureKind kind) =>
        new("claude", "fixture", 1, "", "inconclusive", at, ProviderFailureKind: kind);
    private static ProgressEventSnapshot Note(GoalSnapshot goal, string? taskId, string message, DateTimeOffset at) =>
        new(goal.Id, taskId, ProgressKind.TaskNote, message, at);

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = SharedTestSupport.CreateTempDirectory();
        private string Db => Path.Combine(_root, "state.db");
        public GoalSnapshot[] Goals { get; private set; } = [];
        public IOrchestratorStateQueries Queries => SqliteOrchestratorStateRepository.OpenReadOnly(Db);
        public SqliteConnection Connection() => new($"Data Source={Db};Pooling=False");
        public static async Task<Fixture> CreateAsync(int count)
        {
            var fixture = new Fixture();
            StateDbMigrations.EnsureUpToDate(fixture.Db);
            var kernel = new AgentOrchestratorKernel();
            for (var index = 0; index < count; index++)
                kernel.CreateGoal(new GoalId($"{index + 1:x8}aaaaaaaaaaaaaaaaaaaaaaaa"), "Projection " + index);
            fixture.Goals = kernel.ExportSnapshot().Goals.ToArray();
            await new SqliteOrchestratorStateRepository(fixture.Db).SaveAsync(kernel);
            return fixture;
        }
        public async Task WriteAsync(GoalSnapshot goal, bool archive = false, bool compress = false)
        {
            await using var connection = Connection();
            await connection.OpenAsync();
            var json = JsonSerializer.Serialize(goal, Options);
            if (archive)
            {
                await using var insert = connection.CreateCommand();
                insert.CommandText = "INSERT INTO goal_snapshot_archive (goal_id, snapshot_json, archived_at) VALUES ($id, $json, $at)";
                insert.Parameters.AddWithValue("$id", goal.Id);
                insert.Parameters.AddWithValue("$json", compress ? (object)GoalSnapshotArchiveCodec.Encode(json) : json);
                insert.Parameters.AddWithValue("$at", At.ToString("O"));
                Assert.Equal(1, await insert.ExecuteNonQueryAsync());
                json = JsonSerializer.Serialize(goal with { IsMetadataOnly = true, Tasks = [], Timeline = [], CurrentHold = null, BriefVersions = null }, Options);
            }
            await using var update = connection.CreateCommand();
            update.CommandText = "UPDATE goals SET status = $status, snapshot_json = $json WHERE id = $id";
            update.Parameters.AddWithValue("$id", goal.Id);
            update.Parameters.AddWithValue("$status", goal.Status.ToString());
            update.Parameters.AddWithValue("$json", json);
            Assert.Equal(1, await update.ExecuteNonQueryAsync());
        }
        public async Task PoisonUnusedArchiveFieldsAsync(string id, bool compress)
        {
            await using var connection = Connection();
            await connection.OpenAsync();
            await using var read = connection.CreateCommand();
            read.CommandText = "SELECT snapshot_json FROM goal_snapshot_archive WHERE goal_id = $id";
            read.Parameters.AddWithValue("$id", id);
            var root = JsonNode.Parse(GoalSnapshotArchiveCodec.Decode(await read.ExecuteScalarAsync())!)!;
            root["RefinedSpec"] = "not-a-refined-spec";
            root["Tasks"]![0]!["LastExecution"] = "not-an-execution";
            var json = root.ToJsonString();
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<GoalSnapshot>(json, Options));
            await using var update = connection.CreateCommand();
            update.CommandText = "UPDATE goal_snapshot_archive SET snapshot_json = $json WHERE goal_id = $id";
            update.Parameters.AddWithValue("$id", id);
            update.Parameters.AddWithValue("$json", compress ? (object)GoalSnapshotArchiveCodec.Encode(json) : json);
            Assert.Equal(1, await update.ExecuteNonQueryAsync());
        }
        public void Dispose() => SharedTestSupport.RemoveTempDirectory(_root);
    }
}
