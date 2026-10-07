using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Core.Tests;

public sealed class FailedGoalRecoveryDecisionRecordsTestsDriverRungs
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BuildCheckRecordsHaveNamedEvidenceDistinctRungsAndGoalIdentity(bool hasTask)
    {
        var identity = new FailedGoalRecoveryIdentity(
            new GoalId("build-check-goal"), hasTask ? new TaskId("build-check-task") : null, null, "context-v1");
        const string reason = "Unchanged recovery reason.";
        var records = new[]
        {
            FailedGoalRecoveryDecisionRecords.BuildLogUnavailable(identity, reason),
            FailedGoalRecoveryDecisionRecords.WorktreeUnavailable(identity, reason),
            FailedGoalRecoveryDecisionRecords.WorktreeInspectionUnavailable(identity, reason),
            FailedGoalRecoveryDecisionRecords.CheckpointCommitFailed(identity, reason),
            FailedGoalRecoveryDecisionRecords.CheckpointDirtyAfterCommit(identity, reason),
            FailedGoalRecoveryDecisionRecords.BuildCheckRecoveryExhausted(identity, reason),
            FailedGoalRecoveryDecisionRecords.BuildCheckRetryFailed(identity, reason),
            FailedGoalRecoveryDecisionRecords.BuildCheckRetryApplied(identity, reason)
        };
        Assert.Equal(new[]
        {
            "build-log-unavailable", "worktree-unavailable", "worktree-inspection-unavailable",
            "checkpoint-commit-failed", "checkpoint-dirty-after-commit", "build-check-recovery-exhausted",
            "build-check-retry-failed", "build-check-retry-applied"
        }, records.Select(record => record.DiscriminatingEvidence));
        Assert.Equal(new[] { "Escalate", "Escalate", "Escalate", "Escalate", "Escalate", "Escalate", "Escalate", "Hold" },
            records.Select(record => record.Action));
        var existingDriverRungs = new[]
        {
            FailedGoalRecoveryDecisionRecords.StaleRecoveryFacts(identity, reason).Rung,
            FailedGoalRecoveryDecisionRecords.CandidateRedLifecycleConflict(identity, reason).Rung,
            FailedGoalRecoveryDecisionRecords.ExitedUnappliedProcessRecord(identity, reason).Rung,
            FailedGoalRecoveryDecisionRecords.ReconcileBeforeFailureHandling(identity, reason).Rung
        };
        // FailedGoalRecoveryPolicy owns rungs 1–9, below the driver block.
        var allRungs = Enumerable.Range(1, 9).Concat(existingDriverRungs).Concat(records.Select(record => record.Rung)).ToArray();
        Assert.Equal(allRungs.Length, allRungs.Distinct().Count());
        Assert.All(records, record =>
        {
            Assert.InRange(record.Rung, 105, int.MaxValue);
            Assert.Equal("failed-goal-recovery", record.Stage);
            Assert.Equal(reason, record.Reason);
            Assert.Equal("build-check-goal", Assert.Single(record.Facts, fact => fact.Name == "goalId").Value);
            Assert.Equal("context-v1", Assert.Single(record.Facts, fact => fact.Name == "contextVersion").Value);
            if (hasTask)
                Assert.Equal("build-check-task", Assert.Single(record.Facts, fact => fact.Name == "targetTaskId").Value);
            else
                Assert.DoesNotContain(record.Facts, fact => fact.Name == "targetTaskId");
        });
    }
}
