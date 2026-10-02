using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: every store and log lives under this test's unique temporary root.
public sealed class ConductorAuthorHostRetryTests
{
    private const string Answer = """
        {"kind":"answer","text":"Use Windows","evidenceReferences":["src/Runtime.cs:12"],"precedent":"runtime"}
        """;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-02T17:00:00Z");

    [Xunit.Fact]
    public async Task Timed_out_round_is_retried_once_and_answer_is_submitted()
    {
        using var harness = new Harness();
        var goal = harness.Kernel.CreateGoal("Choose runtime");
        var item = await Await(harness.Raise(goal), "clarification raised");
        var model = new TimeoutThenAnswerModel();
        var host = harness.NewHost(model);
        host.ServiceTick(harness.Kernel);
        await Await(model.Started.Task, "first Author dispatch started");
        var round = Xunit.Assert.Single(host.CurrentRounds);
        model.Output.SetCanceled();
        await Xunit.Assert.ThrowsAnyAsync<OperationCanceledException>(() => Await(round, "first Author cancellation completed"));

        host.ServiceTick(harness.Kernel);
        await Await(Task.WhenAll(host.CurrentRounds), "retry Author round completed");
        host.ServiceTick(harness.Kernel);

        Xunit.Assert.Equal(2, model.Calls);
        var intent = Xunit.Assert.Single(await Await(harness.Intents.ListForGoalAsync(goal.Id.Value), "answer intents read"));
        Xunit.Assert.Equal(OperatorIntentVerbs.Answer, intent.Verb);
        var payload = JsonSerializer.Deserialize<AnswerOperatorIntentPayload>(intent.PayloadJson, Json);
        Xunit.Assert.Equal(OperatorAnswerTargetKind.Clarification, payload?.TargetKind);
        Xunit.Assert.Equal(item.Id, payload?.TargetId);
        var log = File.ReadAllText(harness.ConductPath);
        var retry = log.IndexOf("kind=retry-scheduled reason=timeout attempt=1/2", StringComparison.Ordinal);
        var answer = log.IndexOf("kind=answer", StringComparison.Ordinal);
        Xunit.Assert.True(retry >= 0, "The first timeout must record its retry-scheduled outcome.");
        Xunit.Assert.True(answer > retry, "The retry-scheduled outcome must precede the answer.");
    }

    [Xunit.Fact]
    public async Task Faulted_round_is_retried_once_then_left_for_operator()
    {
        using var harness = new Harness();
        var goal = harness.Kernel.CreateGoal("Choose runtime");
        await Await(harness.Raise(goal), "clarification raised");
        var model = new FaultModel();
        var host = harness.NewHost(model);
        // Each drain finishes the tracked fault before the next tick; three ticks harvest both attempts.
        for (var tick = 0; tick < 3; tick++)
        {
            host.ServiceTick(harness.Kernel);
            foreach (var round in host.CurrentRounds)
                await Xunit.Assert.ThrowsAsync<InvalidOperationException>(() => Await(round, "faulted Author round completed"));
        }
        for (var tick = 0; tick < 3; tick++) host.ServiceTick(harness.Kernel);

        Xunit.Assert.Equal(2, model.Calls);
        Xunit.Assert.Empty(host.CurrentRounds);
        var log = File.ReadAllText(harness.ConductPath);
        Xunit.Assert.Contains("kind=retry-scheduled reason=InvalidOperationException attempt=1/2", log);
        Xunit.Assert.Contains("kind=model-failure reason=InvalidOperationException attempt=2/2", log);
        Xunit.Assert.Empty(await Await(harness.Intents.ListForGoalAsync(goal.Id.Value), "answer intents read"));
        Xunit.Assert.Equal(CollaborationItemStatus.Raised,
            Xunit.Assert.Single(await Await(harness.Collaboration.ListAsync(goal.Id.Value), "open clarification read")).Status);
    }

