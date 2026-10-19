using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: every fixture owns its files, SQLite databases and injected clock.
public sealed class ProviderResetTerminalSourceTests
{
    private const string ResetText = "2026-10-20T21:03:00-05:00";
    private const string Binding = "xai::<provider-default>";
    private const string Rationale = "Provider usage page confirms this reset.";
    private static readonly DateTimeOffset ResetAt = DateTimeOffset.Parse("2026-10-21T02:03:00Z");

    [Theory]
    [InlineData(GoalStatus.Completed)]
    [InlineData(GoalStatus.Failed)]
    [InlineData(GoalStatus.Cancelled)]
    [InlineData(GoalStatus.Superseded)]
    public async Task TerminalSource_SubmissionQueuesWorkspaceIntent(GoalStatus status)
    {
        using var fixture = new Fixture();
        await fixture.Initialize(status);
        using var lease = ConductorLoopLease.Acquire(fixture.Workspace.OrchestratorDirectory);
        Assert.True(ConductorLoopLease.IsActive(fixture.Workspace.OrchestratorDirectory));
        var output = await fixture.Submit();
        var intent = Assert.Single(await fixture.Store.ListForGoalAsync(OperatorIntentScopes.Workspace));
        Assert.Equal(OperatorIntentScopes.Workspace, intent.GoalId);
        Assert.Equal(OperatorIntentVerbs.ProviderReset, intent.Verb);
        Assert.Null(intent.TaskId);
        var payload = JsonSerializer.Deserialize<ProviderResetOperatorIntentPayload>(intent.PayloadJson, OperatorIntentJson.Options)!;
        Assert.Equal(Binding, payload.BindingKey);
        Assert.Equal(ResetAt, payload.ResetAt);
        Assert.Equal(Rationale, payload.Rationale);
        Assert.Empty(await fixture.Store.ListForGoalAsync(fixture.Source.Id.Value));
        Assert.Equal($"Operator intent queued: id={intent.Id} verb=provider-reset scope=workspace status=Pending; poll with operator-intent-status {intent.Id} (or add --wait)." + Environment.NewLine, output);
    }

    [Fact]
    public async Task WorkspaceApply_ChangesScheduleAndDispatchFindingToOperator()
    {
        using var fixture = new Fixture();
        await fixture.Initialize();
        Assert.Equal("backoff", fixture.Schedule().ResetSource);
        Assert.Contains("reset_source=backoff", fixture.Finding(fixture.Kernel));
        await fixture.Submit();
        var intent = Assert.Single(await fixture.Store.ListForGoalAsync(OperatorIntentScopes.Workspace));

        Assert.Equal($"OPERATOR_INTENT id={intent.Id} verb=provider-reset scope=workspace result=applied",
            Assert.Single(fixture.Coordinator().ExecuteWorkspacePending(fixture.Kernel)));
        var applied = (await fixture.Store.GetAsync(intent.Id))!;
        Assert.Equal(OperatorIntentStatus.Applied, applied.Status);
        Assert.Equal("Applied provider-reset; tier=Mutate; replayed=False.", applied.Outcome);
        AssertOperatorSchedule(fixture.Schedule());
        Assert.Contains("next_probe=2026-10-21T02:03:00.0000000+00:00; reset_source=operator", fixture.Finding(fixture.Kernel));
    }

