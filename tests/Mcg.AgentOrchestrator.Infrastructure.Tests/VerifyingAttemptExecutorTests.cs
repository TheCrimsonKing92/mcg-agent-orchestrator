using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: all effects and clock values are per-test recording fakes.
public sealed class VerifyingAttemptExecutorTests
{
    [Fact]
    public void Tick_StartedPassed_RestartsOnceWithFailPolicy()
    {
        var first = ConductorParallelAcceptanceAttemptDecision.Started(
            Attempt("first", ConductorParallelAcceptanceAttemptOutcome.Passed));
        var second = ConductorParallelAcceptanceAttemptDecision.Started(
            Attempt("second", ConductorParallelAcceptanceAttemptOutcome.Passed));
        var fixture = new RecordingEffects(first, second);

        var result = fixture.Execute(isConductorTick: true);

        Assert.Same(second, result.Decision);
        Assert.Equal(new[] { AcceptanceStableSlotExhaustionPolicy.Fail,
            AcceptanceStableSlotExhaustionPolicy.Fail }, fixture.ExhaustionPolicies);
        Assert.Equal(new[] { "evaluate", "evaluate" }, fixture.Events);
        Assert.Empty(fixture.ObservedAttempts);
        Assert.Empty(fixture.StartedAttempts);
        Assert.Empty(fixture.Delays);
        Assert.Null(result.NoTickWaitOutcome);
        Assert.Null(result.ArtifactWriterBusyMessage);
    }

    [Fact]
    public void Tick_StartedRunning_DoesNotRestartOrPoll()
    {
        var first = ConductorParallelAcceptanceAttemptDecision.Started(Attempt("first"));
        var fixture = new RecordingEffects(first);

        var result = fixture.Execute(isConductorTick: true);

        Assert.Same(first, result.Decision);
        Assert.Equal(new[] { AcceptanceStableSlotExhaustionPolicy.Fail }, fixture.ExhaustionPolicies);
        Assert.Equal(new[] { "evaluate" }, fixture.Events);
        Assert.Empty(fixture.ObservedAttempts);
        Assert.Empty(fixture.StartedAttempts);
        Assert.Empty(fixture.Delays);
        Assert.Null(result.NoTickWaitOutcome);
        Assert.Null(result.ArtifactWriterBusyMessage);
    }

    [Fact]
    public void NoTick_Started_EmitsBeforeClockAndPollsToCompletion()
    {
        var first = ConductorParallelAcceptanceAttemptDecision.Started(Attempt("first"));
        var running = ConductorParallelAcceptanceAttemptDecision.Running(Attempt("observed"));
        var completed = Completed(Attempt("completed", ConductorParallelAcceptanceAttemptOutcome.Passed));
        var observations = new Queue<ConductorParallelAcceptanceAttemptDecision>([running, completed]);
        var fixture = new RecordingEffects(first) { Observe = _ => observations.Dequeue() };

        var result = fixture.Execute(isConductorTick: false);

        Assert.Same(completed, result.Decision);
        Assert.Equal(new[] { AcceptanceStableSlotExhaustionPolicy.DegradeToSerial }, fixture.ExhaustionPolicies);
        Assert.Same(first.Attempt, Assert.Single(fixture.StartedAttempts));
        Assert.Equal(new[] { "evaluate", "started", "clock", "clock", "delay", "observe",
            "clock", "delay", "observe" }, fixture.Events);
        Assert.Equal(new[] { fixture.PollInterval, fixture.PollInterval }, fixture.Delays);
        Assert.Collection(fixture.ObservedAttempts,
            attempt => Assert.Same(first.Attempt, attempt),
            attempt => Assert.Same(running.Attempt, attempt));
        Assert.Null(result.NoTickWaitOutcome);
        Assert.Null(result.ArtifactWriterBusyMessage);
    }

    [Fact]
    public void NoTick_Running_PollsWithoutStartedCallback()
    {
        var first = ConductorParallelAcceptanceAttemptDecision.Running(Attempt("first"));
        var completed = Completed(Attempt("completed", ConductorParallelAcceptanceAttemptOutcome.Passed));
        var fixture = new RecordingEffects(first) { Observe = _ => completed };

        var result = fixture.Execute(isConductorTick: false);

        Assert.Same(completed, result.Decision);
        Assert.Equal(new[] { AcceptanceStableSlotExhaustionPolicy.DegradeToSerial }, fixture.ExhaustionPolicies);
        Assert.Empty(fixture.StartedAttempts);
        Assert.Equal(new[] { "evaluate", "clock", "clock", "delay", "observe" }, fixture.Events);
        Assert.Same(first.Attempt, Assert.Single(fixture.ObservedAttempts));
        Assert.Equal(fixture.PollInterval, Assert.Single(fixture.Delays));
        Assert.Null(result.NoTickWaitOutcome);
        Assert.Null(result.ArtifactWriterBusyMessage);
    }

