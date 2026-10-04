using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

internal sealed class OwnerDigestJudgePanelFixture : IDisposable
{
    internal const string GoalE = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
    internal const string GoalF = "ffffffffffffffffffffffffffffffff";
    internal const string GoalG = "gggggggggggggggggggggggggggggggg";
    internal static DateTimeOffset Start => OwnerDigestTestFixture.Start;
    internal OrchestratorWorkspace Workspace => _base.Workspace;
    internal string PanelPath => CliOwnerDigestJudgePanel.StorePath(Workspace);
    internal string IntentsPath => Path.Combine(Workspace.OrchestratorDirectory, SqliteOperatorIntentStore.DatabaseFileName);
    private readonly OwnerDigestTestFixture _base;
    private int _cursor;

    private OwnerDigestJudgePanelFixture(OwnerDigestTestFixture fixture) => _base = fixture;

    internal static async Task<OwnerDigestJudgePanelFixture> CreateAsync()
    {
        var fixture = new OwnerDigestJudgePanelFixture(await OwnerDigestTestFixture.CreateAsync());
        File.Delete(fixture.IntentsPath); // The shared fixture intentionally uses a plain text sentinel.
        _ = new SqliteOperatorIntentStore(fixture.IntentsPath, fixture.Workspace.OrchestratorDirectory);
        return fixture;
    }

    internal async Task AddGoalAsync(string id, ProgressEventSnapshot[]? events = null, double[]? dispatchHours = null)
    {
        var task = new TaskSnapshot("developer", "Develop", AgentRole.Developer, WorkTaskStatus.Completed,
            null, null, null, [], null, null, DispatchHistory: (dispatchHours ?? []).Select(hour =>
                new TaskDispatchSnapshot("worker", "command", "root", Start.AddHours(hour), "p", "m")).ToArray());
        await new SqliteOrchestratorStateRepository(Workspace.SqliteStatePath).SaveGoalSnapshotsAsync(
            [new GoalSnapshot(id, "Panel digest", GoalStatus.Active, [task], events ?? [])]);
    }

    internal static ProgressEventSnapshot Retry(string goal, double hour) =>
        new(goal, "developer", ProgressKind.TaskRetried, "retry receipt", Start.AddHours(hour));

    internal sealed record Call(string Judge, string? Action = "retry", PanelJudgeOutcome Outcome = PanelJudgeOutcome.Valid,
        string Owner = "Developer", bool Launched = true, string? AnswerOverride = null);

    internal string AddCase(string goal, double hour, params Call[] calls) => AddCase(goal, hour, true, calls);

    internal string AddCase(string goal, double hour, bool recordTrigger, params Call[] calls)
    {
        var cursor = ++_cursor;
        var trigger = $"goal-event:{goal}:{cursor}";
        if (recordTrigger)
        {
            Directory.CreateDirectory(Workspace.GoalLifecycleEventsDirectory);
            File.AppendAllText(Path.Combine(Workspace.GoalLifecycleEventsDirectory, "panel-fixtures.jsonl"),
                JsonSerializer.Serialize(new
                {
                    eventType = "GoalEscalated", goalId = goal, cursor, timestamp = Start.AddHours(hour),
                    reason = "PRE_REVIEW_RED_UNCHANGED_CANDIDATE: panel fixture"
                }) + "\n");
        }
        var store = new ConductorJudgePanelCaseStore(PanelPath);
        store.Start(Start.AddHours(-1), [new PanelGoalEnrollment(goal, Start)]);
        var queued = store.Enqueue(new(goal, "candidate", "base", "criteria", trigger, "PreReviewEvidence", "packet"));
        Assert.Equal("pending", queued.Status);
        var claim = store.ClaimNext(Start.AddHours(hour));
        Assert.NotNull(claim);
        Assert.Equal(queued.Id, claim.Id);
        foreach (var call in calls)
        {
            var answer = call.AnswerOverride ?? (call.Action is null ? null : JsonSerializer.Serialize(new
            {
                schema = "panel-v0", case_id = claim.Id, assessment = "supported",
                next_action = new { kind = call.Action, owner = call.Owner, detail = "fixture recommendation" },
                discriminating_observation = "fixture", missing_evidence = Array.Empty<string>()
            }));
            var result = new PanelJudgeResult(call.Judge, call.Outcome, 0, answer ?? "", "", Answer: answer);
            if (call.Launched)
            {
                Assert.True(store.ReserveCall(claim, call.Judge));
                store.RecordResult(claim, result);
            }
            else Assert.False(store.ReserveCall(claim, call.Judge, result));
        }
        store.Complete(claim, PanelCaseTerminal.Completed, null);
        return claim.Id;
    }

    internal void Intent(string goal, string verb, double hour, string? shape = null,
        string? task = "developer", string status = "Applied")
    {
        using var connection = new SqliteConnection($"Data Source={IntentsPath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO operator_intents
                (id, idempotency_key, verb, goal_id, task_id, payload_json, payload_file_references_json,
                 actor, channel, authentication_assurance, created_at, status, completed_at, actor_kind)
            VALUES ($id, $id, $verb, $goal, $task, $payload, '[]', 'operator', 'cli', 'local', $at, $status, $at, 'Human')
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$verb", verb);
        command.Parameters.AddWithValue("$goal", goal);
        command.Parameters.AddWithValue("$task", (object?)task ?? DBNull.Value);
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(new { shape }));
        command.Parameters.AddWithValue("$at", Start.AddHours(hour).ToString("O"));
        command.Parameters.AddWithValue("$status", status);
        command.ExecuteNonQuery();
    }

    internal void Land(string goal, double hour)
    {
        Directory.CreateDirectory(Workspace.GoalLifecycleEventsDirectory);
        File.AppendAllText(Path.Combine(Workspace.GoalLifecycleEventsDirectory, "panel-fixtures.jsonl"),
            JsonSerializer.Serialize(new { eventType = "GoalLanded", goalId = goal, timestamp = Start.AddHours(hour), mainSha = "sha" }) + "\n");
    }

    internal void CreateEmptyPanelStore() => new ConductorJudgePanelCaseStore(PanelPath).Start(Start.AddHours(-1), []);

    internal string Run(bool json = false, bool rounds = false)
    {
        var args = new List<string> { "owner-digest", "--since", Start.ToString("O"), "--until", OwnerDigestTestFixture.End.ToString("O") };
        if (json) args.Add("--json");
        if (rounds) args.Add("--rounds");
        using var writer = new StringWriter();
        Assert.Equal(0, CliOwnerDigestCommand.Run(args, Workspace, _base.Clock, writer));
        return writer.ToString();
    }

    internal static string CaseLine(string output, string goal) => output.Split(Environment.NewLine)
        .Single(line => line.StartsWith(goal[..8] + " | PreReviewEvidence |", StringComparison.Ordinal));

    public void Dispose() => _base.Dispose();
}
