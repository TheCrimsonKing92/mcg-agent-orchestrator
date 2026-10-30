using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel-safe: AsyncLocal console capture and immutable, local fixtures.
// Expected text follows ConsoleViews.CrossGoalStartPlan.cs at base 8711cbf2f.
public sealed class CrossGoalStartPlanTextViewCharacterizationTests
{
    [Fact]
    public void Print_NoCandidatesOrBatches_PreservesExactText()
    {
        var plan = new CrossGoalSubscriptionStartPlan([], new([], []));

        var actual = AsyncLocalConsoleRouter.Capture(() =>
            CrossGoalStartPlanTextView.PrintCrossGoalSubscriptionStartPlan(plan));

        Assert.Equal(string.Join(Environment.NewLine,
            "",
            "Cross-goal subscription start plan: 0 candidate goal(s)",
            "Batches:",
            "  none",
            "Decisions:",
            "", ""), actual);
    }

    [Fact]
    public void Print_CandidatesBatchesAndDecisions_PreservesExactText()
    {
        var plan = new CrossGoalSubscriptionStartPlan(
            [new("abcdef123456", "abcdef12", "Update CLI", [2, 3], ["task-a", "task-b"],
                ["src/first.cs", "src/second.cs"], ["repo", "provider"], [], "OpenAI", true,
                "Ready to start.", RepositoryScopeConfidence.Precise),
             new("short", "short", "Inspect plan", [], [], [], [], [], null, false,
                "Needs approval.", RepositoryScopeConfidence.Unknown)],
            new([new(1, ["abcdef123456", "short"])],
                [new("abcdef123456", ParallelExecutionDisposition.Concurrent, 1, ["disjoint paths", "slot available"]),
                 new("short", ParallelExecutionDisposition.RequiresOperatorApproval, null, ["scope unknown"])]));

        var actual = AsyncLocalConsoleRouter.Capture(() =>
            CrossGoalStartPlanTextView.PrintCrossGoalSubscriptionStartPlan(plan));

        Assert.Equal(string.Join(Environment.NewLine,
            "",
            "Cross-goal subscription start plan: 2 candidate goal(s)",
            "  abcdef12: tasks=2,3 provider=OpenAI costConfirm=True",
            "     objective: Update CLI",
            "     paths: src/first.cs, src/second.cs",
            "     resources: repo, provider",
            "     detail: Ready to start.",
            "  short: tasks= provider=none costConfirm=False",
            "     objective: Inspect plan",
            "     paths: none",
            "     resources: ",
            "     detail: Needs approval.",
            "Batches:",
            "  batch 1: abcdef12, short",
            "Decisions:",
            "  abcdef12: Concurrent batch=1 (disjoint paths; slot available)",
            "  short: RequiresOperatorApproval (scope unknown)",
            "", ""), actual);
    }
}
