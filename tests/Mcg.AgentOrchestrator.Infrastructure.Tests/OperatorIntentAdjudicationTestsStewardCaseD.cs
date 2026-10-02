using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: each test uses unique SQLite/filesystem stores and an injected head and clock.
public sealed class OperatorIntentAdjudicationTestsStewardCaseD
{
    [Xunit.Theory]
    [Xunit.InlineData("tester")]
    [Xunit.InlineData("reviewer")]
    [Xunit.InlineData("A")]
    [Xunit.InlineData("B")]
    [Xunit.InlineData("C")]
    [Xunit.InlineData("moved-head")]
    [Xunit.InlineData("committed-result")]
    [Xunit.InlineData("unrelated-retry")]
    [Xunit.InlineData("confirmed-red")]
    [Xunit.InlineData("cap-spent")]
    public async Task Ineligible_Steward_close_rejects_without_task_mutation(string invalidCase)
    {
        using var harness = new StewardCaseDHarness(invalidCase switch
        {
            "tester" => AgentRole.Tester,
            "reviewer" => AgentRole.Reviewer,
            _ => AgentRole.Developer
        });
        harness.Seed(acceptanceRetry: invalidCase != "unrelated-retry", confirmedRed: invalidCase == "confirmed-red",
            resultCommit: invalidCase == "committed-result" ? StewardCaseDHarness.OtherSha : null);
        if (invalidCase == "moved-head") harness.Head = StewardCaseDHarness.OtherSha;
        if (invalidCase == "cap-spent") harness.SeedRegates(harness.Sources.RegateCap);
        var before = Snapshot(harness);
        var count = AcceptanceFailingTestIndex.CountRegates(harness.Index.Read(), harness.Goal.Id.Value);
        var intent = await harness.EnqueueClose(invalidCase is "A" or "B" or "C" ? invalidCase : "D");

        var result = harness.Coordinator.ExecutePending(harness.Kernel, harness.Goal);

        Assert.False(result.MutatedGoalState);
        Assert.Equal(before, Snapshot(harness));
        Assert.Equal(WorkTaskStatus.Failed, harness.Task.Status);
        Assert.Equal(count, AcceptanceFailingTestIndex.CountRegates(harness.Index.Read(), harness.Goal.Id.Value));
        var decision = await harness.Decisions.GetDecisionStateAsync($"adjudicate-{intent.Id}");
        Assert.Equal(EffectReceiptStatus.Rejected, decision?.Effect?.Status);
        Assert.Equal("steward-capability-boundary", decision!.Effect!.Result);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task Unavailable_head_rejects_case_D_without_recording_regate(bool throws)
    {
        using var harness = new StewardCaseDHarness();
        harness.Seed();
        var coordinator = harness.CreateCoordinator(_ => throws ? throw new IOException("head unavailable") : null);
        var before = Snapshot(harness);
        var intent = await harness.EnqueueClose();

        coordinator.ExecutePending(harness.Kernel, harness.Goal);

        Assert.Equal(before, Snapshot(harness));
        Assert.Equal(0, AcceptanceFailingTestIndex.CountRegates(harness.Index.Read(), harness.Goal.Id.Value));
        var decision = await harness.Decisions.GetDecisionStateAsync($"adjudicate-{intent.Id}");
        Assert.Equal("steward-capability-boundary", decision?.Effect?.Result);
    }

    [Xunit.Fact]
    public async Task Missing_regate_index_rejects_case_D_close_at_the_boundary()
    {
        using var harness = new StewardCaseDHarness();
        harness.Seed();
        var coordinator = new OperatorIntentCoordinator(harness.Intents,
            goalHeadResolver: _ => StewardCaseDHarness.Sha, decisions: harness.Decisions,
            goalStateVersionResolver: _ => StewardCaseDHarness.Version);
        var before = Snapshot(harness);
        var intent = await harness.EnqueueClose();

        coordinator.ExecutePending(harness.Kernel, harness.Goal);

        Assert.Equal(before, Snapshot(harness));
        var decision = await harness.Decisions.GetDecisionStateAsync($"adjudicate-{intent.Id}");
        Assert.Equal("steward-capability-boundary", decision?.Effect?.Result);
        Assert.Equal(0, AcceptanceFailingTestIndex.CountRegates(harness.Index.Read(), harness.Goal.Id.Value));
    }

    [Xunit.Fact]
    public async Task Repeated_direct_close_is_rejected_for_the_same_task_and_candidate()
    {
        using var harness = new StewardCaseDHarness();
        harness.Seed();
        var first = await harness.EnqueueClose();
        harness.Coordinator.ExecutePending(harness.Kernel, harness.Goal);
        harness.Coordinator.CompletePersisted([harness.Goal.Id]);
        Assert.Equal(WorkTaskStatus.Completed, harness.Task.Status);
        var firstDecision = await harness.Decisions.GetDecisionStateAsync($"adjudicate-{first.Id}");
        Assert.Equal(EffectReceiptStatus.Applied, firstDecision?.Effect?.Status);
        harness.Seed();
        var before = Snapshot(harness);
        var second = await harness.EnqueueClose();

        harness.Coordinator.ExecutePending(harness.Kernel, harness.Goal);

        Assert.Equal(before, Snapshot(harness));
        Assert.Equal(1, AcceptanceFailingTestIndex.CountRegates(harness.Index.Read(), harness.Goal.Id.Value));
        var decision = await harness.Decisions.GetDecisionStateAsync($"adjudicate-{second.Id}");
        Assert.Equal("steward-capability-boundary", decision?.Effect?.Result);
    }

    [Xunit.Fact]
    public async Task Failed_index_write_rejects_close_without_completing_Developer()
    {
        using var harness = new StewardCaseDHarness();
        harness.Seed();
        var blockedPath = Path.Combine(harness.Root, "index-is-a-directory");
        Directory.CreateDirectory(blockedPath);
        var blockedIndex = new AcceptanceFailingTestIndex(blockedPath);
        var coordinator = harness.CreateCoordinator(index: blockedIndex);
        var before = Snapshot(harness);
        var intent = await harness.EnqueueClose();

        var result = coordinator.ExecutePending(harness.Kernel, harness.Goal);

        Assert.False(result.MutatedGoalState);
        Assert.Equal(before, Snapshot(harness));
        Assert.Equal(WorkTaskStatus.Failed, harness.Task.Status);
        Assert.Empty(blockedIndex.Read());
        var decision = await harness.Decisions.GetDecisionStateAsync($"adjudicate-{intent.Id}");
        Assert.Equal(EffectReceiptStatus.Rejected, decision?.Effect?.Status);
        Assert.Equal("apparatus-regate-unrecorded", decision!.Effect!.Result);
    }

    [Xunit.Fact]
    public async Task Apply_rechecks_head_after_decision_before_recording_regate()
    {
        using var harness = new StewardCaseDHarness();
        harness.Seed();
        var calls = 0;
        var coordinator = harness.CreateCoordinator(_ => ++calls == 1 ? StewardCaseDHarness.Sha : StewardCaseDHarness.OtherSha);
        var intent = await harness.EnqueueClose();
        var before = Snapshot(harness);

        coordinator.ExecutePending(harness.Kernel, harness.Goal);

        Assert.Equal(2, calls);
        Assert.Equal(before, Snapshot(harness));
        Assert.Equal(0, AcceptanceFailingTestIndex.CountRegates(harness.Index.Read(), harness.Goal.Id.Value));
        var decision = await harness.Decisions.GetDecisionStateAsync($"adjudicate-{intent.Id}");
        Assert.Equal("steward-capability-boundary", decision?.Effect?.Result);
    }

    private static string Snapshot(StewardCaseDHarness harness) => JsonSerializer.Serialize(
        harness.Kernel.ExportGoalSnapshot(harness.Goal.Id) with { Timeline = [] }, OperatorIntentJson.Options);
}