    [Fact]
    public void NoTick_AlwaysRunning_DeadlineElapsesAfterThreeObservations()
    {
        var first = ConductorParallelAcceptanceAttemptDecision.Started(Attempt("first"));
        var running = ConductorParallelAcceptanceAttemptDecision.Running(Attempt("observed"));
        var fixture = new RecordingEffects(first) { Observe = _ => running };

        var result = fixture.Execute(isConductorTick: false);

        Assert.Equal("deadline-elapsed", result.NoTickWaitOutcome);
        Assert.Same(running, result.Decision);
        Assert.Equal(3, fixture.ObservedAttempts.Count);
        Assert.Equal(3, fixture.Delays.Count);
        Assert.All(fixture.Delays, delay => Assert.Equal(fixture.PollInterval, delay));
        Assert.Equal(DateTimeOffset.UnixEpoch.Add(fixture.PollTimeout), fixture.Now);
        Assert.Equal("clock", fixture.Events[^1]);
        Assert.Null(result.ArtifactWriterBusyMessage);
    }

    [Theory]
    [InlineData("Attempt metadata is unreadable.")]
    [InlineData("Attempt metadata is unreadable. after reconciliation")]
    public void NoTick_UnreadableObservation_KeepsAttemptAndContinues(string message)
    {
        var first = ConductorParallelAcceptanceAttemptDecision.Started(Attempt("first"));
        var completed = Completed(Attempt("completed", ConductorParallelAcceptanceAttemptOutcome.Passed));
        var calls = 0;
        var fixture = new RecordingEffects(first)
        {
            Observe = _ => ++calls == 1 ? throw new InvalidDataException(message) : completed
        };

        var result = fixture.Execute(isConductorTick: false);

        Assert.Same(completed, result.Decision);
        Assert.Equal(2, fixture.ObservedAttempts.Count);
        Assert.All(fixture.ObservedAttempts, attempt => Assert.Same(first.Attempt, attempt));
        Assert.Equal(new[] { fixture.PollInterval, fixture.PollInterval }, fixture.Delays);
        Assert.Null(result.NoTickWaitOutcome);
        Assert.Null(result.ArtifactWriterBusyMessage);
    }

