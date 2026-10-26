using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

public sealed class StorageRetentionTerminalGoalAttemptTests
{
    [Xunit.Fact]
    public void CompletedFlatFinalAndFailureAttemptsAreRemovedAsUnits()
    {
        using var fixture = new RetentionReclaimFixture();
        var oldFailure = fixture.WriteFlatAttempt(RetentionReclaimFixture.CompletedId, "old-failure", 2, 30, failed: true);
        var oldFinal = fixture.WriteFlatAttempt(RetentionReclaimFixture.CompletedId, "old-final", 3, 30);
        var young = fixture.WriteFlatAttempt(RetentionReclaimFixture.CompletedId, "young", 1, 2);
        var active = fixture.WriteFlatAttempt(RetentionReclaimFixture.ActiveId, "active-old", 1, 30);

        var result = fixture.Run(null,
            fixture.Goal(RetentionReclaimFixture.CompletedId, GoalStatus.Completed),
            fixture.Goal(RetentionReclaimFixture.ActiveId, GoalStatus.Active));

        Assert.False(File.Exists(oldFailure));
        Assert.False(File.Exists(oldFinal));
        Assert.False(File.Exists(oldFailure[..^".out.log".Length] + ".attempt.json"));
        Assert.False(File.Exists(oldFinal[..^".out.log".Length] + ".attempt.json"));
        Assert.True(File.Exists(young));
        Assert.True(File.Exists(active));
        Assert.Contains(result.Decisions, decision => decision.Path == oldFailure &&
            decision.Action == EvidenceRetentionAction.Deleted &&
            decision.Reason == "terminal-goal-attempt-past-age");
        Assert.Contains(result.Decisions, decision => decision.Path == oldFinal &&
            decision.Action == EvidenceRetentionAction.Deleted &&
            decision.Reason == "terminal-goal-attempt-past-age");
    }

    [Xunit.Fact]
    public void CompletedOldFinalAndFailureAttemptsAreRemovedAsUnits()
    {
        using var fixture = new RetentionReclaimFixture();
        var oldFailure = fixture.WriteAttempt(RetentionReclaimFixture.CompletedId, "old-failure", 2, 30, failed: true);
        var oldFinal = fixture.WriteAttempt(RetentionReclaimFixture.CompletedId, "old-final", 3, 30);
        var young = fixture.WriteAttempt(RetentionReclaimFixture.CompletedId, "young", 1, 2);
        var active = fixture.WriteAttempt(RetentionReclaimFixture.ActiveId, "active-old", 1, 30);
        fixture.WriteAttempt(RetentionReclaimFixture.FailedId, "failed-old", 1, 30);

        var result = fixture.Run(null,
            fixture.Goal(RetentionReclaimFixture.CompletedId, GoalStatus.Completed),
            fixture.Goal(RetentionReclaimFixture.ActiveId, GoalStatus.Active),
            fixture.Goal(RetentionReclaimFixture.FailedId, GoalStatus.Failed));

        Assert.False(File.Exists(oldFailure));
        Assert.False(File.Exists(oldFinal));
        Assert.True(File.Exists(young));
        Assert.True(File.Exists(active));
        Assert.DoesNotContain(result.Decisions, decision => decision.GoalId == RetentionReclaimFixture.FailedId &&
            decision.Reason == "terminal-goal-attempt-past-age");
        Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(
            Path.Combine(fixture.AttemptRoot(), RetentionReclaimFixture.CompletedId)),
            path => Path.GetFileName(path).StartsWith("old-", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Decisions, decision => decision.Action == EvidenceRetentionAction.Deleted &&
            decision.Reason == "terminal-goal-attempt-past-age" && decision.AttemptId == "old-final");
    }

    [Xunit.Fact]
    public void YoungCompletedFinalAttemptIsPreserved()
    {
        using var fixture = new RetentionReclaimFixture();
        var young = fixture.WriteAttempt(RetentionReclaimFixture.CompletedId, "young-final", 2, 2);

        fixture.Run(null, fixture.Goal(RetentionReclaimFixture.CompletedId, GoalStatus.Completed));

        Assert.True(File.Exists(young));
        Assert.True(File.Exists(Path.Combine(fixture.AttemptRoot(),
            RetentionReclaimFixture.CompletedId, "young-final.attempt.json")));
    }

    [Xunit.Fact]
    public void FailedPayloadDeletionKeepsAttemptMetadata()
    {
        using var fixture = new RetentionReclaimFixture();
        var payload = fixture.WriteAttempt(RetentionReclaimFixture.CompletedId, "locked", 1, 30);
        fixture.WriteAttempt(RetentionReclaimFixture.CompletedId, "later-final", 2, 30);
        var metadata = Path.Combine(fixture.AttemptRoot(), RetentionReclaimFixture.CompletedId,
            "locked.attempt.json");
        using var locked = new FileStream(payload, FileMode.Open, FileAccess.Read, FileShare.None);

        var result = fixture.Run(null,
            fixture.Goal(RetentionReclaimFixture.CompletedId, GoalStatus.Completed));

        Assert.True(File.Exists(metadata));
        Assert.Contains(result.Decisions, decision => decision.Path == Path.GetDirectoryName(payload) &&
            decision.Action == EvidenceRetentionAction.DeferredLocked);
    }
}
