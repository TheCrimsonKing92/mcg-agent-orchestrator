using Mcg.AgentOrchestrator.Core;

public sealed class InterruptedWorkCheckpointContractTests
{
    [Xunit.Fact]
    public void IdempotencyKeyAndTrailersAreStableAndRoundTrip()
    {
        var checkpoint = InterruptedWorkCheckpoint.Create(
            "goal/task/2026-09-17T12:00:00.0000000+00:00",
            new string('1', 32),
            new string('2', 32),
            AgentRole.Developer,
            "goal/11111111",
            "C:\\repo",
            new string('a', 40),
            ProviderFailureKind.Connectivity,
            DateTimeOffset.Parse("2026-09-17T12:01:00Z"));

        Xunit.Assert.Equal(
            InterruptedWorkCheckpoint.ComputeIdempotencyKey(checkpoint.DispatchId, checkpoint.ParentCommit),
            checkpoint.IdempotencyKey);
        Xunit.Assert.True(InterruptedWorkCheckpoint.TryParseCommitMessage(checkpoint.RenderCommitMessage(), out var parsed));
        Xunit.Assert.Equal(checkpoint.IdempotencyKey, parsed!.IdempotencyKey);
        Xunit.Assert.Equal(checkpoint.DispatchId, parsed.DispatchId);
        Xunit.Assert.Equal(checkpoint.ParentCommit, parsed.ParentCommit);
    }

    [Xunit.Fact]
    public void EarlyConvergenceEvidenceSourcesCannotSubstituteForEachOther()
    {
        var hash = new string('b', 64);
        var candidate = new string('c', 40);
        WorkerContextPackageReceipt Receipt(string identity, EarlyConvergenceEvidenceKind kind) => new(
            "ctxpkg",
            [new WorkerContextSectionReceipt(identity, 1, 1, hash, ContextDeliveryMode.OnDemandFile, 1, [AgentRole.Developer])],
            ProviderUsageValue.Unknown("test"),
            ProviderUsageValue.Unknown("test"),
            ProviderUsageValue.Unknown("test"),
            EarlyConvergenceEligible: true,
            EarlyConvergenceCandidateSha: candidate,
            EarlyConvergenceReceiptHashes: [hash],
            EarlyConvergenceEvidenceSource: kind);

        Xunit.Assert.True(Receipt($"goal/review-finding-receipts/{hash}.json", EarlyConvergenceEvidenceKind.ReviewFindingReceipt)
            .HasEarlyConvergenceEvidenceFor(candidate));
        Xunit.Assert.True(Receipt($"goal/interrupted-work-checkpoint/{hash}.json", EarlyConvergenceEvidenceKind.InterruptedWorkCheckpoint)
            .HasEarlyConvergenceEvidenceFor(candidate));
        Xunit.Assert.False(Receipt($"goal/interrupted-work-checkpoint/{hash}.json", EarlyConvergenceEvidenceKind.ReviewFindingReceipt)
            .HasEarlyConvergenceEvidenceFor(candidate));
        Xunit.Assert.False(Receipt($"goal/review-finding-receipts/{hash}.json", EarlyConvergenceEvidenceKind.InterruptedWorkCheckpoint)
            .HasEarlyConvergenceEvidenceFor(candidate));
        Xunit.Assert.False(Receipt($"goal/interrupted-work-checkpoint/{hash}.json", EarlyConvergenceEvidenceKind.InterruptedWorkCheckpoint)
            .HasEarlyConvergenceEvidenceFor(new string('d', 40)));
    }

    [Xunit.Fact]
    public void CheckpointSurvivesInterruptedVerificationAndSnapshotButClearsAfterResumedRound()
    {
        var recordedAt = DateTimeOffset.Parse("2026-09-17T12:01:00Z");
        var task = new TaskSpec(TaskId.New(), "Resume", AgentRole.Developer);
        var checkpoint = InterruptedWorkCheckpoint.Create(
            "dispatch",
            new string('1', 32),
            task.Id.Value,
            AgentRole.Developer,
            "goal/11111111",
            "C:\\repo",
            new string('a', 40),
            ProviderFailureKind.Connectivity,
            recordedAt,
            new string('b', 40));
        task.SetInterruptedWorkCheckpoint(checkpoint);
        task.RecordVerification(Verification(recordedAt.AddMinutes(-1)));

        Xunit.Assert.Equal(checkpoint, task.PendingInterruptedWorkCheckpoint);
        var restored = TaskSpec.FromSnapshot(task.ToSnapshot());
        Xunit.Assert.Equal(checkpoint, restored.PendingInterruptedWorkCheckpoint);

        restored.RecordVerification(Verification(recordedAt.AddMinutes(1)));
        Xunit.Assert.Null(restored.PendingInterruptedWorkCheckpoint);
    }

    private static TaskVerificationRecord Verification(DateTimeOffset dispatchStartedAt) => new(
        "codex exec",
        "C:\\repo",
        1,
        string.Empty,
        "provider interruption",
        dispatchStartedAt.AddMinutes(1),
        ProviderFailureKind: ProviderFailureKind.Connectivity,
        DispatchStartedAt: dispatchStartedAt);
}