    [Theory]
    [InlineData("Attempt changed after reconciliation", "reconciliation-ownership-changed")]
    [InlineData("Attempt ownership changed", "ownership-changed")]
    [InlineData("Attempt changed AFTER RECONCILIATION", "ownership-changed")]
    public void NoTick_OwnershipChanged_ReturnsLastObservedDecision(string message, string outcome)
    {
        var first = ConductorParallelAcceptanceAttemptDecision.Started(Attempt("first"));
        var running = ConductorParallelAcceptanceAttemptDecision.Running(Attempt("observed"));
        var calls = 0;
        var fixture = new RecordingEffects(first)
        {
            Observe = _ => ++calls == 1 ? running : throw new InvalidDataException(message)
        };

        var result = fixture.Execute(isConductorTick: false);

        Assert.Equal(outcome, result.NoTickWaitOutcome);
        Assert.Same(running, result.Decision);
        Assert.Collection(fixture.ObservedAttempts,
            attempt => Assert.Same(first.Attempt, attempt),
            attempt => Assert.Same(running.Attempt, attempt));
        Assert.Equal(new[] { fixture.PollInterval, fixture.PollInterval }, fixture.Delays);
        Assert.Null(result.ArtifactWriterBusyMessage);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void Evaluate_BusyOnStartOrRestart_ReturnsMessageAndNullDecision(
        bool isConductorTick, bool onRestart)
    {
        var first = ConductorParallelAcceptanceAttemptDecision.Started(
            Attempt("first", ConductorParallelAcceptanceAttemptOutcome.Passed));
        var exception = new AcceptanceArtifactWriterLeaseBusyException("goal-dir");
        var calls = 0;
        var fixture = new RecordingEffects(first)
        {
            Evaluate = _ => ++calls == 1 && onRestart ? first : throw exception
        };

        var result = fixture.Execute(isConductorTick);

        Assert.Null(result.Decision);
        Assert.Equal(exception.Message, result.ArtifactWriterBusyMessage);
        Assert.Null(result.NoTickWaitOutcome);
        Assert.Equal(onRestart ? 2 : 1, fixture.ExhaustionPolicies.Count);
        Assert.All(fixture.ExhaustionPolicies, policy => Assert.Equal(isConductorTick
            ? AcceptanceStableSlotExhaustionPolicy.Fail
            : AcceptanceStableSlotExhaustionPolicy.DegradeToSerial, policy));
        Assert.All(fixture.Events, entry => Assert.Equal("evaluate", entry));
        Assert.Empty(fixture.ObservedAttempts);
        Assert.Empty(fixture.StartedAttempts);
        Assert.Empty(fixture.Delays);
    }

    [Fact]
    public void NoTick_ObserveBusy_PropagatesOutsideEvaluateCatch()
    {
        var first = ConductorParallelAcceptanceAttemptDecision.Running(Attempt("first"));
        var exception = new AcceptanceArtifactWriterLeaseBusyException("goal-dir");
        var fixture = new RecordingEffects(first) { Observe = _ => throw exception };

        Assert.Same(exception, Assert.Throws<AcceptanceArtifactWriterLeaseBusyException>(
            () => fixture.Execute(isConductorTick: false)));
        Assert.Same(first.Attempt, Assert.Single(fixture.ObservedAttempts));
        Assert.Equal(fixture.PollInterval, Assert.Single(fixture.Delays));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CompletedDecision_SkipsRestartAndWait(bool isConductorTick)
    {
        var completed = Completed(Attempt("completed", ConductorParallelAcceptanceAttemptOutcome.Passed));
        var fixture = new RecordingEffects(completed);

        var result = fixture.Execute(isConductorTick);

        Assert.Same(completed, result.Decision);
        Assert.Single(fixture.ExhaustionPolicies);
        Assert.Equal(new[] { "evaluate" }, fixture.Events);
        Assert.Null(result.NoTickWaitOutcome);
        Assert.Null(result.ArtifactWriterBusyMessage);
    }

    private static ConductorParallelAcceptanceAttemptDecision Completed(
        ConductorParallelAcceptanceAttempt attempt) =>
        new(ConductorParallelAcceptanceAttemptDecisionKind.Completed, attempt);

    private static ConductorParallelAcceptanceAttempt Attempt(
        string id,
        ConductorParallelAcceptanceAttemptOutcome outcome = ConductorParallelAcceptanceAttemptOutcome.Running) =>
        new(id, "goal-id", "goal", 0, "branch", "main", DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch, 1, outcome, "stdout", "stderr", "exit", "heartbeat", "result", "metadata");

    private sealed class RecordingEffects
    {
        public RecordingEffects(params ConductorParallelAcceptanceAttemptDecision[] decisions)
        {
            var pending = new Queue<ConductorParallelAcceptanceAttemptDecision>(decisions);
            Evaluate = _ => pending.Dequeue();
        }

        public Func<AcceptanceStableSlotExhaustionPolicy, ConductorParallelAcceptanceAttemptDecision> Evaluate { get; set; }
        public Func<ConductorParallelAcceptanceAttempt, ConductorParallelAcceptanceAttemptDecision> Observe { get; init; } =
            _ => throw new InvalidOperationException("Unexpected observation.");
        public List<AcceptanceStableSlotExhaustionPolicy> ExhaustionPolicies { get; } = [];
        public List<ConductorParallelAcceptanceAttempt> ObservedAttempts { get; } = [];
        public List<ConductorParallelAcceptanceAttempt> StartedAttempts { get; } = [];
        public List<TimeSpan> Delays { get; } = [];
        public List<string> Events { get; } = [];
        public DateTimeOffset Now { get; private set; } = DateTimeOffset.UnixEpoch;
        public TimeSpan PollInterval { get; } = TimeSpan.FromSeconds(7);
        public TimeSpan PollTimeout => PollInterval * 3;

        public VerifyingAttemptExecution Execute(bool isConductorTick) => VerifyingAttemptExecutor.Execute(
            isConductorTick,
            exhaustion =>
            {
                Events.Add("evaluate");
                ExhaustionPolicies.Add(exhaustion);
                return Evaluate(exhaustion);
            },
            attempt =>
            {
                Events.Add("observe");
                ObservedAttempts.Add(attempt);
                return Observe(attempt);
            },
            attempt =>
            {
                Events.Add("started");
                StartedAttempts.Add(attempt);
            },
            () =>
            {
                Events.Add("clock");
                return Now;
            },
            delay =>
            {
                Events.Add("delay");
                Delays.Add(delay);
                Now = Now.Add(delay);
            },
            PollInterval,
            PollTimeout);
    }
}
