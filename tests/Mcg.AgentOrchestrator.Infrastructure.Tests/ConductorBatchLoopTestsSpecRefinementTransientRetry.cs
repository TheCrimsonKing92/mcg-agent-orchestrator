using System.Text.Json;
using Mcg.AgentOrchestrator.App.Application;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsSpecRefinementTransientRetry(ITestOutputHelper output)
    : ConductorBatchLoopTests(output)
{
    private const string LockFailure =
        "SPEC_REFINEMENT_FAILED owner=durable-outbox phase=attach-snapshot detail=SQLite Error 5: 'database is locked'.";
    private const string ExhaustedReason = "SPEC_REFINEMENT_TRANSIENT_RETRY_EXHAUSTED";

    [Xunit.Fact(DisplayName = "Transient lock relaunch holds dispatch with attempt one and durable ownership")]
    public async Task TransientLockRelaunchesWithAttemptOne()
    {
        using var fixture = new RetryFixture();
        await fixture.InitializeAsync(LockFailure);

        var hold = fixture.Evaluate();

        Assert.Equal(1, fixture.Launches);
        Assert.StartsWith("SPEC_REFINEMENT_PENDING", hold.Message, StringComparison.Ordinal);
        Assert.Contains("reason=retrying-after-transient-lock", hold.Message, StringComparison.Ordinal);
        Assert.Contains("transient_retry_attempt=1", hold.Message, StringComparison.Ordinal);
        var dispatch = DispatchStartOutcome.FromDispatchException(hold, "dispatch failed");
        Assert.Equal(DispatchStartOutcomeCategory.Deferred, dispatch.Category);
        Assert.Equal(ConductorHoldOwner.DurableOutbox, dispatch.HoldOwner);
        Assert.Equal(1, fixture.PersistedAttempts());
        var state = await fixture.Repository.GetOutboxStateAsync(fixture.Message.Id, TestContext.Current.CancellationToken);
        Assert.Equal(OrchestratorStateOutboxStatus.Failed, state!.Status);
        Assert.Equal(LockFailure, state.Detail);
    }

    [Xunit.Fact(DisplayName = "Transient retries exhaust once after actual launches and preserve failed receipt")]
    public async Task TransientRetriesExhaustOnceAtBound()
    {
        using var fixture = new RetryFixture();
        await fixture.InitializeAsync(LockFailure);
        InvalidOperationException? exhausted = null;
        for (var iteration = 0; iteration < 10; iteration++)
        {
            var result = fixture.Evaluate();
            if (!result.Message.StartsWith("SPEC_REFINEMENT_PENDING", StringComparison.Ordinal))
            {
                exhausted = result;
                break;
            }
            Assert.Contains($"transient_retry_attempt={fixture.Launches}", result.Message, StringComparison.Ordinal);
            var sameInstant = fixture.Evaluate();
            Assert.StartsWith("SPEC_REFINEMENT_PENDING", sameInstant.Message, StringComparison.Ordinal);
            Assert.Contains("executor_started=false", sameInstant.Message, StringComparison.Ordinal);
            Assert.Contains($"transient_retry_attempt={fixture.Launches}", sameInstant.Message, StringComparison.Ordinal);
            Assert.Equal(fixture.Launches, fixture.PersistedAttempts());
            fixture.AdvanceCadence();
        }

        Assert.NotNull(exhausted);
        Assert.InRange(fixture.Launches, 3, 5);
        Assert.StartsWith(ExhaustedReason, exhausted.Message, StringComparison.Ordinal);
        Assert.Contains($"goal={fixture.Goal.Id.Value}", exhausted.Message, StringComparison.Ordinal);
        Assert.Contains($"attempts={fixture.Launches}", exhausted.Message, StringComparison.Ordinal);
        Assert.Contains($"detail={LockFailure}", exhausted.Message, StringComparison.Ordinal);
        var launches = fixture.Launches;
        for (var repeat = 0; repeat < 2; repeat++)
        {
            fixture.AdvanceCadence();
            Assert.Equal(exhausted.Message, fixture.Evaluate().Message);
        }
        Assert.Equal(launches, fixture.Launches);
        var eventPath = new GoalLifecycleEventWriter(fixture.Workspace.GoalLifecycleEventsDirectory)
            .EventFilePath(fixture.Goal.Id);
        var escalation = Assert.Single(File.ReadLines(eventPath), line =>
            line.Contains(ExhaustedReason, StringComparison.Ordinal));
        using var eventJson = JsonDocument.Parse(escalation);
        Assert.Equal("GoalEscalated", eventJson.RootElement.GetProperty("eventType").GetString());
        Assert.Equal(fixture.Goal.Id.Value, eventJson.RootElement.GetProperty("goalId").GetString());
        Assert.Equal(exhausted.Message, eventJson.RootElement.GetProperty("reason").GetString());
        Assert.Equal("spec-refinement-transient-retry", eventJson.RootElement.GetProperty("source").GetString());
        var state = await fixture.Repository.GetOutboxStateAsync(fixture.Message.Id, TestContext.Current.CancellationToken);
        Assert.Equal(OrchestratorStateOutboxStatus.Failed, state!.Status);
        Assert.Equal(LockFailure, state.Detail);
    }

    [Xunit.Theory(DisplayName = "Semantic failures and locks outside write phases retain original failure behavior")]
    [Xunit.InlineData("phase=refine detail=model output unparseable")]
    [Xunit.InlineData("phase=provider-refinement detail=SQLite Error 5: 'database is locked'.")]
    [Xunit.InlineData("phase=load-goal detail=SQLite Error 6: 'database table is locked'.")]
    [Xunit.InlineData("phase=attach-snapshot detail=SQLite Error 19: 'constraint failed'.")]
    [Xunit.InlineData("phase=attach-snapshot detail=SQLite Error 50: 'database is locked'.")]
    [Xunit.InlineData("phase=attach-snapshot detail=database is locked")]
    [Xunit.InlineData("phase=attach-snapshot-extra detail=SQLite Error 5: 'database is locked'.")]
    [Xunit.InlineData("detail=phase=attach-snapshot SQLite Error 5: 'database is locked'.")]
    public async Task NonTransientFailureDoesNotLaunch(string detail)
    {
        using var fixture = new RetryFixture();
        var failure = $"SPEC_REFINEMENT_FAILED owner=durable-outbox {detail}";
        await fixture.InitializeAsync(failure);

        var result = fixture.Evaluate();

        Assert.Equal($"SPEC_REFINEMENT_FAILED goal={fixture.Goal.Id.Value} owner=durable-outbox {detail}", result.Message);
        Assert.Equal(0, fixture.Launches);
        Assert.False(Directory.Exists(Path.Combine(
            fixture.Workspace.SpecRefinementLaunchAttemptsDirectory, "transient-retries")));
    }

    [Xunit.Theory(DisplayName = "Both SQLite lock codes retry only on declared write phases")]
    [Xunit.InlineData("attach-snapshot", 6)]
    [Xunit.InlineData("record-policy-receipt", 5)]
    [Xunit.InlineData("emit-clarification-event", 6)]
    public async Task WritePhaseLockRelaunches(string phase, int code)
    {
        using var fixture = new RetryFixture();
        await fixture.InitializeAsync(
            $"SPEC_REFINEMENT_FAILED owner=durable-outbox phase={phase} detail=SQLite Error {code}: 'database is locked'.");
        Assert.StartsWith("SPEC_REFINEMENT_PENDING", fixture.Evaluate().Message, StringComparison.Ordinal);
        Assert.Equal(1, fixture.Launches);
    }

    [Xunit.Fact(DisplayName = "Deferred launch and cadence holds consume no transient attempts")]
    public async Task DeferredLaunchDoesNotConsumeAttempts()
    {
        using var fixture = new RetryFixture { LaunchStarts = false };
        await fixture.InitializeAsync(LockFailure);
        Assert.Contains("transient_retry_attempt=0", fixture.Evaluate().Message, StringComparison.Ordinal);
        Assert.Equal(0, fixture.Launches);
        fixture.LaunchStarts = true;
        Assert.Contains("transient_retry_attempt=1", fixture.Evaluate().Message, StringComparison.Ordinal);
        Assert.Equal(1, fixture.Launches);
        for (var evaluation = 0; evaluation < 8; evaluation++)
        {
            Assert.Contains("transient_retry_attempt=1", fixture.Evaluate().Message, StringComparison.Ordinal);
            Assert.Equal(1, fixture.Launches);
        }
    }

    [Xunit.Fact(DisplayName = "A non transient outcome resets consecutive retries for the same receipt")]
    public async Task NonTransientOutcomeResetsAttempts()
    {
        using var fixture = new RetryFixture();
        await fixture.InitializeAsync(LockFailure);
        Assert.Contains("transient_retry_attempt=1", fixture.Evaluate().Message, StringComparison.Ordinal);
        await fixture.FailAsync("SPEC_REFINEMENT_FAILED owner=durable-outbox phase=refine detail=model output unparseable");
        Assert.StartsWith("SPEC_REFINEMENT_FAILED", fixture.Evaluate().Message, StringComparison.Ordinal);
        await fixture.FailAsync(LockFailure);
        fixture.AdvanceCadence();
        Assert.Contains("transient_retry_attempt=1", fixture.Evaluate().Message, StringComparison.Ordinal);
        Assert.Equal(2, fixture.Launches);
        Assert.Equal(1, fixture.PersistedAttempts());
    }

    [Xunit.Fact(DisplayName = "Completed receipt starts a fresh retry episode for later refinement")]
    public async Task CompletedReceiptResetsAttempts()
    {
        using var fixture = new RetryFixture();
        await fixture.InitializeAsync(LockFailure);
        Assert.Contains("transient_retry_attempt=1", fixture.Evaluate().Message, StringComparison.Ordinal);
        Assert.True(await fixture.Repository.TryProcessOutboxMessageAsync(fixture.Message.Id,
            (_, _) => Task.FromResult(OrchestratorStateOutboxProcessingResult.Completed),
            TestContext.Current.CancellationToken));
        fixture.AdvanceCadence();
        fixture.Message = fixture.Message with { CreatedAt = fixture.Now };
        await fixture.Repository.EnsureOutboxMessageAsync(fixture.Message, TestContext.Current.CancellationToken);
        await fixture.FailAsync(LockFailure);
        Assert.Contains("transient_retry_attempt=1", fixture.Evaluate().Message, StringComparison.Ordinal);
        Assert.Equal(2, fixture.Launches);
        Assert.Equal(1, fixture.PersistedAttempts());
    }

    private sealed class RetryFixture : IDisposable
    {
        private readonly string _root = CreateTempDirectory("mcg-refinement-transient-retry");
        public OrchestratorWorkspace Workspace { get; }
        public SqliteOrchestratorStateRepository Repository { get; }
        public AgentOrchestratorKernel Kernel { get; } = new();
        public Goal Goal { get; }
        public OrchestratorStateOutboxMessage Message { get; set; }
        public DateTimeOffset Now { get; private set; } = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
        public int Launches { get; private set; }
        public bool LaunchStarts { get; set; } = true;

        public RetryFixture()
        {
            Workspace = OrchestratorWorkspace.ForDirectory(_root);
            Repository = OpenStateRepository(Workspace.SqliteStatePath);
            Goal = GoalLifecycleCommands.CreateAndActivateGoal(Kernel, AgentCatalog.Default().Agents,
                GoalObjectivePlanner.Build("Plan a focused implementation with tests.", GoalIntakePipeline.FiveRole));
            GoalRefinementWorkCoordinator.RecordPending(Kernel, Goal.Id);
            Message = GoalRefinementWorkCoordinator.CreateMessage(Goal.Id) with { CreatedAt = Now };
            GoalRefinementWorkCoordinator.UtcNowOverride = () => Now;
            GoalRefinementWorkCoordinator.LaunchOverride = (_, _) =>
            {
                if (!LaunchStarts)
                    return new GoalRefinementWorkLaunchResult(false, null, "test-launch-deferred");
                Launches++;
                return new GoalRefinementWorkLaunchResult(true, 7000 + Launches, "test-executor-started");
            };
        }

        public async Task InitializeAsync(string failure)
        {
            await Repository.SaveAsync(Kernel);
            await Repository.EnsureOutboxMessageAsync(Message);
            await FailAsync(failure);
        }

        public async Task FailAsync(string failure)
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Repository.TryProcessOutboxMessageAsync(Message.Id,
                    (_, _) => Task.FromException<OrchestratorStateOutboxProcessingResult>(
                        new InvalidOperationException(failure))));
            Assert.Equal(failure, exception.Message);
            var state = await Repository.GetOutboxStateAsync(Message.Id);
            Assert.Equal(OrchestratorStateOutboxStatus.Failed, state!.Status);
            Assert.Equal(failure, state.Detail);
        }

        public InvalidOperationException Evaluate()
        {
            // Reload both durable homes on every dispatch, rather than carrying an in-memory count.
            var kernel = Repository.LoadAsync().GetAwaiter().GetResult();
            return Assert.Throws<InvalidOperationException>(() =>
                GoalDispatchOperations.EnsureRefinedForSpecConsumer(kernel,
                    OrchestratorWorkspace.ForDirectory(_root), new InMemoryModelProviderRegistry([]),
                    kernel.GetGoal(Goal.Id)));
        }

        public int PersistedAttempts()
        {
            var path = Assert.Single(Directory.GetFiles(
                Path.Combine(Workspace.SpecRefinementLaunchAttemptsDirectory, "transient-retries"), "*.json"));
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            return json.RootElement.GetProperty("attempts").GetInt32();
        }

        public void AdvanceCadence() => Now += GoalRefinementWorkCoordinator.RecoveryLaunchCadence + TimeSpan.FromSeconds(1);

        public void Dispose()
        {
            GoalRefinementWorkCoordinator.LaunchOverride = null;
            GoalRefinementWorkCoordinator.UtcNowOverride = null;
            TryDeleteDirectory(_root);
        }
    }
}
