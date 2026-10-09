using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel-safe: CaptureConsole routes output through AsyncLocal; fixtures have no shared state.
public sealed class FailureTriageTextViewCharacterizationTests
{
    // Derived from ConsoleViews.FailureTriage.cs:7-18 before the extraction.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Print_GoalAndTaskTargetsWithAutoApply_PreservesExactText(bool autoApply)
    {
        var report = new FailureTriageReport(GoalId.New(), "goal-abc", "safe-auto",
        [
            new(null, null, FailureTriageCause.StaleBranch, FailureTriageAction.RequestHumanInput,
                null, false, autoApply, true, "Refresh the goal branch.", "refresh"),
            new(7, TaskId.New(), FailureTriageCause.MissingVerification, FailureTriageAction.Verify,
                AutonomyAction.BuildTest, true, autoApply, false, "Focused verification is missing.", "verify 7")
        ]);
        var actual = AsyncLocalConsoleRouter.Capture(() => FailureTriageTextView.PrintFailureTriageReport(report));
        var expected = string.Join(Environment.NewLine,
            "Failure triage goal: goal-abc",
            "Policy: safe-auto",
            "Findings:",
            autoApply
                ? "  StaleBranch goal: action=RequestHumanInput; canAutoApply=True; gate=True; policyAllows=False; command=refresh"
                : "  StaleBranch goal: action=RequestHumanInput; canAutoApply=False; gate=True; policyAllows=False; command=refresh",
            "    explanation: Refresh the goal branch.",
            autoApply
                ? "  MissingVerification task 7: action=Verify; canAutoApply=True; gate=False; policyAllows=True; command=verify 7"
                : "  MissingVerification task 7: action=Verify; canAutoApply=False; gate=False; policyAllows=True; command=verify 7",
            "    explanation: Focused verification is missing.",
            "");
        Assert.Equal(expected, actual);
    }
}
