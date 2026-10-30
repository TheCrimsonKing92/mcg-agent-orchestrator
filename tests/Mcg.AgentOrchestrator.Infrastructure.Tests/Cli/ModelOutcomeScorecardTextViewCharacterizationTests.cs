using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel-safe: AsyncLocal console capture and immutable, local fixtures.
// Expected text follows ConsoleViews.ModelOutcomes.cs at base 8711cbf2f.
public sealed class ModelOutcomeScorecardTextViewCharacterizationTests
{
    [Fact]
    public void Print_EmptyHistoryWithoutBoundSet_PreservesExactText()
    {
        var actual = AsyncLocalConsoleRouter.Capture(() =>
            ModelOutcomeScorecardTextView.PrintModelOutcomeScorecard([], null));

        Assert.Equal(string.Join(Environment.NewLine,
            "",
            "Model outcome scorecard (0 model(s)):",
            "  No completed or failed dispatches with model selection found.",
            "",
            "  Bound model set unavailable (bound model set not supplied); all models listed as bound.",
            "", ""), actual);
    }

    [Fact]
    public void Print_BoundAndRetiredModels_PreservesExactText()
    {
        ModelOutcomeRecord[] records =
        [
            new("OpenAI", "model-a", 4, 3, 2, 1, 1, 2,
                ModelOutcomeRecommendation.Neutral, "Mixed outcomes.",
                RealFailures: 1, EnvironmentalFailures: 1, ManufacturedFixedFailures: 1,
                UnknownEraFailures: 2, DispatchLane: "codex-cli", ClassMismatchFailures: 2,
                ClassMismatchRules: "reconciled=2", Findings: 5),
            new("Anthropic", "model-b", 2, 1, 0, 0, 0, 0,
                ModelOutcomeRecommendation.Avoid, "Retired.", DispatchLane: " ", IsBound: false)
        ];
        var bound = BoundModelSet.Available(["model-a"]);

        var actual = AsyncLocalConsoleRouter.Capture(() =>
            ModelOutcomeScorecardTextView.PrintModelOutcomeScorecard(records, bound));

        Assert.Equal(string.Join(Environment.NewLine,
            "",
            "Model outcome scorecard (2 model(s)):",
            "",
            "  OpenAI/model-a lane=codex-cli: Neutral",
            "    completed=4 failed=3 findings=5 realFailed=1 environmentalFailed=1 manufacturedFixedFailed=1 unknownEraFailed=2 classMismatchFailed=2 classMismatchRules=reconciled=2 adequate=2 overkill=1 underpowered=1 divergence=2",
            "    reason: Mixed outcomes. Class-mismatch failures: 2.",
            "  Retired models: Anthropic/model-b lane=unrecorded completed=2 failed=1",
            "", ""), actual);
    }

    [Fact]
    public void Print_UnavailableBoundSetListsUnboundRecord_PreservesExactText()
    {
        ModelOutcomeRecord[] records =
        [new("OpenAI", "model-a", 1, 0, 1, 0, 0, 0,
            ModelOutcomeRecommendation.Prefer, "Reliable.", IsBound: false)];
        var unavailable = BoundModelSet.Unavailable("catalog missing");

        var actual = AsyncLocalConsoleRouter.Capture(() =>
            ModelOutcomeScorecardTextView.PrintModelOutcomeScorecard(records, unavailable));

        Assert.Equal(string.Join(Environment.NewLine,
            "",
            "Model outcome scorecard (1 model(s)):",
            "",
            "  OpenAI/model-a lane=unrecorded: Prefer",
            "    completed=1 failed=0 findings=0 realFailed=0 environmentalFailed=0 manufacturedFixedFailed=0 unknownEraFailed=0 classMismatchFailed=0 classMismatchRules= adequate=1 overkill=0 underpowered=0 divergence=0",
            "    reason: Reliable.",
            "  Bound model set unavailable (catalog missing); all models listed as bound.",
            "", ""), actual);
    }

    [Fact]
    public void Print_EmptyHistoryWithAvailableBoundSet_PreservesExactText()
    {
        var bound = BoundModelSet.Available([]);

        var actual = AsyncLocalConsoleRouter.Capture(() =>
            ModelOutcomeScorecardTextView.PrintModelOutcomeScorecard([], bound));

        Assert.Equal(string.Join(Environment.NewLine,
            "",
            "Model outcome scorecard (0 model(s)):",
            "  No completed or failed dispatches with model selection found.",
            "", "", ""), actual);
    }
}
