using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel-safe: AsyncLocal console capture and local fixtures.
// Every expected string is derived from ConsoleViews.GoalPlan.cs at base fd85596fc.
public sealed class GoalDependencyPlanTextViewCharacterizationTests
{
    [Fact]
    public void Print_EmptyPlan_PreservesExactText()
    {
        var plan = new GoalDependencyPlan([], [], new("empty", [], [], [], true), new([], []));

        var actual = AsyncLocalConsoleRouter.Capture(() => GoalDependencyPlanTextView.Print(plan));

        Assert.Equal(string.Join(Environment.NewLine,
            "",
            "Goal plan: 0 node(s), 0 dependency edge(s)",
            "Compiled graph: empty runnable=True",
            "",
            "Parallel batches:",
            "Parallel decisions:",
            "",
            "Create commands:",
            "  goal-plan --create-goals --backlog-coverage <full|slice>",
            "  goal-plan --create-simple-goals --backlog-coverage <full|slice>",
            "", ""), actual);
    }

    [Fact]
    public void Print_DependenciesAndFindings_PreservesExactText()
    {
        var plan = new GoalDependencyPlan(
            [new("a", "First slice", Intake("a", ["src/a.cs"], [AgentRole.Developer, AgentRole.Tester], ["shared"]), [], "First"),
             new("b", "Second slice", Intake("b", [], [], []), ["a"], "Second")],
            [new("a", "b", "requires first")],
            new("graph-1",
                [new("a", "First slice", ["src/a.cs"], ["edit"], ["focused tests"], "commit-a", 1,
                    ParallelExecutionDisposition.Concurrent, true),
                 new("b", "Second slice", [], [], [], "commit-b", null,
                    ParallelExecutionDisposition.RequiresOperatorApproval, false)],
                [new("a", "b", "requires first")], [new("warning", "b", "needs approval")], false),
            new([new(1, ["a"])],
                [new("a", ParallelExecutionDisposition.Concurrent, 1, ["disjoint scopes", "capacity available"]),
                 new("b", ParallelExecutionDisposition.RequiresOperatorApproval, null, ["operator gate"])]));

        var actual = AsyncLocalConsoleRouter.Capture(() => GoalDependencyPlanTextView.Print(plan));

        Assert.Equal(string.Join(Environment.NewLine,
            "",
            "Goal plan: 2 node(s), 1 dependency edge(s)",
            "Compiled graph: graph-1 runnable=False",
            "",
            "a: First slice",
            "Roles: Developer, Tester",
            "Risks: shared",
            "Depends on: none",
            "Rollback boundary: commit-a",
            "Parallel: Concurrent batch=1 create=True",
            "Target files/scopes:",
            "  - src/a.cs",
            "Capabilities:",
            "  - edit",
            "Verification contracts:",
            "  - focused tests",
            "",
            "b: Second slice",
            "Roles: ",
            "Risks: ",
            "Depends on: a",
            "Rollback boundary: commit-b",
            "Parallel: RequiresOperatorApproval create=False",
            "Target files/scopes:",
            "Capabilities:",
            "Verification contracts:",
            "",
            "Dependency edges:",
            "  a -> b: requires first",
            "",
            "Compiled graph findings:",
            "  warning b: needs approval",
            "",
            "Parallel batches:",
            "  batch 1: a",
            "Parallel decisions:",
            "  a: Concurrent batch=1 (disjoint scopes; capacity available)",
            "  b: RequiresOperatorApproval (operator gate)",
            "",
            "Create commands:",
            "  goal-plan --create-goals --backlog-coverage <full|slice>",
            "  goal-plan --create-simple-goals --backlog-coverage <full|slice>",
            "", ""), actual);
    }

    private static BacklogIntakeItem Intake(string id, IReadOnlyList<string> files,
        IReadOnlyList<AgentRole> roles, IReadOnlyList<string> risks) =>
        new(id, id, "", files, roles, risks, [], [], "", "", "", "",
            RepositoryScopeConfidence.Precise, []);
}