    [Fact]
    public async Task WorkspaceApply_PersistsOnlyNoteAndSurvivesRestart()
    {
        using var fixture = new Fixture();
        await fixture.Initialize();
        var before = await fixture.Stored();
        await fixture.Submit();
        var intent = Assert.Single(await fixture.Store.ListForGoalAsync(OperatorIntentScopes.Workspace));
        fixture.Coordinator().ExecuteWorkspacePending(fixture.Kernel);

        var after = await fixture.Stored();
        Assert.Equal(GoalStatus.Completed, after.Status);
        Assert.Equal(Serialize(before.Tasks), Serialize(after.Tasks));
        Assert.Equal(before.Timeline.Count + 1, after.Timeline.Count);
        Assert.Equal(Serialize(before.Timeline), Serialize(after.Timeline.Take(before.Timeline.Count)));
        var note = after.Timeline.Last();
        Assert.Equal(ProgressKind.TaskNote, note.Kind);
        Assert.Equal(fixture.Hold.SourceTaskId.Value, note.TaskId);
        Assert.Equal($"PROVIDER_BUDGET_RESET binding=xai::<provider-default> reset_at=2026-10-21T02:03:00.0000000+00:00 source=operator intent={intent.Id}", note.Message);
        Assert.Equal(fixture.Clock.UtcNow, note.OccurredAt);
        var retained = Assert.Single(fixture.Kernel.ProviderHoldHistory.Retained);
        var storedGoal = (await fixture.Repository.LoadGoalsAsync([fixture.Source.Id])).GetGoal(fixture.Source.Id);
        Assert.Equal(Serialize(storedGoal.Timeline), Serialize(retained.Timeline));

        var restarted = fixture.Restart();
        Assert.DoesNotContain(restarted.Goals, goal => goal.Id == fixture.Source.Id);
        Assert.Equal(fixture.Source.Id, Assert.Single(restarted.ProviderHoldHistory.Retained).Id);
        Assert.Equal(fixture.Schedule(), fixture.Schedule(restarted));
        AssertOperatorSchedule(fixture.Schedule(restarted));
        Assert.Contains("next_probe=2026-10-21T02:03:00.0000000+00:00; reset_source=operator", fixture.Finding(restarted));
    }

    [Fact]
    public async Task Replay_LiveAndRestartedScopesKeepExactlyOneStoredNote()
    {
        using var fixture = new Fixture();
        await fixture.Initialize();
        await fixture.Submit("same-reset");
        await fixture.Submit("same-reset");
        var intent = Assert.Single(await fixture.Store.ListForGoalAsync(OperatorIntentScopes.Workspace));
        fixture.Coordinator().ExecuteWorkspacePending(fixture.Kernel);
        var after = Serialize(await fixture.Stored());
        var recorder = new ProviderResetRetainedSourceRecorder(() => fixture.Repository);

        Assert.True(recorder.Apply(fixture.Kernel, intent, fixture.Clock.UtcNow));
        var restarted = fixture.Restart();
        Assert.True(recorder.Apply(restarted, intent, fixture.Clock.UtcNow));
        Assert.Equal(after, Serialize(await fixture.Stored()));
        Assert.Single(ResetNotes(fixture.Kernel, intent.Id));
        Assert.Single(ResetNotes(restarted, intent.Id));
        Assert.Single(ResetNotes(await fixture.Repository.LoadAsync(), intent.Id));
    }

    [Fact]
    public async Task Replay_StoredNoteRepairsStaleRetainedCopyWithoutWriting()
    {
        using var fixture = new Fixture();
        await fixture.Initialize();
        var stale = fixture.Restart();
        var intent = await fixture.Queue();
        fixture.Coordinator().ExecuteWorkspacePending(fixture.Kernel);
        var after = Serialize(await fixture.Stored());
        Assert.Empty(ResetNotes(stale, intent.Id));

        Assert.True(new ProviderResetRetainedSourceRecorder(() => fixture.Repository)
            .Apply(stale, intent, fixture.Clock.UtcNow));
        Assert.Equal(after, Serialize(await fixture.Stored()));
        Assert.Single(ResetNotes(stale, intent.Id));
        Assert.Equal(fixture.Schedule(), fixture.Schedule(stale));
        var storedGoal = (await fixture.Repository.LoadGoalsAsync([fixture.Source.Id])).GetGoal(fixture.Source.Id);
        Assert.Equal(Serialize(storedGoal), Serialize(Assert.Single(stale.ProviderHoldHistory.Retained)));
    }

