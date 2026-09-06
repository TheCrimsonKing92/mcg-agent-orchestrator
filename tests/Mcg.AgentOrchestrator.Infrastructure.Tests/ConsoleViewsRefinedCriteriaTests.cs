using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;

public sealed class ConsoleViewsRefinedCriteriaTests
{
    [Xunit.Fact]
    public void PrintGoalRendersRefinedCriteriaAndWarnsWhenDeclaredCountDiffers()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("""
            Keep criteria visible.

            ## Acceptance criteria

            1. First declared criterion.
            2. Second declared criterion.
            """);
        kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec(
            "Keep the refined list visible.",
            ["First refined criterion.", "Second refined criterion.", "Third refined criterion."],
            VerificationClass.TestVerifiable,
            [],
            []));

        var output = InfrastructureTestSupport.CaptureConsole(() => ConsoleViews.PrintGoal(goal));

        Xunit.Assert.Contains("Brief acceptance criteria (2):", output, StringComparison.Ordinal);
        Xunit.Assert.Contains("  2. Second declared criterion.", output, StringComparison.Ordinal);
        Xunit.Assert.Contains("Refined acceptance criteria (3):", output, StringComparison.Ordinal);
        Xunit.Assert.Contains("  1. First refined criterion.", output, StringComparison.Ordinal);
        Xunit.Assert.Contains("  3. Third refined criterion.", output, StringComparison.Ordinal);
        Xunit.Assert.Contains(
            "WARNING: refined acceptance criteria count 3 differs from brief declared count 2.",
            output,
            StringComparison.Ordinal);
    }
}
