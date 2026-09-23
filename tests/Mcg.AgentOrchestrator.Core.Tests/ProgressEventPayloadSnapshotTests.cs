using Mcg.AgentOrchestrator.Core;

public sealed class ProgressEventPayloadSnapshotTests
{
    [Xunit.Fact]
    public void ToSnapshot_OperatorIntentAppliedPayload_RoundTrips()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal(
            "Persist operator intent application.",
            [new TaskSpec(TaskId.New(), "Implement.", AgentRole.Developer)]);
        const string intentId = "intent-123";
        goal.Append(new ProgressEvent(
            goal.Id,
            null,
            ProgressKind.GoalPolicyDecision,
            "Display text without an application marker.",
            clock.UtcNow,
            OperatorIntentApplied: new OperatorIntentAppliedPayload(
                intentId,
                "retry",
                null,
                "operator",
                "cli",
                "local-process")));

        var restored = Goal.FromSnapshot(goal.ToSnapshot());

        var restoredEvent = Assert.Single(
            restored.Timeline,
            item => item.OperatorIntentApplied?.IntentId == intentId);
        Assert.Equal(intentId, restoredEvent.OperatorIntentApplied!.IntentId);
    }

    [Xunit.Fact]
    public void ToSnapshot_HumanInputSupersededPayload_RoundTrips()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal(
            "Persist supersede resolution evidence.",
            [new TaskSpec(TaskId.New(), "Implement.", AgentRole.Developer)]);
        goal.Append(new ProgressEvent(
            goal.Id,
            null,
            ProgressKind.HumanInputSuperseded,
            "Display text with resolvedStableIds=other.",
            clock.UtcNow,
            HumanInputSuperseded: new HumanInputSupersededPayload(["finding-b", "finding-a"])));

        var restored = Goal.FromSnapshot(goal.ToSnapshot());

        var restoredEvent = Assert.Single(
            restored.Timeline,
            item => item.HumanInputSuperseded is not null);
        Assert.Equal(
            ["finding-b", "finding-a"],
            Assert.IsType<HumanInputSupersededPayload>(restoredEvent.HumanInputSuperseded).ResolvedStableIds);
    }
}