    [Theory]
    [InlineData("success", "no-current-hold")]
    [InlineData("future", "reset-too-far")]
    [InlineData("active", "source-goal-in-working-set")]
    [InlineData("missing", "source-goal-not-stored")]
    [InlineData("services", "Provider reset intent services are unavailable.")]
    public async Task RejectedApply_ReportsReasonAndPreservesStoreScopeAndSchedule(
        string scenario, string reason)
    {
        using var fixture = new Fixture();
        await fixture.Initialize(scenario == "active" ? null : GoalStatus.Completed);
        var intent = await fixture.Queue(scenario == "future"
            ? DateTimeOffset.Parse("2026-12-01T00:00:00Z") : ResetAt);
        if (scenario == "success") fixture.Succeed();
        var empty = InfrastructureTestSupport.CreateMigratedStateRepository(Path.Combine(fixture.Root, "empty.db"));
        Assert.Null(await empty.LoadGoalAsync(fixture.Source.Id));
        var recorder = scenario == "services" ? null : new ProviderResetRetainedSourceRecorder(
            () => scenario == "missing" ? empty : fixture.Repository);
        var coordinator = new OperatorIntentCoordinator(fixture.Store, utcNow: () => fixture.Clock.UtcNow)
        {
            ProviderResets = recorder
        };
        var before = Serialize(await fixture.Stored());
        var retained = Serialize(fixture.Kernel.ProviderHoldHistory.Retained.Select(goal => goal.Timeline));
        var schedule = fixture.Schedule();

        Assert.Equal($"OPERATOR_INTENT id={intent.Id} verb=provider-reset scope=workspace result=rejected reason={reason}",
            Assert.Single(coordinator.ExecuteWorkspacePending(fixture.Kernel)));
        var rejected = (await fixture.Store.GetAsync(intent.Id))!;
        Assert.Equal(OperatorIntentStatus.Rejected, rejected.Status);
        Assert.Equal($"Rejected provider-reset: {reason}", rejected.Outcome);
        Assert.Equal(before, Serialize(await fixture.Stored()));
        Assert.Equal(retained, Serialize(fixture.Kernel.ProviderHoldHistory.Retained.Select(goal => goal.Timeline)));
        Assert.Equal(schedule, fixture.Schedule());
        Assert.Null(await empty.LoadGoalAsync(fixture.Source.Id));
        if (scenario == "success")
            Assert.False(ProviderBudgetHoldSchedules.TryGetHoldByBindingKey(fixture.Kernel.ProviderHoldScope, Binding, out _));
    }

    [Fact]
    public async Task ActiveSource_SubmissionKeepsGoalAndTaskScope()
    {
        using var fixture = new Fixture();
        await fixture.Initialize(null);
        await fixture.Submit();
        var intent = Assert.Single(await fixture.Store.ListForGoalAsync(fixture.Source.Id.Value));
        Assert.Equal(fixture.Source.Id.Value, intent.GoalId);
        Assert.Equal(fixture.Source.Tasks.Single().Id.Value, intent.TaskId);
        Assert.Empty(await fixture.Store.ListForGoalAsync(OperatorIntentScopes.Workspace));
    }

    [Fact]
    public async Task EmptyWorkspacePass_PreservesRetainedIdsStoreAndSchedule()
    {
        using var fixture = new Fixture();
        await fixture.Initialize();
        var ids = fixture.Kernel.ProviderHoldHistory.Retained.Select(goal => goal.Id).ToArray();
        Assert.Single(ids);
        var before = Serialize(await fixture.Stored());
        var schedule = fixture.Schedule();

        Assert.Empty(fixture.Coordinator().ExecuteWorkspacePending(fixture.Kernel));
        Assert.Equal(ids, fixture.Kernel.ProviderHoldHistory.Retained.Select(goal => goal.Id));
        Assert.Equal(before, Serialize(await fixture.Stored()));
        Assert.Equal(schedule, fixture.Schedule());
    }

    [Theory]
    [InlineData(false, "provider-reset-workspace-scope-required")]
    [InlineData(true, "provider-reset requires a payload.")]
    public async Task InvalidEnvelope_RejectsBeforeOpeningRepository(bool workspace, string reason)
    {
        using var fixture = new Fixture();
        await fixture.Initialize();
        var intent = await fixture.Queue();
        intent = intent with { GoalId = workspace ? OperatorIntentScopes.Workspace : fixture.Source.Id.Value, PayloadJson = "null" };
        var recorder = new ProviderResetRetainedSourceRecorder(() =>
            throw new InvalidOperationException("Repository must not be opened for invalid envelopes."));
        Assert.Equal(reason, Assert.Throws<InvalidOperationException>(() =>
            recorder.Apply(fixture.Kernel, intent, fixture.Clock.UtcNow)).Message);
    }

