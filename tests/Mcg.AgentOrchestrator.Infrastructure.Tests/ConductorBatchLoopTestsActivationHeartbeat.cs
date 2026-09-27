using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ConductorBatchLoopTestsActivationHeartbeat(ITestOutputHelper output)
    : ConductorBatchLoopTests(output)
{
    [Xunit.Fact]
    public async Task OperatorIntentIdleCycleEmitsOneCompletedTickHeartbeat()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mcg-activation-intent-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var (kernel, goal) = SimpleGoal("Reject an operator intent while idle");
            kernel.ParkGoal(goal.Id, "parked");
            var store = new SqliteOperatorIntentStore(
                Path.Combine(root, "operator-intents.db"), Path.Combine(root, "logs"));
            var intent = new OperatorIntentRecord(
                "activation-intent", "activation-intent-key", OperatorIntentVerbs.Retry,
                goal.Id.Value, TaskId.New().Value,
                JsonSerializer.Serialize(
                    new RetryOperatorIntentPayload("invalid task", null, RetryCause: RetryCause.ContractClarification),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                [], "operator", "test", "test", DateTimeOffset.UtcNow);
            await store.EnqueueAsync(intent);

            var sleeps = 0;
            var outputText = AsyncLocalConsoleRouter.Capture(() =>
            {
                new ConductorBatchLoop(operatorIntents: new OperatorIntentCoordinator(store)).Run(
                    kernel, MakeDriver(), ConductorAutonomyPolicy.Conservative, NoStopPath(),
                    maxIterations: 3, watchInterval: TimeSpan.FromSeconds(1),
                    sleepFunc: _ => ++sleeps == 2, keepAliveWhenIdle: true,
                    emitActivationHeartbeat: true);
            });

            Assert.Equal(OperatorIntentStatus.Rejected, (await store.GetAsync(intent.Id))!.Status);
            Assert.Equal(2, sleeps);
            var lines = outputText.Split('\n', StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries);
            Assert.Equal(1, lines.Count(line => line.StartsWith("TICK_END tick=1 activation=true ",
                StringComparison.Ordinal)));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact]
    public void SupervisedIdleCyclesEmitCompletedTickHeartbeats()
    {
        var sleeps = 0;
        var outputText = AsyncLocalConsoleRouter.Capture(() =>
        {
            new ConductorBatchLoop().Run(
                new AgentOrchestratorKernel(), MakeDriver(), ConductorAutonomyPolicy.Conservative,
                NoStopPath(), maxIterations: 5, watchInterval: TimeSpan.FromSeconds(1),
                sleepFunc: _ => ++sleeps == 3, keepAliveWhenIdle: true,
                emitActivationHeartbeat: true);
        });

        Assert.Equal(3, sleeps);
        var lines = outputText.Split('\n', StringSplitOptions.RemoveEmptyEntries |
            StringSplitOptions.TrimEntries);
        Assert.Equal(3, lines.Count(line => line.StartsWith("TICK_END tick=1 activation=true ",
            StringComparison.Ordinal)));
    }

    [Xunit.Fact]
    public void SupervisedQuietWorkerTicksEmitCompletedTickHeartbeats()
    {
        var (kernel, _) = SimpleGoal();
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningCount: () => ConductorAutonomyPolicy.Conservative.MaxConcurrentPaidWorkers);
        var outputText = AsyncLocalConsoleRouter.Capture(() =>
        {
            new ConductorBatchLoop().Run(kernel, driver, ConductorAutonomyPolicy.Conservative,
                NoStopPath(), maxIterations: 4, watchInterval: TimeSpan.FromSeconds(1),
                sleepFunc: _ => false, emitActivationHeartbeat: true);
        });

        var lines = outputText.Split('\n', StringSplitOptions.RemoveEmptyEntries |
            StringSplitOptions.TrimEntries);
        Assert.Equal(4, lines.Count(line => line.StartsWith("TICK_END tick=", StringComparison.Ordinal) &&
            line.Contains(" activation=true ", StringComparison.Ordinal)));
    }
}
