using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

// Owned databases/clock; console capture shares the existing CLI collection.
[Collection(CliTestCollections.ConsoleSerialized)]
public sealed class CliAttentionJsonSelectiveReadTests : CliTaskQueryTestSupport
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 0, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions Options = new() { Converters = { new JsonStringEnumConverter() } };

    [Theory]
    [InlineData(4, false)]
    [InlineData(80, true)]
    public async Task MixedHistory_EnvelopeMatchesBaseline_LoadsOnlyActionableOwners(int history, bool compress)
    {
        using var fixture = await Fixture.CreateAsync(history + 10);
        var goals = fixture.Goals;
        var current = goals[0] with
        {
            Status = GoalStatus.Active, BriefVersions =
            [new(1, goals[0].Objective, Now.AddDays(-1), SupersededByVersion: 2), new(2, goals[0].Objective, Now)],
            GateTestObligations = [Gate(1), Gate(2)]
        };
        await fixture.WriteGoalAsync(current);
        await fixture.WriteWaitAsync(new("current-wait", current.Id, null, "Current", Now,
            Kind: HumanWaitKind.ProspectiveAcceptanceEvidence, PlannerIdentity: Identity(current, 2)));
        await fixture.WriteWaitAsync(new("stale-wait", current.Id, null, "Stale", Now,
            PlannerIdentity: Identity(current, 1)));
        await fixture.WriteWaitAsync(new("completed-wait", current.Id, null, "Answered", Now,
            IsCompleted: true, Answer: "Proceed", AnsweredAt: Now));
        await fixture.WriteGoalAsync(goals[1] with { Status = GoalStatus.Active });
        await fixture.WriteGoalAsync(goals[2] with { Status = GoalStatus.Parked, CurrentHold =
            Hold("author-owner-question", "author-owner-question item=HumanInput:wait-x reason=choose question=Choose? recommendation=Yes") });
        await fixture.WriteGoalAsync(goals[3] with { Status = GoalStatus.Failed, CurrentHold = Hold("steward-owner-question",
            $"held trigger=worker:{goals[3].Tasks[1].Id}:round:1 question=Retry? evidence=[receipt]") });
        await fixture.WriteGoalAsync(goals[4] with { Status = GoalStatus.Active,
            CurrentHold = Hold("steward-owner-question", "malformed marker fallback") });
        foreach (var index in new[] { 2, 3, 5, 6, 7 })
        {
            var status = index switch { 2 => GoalStatus.Parked, 3 => GoalStatus.Failed,
                5 => GoalStatus.Completed, 6 => GoalStatus.Cancelled, _ => GoalStatus.Superseded };
            if (index >= 5) await fixture.WriteGoalAsync(goals[index] with { Status = status,
                CurrentHold = Hold("steward-owner-question", "excluded question"), GateTestObligations = [Gate(1)] },
                archive: index != 5, compress: compress);
            await fixture.WriteWaitAsync(new("hidden-" + index, goals[index].Id, null, "Nonlive", Now));
            await fixture.Store.RaiseAsync(CollaborationItemType.Decision, goals[index].Id, "Hidden", "Body");
        }
        await fixture.WriteGoalAsync(goals[8] with { Status = GoalStatus.Active, BriefVersions =
            [new(1, goals[8].Objective, Now.AddDays(-1), SupersededByVersion: 2), new(2, goals[8].Objective, Now)] });
        await fixture.WriteWaitAsync(new("stale-only", goals[8].Id, null, "Stale only", Now,
            PlannerIdentity: Identity(goals[8], 1)));
        await fixture.WriteWaitAsync(new("orphan-wait", Id(9999), null, "Orphan", Now));
        await fixture.Store.RaiseAsync(CollaborationItemType.Decision, goals[1].Id, "Live item", "Body");
        await fixture.Store.RaiseAsync(CollaborationItemType.Decision, null, "Global", "Body");
        await fixture.Store.RaiseAsync(CollaborationItemType.Decision, Id(9999), "Orphan", "Body");
        await fixture.WriteGoalAsync(goals[9] with { Status = GoalStatus.Completed, Tasks =
            [goals[9].Tasks[0] with
            {
                LastDispatch = new("claude-cli", "claude", "fixture", Now.AddHours(-2),
                    ProviderName: "Claude", ClaudeCredentialSourceDirectory: "account-a"),
                LastVerification = new("claude", "fixture", 1, "", "You've hit your usage limit", Now.AddHours(-1),
                    StandardErrorPath: "receipt", ProviderFailureKind: ProviderFailureKind.BudgetExhausted)
            }] }, archive: true, compress: compress);
        for (var index = 10; index < goals.Length; index++)
            await fixture.WriteGoalAsync(goals[index] with { Status = GoalStatus.Completed }, archive: true, compress: compress);

        var probe = new QueryProbe(fixture.Queries);
        var expected = await fixture.BaselineAsync();
        foreach (var args in new[] { new[] { "attention", "--json" }, new[] { "attention", "list", "--json" } })
        {
            probe.Loads.Clear();
            var actual = CaptureConsole(() => CliAttentionJsonQueryCommand.Execute(args, probe, fixture.Workspace, new FixedTime()));
            AssertJsonEqual(expected, actual);
            Assert.Equal(new[] { current.Id, goals[1].Id }, Assert.Single(probe.Loads));
        }
        using var document = JsonDocument.Parse(expected);
        var payload = document.RootElement.GetProperty("payload");
        Assert.Single(payload.GetProperty("waits").EnumerateArray());
        Assert.Equal(2, payload.GetProperty("items").GetArrayLength());
        Assert.Equal(3, payload.GetProperty("ownerQuestionHolds").GetArrayLength());
        Assert.Equal(2, payload.GetProperty("ownerQuestionHolds")[1].GetProperty("taskNumber").GetInt32());
        Assert.Equal(4, payload.GetProperty("gateEvidence").GetArrayLength());
        Assert.Single(payload.GetProperty("providerHolds").EnumerateArray());
        Assert.Equal(history + 3, probe.LastProjection!.Reads.ArchivePayloadsRead);
        Assert.Equal(probe.LastProjection.Reads.ArchivePayloadsRead, probe.LastProjection.Reads.ArchivePayloadsDecoded);
        Assert.Equal(0, probe.BoardLoads);
        var fallback = new QueryProbe(fixture.Queries) { Unsupported = true };
        AssertJsonEqual(expected, Render(await AttentionJsonReadContext.ReadAsync(fallback, fixture.Workspace, Now)));
        Assert.Equal(goals.Length, Assert.Single(fallback.Loads).Length);
        foreach (var args in SummaryForms())
        {
            probe.Loads.Clear();
            var summary = CaptureConsole(() => CliAttentionJsonQueryCommand.Execute(args, probe, fixture.Workspace, new FixedTime()));
            AssertSummaryMatches(expected, summary);
            Assert.Empty(probe.LastProjection!.GateEvidence);
            Assert.Equal(new[] { current.Id, goals[1].Id }, Assert.Single(probe.Loads));
            var legacy = new QueryProbe(fixture.Queries) { Unsupported = true };
            AssertJsonEqual(summary, CaptureConsole(() => CliAttentionJsonQueryCommand.Execute(args, legacy, fixture.Workspace, new FixedTime())));
            Assert.Equal(goals.Length, Assert.Single(legacy.Loads).Length);
        }
    }

    [Fact]
    public async Task HistoricalGateGrowth_LeavesSummaryBytesAndActionableRowsUnchanged()
    {
        using var fixture = await Fixture.CreateAsync(4);
        for (var index = 1; index < 4; index++)
            await fixture.WriteGoalAsync(fixture.Goals[index] with { Status = GoalStatus.Completed });
        await fixture.WriteWaitAsync(new("fixed-wait", fixture.Goals[0].Id, null, "Choose?", Now));
        await fixture.Store.RaiseAsync(CollaborationItemType.Decision, fixture.Goals[0].Id, "Fixed item", "Body");
        string Read(string[] args) => CaptureConsole(() =>
            CliAttentionJsonQueryCommand.Execute(args, fixture.Queries, fixture.Workspace, new FixedTime()));
        var before = Read(SummaryForms()[0]);
        AssertSummaryMatches(Read(["attention", "list", "--json"]), before);
        for (var index = 1; index < 4; index++)
            await fixture.WriteGoalAsync(fixture.Goals[index] with
            {
                Status = GoalStatus.Completed,
                GateTestObligations = Enumerable.Range(0, 150)
                    .Select(number => new GateTestObligation("historical-" + number, 1, ["Selector"], null, "Pending", Now)).ToArray()
            }, archive: index > 1, compress: index == 3);
        var full = Read(["attention", "list", "--json"]);
        using var document = JsonDocument.Parse(full);
        Assert.Equal(450, document.RootElement.GetProperty("payload").GetProperty("gateEvidence").GetArrayLength());
        foreach (var args in SummaryForms())
        {
            var after = Read(args);
            Assert.Equal(before, after);
            Assert.Equal(System.Text.Encoding.UTF8.GetByteCount(before), System.Text.Encoding.UTF8.GetByteCount(after));
            AssertSummaryMatches(full, after);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NoWaits_GlobalOrEmpty_LoadsNoSnapshots(bool global)
    {
        using var fixture = await Fixture.CreateAsync(24);
        foreach (var goal in fixture.Goals)
            await fixture.WriteGoalAsync(goal with { Status = GoalStatus.Completed }, archive: true, compress: true);
        if (global) await fixture.Store.RaiseAsync(CollaborationItemType.Decision, null, "Global only", "Body");
        var probe = new QueryProbe(fixture.Queries);
        var actual = await AttentionJsonReadContext.ReadAsync(probe, fixture.Workspace, Now);
        AssertJsonEqual(await fixture.BaselineAsync(), Render(actual));
        Assert.Empty(probe.Loads);
        Assert.Empty(actual.Waits);
        Assert.Equal(global ? 1 : 0, actual.Items.Count);
        Assert.Equal(24, probe.LastProjection!.Reads.ArchivePayloadsRead);
        Assert.Equal(0, probe.LastProjection.Reads.ProviderTasks);
    }

    [Fact]
    public async Task MissingPersistedWait_SelectsItsWaitingTaskOwner_AndMatchesRepair()
    {
        using var fixture = await Fixture.CreateAsync(3);
        var goal = fixture.Goals[0];
        await fixture.WriteGoalAsync(goal with { Status = GoalStatus.WaitingForHuman,
            Tasks = goal.Tasks.Select((task, index) => index == 0 ? task with { Status = WorkTaskStatus.WaitingForHuman } : task).ToArray(),
            Timeline = [new(goal.Id, goal.Tasks[0].Id, ProgressKind.HumanInputRequested, "Recovered question", Now)] });
        var probe = new QueryProbe(fixture.Queries);
        var actual = await AttentionJsonReadContext.ReadAsync(probe, fixture.Workspace, Now);
        AssertJsonEqual(await fixture.BaselineAsync(), Render(actual));
        Assert.Equal("Recovered question", Assert.Single(actual.Waits).Question);
        Assert.Equal(new[] { goal.Id }, Assert.Single(probe.Loads));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderEnvelope_CrossGoalArchivedResetAndProbe_NeedsNoFullLoads(bool compress)
    {
        using var fixture = await Fixture.CreateAsync(4);
        var goals = fixture.Goals;
        var dispatch = new TaskDispatchSnapshot("claude-cli", "claude", "fixture", Now.AddHours(-2),
            ProviderName: "Claude", ClaudeCredentialSourceDirectory: "account-a");
        var verification = new TaskVerificationSnapshot("claude", "fixture", 1, "", "You've hit your usage limit",
            Now.AddHours(-1), StandardErrorPath: "receipt", ProviderFailureKind: ProviderFailureKind.BudgetExhausted);
        await fixture.WriteGoalAsync(goals[0] with { Status = GoalStatus.Completed,
            Tasks = [goals[0].Tasks[0] with { LastDispatch = dispatch, LastVerification = verification }] },
            archive: true, compress: compress);
        var resetAt = Now.AddHours(2);
        const string binding = "claude::account-a";
        await fixture.WriteGoalAsync(goals[1] with { Status = GoalStatus.Cancelled, Timeline =
            [new(goals[1].Id, null, ProgressKind.TaskNote,
                ProviderBudgetHoldSchedules.FormatReset(new(binding, resetAt, "intent")), Now.AddMinutes(-30))] },
            archive: true, compress: !compress);
        await fixture.WriteGoalAsync(goals[2] with { Status = GoalStatus.Active,
            Tasks = [goals[2].Tasks[0] with { Status = WorkTaskStatus.Assigned, AssignedAgentId = "agent" }], Timeline =
            [new(goals[2].Id, goals[2].Tasks[0].Id, ProgressKind.TaskNote,
                ProviderBudgetHoldSchedules.FormatProbe(new(binding, resetAt, 1, "operator")), Now.AddMinutes(-10))] });
        await fixture.Store.RaiseAsync(CollaborationItemType.Decision, null, "Global", "Body");
        var probe = new QueryProbe(fixture.Queries);
        var context = await AttentionJsonReadContext.ReadAsync(probe, fixture.Workspace, Now);
        AssertJsonEqual(await fixture.BaselineAsync(), Render(context));
        Assert.Empty(probe.Loads);
        var hold = Assert.Single(context.ProviderHolds);
        Assert.Equal(goals[0].Id, hold.Hold.SourceGoalId.Value);
        Assert.Equal(resetAt, hold.Schedule!.NextProbeAt);
        Assert.Equal(new GoalId(goals[2].Id), hold.Schedule.AdmittedGoalId);
        Assert.Equal(2, probe.LastProjection!.Reads.ArchivePayloadsRead);
        var fallback = new QueryProbe(fixture.Queries) { Unsupported = true };
        AssertJsonEqual(Render(context), Render(await AttentionJsonReadContext.ReadAsync(fallback, fixture.Workspace, Now)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImplementedProjectionFailure_Propagates_WithoutFallback(bool cancel)
    {
        using var fixture = await Fixture.CreateAsync(2);
        using var cancellation = new CancellationTokenSource();
        if (cancel) cancellation.Cancel();
        var failure = cancel ? (Exception)new OperationCanceledException(cancellation.Token) : new IOException("projection fault");
        var probe = new QueryProbe(fixture.Queries) { Failure = failure };
        var actual = await Assert.ThrowsAnyAsync<Exception>(() =>
            AttentionJsonReadContext.ReadAsync(probe, fixture.Workspace, Now, cancellation.Token));
        Assert.IsType(failure.GetType(), actual);
        Assert.Empty(probe.Loads);
        Assert.Equal(0, probe.MetadataLoads);
        var summaryFailure = await Assert.ThrowsAnyAsync<Exception>(() =>
            AttentionJsonReadContext.ReadAsync(probe, fixture.Workspace, Now, new(false), cancellation.Token));
        Assert.IsType(failure.GetType(), summaryFailure);
        Assert.Empty(probe.Loads);
        Assert.Equal(0, probe.MetadataLoads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationDuringSelectedOrFallbackLoad_Propagates(bool unsupported)
    {
        using var fixture = await Fixture.CreateAsync(3);
        await fixture.WriteWaitAsync(new("wait", fixture.Goals[0].Id, null, "Question", Now));
        using var cancellation = new CancellationTokenSource();
        var probe = new QueryProbe(fixture.Queries) { Unsupported = unsupported, BeforeLoad = cancellation.Cancel };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AttentionJsonReadContext.ReadAsync(probe, fixture.Workspace, Now, cancellation.Token));
        Assert.Single(probe.Loads);
        probe.Loads.Clear();
        using var summaryCancellation = new CancellationTokenSource();
        var summaryProbe = new QueryProbe(fixture.Queries)
            { Unsupported = unsupported, BeforeLoad = summaryCancellation.Cancel };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AttentionJsonReadContext.ReadAsync(
            summaryProbe, fixture.Workspace, Now, new(false), summaryCancellation.Token));
        Assert.Single(summaryProbe.Loads);
    }

    [Fact]
    public async Task ImplementedNotSupportedFailure_IsNotTheUnsupportedCapabilityValue()
    {
        using var fixture = await Fixture.CreateAsync(1);
        var probe = new QueryProbe(fixture.Queries) { Failure = new NotSupportedException("implemented query failed") };
        await Assert.ThrowsAsync<NotSupportedException>(() => AttentionJsonReadContext.ReadAsync(probe, fixture.Workspace, Now));
        Assert.Empty(probe.Loads);
        Assert.Equal(0, probe.MetadataLoads);
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            AttentionJsonReadContext.ReadAsync(probe, fixture.Workspace, Now, new AttentionProjectionReadOptions(false), default));
        Assert.Empty(probe.Loads);
        Assert.Equal(0, probe.MetadataLoads);
    }

    [Fact]
    public async Task EmptySqliteStore_ProducesBaselineEnvelopeWithoutGoalLoads()
    {
        using var fixture = await Fixture.CreateAsync(0);
        var probe = new QueryProbe(fixture.Queries);
        var actual = await AttentionJsonReadContext.ReadAsync(probe, fixture.Workspace, Now);
        AssertJsonEqual(await fixture.BaselineAsync(), Render(actual));
        Assert.Empty(probe.Loads);
        Assert.Equal(new AttentionProjectionReadCounts(0, 0, 0, 0, 0), probe.LastProjection!.Reads);
    }

    private static string[][] SummaryForms() =>
        [["attention", "list", "--summary", "--json"], ["attention", "list", "--json", "--summary"]];

    private static void AssertSummaryMatches(string full, string summary)
    {
        using var ordinary = JsonDocument.Parse(full);
        using var compact = JsonDocument.Parse(summary);
        var envelope = compact.RootElement;
        Assert.Equal(new[] { "schema", "schemaVersion", "generatedAt", "omittedSections", "payload" },
            envelope.EnumerateObject().Select(property => property.Name));
        Assert.Equal("mcg.attention-summary", envelope.GetProperty("schema").GetString());
        Assert.Equal(1, envelope.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(ordinary.RootElement.GetProperty("generatedAt").GetString(), envelope.GetProperty("generatedAt").GetString());
        Assert.Equal("gateEvidence", Assert.Single(envelope.GetProperty("omittedSections").EnumerateArray()).GetString());
        var payload = envelope.GetProperty("payload");
        Assert.Equal(new[] { "waits", "items", "providerHolds", "ownerQuestionHolds" },
            payload.EnumerateObject().Select(property => property.Name));
        foreach (var section in payload.EnumerateObject())
            Assert.Equal(ordinary.RootElement.GetProperty("payload").GetProperty(section.Name).GetRawText(), section.Value.GetRawText());
    }

    private static void AssertJsonEqual(string expected, string actual) =>
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(actual)), "Attention envelopes differ");
    private static string Render(AttentionJsonReadContext context) => AttentionAgentReadView.Render(context.Waits,
        context.Items, new FixedTime(), context.ProviderHolds, gateEvidence: context.GateEvidence, ownerQuestions: context.OwnerQuestions);
    private static string Id(int index) => $"{index:x8}aaaaaaaaaaaaaaaaaaaaaaaa";
    private static PlannerEvidenceIdentity Identity(GoalSnapshot goal, int version) => new(goal.Id, goal.Tasks[0].Id, version, 0, "wait");
    private static GateTestObligation Gate(int version) => new("gate-" + version, version, ["Selector"], null, "Pending", Now);
    private static GoalHoldSnapshot Hold(string state, string blocker) => new("hold-" + state, state, blocker, Now);
    private sealed class FixedTime : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }

    private sealed class QueryProbe(IOrchestratorStateQueries inner) : IOrchestratorStateQueries
    {
        public List<string[]> Loads { get; } = [];
        public int BoardLoads { get; private set; }
        public int MetadataLoads { get; private set; }
        public bool Unsupported { get; init; }
        public Exception? Failure { get; init; }
        public Action? BeforeLoad { get; init; }
        public AttentionReadProjection? LastProjection { get; private set; }
        public async Task<AttentionReadProjection?> ReadAttentionProjectionAsync(CancellationToken cancellationToken = default)
            => await ReadAttentionProjectionAsync(new AttentionProjectionReadOptions(), cancellationToken);
        public async Task<AttentionReadProjection?> ReadAttentionProjectionAsync(AttentionProjectionReadOptions options,
            CancellationToken cancellationToken)
        {
            if (Failure is not null) throw Failure;
            return LastProjection = Unsupported ? null : await inner.ReadAttentionProjectionAsync(options, cancellationToken);
        }
        public Task<IReadOnlyList<GoalSummary>> ListGoalMetadataAsync(CancellationToken cancellationToken = default)
        { MetadataLoads++; return inner.ListGoalMetadataAsync(cancellationToken); }
        public Task<IReadOnlyList<HumanInputRequestSnapshot>> ListOpenHumanInputRequestsAsync(CancellationToken cancellationToken = default) =>
            inner.ListOpenHumanInputRequestsAsync(cancellationToken);
        public Task<AgentOrchestratorKernel> LoadGoalsAsync(IReadOnlyCollection<GoalId> goalIds, CancellationToken cancellationToken = default)
        { Loads.Add(goalIds.Select(id => id.Value).ToArray()); BeforeLoad?.Invoke(); return inner.LoadGoalsAsync(goalIds, cancellationToken); }
        public Task<AgentOrchestratorKernel> LoadBoardSnapshotAsync(CancellationToken cancellationToken = default)
        { BoardLoads++; throw new InvalidOperationException("Whole board must not load"); }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = CreateTempDirectory();
        public OrchestratorWorkspace Workspace { get; }
        public GoalSnapshot[] Goals { get; private set; } = [];
        public IOrchestratorStateQueries Queries => SqliteOrchestratorStateRepository.OpenReadOnly(Workspace.SqliteStatePath);
        public CollaborationItemStore Store { get; }
        private Fixture()
        {
            Workspace = OrchestratorWorkspace.ForDirectory(_root);
            StateDbMigrations.EnsureUpToDate(Workspace.SqliteStatePath);
            Store = CollaborationItemStore.ForDirectory(Workspace.OrchestratorDirectory);
        }
        public static async Task<Fixture> CreateAsync(int count)
        {
            var fixture = new Fixture();
            var kernel = new AgentOrchestratorKernel();
            for (var index = 0; index < count; index++) kernel.CreateGoal(new GoalId(Id(index + 1)), "Goal " + index);
            fixture.Goals = kernel.ExportSnapshot().Goals.ToArray();
            await new SqliteOrchestratorStateRepository(fixture.Workspace.SqliteStatePath).SaveAsync(kernel);
            return fixture;
        }
        public async Task WriteGoalAsync(GoalSnapshot snapshot, bool archive = false, bool compress = false)
        {
            await using var connection = new SqliteConnection($"Data Source={Workspace.SqliteStatePath};Pooling=False");
            await connection.OpenAsync();
            if (archive)
            {
                await using var insert = connection.CreateCommand();
                insert.CommandText = "INSERT INTO goal_snapshot_archive (goal_id, snapshot_json, archived_at) VALUES ($id, $json, $at)";
                insert.Parameters.AddWithValue("$id", snapshot.Id);
                var json = JsonSerializer.Serialize(snapshot, Options);
                insert.Parameters.AddWithValue("$json", compress ? (object)GoalSnapshotArchiveCodec.Encode(json) : json);
                insert.Parameters.AddWithValue("$at", Now.ToString("O"));
                Assert.Equal(1, await insert.ExecuteNonQueryAsync());
            }
            await using var update = connection.CreateCommand();
            update.CommandText = "UPDATE goals SET status = $status, snapshot_json = $json WHERE id = $id";
            update.Parameters.AddWithValue("$id", snapshot.Id);
            update.Parameters.AddWithValue("$status", snapshot.Status.ToString());
            update.Parameters.AddWithValue("$json", JsonSerializer.Serialize(archive ? snapshot with
                { IsMetadataOnly = true, Tasks = [], Timeline = [], GateTestObligations = null, CurrentHold = null, BriefVersions = null } : snapshot, Options));
            Assert.Equal(1, await update.ExecuteNonQueryAsync());
        }
        public async Task WriteWaitAsync(HumanInputRequestSnapshot wait)
        {
            await using var connection = new SqliteConnection($"Data Source={Workspace.SqliteStatePath};Pooling=False");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO human_input_requests (id, goal_id, snapshot_json) VALUES ($id, $goal, $json)";
            command.Parameters.AddWithValue("$id", wait.Id);
            command.Parameters.AddWithValue("$goal", wait.GoalId);
            command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(wait, Options));
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }
        public async Task<string> BaselineAsync()
        {
            var metadata = await Queries.ListGoalMetadataAsync();
            var kernel = await Queries.LoadGoalsAsync(metadata.Select(goal => new GoalId(goal.Id)).ToArray());
            var items = await Store.GetAttentionQueueAsync();
            return AttentionAgentReadView.Render(CliCommandHandlers.HumanWaitsForAttention(kernel, null, false),
                CliCommandHandlers.CollaborationItemsForAttention(items, kernel, null, false), new FixedTime(),
                ProviderUsageHoldView.Read(kernel.Goals, Now), kernel.Goals);
        }
        public void Dispose() => Directory.Delete(_root, true);
    }
}
