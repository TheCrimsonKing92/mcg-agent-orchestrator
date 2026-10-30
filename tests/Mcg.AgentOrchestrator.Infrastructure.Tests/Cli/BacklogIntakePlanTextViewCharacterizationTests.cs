using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel-safe: AsyncLocal console capture and local fixtures.
// Expected text follows ConsoleViews.BacklogIntake.cs at base 781c2fd21.
public sealed class BacklogIntakePlanTextViewCharacterizationTests
{
    [Fact]
    public void Print_EmptyPlan_PreservesExactText()
    {
        var plan = new BacklogIntakePlan("backlog.db", [], 0, null);

        var actual = AsyncLocalConsoleRouter.Capture(() => BacklogIntakePlanTextView.Print(plan));

        Assert.Equal(string.Join(Environment.NewLine,
            "", "Backlog intake: 0 item(s) from backlog.db", "", ""), actual);
    }

    [Fact]
    public void Print_FilteredItemWithQuotedHeading_PreservesExactText()
    {
        var item = new BacklogIntakeItem(
            "item-1", "Update \"CLI\" at C:\\repo", "Details",
            ["src/View.cs", "tests/ViewTests.cs"], [AgentRole.Developer, AgentRole.Tester],
            ["cli", "output"], ["Build", "Check output"], ["first", "second"],
            "Preserve CLI output.", "isolated worktree", "Exact output", "Close item",
            RepositoryScopeConfidence.Precise, []);
        var plan = new BacklogIntakePlan("backlog.db", [item], 3, "cli");

        var actual = AsyncLocalConsoleRouter.Capture(() => BacklogIntakePlanTextView.Print(plan));

        Assert.Equal(string.Join(Environment.NewLine,
            "",
            "Backlog intake: 1 of 3 item(s) from backlog.db (filter: cli; 2 withheld)",
            "Use `backlog-list` to see all items.",
            "",
            "## Update \"CLI\" at C:\\repo",
            "Roles: Developer, Tester",
            "Risks: cli, output",
            "Target files/scopes:",
            "  - src/View.cs",
            "  - tests/ViewTests.cs",
            "Verification:",
            "  - Build",
            "  - Check output",
            "Dependencies:",
            "  - first",
            "  - second",
            "Workspace: isolated worktree",
            "Acceptance: Exact output",
            "Follow-up: Close item",
            "Create commands:",
            "  backlog-intake \"Update \\\"CLI\\\" at C:\\\\repo\" --create-goal --backlog-coverage <full|slice>",
            "  backlog-intake \"Update \\\"CLI\\\" at C:\\\\repo\" --create-simple-goal --backlog-coverage <full|slice>",
            "Ready objective:",
            "Preserve CLI output.",
            "", ""), actual);
    }

    [Fact]
    public void Print_FilteredEmptySelection_PreservesExactText()
    {
        var plan = new BacklogIntakePlan("backlog.db", [], 2, "none");

        var actual = AsyncLocalConsoleRouter.Capture(() => BacklogIntakePlanTextView.Print(plan));

        Assert.Equal(string.Join(Environment.NewLine,
            "",
            "Backlog intake: 0 of 2 item(s) from backlog.db (filter: none; 2 withheld)",
            "Use `backlog-list` to see all items.",
            "", ""), actual);
    }
}
