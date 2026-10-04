using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using static InfrastructureTestSupport;

// Parallel-safe: console capture is AsyncLocal-routed, with immutable fixture rows.
public sealed class ModelOutcomeRetiredModelsConsoleTests
{
    [Fact]
    public void BoundModelsPrintFirstAndRetiredModelPrintsOnlyOnSummaryLine()
    {
        var bound = BoundModelSet.Available(["OpenAI/GPT-6.1-SOL", "claude-opus-5-5"]);
        var records = ModelOutcomeScorecard.Build(Rows(), bound);

        var output = CaptureConsole(() => ConsoleViews.PrintModelOutcomeScorecard(records, bound));
        var lines = Lines(output);

        Assert.Contains("Model outcome scorecard (3 model(s)):", output);
        Assert.Contains("  Anthropic/gpt-6.1-sol lane=codex-cli: Prefer", lines);
        Assert.Contains("  Anthropic/claude-opus-5-5 lane=claude-cli: Prefer", lines);
        Assert.DoesNotContain(lines, line => line.StartsWith("  OpenAI/gpt-5.5 lane=", StringComparison.Ordinal));
        var retired = Assert.Single(lines.Where(line => line.StartsWith("  Retired models:", StringComparison.Ordinal)));
        Assert.Equal("  Retired models: OpenAI/gpt-5.5 lane=codex-cli completed=1 failed=2", retired);
        Assert.True(Array.IndexOf(lines, retired) > Array.FindLastIndex(lines, line => line.Contains(": Prefer")));
        Assert.DoesNotContain("Bound model set unavailable", output);
        Assert.Equal(2, records.Count(record => record.IsBound));
        Assert.False(Assert.Single(records.Where(record => record.ModelName == "gpt-5.5")).IsBound);
    }

