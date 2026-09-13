using Mcg.AgentOrchestrator.Core;

public sealed class PreReviewEvidenceCountRestoreTests
{
    [Xunit.Theory]
    [Xunit.InlineData(0)]
    [Xunit.InlineData(9)]
    public void NullHistoryElementDoesNotDiscardPersistedCount(int persistedCount)
    {
        var task = new TaskSpec(TaskId.New(), "Restore receipt history", AgentRole.Reviewer);
        var snapshot = task.ToSnapshot() with
        {
            PreReviewEvidenceHistory = [null!],
            PreReviewEvidenceAttemptCount = persistedCount
        };

        var restored = TaskSpec.FromSnapshot(snapshot);

        Assert.Equal(persistedCount, restored.PreReviewEvidenceAttemptCount);
        Assert.Single(restored.PreReviewEvidenceHistory);
    }
}
