using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsStewardHarvestVersion
{
    [Xunit.Fact]
    public async Task Unchanged_failed_attempt_uses_harvest_version_after_goal_save()
    {
        using var harness = new StewardHarness("B");
        StewardHarvestFixture.SeedPlannerFailure(harness);
        long version = 7;
        var host = StewardHarvestFixture.CreateHost(harness, _ => version);
        harness.Model.Reply(StewardHarvestFixture.Route(harness));

        host.ServiceTick(harness.Kernel);
        await harness.Model.Started.Task;
        await host.CurrentRound!;
        version = 8;
        host.ServiceTick(harness.Kernel);

        var intent = Xunit.Assert.Single(await harness.Intents.ListForGoalAsync(harness.Goal.Id.Value));
        Xunit.Assert.Equal(OperatorIntentVerbs.Adjudicate, intent.Verb);
        var payload = JsonSerializer.Deserialize<AdjudicateOperatorIntentPayload>(intent.PayloadJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Xunit.Assert.Equal(8, payload?.ExpectedGoalStateVersion);
        var decisions = File.ReadAllText(harness.ConductPath);
        Xunit.Assert.Contains("reason=route-submitted", decisions);
        Xunit.Assert.Contains("claimed_goal_version=7 harvest_goal_version=8", decisions);
        Xunit.Assert.DoesNotContain("reason=trigger-superseded", decisions);
    }
}

internal static class StewardHarvestFixture
{
    internal static readonly DateTimeOffset DispatchAt = new(2026, 9, 27, 9, 15, 54, TimeSpan.Zero);
    internal static readonly DateTimeOffset StartedAt = DispatchAt.AddSeconds(2);

    internal static void SeedPlannerFailure(StewardHarness harness)
    {
        harness.Kernel.RecordTaskDispatch(harness.Goal.Id, harness.Task.Id,
            new TaskDispatchRecord("test-worker", "test.exe", harness.Root, DispatchAt,
                BaseCommit: StewardHarness.Sha));
        harness.Kernel.RecordTaskVerification(harness.Goal.Id, harness.Task.Id,
            new TaskVerificationRecord("test.exe", harness.Root, 1,
                "WORKER_RESULT: files: none END_WORKER_RESULT",
                "rule=planner-output-contract-rejected", StartedAt.AddSeconds(1),
                WorkerResultPresent: true, DispatchStartedAt: StartedAt,
                CompletionVerdictRule: "planner-output-contract-rejected"));
        harness.Kernel.ReportTaskProgress(harness.Goal.Id, harness.Task.Id, WorkTaskStatus.Failed, "failed");
    }

    internal static ConductorStewardHost CreateHost(StewardHarness harness, Func<GoalId, long?> version) =>
        new(new ConductorStewardTriggerStore(Path.Combine(harness.Root, "steward-triggers.db")),
            new ConductorStewardTriggerDetector(), harness.Model, harness.Intents,
            new AdjudicationEvidenceResolver(harness.Root), version, _ => harness.Root,
            new GoalLifecycleEventWriter(Path.Combine(harness.Root, "lifecycle")),
            new ConductEventLogWriter(harness.ConductPath));

    internal static string Route(StewardHarness harness) => JsonSerializer.Serialize(new
    {
        kind = "route", targetTaskId = harness.Task.Id.Value,
        cause = "ContractClarification", text = "Named failure diagnosis",
        instruction = "Apply the named repair and check the evidence",
        evidenceReferences = new[] { "worker-output=receipt-1" },
        reversibility = "reversible-with-cost", precedent = "model-proposed-example"
    });
}
