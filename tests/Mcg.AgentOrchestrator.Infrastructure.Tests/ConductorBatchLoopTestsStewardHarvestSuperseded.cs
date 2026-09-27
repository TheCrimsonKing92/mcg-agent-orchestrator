using Mcg.AgentOrchestrator.Core;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsStewardHarvestSuperseded
{
    [Xunit.Theory]
    [Xunit.InlineData("new-dispatch")]
    [Xunit.InlineData("new-verification")]
    [Xunit.InlineData("task-recovered")]
    [Xunit.InlineData("trigger-gone")]
    public async Task Changed_target_attempt_or_trigger_discards_round(string change)
    {
        using var harness = new StewardHarness("B");
        StewardHarvestFixture.SeedPlannerFailure(harness);
        var host = StewardHarvestFixture.CreateHost(harness, _ => 7);
        harness.Model.Reply(StewardHarvestFixture.Route(harness));
        harness.Model.Hold();
        host.ServiceTick(harness.Kernel);
        await harness.Model.Started.Task;

        switch (change)
        {
            case "new-dispatch":
                harness.Kernel.RetryTaskWithAuthoritativeFeedback(harness.Goal.Id, harness.Task.Id,
                    "Retry before new dispatch", RetryCause.ContractClarification);
                harness.Kernel.RecordTaskDispatch(harness.Goal.Id, harness.Task.Id,
                    new TaskDispatchRecord("test-worker", "test.exe", harness.Root,
                        StewardHarvestFixture.StartedAt.AddSeconds(2),
                        BaseCommit: StewardHarness.Sha));
                Xunit.Assert.Equal(StewardHarvestFixture.StartedAt.AddSeconds(2),
                    harness.Task.LastDispatch?.DispatchedAt);
                break;
            case "new-verification":
                RecordVerification(harness, StewardHarvestFixture.StartedAt.AddSeconds(2),
                    "planner-output-contract-rejected");
                break;
            case "task-recovered":
                harness.Kernel.RetryTaskWithAuthoritativeFeedback(harness.Goal.Id, harness.Task.Id,
                    "Owner supplied repair", RetryCause.ContractClarification);
                break;
            case "trigger-gone":
                RecordVerification(harness, StewardHarvestFixture.StartedAt, "other-rule");
                break;
        }

        harness.Model.Release();
        await host.CurrentRound!;
        host.ServiceTick(harness.Kernel);

        Xunit.Assert.Empty(await harness.Intents.ListForGoalAsync(harness.Goal.Id.Value));
        var decisions = File.ReadAllText(harness.ConductPath);
        Xunit.Assert.Contains("kind=no-action", decisions);
        Xunit.Assert.Contains("reason=trigger-superseded", decisions);
    }

    private static void RecordVerification(StewardHarness harness, DateTimeOffset startedAt, string rule) =>
        harness.Kernel.RecordTaskVerification(harness.Goal.Id, harness.Task.Id,
            new TaskVerificationRecord("test.exe", harness.Root, 1,
                "WORKER_RESULT: files: none END_WORKER_RESULT", $"rule={rule}",
                startedAt.AddSeconds(1), WorkerResultPresent: true,
                DispatchStartedAt: startedAt, CompletionVerdictRule: rule));
}