    [Xunit.Fact]
    public async Task Queued_human_answer_after_retry_is_scheduled_prevents_redispatch()
    {
        using var harness = new Harness();
        var goal = harness.Kernel.CreateGoal("Choose runtime");
        var item = await Await(harness.Raise(goal), "clarification raised");
        var model = new TimeoutThenAnswerModel();
        Action? duringWalk = null;
        var host = harness.NewHost(model, () =>
        {
            duringWalk?.Invoke();
            return Now;
        });
        host.ServiceTick(harness.Kernel);
        await Await(model.Started.Task, "first Author dispatch started");
        var round = Xunit.Assert.Single(host.CurrentRounds);
        // Complete inside the walk, after the leading Harvest: only the trailing Harvest schedules retry.
        duringWalk = () =>
        {
            duringWalk = null;
            model.Output.SetCanceled();
            Xunit.Assert.ThrowsAny<OperationCanceledException>(() =>
                Await(round, "Author cancellation inside the walk completed").GetAwaiter().GetResult());
        };
        host.ServiceTick(harness.Kernel);
        Xunit.Assert.Empty(host.CurrentRounds);
        Xunit.Assert.Contains("kind=retry-scheduled reason=timeout attempt=1/2", File.ReadAllText(harness.ConductPath));

        var payload = new AnswerOperatorIntentPayload(OperatorAnswerTargetKind.Clarification,
            item.Id, goal.Id.Value, "Use Linux", OperatorActorKind.Human);
        var intent = await Await(harness.Intents.EnqueueAsync(new OperatorIntentRecord(Guid.NewGuid().ToString("N"),
            Guid.NewGuid().ToString("N"), OperatorIntentVerbs.Answer, goal.Id.Value, null,
            JsonSerializer.Serialize(payload, Json), [], "operator", "cli", "local-process", Now,
            ActorKind: OperatorActorKind.Human)), "human answer queued");
        Xunit.Assert.Equal(OperatorActorKind.Human, intent.ActorKind);
        Xunit.Assert.Equal(OperatorIntentStatus.Pending, intent.Status);
        host.ServiceTick(harness.Kernel);
        host.ServiceTick(harness.Kernel);

        Xunit.Assert.Equal(1, model.Calls);
        Xunit.Assert.Empty(host.CurrentRounds);
    }

