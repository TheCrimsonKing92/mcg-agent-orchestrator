using System.Globalization;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel-safe: AsyncLocal console capture, async-context culture, and local fixtures.
// Every expected string is derived from ConsoleViews.LoopHealth.cs at base fd85596fc.
public sealed class LoopHealthReportTextViewCharacterizationTests
{
    [Fact]
    public void Print_EmptyWindow_PreservesExactText()
    {
        var snapshot = new LoopHealthSnapshot(0, 0, 0, 0, 0, 0, 0, 0, null, [], [], 0, 0, 0);

        var actual = Capture(snapshot);

        Assert.Equal(string.Join(Environment.NewLine,
            "",
            "Loop health report (0 goals):",
            "  Goals: 0 total, 0 completed",
            "  Tasks: 0 total, 0 dispatches",
            "",
            "  Dispatches / successful merge:  0.0 dispatches/merge (no completed goals)",
            "  False-completion catch rate:    0%",
            "  Operator prompts / goal:        0.00",
            "  Rework / retry rate:            0%",
            "  Median time to acceptance:      n/a (no completed goals)",
            "  Paid retry dispatches / landed: 0.00 (0 total)",
            "  Same-context repeats prevented: 0",
            "  Legacy same-context observed:   0",
            "  Median retry resolution:        n/a (no completed goals)",
            "  Retry authority unavailable:    fingerprint=0, paid=0, cause=0",
            "  Focused evidence:               attempts=0, elapsed_ms=unavailable, provider_usage_unavailable=0",
            "  First-pass completion by role:",
            "    All five roles: 0/0 goals",
            "  Retry causes:",
            "",
            "  Per-model outcome mix:",
            "    No completed or failed dispatches with model selection found.",
            "", ""), actual);
    }

    [Fact]
    public void Print_CompletedGoalWithRetryEvidence_PreservesExactText()
    {
        var snapshot = new LoopHealthSnapshot(1, 1, 4, 6, 6, 0.5, 1.25, 0.25, 0.5,
            [new("OpenAI", "model-a", 3, 1, 2, 1, 1, 0, ModelOutcomeRecommendation.Neutral, "mixed")],
            [], 0, 0, 0,
            PaidRetryDispatchCount: 2, PaidRetryDispatchesPerLandedGoal: 2,
            SameFingerprintPreventedCount: 3, LegacyObservedSameFingerprintRepeatCount: 4,
            RetryCauseDistribution: [new(RetryCause.NewTestFinding, 2)],
            MedianRetryResolutionHours: 1.5,
            FirstPassCompletionByRole: [new(AgentRole.Developer, 2, 1, 0.5), new(AgentRole.Tester, 0, 0, null)],
            FiveRoleFirstPassGoalCount: 1, FiveRoleGoalCount: 1,
            RetryFingerprintUnavailableCount: 5, RetryPaidAuthorityUnknownCount: 6,
            RetryCauseUnavailableCount: 7, EvidenceAttemptCount: 8,
            EvidenceElapsedMilliseconds: 1234, EvidenceProviderUsageUnavailableCount: 9);

        var actual = Capture(snapshot);

        Assert.Equal(string.Join(Environment.NewLine,
            "",
            "Loop health report (1 goal):",
            "  Goals: 1 total, 1 completed",
            "  Tasks: 4 total, 6 dispatches",
            "",
            "  Dispatches / successful merge:  6.0 dispatches/merge",
            "  False-completion catch rate:    50%",
            "  Operator prompts / goal:        1.25",
            "  Rework / retry rate:            25%",
            "  Median time to acceptance:      30 min",
            "  Paid retry dispatches / landed: 2.00 (2 total)",
            "  Same-context repeats prevented: 3",
            "  Legacy same-context observed:   4",
            "  Median retry resolution:        1.5 hr",
            "  Retry authority unavailable:    fingerprint=5, paid=6, cause=7",
            "  Focused evidence:               attempts=8, elapsed_ms=1234, provider_usage_unavailable=9",
            "  First-pass completion by role:",
            "    Developer: 50% (1/2)",
            "    Tester: N/A (0/0)",
            "    All five roles: 1/1 goals",
            "  Retry causes:",
            "    NewTestFinding: 2",
            "",
            "  Per-model outcome mix:",
            "    OpenAI/model-a: Neutral",
            "      completed=3 failed=1 adequate=2 overkill=1 underpowered=1",
            "", ""), actual);
    }

    private static string Capture(LoopHealthSnapshot snapshot)
    {
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            return AsyncLocalConsoleRouter.Capture(() => LoopHealthReportTextView.Print(snapshot));
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }
}
