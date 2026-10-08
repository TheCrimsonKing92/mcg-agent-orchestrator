using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Core.Tests;

public sealed class FailedGoalRecoveryDecisionRecordsTestsTimedOutRerun
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TimedOutRerunRecordsHaveNamedEvidenceDistinctRungsAndGoalIdentity(bool hasTask)
    {
        var identity = new FailedGoalRecoveryIdentity(
            new GoalId("timed-out-goal"), hasTask ? new TaskId("timed-out-task") : null, null, "context-v1");
        const string reason = "Unchanged recovery reason.";
        var records = new[]
        {
            FailedGoalRecoveryDecisionRecords.TimedOutRerunObservationEscalated(identity, reason),
            FailedGoalRecoveryDecisionRecords.TimedOutRerunRetryAuthorityChanged(identity, reason),
            FailedGoalRecoveryDecisionRecords.TimedOutRoundUnchanged(identity, reason),
            FailedGoalRecoveryDecisionRecords.TimedOutRerunEvidencePending(identity, reason)
        };
        Assert.Equal(new[]
        {
            "timed-out-rerun-observation-escalated", "timed-out-rerun-retry-authority-changed",
            "timed-out-round-unchanged", "timed-out-rerun-evidence-pending"
        }, records.Select(record => record.DiscriminatingEvidence));
        Assert.Equal(new[] { "Escalate", "Hold", "Escalate", "Hold" }, records.Select(record => record.Action));
        var existingDriverRungs = new[]
        {
            FailedGoalRecoveryDecisionRecords.StaleRecoveryFacts(identity, reason).Rung,
            FailedGoalRecoveryDecisionRecords.CandidateRedLifecycleConflict(identity, reason).Rung,
            FailedGoalRecoveryDecisionRecords.ExitedUnappliedProcessRecord(identity, reason).Rung,
            FailedGoalRecoveryDecisionRecords.ReconcileBeforeFailureHandling(identity, reason).Rung,
            FailedGoalRecoveryDecisionRecords.BuildLogUnavailable(identity, reason).Rung,
            FailedGoalRecoveryDecisionRecords.WorktreeUnavailable(identity, reason).Rung,
            FailedGoalRecoveryDecisionRecords.WorktreeInspectionUnavailable(identity, reason).Rung,
            FailedGoalRecoveryDecisionRecords.CheckpointCommitFailed(identity, reason).Rung,
            FailedGoalRecoveryDecisionRecords.CheckpointDirtyAfterCommit(identity, reason).Rung,
            FailedGoalRecoveryDecisionRecords.BuildCheckRecoveryExhausted(identity, reason).Rung,
            FailedGoalRecoveryDecisionRecords.BuildCheckRetryFailed(identity, reason).Rung,
            FailedGoalRecoveryDecisionRecords.BuildCheckRetryApplied(identity, reason).Rung
        };
        // FailedGoalRecoveryPolicy and the timed-out rule own policy rungs 1–9.
        var allRungs = Enumerable.Range(1, 9).Concat(existingDriverRungs).Concat(records.Select(record => record.Rung)).ToArray();
        Assert.Equal(allRungs.Length, allRungs.Distinct().Count());
        Assert.Equal(Enumerable.Range(existingDriverRungs.Max() + 1, records.Length), records.Select(record => record.Rung));
        Assert.All(records, record =>
        {
            Assert.Equal("failed-goal-recovery", record.Stage);
            Assert.Equal(reason, record.Reason);
            Assert.Equal("timed-out-goal", Assert.Single(record.Facts, fact => fact.Name == "goalId").Value);
            Assert.Equal("context-v1", Assert.Single(record.Facts, fact => fact.Name == "contextVersion").Value);
            if (hasTask)
                Assert.Equal("timed-out-task", Assert.Single(record.Facts, fact => fact.Name == "targetTaskId").Value);
            else
                Assert.DoesNotContain(record.Facts, fact => fact.Name == "targetTaskId");
        });
    }
}
