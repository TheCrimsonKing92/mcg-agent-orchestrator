using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

public sealed class StorageRetentionTerminalGoalAttemptTests
{
    [Xunit.Fact]
    public void CompletedOldFinalAndFailureAttemptsAreRemovedAsUnits()
    {
        using var fixture = new RetentionReclaimFixture();
        var oldFailure = fixture.WriteAttempt(RetentionReclaimFixture.CompletedId, "old-failure", 2, 30, failed: true);
        var oldFinal = fixture.WriteAttempt(RetentionReclaimFixture.CompletedId, "old-final", 3, 30);
        var young = fixture.WriteAttempt(RetentionReclaimFixture.CompletedId, "young", 1, 2);
        var active = fixture.WriteAttempt(RetentionReclaimFixture.ActiveId, "active-old", 1, 30);
        var failedGoal = fixture.WriteAttempt(RetentionReclaimFixture.FailedId, "failed-old", 1, 30);

        var result = fixture.Run(null,
            fixture.Goal(RetentionReclaimFixture.CompletedId, GoalStatus.Completed),
            fixture.Goal(RetentionReclaimFixture.ActiveId, GoalStatus.Active),
            fixture.Goal(RetentionReclaimFixture.FailedId, GoalStatus.Failed));

        Assert.False(File.Exists(oldFailure));
        Assert.False(File.Exists(oldFinal));
        Assert.True(File.Exists(young));
        Assert.True(File.Exists(active));
        Assert.True(File.Exists(failedGoal));
        Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(
            Path.Combine(fixture.AttemptRoot(), RetentionReclaimFixture.CompletedId)),
            path => Path.GetFileName(path).StartsWith("old-", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Decisions, decision => decision.Action == EvidenceRetentionAction.Deleted &&
            decision.Reason == "terminal-goal-attempt-past-age" && decision.AttemptId == "old-final");
    }
}
