using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

public sealed class StorageRetentionOwnerlessAttemptTests
{
    [Xunit.Fact]
    public void OldOperatorAndUnknownGoalEntriesAreDeletedAndCounted()
    {
        using var fixture = new RetentionReclaimFixture();
        var oldOperator = fixture.WriteAgedFile(Path.Combine(fixture.AttemptRoot(), "operator", "old", "payload.txt"), 30);
        var youngOperator = fixture.WriteAgedFile(Path.Combine(fixture.AttemptRoot(), "operator", "young", "payload.txt"), 2);
        var withinConfiguredAge = fixture.WriteAgedFile(Path.Combine(fixture.AttemptRoot(), "operator", "within-configured-age", "payload.txt"), 20);
        var unknown = fixture.WriteAgedFile(Path.Combine(fixture.AttemptRoot(), "not-a-goal-id", "payload.txt"), 30);
        var options = StorageRetentionReclaimOptions.Default with { OwnerlessAttemptMaxAge = TimeSpan.FromDays(21) };

        var result = fixture.Run(options, fixture.Goal(RetentionReclaimFixture.ActiveId, GoalStatus.Active));

        Assert.False(File.Exists(oldOperator));
        Assert.False(File.Exists(unknown));
        Assert.True(File.Exists(youngOperator));
        Assert.True(File.Exists(withinConfiguredAge));
        Assert.Equal(2, result.AcceptanceArtifactsDeleted);
        Assert.Contains(result.Decisions, decision => decision.Action == EvidenceRetentionAction.Deleted &&
            decision.Reason == "ownerless-operator-attempt-past-age");
        Assert.Contains(result.Decisions, decision => decision.Action == EvidenceRetentionAction.Deleted &&
            decision.Reason == "unknown-goal-directory-past-age");
    }

    [Xunit.Fact]
    public void UnloadableGoalStandInDoesNotBecomeAnUnknownDirectory()
    {
        using var fixture = new RetentionReclaimFixture();
        var protectedFile = fixture.WriteAgedFile(Path.Combine(fixture.AttemptRoot(),
            RetentionReclaimFixture.CompletedId, "payload.txt"), 30);
        var standIn = fixture.Goal(RetentionReclaimFixture.CompletedId, GoalStatus.Active) with
        {
            IsStoreStandIn = true
        };

        var result = fixture.Run(null,
            fixture.Goal(RetentionReclaimFixture.ActiveId, GoalStatus.Active), standIn);

        Assert.True(File.Exists(protectedFile));
        Assert.Contains(result.Decisions, decision => decision.Path == Path.GetDirectoryName(protectedFile) &&
            decision.Reason == "goal-directory-has-no-unique-full-id-owner");
    }

    [Xunit.Fact]
    public void AcceptanceGraceWinsOverShorterOwnerlessAge()
    {
        using var fixture = new RetentionReclaimFixture();
        var file = fixture.WriteAgedFile(Path.Combine(fixture.AttemptRoot(), "operator", "within-grace", "payload.txt"), 10);
        var options = StorageRetentionReclaimOptions.Default with { OwnerlessAttemptMaxAge = TimeSpan.FromDays(1) };

        var result = fixture.Run(options, fixture.Goal(RetentionReclaimFixture.ActiveId, GoalStatus.Active));

        Assert.True(File.Exists(file));
        Assert.Contains(result.Decisions, decision => decision.Path == Path.GetDirectoryName(file) &&
            decision.Action == EvidenceRetentionAction.Preserved && decision.Reason == "within-grace-period");
    }

    [Xunit.Fact]
    public void EmptyGoalStoreFailsClosedForUnknownDirectoryButStillReclaimsOperatorEntry()
    {
        using var fixture = new RetentionReclaimFixture();
        var unknown = fixture.WriteAgedFile(Path.Combine(fixture.AttemptRoot(), "not-a-goal-id", "payload.txt"), 30);
        var operatorFile = fixture.WriteAgedFile(Path.Combine(fixture.AttemptRoot(), "operator", "old", "payload.txt"), 30);

        var result = fixture.Run();

        Assert.True(File.Exists(unknown));
        Assert.False(File.Exists(operatorFile));
        Assert.False(result.Failed);
        Assert.Contains(result.Decisions, decision => decision.Action == EvidenceRetentionAction.RetainedUndecidable &&
            decision.Reason == "goal-store-empty-goal-rules-skipped");
        Assert.Contains("status=partial", RunEventMaintenanceCadence.FormatArtifactRetentionReceipt(result),
            StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void DisabledOwnerlessAgeKeepsUndecidableReceipt()
    {
        using var fixture = new RetentionReclaimFixture();
        var file = fixture.WriteAgedFile(Path.Combine(fixture.AttemptRoot(), "operator", "old", "payload.txt"), 30);
        var options = StorageRetentionReclaimOptions.Default with { OwnerlessAttemptMaxAge = null };

        var result = fixture.Run(options, fixture.Goal(RetentionReclaimFixture.ActiveId, GoalStatus.Active));

        Assert.True(File.Exists(file));
        Assert.Contains(result.Decisions, decision => decision.Action == EvidenceRetentionAction.RetainedUndecidable &&
            decision.Reason == "goal-directory-has-no-unique-full-id-owner" &&
            decision.Path == Path.Combine(fixture.AttemptRoot(), "operator"));
    }

    [Xunit.Fact]
    public void GoalStoreLoadFailureRecordsExceptionAndSkipsUnknownGoalDeletion()
    {
        using var fixture = new RetentionReclaimFixture();
        var file = fixture.WriteAgedFile(Path.Combine(fixture.AttemptRoot(), "not-a-goal-id", "payload.txt"), 30);
        var load = new StorageRetentionGoalLoadResult([], nameof(IOException), []);

        var result = StorageRetentionMaintenance.Run(fixture.LogDirectory, fixture.OrchestratorDirectory,
            fixture.ExecutionDirectory, load.Goals, RetentionReclaimFixture.Now,
            mtpResultsRoot: Path.Combine(fixture.ExecutionDirectory, "empty-mtp"), goalLoad: load);

        Assert.True(File.Exists(file));
        Assert.False(result.Failed);
        Assert.Contains(result.Decisions, decision =>
            decision.Reason == "goal-store-load-failed-goal-rules-skipped" &&
            decision.Action == EvidenceRetentionAction.RetainedUndecidable &&
            decision.FailureExceptionType == nameof(IOException));
    }
}