    [Fact]
    public void UnavailableSetPrintsEveryModelAndExactlyOneReasonLine()
    {
        const string reason = "agents.json unreadable at fixture: invalid JSON";
        var unavailable = BoundModelSet.Unavailable(reason);
        // Start with all rows retired to prove unavailable membership restores the full listing.
        var retired = ModelOutcomeScorecard.Build(Rows(), BoundModelSet.Available([]));
        var records = ModelOutcomeScorecard.ApplyBoundModels(retired, unavailable);

        var output = CaptureConsole(() => ConsoleViews.PrintModelOutcomeScorecard(records, unavailable));
        var lines = Lines(output);

        Assert.Contains(lines, line => line.StartsWith("  Anthropic/gpt-6.1-sol lane=", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.StartsWith("  Anthropic/claude-opus-5-5 lane=", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.StartsWith("  OpenAI/gpt-5.5 lane=", StringComparison.Ordinal));
        Assert.Equal($"  Bound model set unavailable ({reason}); all models listed as bound.",
            Assert.Single(lines.Where(line => line.Contains("Bound model set unavailable"))));
        Assert.DoesNotContain("Retired models:", output);
        Assert.All(records, record => Assert.True(record.IsBound));
    }

    [Fact]
    public void MismatchCounterRulesAndReasonPrintWithoutChangingStoredReason()
    {
        var rows = new[]
        {
            Row("OpenAI", "model", "codex-cli", 0, WorkTaskStatus.Failed, TaskOutcomeClass.Success)
                with { OutcomeRule = "committed-worker-result-evidence" },
            Row("OpenAI", "model", "codex-cli", 1, WorkTaskStatus.Failed, TaskOutcomeClass.Success)
                with { OutcomeRule = "committed-worker-result-evidence" },
            Row("OpenAI", "model", "codex-cli", 2, WorkTaskStatus.Failed, TaskOutcomeClass.ReconciledToSuccess)
        };
        var bound = BoundModelSet.Available(["model"]);
        var records = ModelOutcomeScorecard.Build(rows, bound);

        var output = CaptureConsole(() => ConsoleViews.PrintModelOutcomeScorecard(records, bound));

        Assert.Contains("classMismatchFailed=3 classMismatchRules=committed-worker-result-evidence=2,none=1", output);
        Assert.Contains("    reason: Mixed outcomes. Class-mismatch failures: 3.", Lines(output));
        Assert.Equal("Mixed outcomes.", Assert.Single(records).Reason);
        Assert.Equal(ModelOutcomeRecommendation.Neutral, Assert.Single(records).Recommendation);
    }

    [Fact]
    public void FullyBoundModelsKeepReasonAndPrintEmptyMismatchRules()
    {
        var bound = BoundModelSet.Available(["gpt-6.1-sol", "claude-opus-5-5", "gpt-5.5"]);
        var records = ModelOutcomeScorecard.Build(Rows(), bound);

        var output = CaptureConsole(() => ConsoleViews.PrintModelOutcomeScorecard(records, bound));

        Assert.Equal(3, Lines(output).Count(line => line.Contains("classMismatchFailed=0 classMismatchRules= ")));
        Assert.All(records, record => Assert.Contains($"    reason: {record.Reason}", Lines(output)));
        Assert.DoesNotContain("Class-mismatch failures:", output);
        Assert.DoesNotContain("Retired models:", output);
    }

    [Fact]
    public void AvailableEmptySetRetiresAllRowsInExistingOrder()
    {
        var bound = BoundModelSet.Available([]);
        var records = ModelOutcomeScorecard.Build(Rows(), bound);

        var output = CaptureConsole(() => ConsoleViews.PrintModelOutcomeScorecard(records, bound));

        Assert.Equal("  Retired models: Anthropic/claude-opus-5-5 lane=claude-cli completed=2 failed=0; " +
            "Anthropic/gpt-6.1-sol lane=codex-cli completed=2 failed=0; " +
            "OpenAI/gpt-5.5 lane=codex-cli completed=1 failed=2",
            Assert.Single(Lines(output).Where(line => line.Contains("Retired models:"))));
        Assert.DoesNotContain(Lines(output), line => line.StartsWith("    completed=", StringComparison.Ordinal));
        Assert.DoesNotContain("Bound model set unavailable", output);
    }

    [Fact]
    public void EmptyHistoryStillExplainsUnavailableCatalogs()
    {
        var output = CaptureConsole(() => ConsoleViews.PrintModelOutcomeScorecard([], BoundModelSet.Unavailable("agents.json missing")));

        Assert.Contains("No completed or failed dispatches with model selection found.", output);
        Assert.Single(Lines(output).Where(line => line.Contains("Bound model set unavailable (agents.json missing)")));
    }

    private static string[] Lines(string output) => output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

    private static ModelFitHistoryRow[] Rows() =>
    [
        Row("Anthropic", "gpt-6.1-sol", "codex-cli", 0, WorkTaskStatus.Completed, TaskOutcomeClass.Success),
        Row("Anthropic", "gpt-6.1-sol", "codex-cli", 1, WorkTaskStatus.Completed, TaskOutcomeClass.Success),
        Row("Anthropic", "claude-opus-5-5", "claude-cli", 2, WorkTaskStatus.Completed, TaskOutcomeClass.Success),
        Row("Anthropic", "claude-opus-5-5", "claude-cli", 3, WorkTaskStatus.Completed, TaskOutcomeClass.Success),
        Row("OpenAI", "gpt-5.5", "codex-cli", 4, WorkTaskStatus.Completed, TaskOutcomeClass.Success),
        Row("OpenAI", "gpt-5.5", "codex-cli", 5, WorkTaskStatus.Failed, TaskOutcomeClass.RealFailure),
        Row("OpenAI", "gpt-5.5", "codex-cli", 6, WorkTaskStatus.Failed, TaskOutcomeClass.Environmental)
    ];

    private static ModelFitHistoryRow Row(string provider, string model, string lane, int index,
        WorkTaskStatus status, TaskOutcomeClass outcomeClass) =>
        new("goal", $"task-{index}", AgentRole.Developer, provider, model, null, null, status,
            ModelFitHistory.Unknown, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(index),
            OutcomeClass: outcomeClass, DispatchLane: lane);
}
