using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: AsyncLocal console capture and immutable, local fixtures.
public sealed class IdeationPlanTextViewCharacterizationTests
{
    [Fact]
    public void Print_ValidIdeas_PreservesExactText()
    {
        var plan = new IdeationPlan(
        [
            new("Extract views", "Three printers remain.", "CLI", "Clear ownership", "Small", "Low"),
            new("Pin output", "Two regressions found.", "Tests", "Stable text", "Medium", "None")
        ], []);

        var actual = AsyncLocalConsoleRouter.Capture(() =>
            IdeationPlanTextView.PrintIdeationPlan(plan));

        Assert.Equal(string.Join(Environment.NewLine,
            "",
            "Ideation: 2 idea(s) proposed",
            "",
            "  1. Extract views",
            "     Rationale: Three printers remain.",
            "     Scope:     CLI",
            "     Value:     Clear ownership",
            "     Effort:    Small",
            "     Risk:      Low",
            "",
            "  2. Pin output",
            "     Rationale: Two regressions found.",
            "     Scope:     Tests",
            "     Value:     Stable text",
            "     Effort:    Medium",
            "     Risk:      None",
            "",
            "Append to the backlog: ideate --append-backlog",
            ""), actual);
    }

    [Fact]
    public void Print_NoIdeas_PreservesExactText()
    {
        var actual = AsyncLocalConsoleRouter.Capture(() =>
            IdeationPlanTextView.PrintIdeationPlan(new([], [])));

        Assert.Equal(string.Join(Environment.NewLine,
            "", "Ideation: 0 idea(s) proposed", "", ""), actual);
    }

    [Fact]
    public void Print_ValidationErrors_PreservesExactText()
    {
        var plan = new IdeationPlan([], ["Idea 1 has no evidence.", "Idea 2 has no scope."]);

        var actual = AsyncLocalConsoleRouter.Capture(() =>
            IdeationPlanTextView.PrintIdeationPlan(plan));

        Assert.Equal(string.Join(Environment.NewLine,
            "",
            "Ideation: 0 idea(s) proposed",
            "",
            "Validation errors (hand-wavy ideas rejected):",
            "  ERROR: Idea 1 has no evidence.",
            "  ERROR: Idea 2 has no scope.",
            "",
            "Fix the validation errors before appending ideas to the backlog.",
            ""), actual);
    }
}
