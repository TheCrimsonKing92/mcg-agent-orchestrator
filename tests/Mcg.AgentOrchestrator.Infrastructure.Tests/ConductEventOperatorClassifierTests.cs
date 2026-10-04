using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: the classifier is pure and these cases use no shared resources.
public sealed class ConductEventOperatorClassifierTests
{
    [Theory]
    [InlineData("sweep-blocker", "SWEEP_BLOCKER goal=g kind=completed-branch-unmerged evidence=\"e\" command=\"acceptance g\" owner=acceptance-queue")]
    [InlineData("canary-gate", "CANARY_GATE sha=s result=queued paths=p")]
    [InlineData("canary-gate", "CANARY_GATE sha=s result=started attempt=1")]
    [InlineData("loop-handoff", "LOOP_HANDOFF tick=1")]
    [InlineData("loop-relaunch", "LOOP_RELAUNCH_DRAIN tick=1 goal=g active=0 admitting=false")]
    [InlineData("author", "item=i kind=answer reason=r")]
    [InlineData("acceptance", "result=running")]
    [InlineData("acceptance", "result=failedx")]
    [InlineData("acceptance", "result=blocked-x")]
    [InlineData("acceptance", "result=passedx")]
    [InlineData("acceptance", "previous-result=passed")]
    [InlineData("acceptance", "reason=result=failed")]
    [InlineData("acceptance", "Result=failed")]
    [InlineData("acceptance", "result=FAILED")]
    [InlineData("Acceptance", "result=failed")]
    [InlineData("author", "kind=ask-owner-extra")]
    [InlineData("author", "reason=kind=model-failure")]
    [InlineData("loop-handoff", "ACTIVATION_ADOPTED_EXTRA candidateCommit=a")]
    [InlineData("loop-handoff", "ACTIVATION_REVERTED_EXTRA candidateCommit=a")]
    [InlineData("loop-handoff", "ACTIVATION_FAILED_BOTH_EXTRA candidateCommit=a")]
    [InlineData("loop-handoff", "prefix ACTIVATION_REVERTED candidateCommit=a")]
    [InlineData("loop-handoff", " ACTIVATION_REVERTED candidateCommit=a")]
    [InlineData("loop-handoff", "activation_reverted candidateCommit=a")]
    [InlineData("loop-relaunch", "LOOP_RELAUNCH_SCHEDULED_EXTRA tick=1")]
    [InlineData("acceptance-cohort", "ACCEPTANCE_COHORT_INFLIGHT tick=1 outcome=failed")]
    [InlineData("acceptance-cohort", "ACCEPTANCE_COHORT tick=1 outcome=passedx")]
    [InlineData("acceptance-cohort", "ACCEPTANCE_COHORT tick=1 previous-outcome=failed")]
    [InlineData("acceptance-cohort", "ACCEPTANCE_COHORT_RECONCILED_DEAD_EXTRA attempt=a")]
    [InlineData("acceptance-cohort", "prefix ACCEPTANCE_COHORT tick=1 outcome=passed")]
    [InlineData("unknown", "result=failed kind=ask-owner")]
    [InlineData("judge-panel", "PANEL_CASE_EXTRA result=completed")]
    [InlineData("acceptance", "")]
    public void Classify_NearMiss_ReturnsNone(string eventKind, string detail)
    {
        Assert.Null(ConductEventOperatorClassifier.Classify(eventKind, detail));
    }

    [Theory]
    [InlineData("acceptance", "result=failed", "decision")]
    [InlineData("judge-panel", "PANEL_CASE case=c result=completed", "outcome")]
    [InlineData("judge-panel", "PANEL_CASE case=c result=superseded", "outcome")]
    [InlineData("canary-gate", "\tresult=passed\r\nsha=s", "outcome")]
    [InlineData("author", "item=i\tkind=model-failure\nreason=r", "decision")]
    [InlineData("loop-handoff", "ACTIVATION_REVERTED", "decision")]
    [InlineData("loop-relaunch", "LOOP_RELAUNCH_SCHEDULED", "outcome")]
    [InlineData("acceptance-cohort", "ACCEPTANCE_COHORT_RECONCILED_DEAD", "outcome")]
    [InlineData("goal-escalation", "", "decision")]
    [InlineData("goal-stalled", "", "decision")]
    [InlineData("sweep-blocker", "", "decision")]
    [InlineData("sweep-blocker", "SWEEP_BLOCKER goal=g kind=completed-branch-unmerged evidence=\"e\" command=\"acceptance g\"", "decision")]
    [InlineData("sweep-blocker", "SWEEP_BLOCKER goal=g kind=completed-branch-unmerged evidence=\"e\" command=\"acceptance g\" owner=acceptance-queuex", "decision")]
    [InlineData("sweep-blocker", "xowner=acceptance-queue", "decision")]
    [InlineData("sweep-blocker", "owner=acceptance-queue-x", "decision")]
    [InlineData("sweep-blocker", "Owner=acceptance-queue", "decision")]
    [InlineData("goal-stalled", "owner=acceptance-queue", "decision")]
    public void Classify_WholeToken_ReturnsOperatorClass(string eventKind, string detail, string expected)
    {
        Assert.Equal(expected, ConductEventOperatorClassifier.Classify(eventKind, detail));
    }
}