    [Xunit.Fact]
    public async Task Legacy_model_failure_row_without_attempt_column_is_never_redispatched()
    {
        using var harness = new Harness();
        var goal = harness.Kernel.CreateGoal("Choose runtime");
        var item = await Await(harness.Raise(goal), "clarification raised");
        var detected = Xunit.Assert.Single(ConductorAuthorItems.Detect(goal, [item], [], []));
        using (var connection = harness.OpenClaims())
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE author_claims (
                    identity TEXT PRIMARY KEY, claimed_at TEXT NOT NULL, outcome TEXT NOT NULL,
                    intent_id TEXT NULL, item_json TEXT NOT NULL, output TEXT NULL
                );
                INSERT INTO author_claims (identity, claimed_at, outcome, item_json)
                VALUES ($identity, $now, 'model-failure', $item);
                """;
            command.Parameters.AddWithValue("$identity", detected.Identity);
            command.Parameters.AddWithValue("$now", Now.ToString("O"));
            command.Parameters.AddWithValue("$item", JsonSerializer.Serialize(detected));
            command.ExecuteNonQuery();
        }
        var model = new TimeoutThenAnswerModel();
        var host = harness.NewHost(model);
        var exception = Xunit.Record.Exception(() => host.ServiceTick(harness.Kernel));

        Xunit.Assert.Null(exception);
        Xunit.Assert.Equal(0, model.Calls);
        Xunit.Assert.Empty(host.CurrentRounds);
        using var migrated = harness.OpenClaims();
        using var query = migrated.CreateCommand();
        query.CommandText = "SELECT outcome, attempt FROM author_claims WHERE identity = $identity";
        query.Parameters.AddWithValue("$identity", detected.Identity);
        using var reader = query.ExecuteReader();
        Xunit.Assert.True(reader.Read());
        Xunit.Assert.Equal("model-failure", reader.GetString(0));
        Xunit.Assert.Equal(1, reader.GetInt32(1));
    }

    [Xunit.Fact]
    public void Retryable_claim_refreshes_item_and_timestamp_and_persists_attempt()
    {
        using var harness = new Harness();
        var item = new ConductorAuthorItem(OperatorAnswerTargetKind.Clarification, "item", "goal", "Old question", "runtime");
        var store = new ConductorAuthorClaimStore(harness.ClaimsPath);
        Xunit.Assert.True(store.TryClaim(item, Now));
        Xunit.Assert.False(store.TryClaim(item, Now));
        store.Complete(item.Identity, "retryable");
        var successor = new ConductorAuthorClaimStore(harness.ClaimsPath);
        var refreshed = item with { Question = "Updated question" };
        var claimedAt = Now.AddMinutes(1);
        Xunit.Assert.True(successor.TryClaim(refreshed, claimedAt));
        Xunit.Assert.False(store.TryClaim(item, Now));

        using var connection = harness.OpenClaims();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT outcome, claimed_at, item_json, attempt FROM author_claims WHERE identity = $identity";
        command.Parameters.AddWithValue("$identity", item.Identity);
        using var reader = command.ExecuteReader();
        Xunit.Assert.True(reader.Read());
        Xunit.Assert.Equal("in-flight", reader.GetString(0));
        Xunit.Assert.Equal(claimedAt.ToString("O"), reader.GetString(1));
        Xunit.Assert.Equal(refreshed, JsonSerializer.Deserialize<ConductorAuthorItem>(reader.GetString(2)));
        Xunit.Assert.Equal(2, reader.GetInt32(3));
    }

    private static async Task<T> Await<T>(Task<T> task, string eventName)
    {
        try { return await task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false); }
        catch (TimeoutException ex) { throw new TimeoutException($"Hang waiting for {eventName}.", ex); }
    }

    private sealed class TimeoutThenAnswerModel : IConductorAuthorModelRound
    {
        private int _calls;
        internal int Calls => Volatile.Read(ref _calls);
        internal TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<string> Output { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<string> DispatchAsync(ConductorAuthorRoundInput input, string workingDirectory,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) != 1) return Answer;
            Started.TrySetResult(true);
            return await Output.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class FaultModel : IConductorAuthorModelRound
    {
        private int _calls;
        internal int Calls => Volatile.Read(ref _calls);
        public Task<string> DispatchAsync(ConductorAuthorRoundInput input, string workingDirectory,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            throw new InvalidOperationException("model failed independently");
        }
    }

    private sealed class Harness : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"author-retry-{Guid.NewGuid():N}");
        private readonly OrchestratorWorkspace _workspace;
        private readonly List<ConductorAuthorHost> _hosts = [];
        internal AgentOrchestratorKernel Kernel { get; } = new();
        internal CollaborationItemStore Collaboration { get; }
        internal SqliteOperatorIntentStore Intents { get; }
        internal string ConductPath => _workspace.ConductEventsLogPath;
        internal string ClaimsPath => Path.Combine(_workspace.OrchestratorDirectory, "author-claims.db");

        internal Harness()
        {
            Directory.CreateDirectory(_root);
            _workspace = OrchestratorWorkspace.ForDirectory(_root);
            Collaboration = CollaborationItemStore.ForDirectory(_workspace.OrchestratorDirectory);
            Intents = SqliteOperatorIntentStore.ForDirectories(_workspace.OrchestratorDirectory, _workspace.LogDirectory);
        }

        internal ConductorAuthorHost NewHost(IConductorAuthorModelRound model, Func<DateTimeOffset>? utcNow = null)
        {
            var host = new ConductorAuthorHost(new ConductorAuthorClaimStore(ClaimsPath),
                Collaboration, model, Intents, new SpecRefinerPrecedentStore(_workspace.SpecRefinerPrecedentsPath),
                _ => _root, new GoalLifecycleEventWriter(_workspace.GoalLifecycleEventsDirectory),
                new ConductEventLogWriter(ConductPath), utcNow: utcNow ?? (() => Now));
            _hosts.Add(host);
            return host;
        }

        internal SqliteConnection OpenClaims()
        {
            Directory.CreateDirectory(_workspace.OrchestratorDirectory);
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = ClaimsPath, Pooling = false }.ToString());
            connection.Open();
            return connection;
        }

        internal Task<CollaborationItem> Raise(Goal goal) => Collaboration.RaiseAsync(
            CollaborationItemType.Clarification, goal.Id.Value, "Runtime",
            "Question: Which runtime?\nFork kind: runtime", $"spec-clarification:{goal.Id.Value}:runtime");

        public void Dispose()
        {
            foreach (var host in _hosts) host.Stop();
            Directory.Delete(_root, recursive: true);
        }
    }
}
