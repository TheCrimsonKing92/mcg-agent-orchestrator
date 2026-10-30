using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel-safe: AsyncLocal console capture and immutable, local fixtures.
public sealed class DogfoodEvaluationTextViewCharacterizationTests
{
    [Fact]
    public void Print_MetricSignsAndRecommendations_PreservesExactText()
    {
        var report = new HistoricalDogfoodEvaluationReport(new GoalId("goal-id"), "goal-abc",
            "Evaluate this goal.", GoalStatus.Active, 85, "B",
        [
            new("Retries", 2, 10, "Two retries."),
            new("Evidence", 3, -5, "Extra evidence."),
            new("Failures", 0, 0, "No failures.")
        ], ["Inspect retries.", "Keep the evidence."]);

        var actual = AsyncLocalConsoleRouter.Capture(() =>
            DogfoodEvaluationTextView.PrintHistoricalDogfoodEvaluation(report));

        Assert.Equal(string.Join(Environment.NewLine,
            "Dogfood evaluation goal-abc Active: Evaluate this goal.",
            "Score: 85/100 (B)",
            "Metrics:",
            "  Retries: value=2, score -10",
            "    Two retries.",
            "  Evidence: value=3, score +5",
            "    Extra evidence.",
            "  Failures: value=0, score 0",
            "    No failures.",
            "Recommendations:",
            "  - Inspect retries.",
            "  - Keep the evidence.",
            ""), actual);
    }

    [Fact]
    public void Print_EmptyMetricsAndRecommendations_PreservesExactText()
    {
        var report = new HistoricalDogfoodEvaluationReport(new GoalId("goal-id"), "goal-abc",
            "Evaluate this goal.", GoalStatus.Active, 100, "A", [], []);

        var actual = AsyncLocalConsoleRouter.Capture(() =>
            DogfoodEvaluationTextView.PrintHistoricalDogfoodEvaluation(report));

        Assert.Equal(string.Join(Environment.NewLine,
            "Dogfood evaluation goal-abc Active: Evaluate this goal.",
            "Score: 100/100 (A)",
            "Metrics:",
            "Recommendations:",
            "  none",
            ""), actual);
    }

    [Fact]
    public void ExtractedOwner_LeavesNoPartialFileOrConsoleViewsForwarders()
    {
        var cliRoot = Path.Combine(VerifiedRepositoryRoot.Find(), "src", "Mcg.AgentOrchestrator.App", "Cli");
        Assert.False(File.Exists(Path.Combine(cliRoot, "ConsoleViews.DogfoodEvaluation.cs")));
        var source = File.ReadAllText(Path.Combine(cliRoot, "DogfoodEvaluationTextView.cs"));
        Assert.Contains("internal static class DogfoodEvaluationTextView", source);
        Assert.DoesNotMatch(@"\bpartial\s+class\b", source);
    }
}
