using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Each case owns its SQLite stores and uses synchronous semantic callbacks; no real workers.
[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsOperatorIntentBoundary(ITestOutputHelper output)
    : ConductorBatchLoopTests(output)
{
    [Xunit.Fact]
    public async Task Retry_IsDurableBeforeUnrelatedSweepAndCleanup()
    {
        var root = CreateTempDirectory("mcg-intent-boundary");
        try
        {
            var (kernel, target) = SimpleGoal();
            var task = target.Tasks.Single();
            kernel.ReportTaskProgress(target.Id, task.Id, WorkTaskStatus.Failed, "repair");
            var unrelated = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "Unrelated failed goal");
            kernel.ReportTaskProgress(unrelated.Id, unrelated.Tasks.Single().Id, WorkTaskStatus.Failed, "unrelated");
            var cleanup = kernel.CreateGoal("Unrelated cleanup");
            kernel.ReplaceGoalWithSnapshot(kernel.ExportGoalSnapshot(cleanup.Id) with { Status = GoalStatus.Completed });
            var statePath = Path.Combine(root, "state.db");
            StateDbMigrations.EnsureUpToDate(statePath);
            var repository = new SqliteOrchestratorStateRepository(statePath);
            await repository.SaveAsync(kernel);
            var store = Store(root);
            var intent = await store.EnqueueAsync(Retry(target, "first"));
            var events = new List<string>();
            var swept = false;

            new ConductorBatchLoop(operatorIntents: new OperatorIntentCoordinator(store), sweep: current =>
            {
                // Negative control: moving ServiceOperatorIntentBoundary below this sweep must fail
                // here with Pending vs Applied, while setup and all assertions remain unchanged.
                Assert.Equal(OperatorIntentStatus.Applied, store.GetAsync(intent.Id).GetAwaiter().GetResult()!.Status);
                var durable = repository.LoadGoalsAsync([target.Id]).GetAwaiter().GetResult().GetGoal(target.Id);
                Assert.Equal(WorkTaskStatus.Assigned, durable.Tasks.Single().Status);
                Assert.Single(durable.Timeline.Where(item => item.OperatorIntentApplied?.IntentId == intent.Id));
                Assert.Equal(WorkTaskStatus.Failed, current.GetGoal(unrelated.Id).Tasks.Single().Status);
                Assert.Contains(current.Goals, goal => goal.Id == cleanup.Id);
                Assert.Equal(WorkTaskStatus.Failed, repository.LoadGoalsAsync([unrelated.Id]).GetAwaiter().GetResult().GetGoal(unrelated.Id).Tasks.Single().Status);
                events.Add("sweep");
                Assert.Equal(1, current.EvictTerminalGoalAggregates([cleanup.Id]));
                events.Add("cleanup");
                swept = true;
            }).Run(kernel, MakeDriver(), ConductorAutonomyPolicy.Conservative, NoStopPath(), maxIterations: 1,
                persistGoalTick: (current, ids) =>
                {
                    repository.SaveGoalSnapshotsAsync(ids.Select(current.ExportGoalSnapshot).ToArray()).GetAwaiter().GetResult();
                    if (!swept) events.Add("persist-target");
                });

            Assert.True(swept);
            Assert.Equal(new[] { "persist-target", "sweep", "cleanup" }, events);
        }
        finally { TryDeleteDirectory(root); }
    }

    [Xunit.Fact]
    public void NoIntent_StillRunsOrdinaryMaintenance()
    {
        var root = CreateTempDirectory("mcg-intent-no-intent");
        try
        {
            var (kernel, _) = SimpleGoal();
            var sweeps = 0;
            new ConductorBatchLoop(operatorIntents: new OperatorIntentCoordinator(Store(root)),
                sweep: _ => sweeps++).Run(kernel, MakeDriver(), ConductorAutonomyPolicy.Conservative,
                NoStopPath(), maxIterations: 1);
            Assert.Equal(1, sweeps);
        }
        finally { TryDeleteDirectory(root); }
    }

    [Xunit.Theory(Timeout = 30000)]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    [Xunit.Trait("Category", "CrossTick")]
    public async Task LaterArrival_WaitsForNextBoundaryWhileMaintenanceRuns(bool duringSweep)
    {
        var root = CreateTempDirectory("mcg-intent-fairness");
        try
        {
            var (kernel, firstGoal) = SimpleGoal();
            var other = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "Later target");
            foreach (var goal in new[] { firstGoal, other })
                kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec("mapping", ["criterion"],
                    VerificationClass.TestVerifiable, [], []));
            var store = Store(root);
            var first = await store.EnqueueAsync(Mapping(firstGoal, "first"));
            var lateSame = Mapping(firstGoal, "late-same");
            var lateOther = Mapping(other, "late-other");
            var injected = false;
            var sweeps = 0;
            void Inject()
            {
                if (injected) return;
                injected = true;
                store.EnqueueAsync(lateSame).GetAwaiter().GetResult();
                store.EnqueueAsync(lateOther).GetAwaiter().GetResult();
            }

            new ConductorBatchLoop(operatorIntents: new OperatorIntentCoordinator(store),
                prepareOperatorIntentTarget: (_, _) => { if (!duringSweep) Inject(); return true; },
                sweep: _ =>
                {
                    sweeps++;
                    Assert.Equal(OperatorIntentStatus.Applied, store.GetAsync(first.Id).GetAwaiter().GetResult()!.Status);
                    if (duringSweep) Inject();
                    var expected = sweeps == 1 ? OperatorIntentStatus.Pending : OperatorIntentStatus.Applied;
                    Assert.Equal(expected, store.GetAsync(lateSame.Id).GetAwaiter().GetResult()!.Status);
                    Assert.Equal(expected, store.GetAsync(lateOther.Id).GetAwaiter().GetResult()!.Status);
                }).Run(kernel, MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                    getRunningCount: () => ConductorAutonomyPolicy.Conservative.MaxConcurrentPaidWorkers),
                ConductorAutonomyPolicy.Conservative, NoStopPath(), maxIterations: 2,
                persistGoalTick: (_, _) => { });

            Assert.True(injected);
            Assert.Equal(2, sweeps);
            Assert.Single(firstGoal.Timeline.Where(item => item.OperatorIntentApplied?.IntentId == lateSame.Id));
            Assert.Single(other.Timeline.Where(item => item.OperatorIntentApplied?.IntentId == lateOther.Id));
        }
        finally { TryDeleteDirectory(root); }
    }

    [Xunit.Fact]
    public async Task UnresolvedTarget_RemainsQueuedUntilOrdinaryMaintenance()
    {
        var root = CreateTempDirectory("mcg-intent-deferred");
        try
        {
            var (kernel, goal) = SimpleGoal();
            kernel.ReportTaskProgress(goal.Id, goal.Tasks.Single().Id, WorkTaskStatus.Failed, "repair");
            var store = Store(root);
            var intent = await store.EnqueueAsync(Retry(goal, "deferred"));
            var prepared = false;
            var swept = false;
            new ConductorBatchLoop(operatorIntents: new OperatorIntentCoordinator(store),
                prepareOperatorIntentTarget: (_, _) => { prepared = true; return false; },
                goalReloadObservation: _ => new ConductorGoalReloadObservation.NotObserved(),
                sweep: _ =>
                {
                    Assert.True(prepared);
                    Assert.Equal(OperatorIntentStatus.Pending, store.GetAsync(intent.Id).GetAwaiter().GetResult()!.Status);
                    Assert.Equal(WorkTaskStatus.Failed, goal.Tasks.Single().Status);
                    swept = true;
                }).Run(kernel, MakeDriver(), ConductorAutonomyPolicy.Conservative, NoStopPath(), maxIterations: 1);
            Assert.True(swept);
            Assert.DoesNotContain(goal.Timeline, item => item.OperatorIntentApplied?.IntentId == intent.Id);
        }
        finally { TryDeleteDirectory(root); }
    }

    [Xunit.Fact(Timeout = 30000)]
    [Xunit.Trait("Category", "CrossTick")]
    public async Task WakeArrival_IsAppliedBeforeWakeMaintenance()
    {
        var root = CreateTempDirectory("mcg-intent-wake-boundary");
        try
        {
            var (kernel, goal) = SimpleGoal();
            var store = Store(root);
            var intent = Retry(goal, "wake");
            var enqueued = false;
            var sweepsAfterWake = 0;
            using var wake = new TestWakeSignal(() =>
            {
                store.EnqueueAsync(intent).GetAwaiter().GetResult();
                enqueued = true;
            });
            new ConductorBatchLoop(operatorIntents: new OperatorIntentCoordinator(store),
                sweep: _ =>
                {
                    if (!enqueued) return;
                    Assert.Equal(OperatorIntentStatus.Applied, store.GetAsync(intent.Id).GetAwaiter().GetResult()!.Status);
                    sweepsAfterWake++;
                }).Run(kernel, MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                    getRunningCount: () => ConductorAutonomyPolicy.Conservative.MaxConcurrentPaidWorkers),
                ConductorAutonomyPolicy.Conservative, NoStopPath(), maxIterations: 2,
                persistGoalTick: (_, _) => { }, watchInterval: TimeSpan.FromMinutes(1), wakeSignal: wake);
            Assert.True(enqueued);
            Assert.Equal(1, wake.SignaledWaits);
            Assert.Equal(2, sweepsAfterWake);
        }
        finally { TryDeleteDirectory(root); }
    }

    [Xunit.Fact(Timeout = 30000)]
    [Xunit.Trait("Category", "CrossTick")]
    public async Task CheckpointHold_PreservesClaimedEffectUntilRecoveryBeforeMaintenance()
    {
        var root = CreateTempDirectory("mcg-intent-held-boundary");
        try
        {
            var (kernel, goal) = SimpleGoal();
            var store = Store(root);
            var intent = await store.EnqueueAsync(Retry(goal, "held"));
            var sweeps = 0;
            var preparations = 0;
            var completedTicks = 0;
            new ConductorBatchLoop(operatorIntents: new OperatorIntentCoordinator(store),
                prepareOperatorIntentTarget: (_, _) => { preparations++; return true; },
                measuredSweepWithCheckpointHolds: (_, held) =>
                {
                    var row = store.GetAsync(intent.Id).GetAwaiter().GetResult()!;
                    Assert.Equal(sweeps == 0 ? OperatorIntentStatus.Claimed : OperatorIntentStatus.Applied, row.Status);
                    Assert.Equal(sweeps == 0, held.Contains(goal.Id.Value));
                    Assert.Equal(1, goal.AutomaticAcceptanceRetryCount);
                    Assert.Single(goal.Timeline.Where(item => item.OperatorIntentApplied?.IntentId == intent.Id));
                    sweeps++;
                    return null;
                }).Run(kernel, MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                    getRunningCount: () => ConductorAutonomyPolicy.Conservative.MaxConcurrentPaidWorkers),
                ConductorAutonomyPolicy.Conservative, NoStopPath(), maxIterations: 2,
                onTick: _ => completedTicks++,
                checkpointGoalTick: (_, ids) => ids.Select(id => new GoalSnapshotCheckpointResult(id.Value,
                    completedTicks == 0 ? GoalSnapshotCheckpointDisposition.Held : GoalSnapshotCheckpointDisposition.Durable,
                    null, "state", Path.Combine(root, "state.db"), "test-checkpoint")).ToArray());
            Assert.Equal(2, sweeps);
            Assert.Equal(1, preparations);
        }
        finally { TryDeleteDirectory(root); }
    }

    [Xunit.Fact]
    public async Task StopBeforeBoundary_DoesNotPrepareClaimOrMaintain()
    {
        var root = CreateTempDirectory("mcg-intent-stop");
        try
        {
            var (kernel, goal) = SimpleGoal();
            var store = Store(root);
            var intent = await store.EnqueueAsync(Retry(goal, "stopped"));
            var stop = Path.Combine(root, "stop");
            File.WriteAllText(stop, "stop");
            var calls = new List<string>();
            new ConductorBatchLoop(operatorIntents: new OperatorIntentCoordinator(store),
                prepareOperatorIntentTarget: (_, _) => { calls.Add("prepare"); return true; },
                sweep: _ => calls.Add("sweep")).Run(kernel, MakeDriver(),
                ConductorAutonomyPolicy.Conservative, stop, maxIterations: 1);
            Assert.Empty(calls);
            Assert.Equal(OperatorIntentStatus.Pending, (await store.GetAsync(intent.Id))!.Status);
        }
        finally { TryDeleteDirectory(root); }
    }

    private static SqliteOperatorIntentStore Store(string root) =>
        new(Path.Combine(root, "intents.db"), Path.Combine(root, "logs"));

    private static OperatorIntentRecord Retry(Goal goal, string id) =>
        Intent(goal, id, OperatorIntentVerbs.Retry, goal.Tasks.Single().Id.Value,
            new RetryOperatorIntentPayload("operator repair", null, RetryCause: RetryCause.ContractClarification));

    private static OperatorIntentRecord Mapping(Goal goal, string id) =>
        Intent(goal, id, OperatorIntentVerbs.CriterionEvidenceMap, null,
            new CriterionEvidenceMappingOperatorIntentPayload(0, 1, CriterionEvidenceOwner.Operator,
                "operator:replay", id, "candidate"));

    private static OperatorIntentRecord Intent(Goal goal, string id, string verb, string? taskId, object payload) =>
        new(id, id, verb, goal.Id.Value, taskId,
            JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            [], "operator", "test", "local-process", new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero));
}
