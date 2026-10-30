using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: Capture routes output through AsyncLocal; fixtures have no shared state.
public sealed class GoalSupervisorTextViewCharacterizationTests
{
    // Expected text derived from ConsoleViews.GoalSupervisor.cs before the extraction.
    [Fact]
    public void Print_DefaultActions_PreservesTaskProposalText()
    {
        var plan = TaskPlan();
        var actual = AsyncLocalConsoleRouter.Capture(() => GoalSupervisorTextView.PrintGoalSupervisorPlan(plan));
        Assert.Equal(TaskPlanOutput(), actual);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Print_NullOrEmptyActions_OmitsAppliedActions(bool empty)
    {
        IReadOnlyList<string>? actions = empty ? Array.Empty<string>() : null;
        var actual = AsyncLocalConsoleRouter.Capture(() => GoalSupervisorTextView.PrintGoalSupervisorPlan(TaskPlan(), actions));
        Assert.Equal(TaskPlanOutput(), actual);
    }

    [Fact]
    public void Print_AppliedActions_PreservesBlockAndOrder()
    {
        var actual = AsyncLocalConsoleRouter.Capture(() => GoalSupervisorTextView.PrintGoalSupervisorPlan(
            TaskPlan(), ["Refreshed task 3.", "Recorded evidence."]));
        var expected = string.Join(Environment.NewLine,
            "Supervisor goal: goal-abc",
            "Policy: supervised-auto",
            "Apply safe: True",
            "Applied actions: 2",
            "  Refreshed task 3.",
            "  Recorded evidence.",
            "Proposals:",
            "  RefreshRunningProcess task 3: canApply=True; gate=False; policyAllows=True; command=refresh-dispatch 3",
            "    reason: Refresh the running task.",
            "");
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Print_TwoProposals_PreservesGoalFallbackAndReasonOrder()
    {
        var plan = TaskPlan() with
        {
            ApplySafe = false,
            Proposals =
            [
                TaskPlan().Proposals[0],
                new(GoalSupervisorProposalKind.AcceptanceGate, null, null, null,
                    false, false, true, "Operator approval required.", "accept goal-abc")
            ]
        };
        var actual = AsyncLocalConsoleRouter.Capture(() => GoalSupervisorTextView.PrintGoalSupervisorPlan(plan));
        var expected = string.Join(Environment.NewLine,
            "Supervisor goal: goal-abc",
            "Policy: supervised-auto",
            "Apply safe: False",
            "Proposals:",
            "  RefreshRunningProcess task 3: canApply=True; gate=False; policyAllows=True; command=refresh-dispatch 3",
            "    reason: Refresh the running task.",
            "  AcceptanceGate goal: canApply=False; gate=True; policyAllows=False; command=accept goal-abc",
            "    reason: Operator approval required.",
            "");
        Assert.Equal(expected, actual);
    }

    private static GoalSupervisorPlan TaskPlan() => new(
        GoalId.New(), "goal-abc", "supervised-auto", true,
        [new(GoalSupervisorProposalKind.RefreshRunningProcess, 3, null, null,
            true, true, false, "Refresh the running task.", "refresh-dispatch 3")]);

    private static string TaskPlanOutput() => string.Join(Environment.NewLine,
        "Supervisor goal: goal-abc",
        "Policy: supervised-auto",
        "Apply safe: True",
        "Proposals:",
        "  RefreshRunningProcess task 3: canApply=True; gate=False; policyAllows=True; command=refresh-dispatch 3",
        "    reason: Refresh the running task.",
        "");

    [Fact]
    public void ConsoleViewsRatchet_UsesReducedAtOrUnderBudget()
    {
        var ceiling = Assert.Single(SourceSizeRatchet.SeededClassCeilings,
            row => row.ClassName == "ConsoleViews");
        Assert.True(ceiling.MaximumTotalLineCount <= 2804);
        Assert.True(ceiling.MaximumPartialFileCount <= 36);
        Assert.Empty(SourceSizeRatchet.EvaluateClasses(VerifiedRepositoryRoot.Find(), [ceiling]));
    }

    [Fact]
    public void ExtractedOwner_LeavesNoForwarderOnConsoleViews()
    {
        var root = VerifiedRepositoryRoot.Find();
        var cliRoot = Path.Combine(root, "src", "Mcg.AgentOrchestrator.App", "Cli");
        Assert.False(File.Exists(Path.Combine(cliRoot, "ConsoleViews.GoalSupervisor.cs")));
        var source = File.ReadAllText(Path.Combine(cliRoot, "GoalSupervisorTextView.cs"));
        Assert.Contains("internal static class GoalSupervisorTextView", source);
        Assert.DoesNotMatch(@"\bpartial\s+class\b", source);

        // Scan every ConsoleViews partial, including ConsoleViews.cs, as the ratchet does.
        var partials = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => segment.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                    segment.Equals("obj", StringComparison.OrdinalIgnoreCase)))
            .Select(File.ReadAllText)
            .Where(text => Regex.IsMatch(text, @"\bpartial\s+class\s+ConsoleViews\b", RegexOptions.CultureInvariant))
            .ToArray();
        Assert.True(partials.Length <= 36);
        foreach (var text in partials)
            Assert.DoesNotMatch(@"\bGoalSupervisorTextView\s*\.", text);
    }
}
