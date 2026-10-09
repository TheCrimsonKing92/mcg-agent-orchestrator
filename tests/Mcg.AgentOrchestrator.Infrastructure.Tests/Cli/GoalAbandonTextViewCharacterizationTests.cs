using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel-safe: CaptureConsole routes output through AsyncLocal; fixtures have no shared state.
public sealed class GoalAbandonTextViewCharacterizationTests
{
    // Derived from ConsoleViews.GoalAbandon.cs:8-27 before the extraction.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Print_StepsAndRetention_PreservesExactText(string? noCommand)
    {
        var goalId = GoalId.New();
        var retention = new GoalArtifactRetentionPlan(goalId, "goal-abc", RetentionGoalState.Abandoned, true,
        [
            new(RetentionArtifactKind.Worktree, RetentionDecision.DeleteWhenSafe, "unused-worktree",
                true, "unused reason", "unused command"),
            new(RetentionArtifactKind.WorkerLogs, RetentionDecision.Keep, "unused-logs",
                false, "unused reason", null)
        ]);
        var plan = new GoalAbandonPlan(goalId, "goal-abc", GoalStatus.Active, "Operator requested stop.", true, true,
        [
            new(GoalAbandonStepKind.RunningDispatches, GoalAbandonDisposition.Apply,
                "Cancel task 7.", "cancel-dispatch 7"),
            new(GoalAbandonStepKind.BuildLease, GoalAbandonDisposition.Missing,
                "No build lease.", noCommand)
        ], retention);
        var actual = CaptureConsole(() => GoalAbandonTextView.PrintGoalAbandonPlan(plan));
        var expected = string.Join(Environment.NewLine,
            "Goal abandon goal-abc Active: Operator requested stop.",
            "Dry run: True",
            "Can apply: True",
            "Steps:",
            "  RunningDispatches: Apply; Cancel task 7.",
            "    command: cancel-dispatch 7",
            "  BuildLease: Missing; No build lease.",
            "Retention:",
            "  Worktree: DeleteWhenSafe; exists=True",
            "  WorkerLogs: Keep; exists=False",
            "");
        Assert.Equal(expected, actual);
    }
}
