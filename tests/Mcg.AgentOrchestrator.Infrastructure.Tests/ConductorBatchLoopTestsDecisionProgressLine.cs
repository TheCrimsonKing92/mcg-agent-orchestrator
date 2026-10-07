using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

public sealed class ConductorBatchLoopTestsDecisionProgressLine
{
    private static readonly PolicyDecisionRecord Decision =
        new("unchanged-candidate", "Hold", 1, "unchanged-candidate", "Waiting on gate", []);

    [Xunit.Fact]
    public void HeldWithDecision_AppendsAttributedDecisionToExistingLine()
    {
        var outcome = new ConductorAdvanceOutcome.Held(GoalLifecycleState.Verified, "Waiting on gate")
        {
            Decision = Decision
        };

        Assert.Equal("GOAL goal=abcdef12 result=held state=Verified reason=Waiting_on_gate decision=unchanged-candidate/1/unchanged-candidate",
            ConductorBatchLoop.FormatGoalProgressLine("abcdef12", outcome));
    }

    [Xunit.Fact]
    public void HeldWithoutDecision_PreservesHeadGoldenLine()
    {
        // Literal captured from 5f55ee9fa; do not compute it using the formatter.
        var outcome = new ConductorAdvanceOutcome.Held(GoalLifecycleState.Verified, "Waiting on gate");

        Assert.Equal("GOAL goal=abcdef12 result=held state=Verified reason=Waiting_on_gate",
            ConductorBatchLoop.FormatGoalProgressLine("abcdef12", outcome));
    }

    [Xunit.Fact]
    public void DecisionSuffix_FollowsSlotAndSanitizedReason()
    {
        var outcome = new ConductorAdvanceOutcome.Held(GoalLifecycleState.Verified, "Waiting\ton\r\ngate")
        {
            Decision = Decision
        };

        Assert.Equal("GOAL goal=abcdef12 result=held state=Verified slot=slot-2 reason=Waiting_on__gate decision=unchanged-candidate/1/unchanged-candidate",
            ConductorBatchLoop.FormatGoalProgressLine("abcdef12", outcome, slotIndex: 2));
    }

    [Xunit.Fact]
    public void EscalatedAndDoneWithDecision_AppendDecision()
    {
        var escalated = new ConductorAdvanceOutcome.Escalated(GoalLifecycleState.Verified, "Waiting on gate") { Decision = Decision };
        var done = new ConductorAdvanceOutcome.Done(GoalLifecycleState.Verified) { Decision = Decision };

        Assert.Equal("GOAL goal=abcdef12 result=escalated state=Verified reason=Waiting_on_gate decision=unchanged-candidate/1/unchanged-candidate",
            ConductorBatchLoop.FormatGoalProgressLine("abcdef12", escalated));
        Assert.Equal("GOAL goal=abcdef12 result=done state=Verified decision=unchanged-candidate/1/unchanged-candidate",
            ConductorBatchLoop.FormatGoalProgressLine("abcdef12", done));
    }

    [Xunit.Fact]
    public void ExecutedWithDecision_PreservesExistingLine()
    {
        var outcome = new ConductorAdvanceOutcome.Executed(GoalLifecycleState.Verified, "Waiting on gate") { Decision = Decision };

        Assert.Equal("GOAL goal=abcdef12 result=executed state=Verified slot=slot-2",
            ConductorBatchLoop.FormatGoalProgressLine("abcdef12", outcome, slotIndex: 2));
    }
}
