using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel-safe: CaptureConsole routes output through AsyncLocal; fixtures have no shared state.
public sealed class GoalArtifactRetentionTextViewCharacterizationTests
{
    // Derived from ConsoleViews.Retention.cs:7-22 before the extraction.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Print_WithAndWithoutSuggestedCommand_PreservesExactText(string? noCommand)
    {
        var plan = new GoalArtifactRetentionPlan(GoalId.New(), "goal-abc", RetentionGoalState.Abandoned, true,
        [
            new(RetentionArtifactKind.Worktree, RetentionDecision.DeleteWhenSafe, "C:\\worktrees\\goal-abc",
                true, "Remove after review.", "workspace remove goal-abc"),
            new(RetentionArtifactKind.WorkerLogs, RetentionDecision.Keep, "C:\\logs\\goal-abc",
                false, "Keep diagnostic evidence.", noCommand)
        ]);
        var actual = AsyncLocalConsoleRouter.Capture(() => GoalArtifactRetentionTextView.PrintGoalArtifactRetentionPlan(plan));
        var expected = string.Join(Environment.NewLine,
            "Retention plan goal: goal-abc",
            "State: Abandoned",
            "Dry run: True",
            "Artifacts:",
            "  Worktree: DeleteWhenSafe; exists=True; path=C:\\worktrees\\goal-abc",
            "    reason: Remove after review.",
            "    command: workspace remove goal-abc",
            "  WorkerLogs: Keep; exists=False; path=C:\\logs\\goal-abc",
            "    reason: Keep diagnostic evidence.",
            "");
        Assert.Equal(expected, actual);
    }
}
