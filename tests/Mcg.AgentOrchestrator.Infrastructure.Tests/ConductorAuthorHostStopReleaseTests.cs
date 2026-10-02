using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Every store and log is isolated under this test's unique temporary root.
public sealed class ConductorAuthorHostStopReleaseTests
{
    private const string Answer = """
        {"kind":"answer","text":"Use Windows","evidenceReferences":["src/Runtime.cs:12"],"precedent":"runtime"}
        """;

    [Xunit.Fact]
    public async Task Stop_interrupted_round_is_released_and_successor_answers_it()
    {
        using var harness = new Harness();
        var goal = harness.Kernel.CreateGoal("Choose runtime");
        var item = await Await(harness.Raise(goal), "clarification raised");
        var delayed = new HeldModel();
        var first = harness.NewHost(delayed);
        first.ServiceTick(harness.Kernel);
        await Await(delayed.Started.Task, "first Author dispatch started");

        first.Stop();

        Xunit.Assert.Empty(first.CurrentRounds);
        var log = File.ReadAllText(harness.ConductPath);
        Xunit.Assert.Contains("kind=released reason=conductor-stop", log);
        Xunit.Assert.DoesNotContain("kind=model-failure", log);
        var fresh = new AnswerModel();
        var successor = harness.NewHost(fresh);
        successor.ServiceTick(harness.Kernel);
        await Await(fresh.Started.Task, "successor Author dispatch started");
        await Await(Task.WhenAll(successor.CurrentRounds), "successor Author round completed");
        successor.ServiceTick(harness.Kernel);

        Xunit.Assert.Equal(1, fresh.Calls);
        var intent = Xunit.Assert.Single(await Await(harness.Intents.ListForGoalAsync(goal.Id.Value),
            "successor answer intents read"));
        Xunit.Assert.Equal(OperatorIntentVerbs.Answer, intent.Verb);
        var payload = System.Text.Json.JsonSerializer.Deserialize<AnswerOperatorIntentPayload>(intent.PayloadJson,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Xunit.Assert.Equal(item.Id, payload?.TargetId);

        // A late result has no tracked round in the stopped host and cannot consume the new claim.
        delayed.Output.TrySetResult(Answer);
        first.Stop();
        successor.Stop();
        Xunit.Assert.Single(await Await(harness.Intents.ListForGoalAsync(goal.Id.Value), "final answer intents read"));
    }

    [Xunit.Fact]
    public async Task Unparseable_round_is_consumed_and_successor_does_not_dispatch()
    {
        using var harness = new Harness();
        var goal = harness.Kernel.CreateGoal("Choose runtime");
        await Await(harness.Raise(goal), "clarification raised");
        var model = new HeldModel();
        var first = harness.NewHost(model);
        first.ServiceTick(harness.Kernel);
        await Await(model.Started.Task, "unparseable Author dispatch started");
        model.Output.SetResult("not a JSON result");
        await Await(Task.WhenAll(first.CurrentRounds), "unparseable Author round completed");
        first.ServiceTick(harness.Kernel);
        first.Stop();

        var fresh = new AnswerModel();
        var successor = harness.NewHost(fresh);
        successor.ServiceTick(harness.Kernel);
        Xunit.Assert.Equal(0, fresh.Calls);
        Xunit.Assert.Empty(successor.CurrentRounds);
        Xunit.Assert.Empty(await Await(harness.Intents.ListForGoalAsync(goal.Id.Value), "answer intents read"));
        Xunit.Assert.Equal(CollaborationItemStatus.Raised,
            Xunit.Assert.Single(await Await(harness.Collaboration.ListAsync(goal.Id.Value), "open item read")).Status);
        Xunit.Assert.Contains("kind=model-failure reason=unparseable-output", File.ReadAllText(harness.ConductPath));
        successor.Stop();
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task Failure_completed_before_stop_is_consumed(bool canceled)
    {
        using var harness = new Harness();
        var goal = harness.Kernel.CreateGoal("Choose runtime");
        await Await(harness.Raise(goal), "clarification raised");
        var model = new HeldModel();
        var first = harness.NewHost(model);
        first.ServiceTick(harness.Kernel);
        await Await(model.Started.Task, "failing Author dispatch started");
        var round = Xunit.Assert.Single(first.CurrentRounds);
        if (canceled) model.Output.SetCanceled(new CancellationToken(canceled: true));
        else model.Output.SetException(new InvalidOperationException("model failed independently"));
        if (canceled)
            await Xunit.Assert.ThrowsAnyAsync<OperationCanceledException>(() => Await(round, "Author cancellation completed"));
        else
            await Xunit.Assert.ThrowsAsync<InvalidOperationException>(() => Await(round, "Author failure completed"));
        first.Stop();

        var fresh = new AnswerModel();
        var successor = harness.NewHost(fresh);
        successor.ServiceTick(harness.Kernel);
        Xunit.Assert.Equal(0, fresh.Calls);
        Xunit.Assert.Empty(successor.CurrentRounds);
        Xunit.Assert.Empty(await Await(harness.Intents.ListForGoalAsync(goal.Id.Value), "answer intents read"));
        var log = File.ReadAllText(harness.ConductPath);
        Xunit.Assert.Contains("kind=model-failure reason=conductor-stop", log);
        Xunit.Assert.DoesNotContain("kind=released", log);
        successor.Stop();
    }

    [Xunit.Fact]
    public async Task Independent_fault_during_stop_is_consumed()
    {
        using var harness = new Harness();
        var goal = harness.Kernel.CreateGoal("Choose runtime");
        await Await(harness.Raise(goal), "clarification raised");
        var model = new HeldModel { FailOnStop = true };
        var first = harness.NewHost(model);
        first.ServiceTick(harness.Kernel);
        await Await(model.Started.Task, "Author dispatch started before stop fault");
        first.Stop();

        var fresh = new AnswerModel();
        var successor = harness.NewHost(fresh);
        successor.ServiceTick(harness.Kernel);
        Xunit.Assert.Equal(0, fresh.Calls);
        Xunit.Assert.Empty(successor.CurrentRounds);
        Xunit.Assert.Empty(await Await(harness.Intents.ListForGoalAsync(goal.Id.Value), "answer intents read"));
        var log = File.ReadAllText(harness.ConductPath);
        Xunit.Assert.Contains("kind=model-failure reason=conductor-stop", log);
        Xunit.Assert.DoesNotContain("kind=released", log);
        successor.Stop();
    }

    private static async Task<T> Await<T>(Task<T> task, string eventName)
    {
        try { return await task.WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (TimeoutException ex) { throw new TimeoutException($"Hang waiting for {eventName}.", ex); }
    }

    private sealed class HeldModel : IConductorAuthorModelRound
    {
        internal bool FailOnStop { get; init; }
        internal TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<string> Output { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<string> DispatchAsync(ConductorAuthorRoundInput input, string workingDirectory,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult(true);
            try { return await Output.Task.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) when (FailOnStop && cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException("model failed independently during drain");
            }
        }
    }

    private sealed class AnswerModel : IConductorAuthorModelRound
    {
        private int _calls;
        internal int Calls => Volatile.Read(ref _calls);
        internal TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<string> DispatchAsync(ConductorAuthorRoundInput input, string workingDirectory,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            Started.TrySetResult(true);
            return Task.FromResult(Answer);
        }
    }

    private sealed class Harness : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"author-stop-{Guid.NewGuid():N}");
        private readonly OrchestratorWorkspace _workspace;
        private readonly List<ConductorAuthorHost> _hosts = [];
        internal AgentOrchestratorKernel Kernel { get; } = new();
        internal CollaborationItemStore Collaboration { get; }
        internal SqliteOperatorIntentStore Intents { get; }
        internal string ConductPath => _workspace.ConductEventsLogPath;

        internal Harness()
        {
            Directory.CreateDirectory(_root);
            _workspace = OrchestratorWorkspace.ForDirectory(_root);
            Collaboration = CollaborationItemStore.ForDirectory(_workspace.OrchestratorDirectory);
            Intents = SqliteOperatorIntentStore.ForDirectories(_workspace.OrchestratorDirectory, _workspace.LogDirectory);
        }

        internal ConductorAuthorHost NewHost(IConductorAuthorModelRound model)
        {
            var host = new ConductorAuthorHost(
                new ConductorAuthorClaimStore(Path.Combine(_workspace.OrchestratorDirectory, "author-claims.db")),
                Collaboration, model, Intents, new SpecRefinerPrecedentStore(_workspace.SpecRefinerPrecedentsPath),
                _ => _root, new GoalLifecycleEventWriter(_workspace.GoalLifecycleEventsDirectory),
                new ConductEventLogWriter(ConductPath));
            _hosts.Add(host);
            return host;
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
