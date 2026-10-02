using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsStewardStopRelease
{
    [Xunit.Fact]
    public async Task Stop_interrupted_round_is_released_and_successor_services_it()
    {
        using var harness = new StewardHarness("B");
        StewardHarvestFixture.SeedPlannerFailure(harness);
        var model = new HeldModel();
        var first = CreateHost(harness, model);
        var trigger = Xunit.Assert.Single(new ConductorStewardTriggerDetector().Detect(harness.Goal));
        var store = new ConductorStewardTriggerStore(Path.Combine(harness.Root, "steward-triggers.db"));
        first.ServiceTick(harness.Kernel);
        await Await(model.Started.Task, "first Steward dispatch started");
        first.Stop();

        Xunit.Assert.Null(first.CurrentRound);
        Xunit.Assert.Equal("pending", store.Observe(trigger).Status);
        var log = File.ReadAllText(harness.ConductPath);
        Xunit.Assert.Contains("kind=released", log);
        Xunit.Assert.Contains("reason=conductor-stop", log);
        Xunit.Assert.DoesNotContain("kind=model-failure", log);

        harness.Model.Reply(StewardHarvestFixture.Route(harness));
        var successor = StewardHarvestFixture.CreateHost(harness, _ => 7);
        successor.ServiceTick(harness.Kernel);
        await Await(harness.Model.Started.Task, "successor Steward dispatch started");
        await Await(successor.CurrentRound!, "successor Steward round completed");
        successor.ServiceTick(harness.Kernel);

        Xunit.Assert.Equal(1, harness.Model.Calls);
        var intent = Xunit.Assert.Single(await Await(harness.Intents.ListForGoalAsync(harness.Goal.Id.Value),
            "successor adjudication intents read"));
        Xunit.Assert.Equal(OperatorIntentVerbs.Adjudicate, intent.Verb);
        var serviced = store.Observe(trigger);
        Xunit.Assert.Equal("serviced", serviced.Status);
        Xunit.Assert.Equal("route-submitted", serviced.Outcome);
        Xunit.Assert.Equal(intent.Id, serviced.IntentId);
        model.Output.TrySetResult(StewardHarvestFixture.Route(harness));
        first.Stop();
        successor.Stop();
        Xunit.Assert.Single(await Await(harness.Intents.ListForGoalAsync(harness.Goal.Id.Value), "final intents read"));
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task Failure_completed_before_stop_is_consumed(bool canceled)
    {
        using var harness = new StewardHarness("B");
        StewardHarvestFixture.SeedPlannerFailure(harness);
        var model = new HeldModel();
        var first = CreateHost(harness, model);
        first.ServiceTick(harness.Kernel);
        await Await(model.Started.Task, "failing Steward dispatch started");
        var round = first.CurrentRound!;
        if (canceled) model.Output.SetCanceled(new CancellationToken(canceled: true));
        else model.Output.SetException(new InvalidOperationException("model failed independently"));
        if (canceled)
            await Xunit.Assert.ThrowsAnyAsync<OperationCanceledException>(() => Await(round, "Steward cancellation completed"));
        else
            await Xunit.Assert.ThrowsAsync<InvalidOperationException>(() => Await(round, "Steward failure completed"));
        first.Stop();

        var successor = StewardHarvestFixture.CreateHost(harness, _ => 7);
        successor.ServiceTick(harness.Kernel);
        Xunit.Assert.Equal(0, harness.Model.Calls);
        Xunit.Assert.Null(successor.CurrentRound);
        Xunit.Assert.Empty(await Await(harness.Intents.ListForGoalAsync(harness.Goal.Id.Value), "adjudication intents read"));
        var log = File.ReadAllText(harness.ConductPath);
        Xunit.Assert.Contains("kind=model-failure", log);
        Xunit.Assert.DoesNotContain("kind=released", log);
        successor.Stop();
    }

    [Xunit.Fact]
    public async Task Independent_fault_during_stop_is_consumed()
    {
        using var harness = new StewardHarness("B");
        StewardHarvestFixture.SeedPlannerFailure(harness);
        var model = new HeldModel { FailOnStop = true };
        var first = CreateHost(harness, model);
        first.ServiceTick(harness.Kernel);
        await Await(model.Started.Task, "Steward dispatch started before stop fault");
        first.Stop();

        var successor = StewardHarvestFixture.CreateHost(harness, _ => 7);
        successor.ServiceTick(harness.Kernel);
        Xunit.Assert.Equal(0, harness.Model.Calls);
        Xunit.Assert.Null(successor.CurrentRound);
        Xunit.Assert.Empty(await Await(harness.Intents.ListForGoalAsync(harness.Goal.Id.Value), "adjudication intents read"));
        var log = File.ReadAllText(harness.ConductPath);
        Xunit.Assert.Contains("kind=model-failure", log);
        Xunit.Assert.DoesNotContain("kind=released", log);
        successor.Stop();
    }

    private static ConductorStewardHost CreateHost(StewardHarness harness, IConductorStewardModelRound model) =>
        new(new ConductorStewardTriggerStore(Path.Combine(harness.Root, "steward-triggers.db")),
            new ConductorStewardTriggerDetector(), model, harness.Intents,
            new AdjudicationEvidenceResolver(harness.Root), _ => 7, _ => harness.Root,
            new GoalLifecycleEventWriter(Path.Combine(harness.Root, "lifecycle")),
            new ConductEventLogWriter(harness.ConductPath));

    private static async Task Await(Task task, string eventName)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (TimeoutException ex) { throw new TimeoutException($"Hang waiting for {eventName}.", ex); }
    }

    private static async Task<T> Await<T>(Task<T> task, string eventName)
    {
        try { return await task.WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (TimeoutException ex) { throw new TimeoutException($"Hang waiting for {eventName}.", ex); }
    }

    private sealed class HeldModel : IConductorStewardModelRound
    {
        internal bool FailOnStop { get; init; }
        internal TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<string> Output { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<string> DispatchAsync(ConductorStewardTrigger trigger, string workingDirectory,
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
}
