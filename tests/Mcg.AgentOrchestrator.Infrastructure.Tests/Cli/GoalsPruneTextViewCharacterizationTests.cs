using System.Reflection;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: AsyncLocal console capture and immutable, local fixtures.
public sealed class GoalsPruneTextViewCharacterizationTests
{
    [Fact]
    public void Print_DryRunWithReapableGoal_PreservesExactText()
    {
        var plan = new GoalsPrunePlan(
        [
            Item("goal-reap", GoalPruneDisposition.Reapable, "Merged branch."),
            Item("goal-skip", GoalPruneDisposition.SkippedNoBranch, "No branch.")
        ], DryRun: true);

        var actual = AsyncLocalConsoleRouter.Capture(() =>
            GoalsPruneTextView.PrintGoalsPrunePlan(plan));

        Assert.Equal(string.Join(Environment.NewLine,
            "",
            "goals-prune (dry-run): 1 reapable, 1 skipped",
            "  goal-reap [Active] REAPABLE: Merged branch.",
            "  goal-skip [Active] skipped/SkippedNoBranch: No branch.",
            "",
            "Re-run with --confirm-prune to cancel 1 goal(s) and delete their merged branches.",
            ""), actual);
    }

    [Fact]
    public void Print_EmptyDryRun_PreservesExactText()
    {
        var actual = AsyncLocalConsoleRouter.Capture(() =>
            GoalsPruneTextView.PrintGoalsPrunePlan(new([], DryRun: true)));

        Assert.Equal(string.Join(Environment.NewLine,
            "",
            "goals-prune (dry-run): 0 reapable, 0 skipped",
            "",
            "No reapable goals found.",
            ""), actual);
    }

    [Fact]
    public void Print_AppliedWithSkippedGoal_PreservesExactText()
    {
        var plan = new GoalsPrunePlan(
        [
            Item("goal-done", GoalPruneDisposition.Pruned, "Pruned branch."),
            Item("goal-skip", GoalPruneDisposition.SkippedNoBranch, "No branch.")
        ], DryRun: false);

        var actual = AsyncLocalConsoleRouter.Capture(() =>
            GoalsPruneTextView.PrintGoalsPrunePlan(plan));

        Assert.Equal(string.Join(Environment.NewLine,
            "",
            "goals-prune: 1 pruned, 1 skipped",
            "  goal-done [Active] PRUNED: Pruned branch.",
            "  goal-skip [Active] skipped/SkippedNoBranch: No branch.",
            ""), actual);
    }

    [Fact]
    public void ExtractedOwner_LeavesNoPartialFileOrConsoleViewsForwarders()
    {
        var cliRoot = Path.Combine(VerifiedRepositoryRoot.Find(), "src", "Mcg.AgentOrchestrator.App", "Cli");
        Assert.False(File.Exists(Path.Combine(cliRoot, "ConsoleViews.GoalsPrune.cs")));
        var source = File.ReadAllText(Path.Combine(cliRoot, "GoalsPruneTextView.cs"));
        Assert.Contains("internal static class GoalsPruneTextView", source);
        Assert.DoesNotMatch(@"\bpartial\s+class\b", source);
        var methods = typeof(ConsoleViews).GetMethods(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly);
        Assert.DoesNotContain(methods, method => method.Name == "PrintGoalsPrunePlan");
    }

    [Fact]
    public void ConsoleViewsRatchet_UsesReducedExactCeiling()
    {
        var ceiling = Assert.Single(SourceSizeRatchet.SeededClassCeilings,
            row => row.ClassName == "ConsoleViews");
        Assert.True(ceiling.MaximumTotalLineCount < 2007);
        Assert.True(ceiling.MaximumPartialFileCount < 31);
        var measurements = SourceSizeRatchet.MeasureClasses(VerifiedRepositoryRoot.Find(),
            ["ConsoleViews"], out var violations);
        Assert.Empty(violations);
        var measured = measurements["ConsoleViews"];
        Assert.Equal(measured.TotalLines, ceiling.MaximumTotalLineCount);
        Assert.Equal(measured.PartialFiles, ceiling.MaximumPartialFileCount);
    }

    private static GoalPruneItem Item(string prefix, GoalPruneDisposition disposition, string reason) =>
        new(new GoalId(prefix), prefix, "Objective", GoalStatus.Active, "goal/branch", disposition, reason);
}