    [Fact]
    public async Task ScopeReplay_PrecedesPayloadValidationAndRepositoryAccess()
    {
        using var fixture = new Fixture();
        await fixture.Initialize();
        var intent = await fixture.Queue();
        fixture.Coordinator().ExecuteWorkspacePending(fixture.Kernel);
        fixture.Succeed();
        var recorder = new ProviderResetRetainedSourceRecorder(() =>
            throw new InvalidOperationException("Repository must not be opened for scope replay."));
        Assert.True(recorder.Apply(fixture.Kernel, intent with { PayloadJson = "null" }, fixture.Clock.UtcNow));
    }

    private static void AssertOperatorSchedule(ProviderBudgetHoldSchedule schedule)
    {
        Assert.Equal("operator", schedule.ResetSource);
        Assert.Equal(ResetAt, schedule.ResetAt);
        Assert.Equal(ResetAt, schedule.NextProbeAt);
    }

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value);

    private static IEnumerable<ProgressEvent> ResetNotes(AgentOrchestratorKernel kernel, string intentId) =>
        kernel.ProviderHoldScope.SelectMany(goal => goal.Timeline).Where(evt =>
            ProviderBudgetHoldSchedules.TryParseReset(evt, out var marker) && marker.IntentId == intentId);

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.Parse("2026-10-17T11:07:00Z");
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "provider-reset-terminal-" + Guid.NewGuid().ToString("N"));
        internal readonly FakeClock Clock = new();
        internal readonly AgentOrchestratorKernel Kernel;
        internal readonly OrchestratorWorkspace Workspace;
        internal readonly SqliteOrchestratorStateRepository Repository;
        internal readonly SqliteOperatorIntentStore Store;
        internal readonly IReadOnlyList<AgentDefinition> Agents;
        internal readonly Goal Source;
        internal readonly Goal Target;
        internal readonly ProviderBudgetExhaustionHold Hold;
        private readonly string _textPath;

        internal Fixture()
        {
            Directory.CreateDirectory(Root);
            Kernel = new(Clock);
            Workspace = OrchestratorWorkspace.ForDirectory(Root);
            Repository = InfrastructureTestSupport.CreateMigratedStateRepository(Workspace.SqliteStatePath);
            Store = SqliteOperatorIntentStore.ForDirectories(Workspace.OrchestratorDirectory, Workspace.LogDirectory);
            Agents = [ProviderHoldAgentSubstitutionTests.Scenario.Agent("xai-tester", "xAI", "grok-cli", "grok-4.7")];
            Source = Kernel.CreateGoal("Hold source", [new TaskSpec(TaskId.New(), "Verify", AgentRole.Tester)]);
            Kernel.ActivateGoal(Source.Id, Agents);
            var dispatchedAt = Clock.UtcNow;
            Kernel.RecordTaskDispatch(Source.Id, Source.Tasks.Single().Id, new("grok-cli", "grok", Root, dispatchedAt,
                ProviderName: "xAI", ModelName: "grok-4.7", AssignedAgentId: Agents.Single().Id.Value));
            Clock.UtcNow = dispatchedAt.AddMinutes(1);
            const string stderr = "API error (status 402 Payment Required): Grok Build usage balance exhausted";
            Kernel.RecordTaskVerification(Source.Id, Source.Tasks.Single().Id, new("grok", Root, 1, "", stderr, Clock.UtcNow,
                ProviderFailureKind: WorkerProviderCatalog.Default().ResolveProfile("grok-cli").ParseOutcome(new(1, "", stderr)),
                DispatchStartedAt: dispatchedAt));
            Kernel.ReportTaskProgress(Source.Id, Source.Tasks.Single().Id, WorkTaskStatus.Failed, "Fixture provider failure.");
            Assert.Equal(GoalStatus.Active, Source.Status);
            Assert.True(ProviderBudgetHoldSchedules.TryGetHoldByBindingKey(Kernel.ProviderHoldScope, Binding, out var hold));
            Hold = hold;
            Target = Kernel.CreateGoal("Dispatch target", [new TaskSpec(TaskId.New(), "Verify", AgentRole.Tester)]);
            Kernel.ActivateGoal(Target.Id, Agents);
            Clock.UtcNow = DateTimeOffset.Parse("2026-10-17T11:40:00Z");
            _textPath = Path.Combine(Root, "rationale.txt");
            File.WriteAllText(_textPath, Rationale);
        }

        internal async Task Initialize(GoalStatus? status = GoalStatus.Completed)
        {
            await Repository.SaveAsync(Kernel);
            Assert.Contains(await Repository.ListModelFitHistoryAsync(), row =>
                row.GoalId == Source.Id.Value && row.OutcomeRule == "provider-budget-exhausted");
            if (status is null) return;
            Kernel.ReplaceGoalWithSnapshot(Kernel.ExportGoalSnapshot(Source.Id) with { Status = status.Value });
            await Repository.SaveAsync(Kernel);
            Assert.Equal(1, Kernel.EvictTerminalGoalAggregates([Source.Id]));
            Assert.Equal(Source.Id, Assert.Single(Kernel.ProviderHoldHistory.Retained).Id);
        }

        internal async Task<string> Submit(string? key = null)
        {
            var loaded = await Repository.LoadAsync();
            Assert.Equal((await Stored()).Status, loaded.GetGoal(Source.Id).Status);
            var context = new CliExecutionContext(loaded, Workspace, new InMemoryModelProviderRegistry([]),
                Agents, WorkerProfileCatalog.Default(), null);
            string[] flags = key is null ? [] : ["--idempotency-key", key];
            return InfrastructureTestSupport.CaptureConsole(() => CliProviderResetCommand.Execute(
                ["provider-reset", "xAI", ResetText, "--text-file", _textPath, .. flags], context, Store));
        }

        internal Task<OperatorIntentRecord> Queue(DateTimeOffset? resetAt = null)
        {
            var id = Guid.NewGuid().ToString("N");
            return Store.EnqueueAsync(new(id, id, OperatorIntentVerbs.ProviderReset, OperatorIntentScopes.Workspace, null,
                JsonSerializer.Serialize(new ProviderResetOperatorIntentPayload(Binding, resetAt ?? ResetAt, Rationale), OperatorIntentJson.Options),
                [], "operator", "cli", "local", Clock.UtcNow));
        }

        internal OperatorIntentCoordinator Coordinator() => new(Store, utcNow: () => Clock.UtcNow)
        {
            ProviderResets = new ProviderResetRetainedSourceRecorder(() => Repository)
        };

        internal async Task<GoalSnapshot> Stored() => (await Repository.LoadGoalAsync(Source.Id))!;

        internal ProviderBudgetHoldSchedule Schedule(AgentOrchestratorKernel? kernel = null) =>
            ProviderBudgetHoldSchedules.Evaluate((kernel ?? Kernel).ProviderHoldScope, Hold, Clock.UtcNow);

        internal string Finding(AgentOrchestratorKernel kernel)
        {
            var goal = kernel.GetGoal(Target.Id);
            var findings = new List<string>();
            WorkerSubscriptionPreflightFindings.AddProviderBudgetExhaustionFinding(findings, kernel.ProviderHoldScope,
                "xAI", null, goal, goal.Tasks.Single(), Clock.UtcNow);
            return Assert.Single(findings);
        }

        internal AgentOrchestratorKernel Restart()
        {
            var restarted = AgentOrchestratorKernel.FromSnapshot(
                CliPersistentStateRunner.LoadConductLoopKernel(Repository, [], Root).ExportSnapshot(), Clock);
            var lines = new List<string>();
            ProviderHoldHistorySeed.Apply(restarted, Repository, lines.Add);
            Assert.Equal($"PROVIDER_HOLD_HISTORY candidates=1 retained=1 goals={Source.Id.Value[..8]}", Assert.Single(lines));
            return restarted;
        }

        internal void Succeed()
        {
            var goal = Kernel.CreateGoal("Fresh success", [new TaskSpec(TaskId.New(), "Verify", AgentRole.Tester)]);
            Kernel.ActivateGoal(goal.Id, Agents);
            Kernel.RecordTaskDispatch(goal.Id, goal.Tasks.Single().Id, new("grok-cli", "grok", Root, Clock.UtcNow, ProviderName: "xAI"));
            Clock.UtcNow = Clock.UtcNow.AddMinutes(1);
            Kernel.RecordTaskVerification(goal.Id, goal.Tasks.Single().Id, new("grok", Root, 0, "Success", "", Clock.UtcNow));
            Assert.False(ProviderBudgetHoldSchedules.TryGetHoldByBindingKey(Kernel.ProviderHoldScope, Binding, out _));
        }

        public void Dispose() => SharedTestSupport.RemoveTempDirectory(Root);
    }
}
