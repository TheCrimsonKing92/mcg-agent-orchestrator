using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: isolated SQLite state, no real workers or shared build resources.
public sealed class ConductorBatchLoopTestsOperatorIntentBatch(ITestOutputHelper output)
    : ConductorBatchLoopTests(output)
{
    [Fact]
    public async Task SevenMappings_OneTick_PersistsGoalOnceAndPublishesAllRows()
    {
        var root = CreateTempDirectory("mcg-loop-intent-batch");
        try
        {
            var (kernel, goal) = CreateGoal();
            var store = new SqliteOperatorIntentStore(Path.Combine(root, "intents.db"), Path.Combine(root, "logs"));
            var intents = await EnqueueMappings(store, goal);
            var persisted = new List<GoalId>();
            var persistCalls = 0;
            var detachPersistCalls = 0;
            var completedTicks = 0;
            var durableSnapshot = kernel.ExportSnapshot();

            var summary = new ConductorBatchLoop(operatorIntents: new OperatorIntentCoordinator(store)).Run(
                kernel,
                MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                    getRunningCount: () => ConductorAutonomyPolicy.Conservative.MaxConcurrentPaidWorkers),
                ConductorAutonomyPolicy.Conservative, NoStopPath(), maxIterations: 1,
                onTick: tick =>
                {
                    Assert.Equal(1, tick.Tick);
                    Assert.Equal(1, persistCalls);
                    AssertBatchState(AgentOrchestratorKernel.FromSnapshot(durableSnapshot).GetGoal(goal.Id), intents);
                    foreach (var intent in intents)
                        Assert.Equal(OperatorIntentStatus.Applied, store.GetAsync(intent.Id).GetAwaiter().GetResult()!.Status);
                    completedTicks++;
                },
                persistGoalTick: (current, ids) =>
                {
                    Assert.Equal(goal.Id, Assert.Single(ids));
                    // Max-iterations shutdown checkpoints the detached goal after the tick has published its intents.
                    if (completedTicks > 0)
                    {
                        detachPersistCalls++;
                        foreach (var intent in intents)
                            Assert.Equal(OperatorIntentStatus.Applied, store.GetAsync(intent.Id).GetAwaiter().GetResult()!.Status);
                        return;
                    }

                    persistCalls++;
                    persisted.AddRange(ids);
                    foreach (var intent in intents)
                        Assert.Equal(OperatorIntentStatus.Claimed, store.GetAsync(intent.Id).GetAwaiter().GetResult()!.Status);
                    durableSnapshot = current.ExportSnapshot();
                });

            Assert.Equal(1, summary.Ticks);
            Assert.Equal(1, completedTicks);
            Assert.Equal(1, persistCalls);
            Assert.Equal(1, detachPersistCalls);
            Assert.Equal(goal.Id, Assert.Single(persisted));
            var durableGoal = AgentOrchestratorKernel.FromSnapshot(durableSnapshot).GetGoal(goal.Id);
            AssertBatchState(durableGoal, intents);
            foreach (var intent in intents)
                Assert.Equal(OperatorIntentStatus.Applied, (await store.GetAsync(intent.Id))!.Status);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SevenMappings_TickPersistFails_PublishesNoneAndKeepsBatchClaimed()
    {
        var root = CreateTempDirectory("mcg-loop-intent-batch-failed-persist");
        try
        {
            var (kernel, goal) = CreateGoal();
            var store = new SqliteOperatorIntentStore(Path.Combine(root, "intents.db"), Path.Combine(root, "logs"));
            var intents = await EnqueueMappings(store, goal);
            var persistAttempts = 0;
            var sleeps = 0;

            _ = new ConductorBatchLoop(operatorIntents: new OperatorIntentCoordinator(store)).Run(
                kernel,
                MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                    getRunningCount: () => ConductorAutonomyPolicy.Conservative.MaxConcurrentPaidWorkers),
                ConductorAutonomyPolicy.Conservative, NoStopPath(), maxIterations: 2,
                watchInterval: TimeSpan.FromSeconds(1),
                sleepFunc: _ => { sleeps++; return true; },
                persistTick: _ => { persistAttempts++; throw SqliteBusy(); },
                keepAliveWhenIdle: true, busyWriteDelay: _ => { });

            Assert.True(persistAttempts > 0);
            Assert.Equal(1, sleeps);
            AssertBatchState(goal, intents);
            foreach (var intent in intents)
            {
                var row = await store.GetAsync(intent.Id);
                Assert.Equal(OperatorIntentStatus.Claimed, row!.Status);
                Assert.Null(row.CompletedAt);
                Assert.Null(row.Outcome);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal) CreateGoal()
    {
        var (kernel, goal) = SimpleGoal("Batch criterion mappings in the conductor");
        kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec("Batch mappings",
            Enumerable.Range(0, 7).Select(index => $"Criterion {index}").ToArray(),
            VerificationClass.RealWorldDependent, [], []));
        return (kernel, goal);
    }

    private static async Task<OperatorIntentRecord[]> EnqueueMappings(SqliteOperatorIntentStore store, Goal goal)
    {
        var intents = new List<OperatorIntentRecord>();
        var createdAt = new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
        for (var index = 0; index < 7; index++)
            intents.Add(await store.EnqueueAsync(new OperatorIntentRecord($"mapping-{index}", $"key-{index}",
                OperatorIntentVerbs.CriterionEvidenceMap, goal.Id.Value, null,
                JsonSerializer.Serialize(new CriterionEvidenceMappingOperatorIntentPayload(index, 1,
                    CriterionEvidenceOwner.Operator, "operator:replay", $"finding-{index}", "candidate"),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)), [], "operator", "cli", "local-process",
                createdAt.AddTicks(index))));
        return intents.ToArray();
    }

    private static void AssertBatchState(Goal goal, IReadOnlyList<OperatorIntentRecord> intents)
    {
        Assert.Equal(7, goal.CriterionEvidenceObligations.Count);
        Assert.Equal(Enumerable.Range(0, 7), goal.CriterionEvidenceObligations.Select(item => item.CriterionIndex).Order());
        Assert.Equal(intents.Select(intent => intent.Id), goal.Timeline
            .Where(item => item.OperatorIntentApplied is not null).Select(item => item.OperatorIntentApplied!.IntentId));
    }
}
