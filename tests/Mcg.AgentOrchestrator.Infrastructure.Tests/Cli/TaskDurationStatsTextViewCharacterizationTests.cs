using System.Globalization;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel-safe: AsyncLocal console capture, async-context culture, and local fixtures.
// Expected strings derive from ConsoleViews.TaskDurations.cs at base
// 3c9cba3f484b78afc3ccb5a7e7a04d5c33dc0d4b.
public sealed class TaskDurationStatsTextViewCharacterizationTests
{
    [Fact]
    public void Print_EmptyHistory_PreservesExactText()
    {
        var actual = Capture(() => TaskDurationStatsTextView.Print([]));

        Assert.Equal(string.Join(Environment.NewLine,
            "",
            "Task duration stats (all time, 0 group(s)):",
            "  No dispatch timing history found.",
            "", ""), actual);
    }

    [Fact]
    public void Print_PublishedStatsWithNullAndZeroDurations_PreservesExactText()
    {
        var since = new DateTimeOffset(2026, 10, 30, 8, 0, 0, TimeSpan.Zero);
        TaskDurationStatsRecord record = new(AgentRole.Developer, TaskComplexity.Complex,
            "OpenAI", "model-a", 4, 8, 4, TimeSpan.Zero, null, TimeSpan.FromSeconds(42.5),
            0.5, RealFailureRate: 0.1, EnvironmentalFailureRate: 0.25,
            ManufacturedFixedFailureRate: 0.15, UnknownEraFailureRate: 0,
            FindingAttemptCount: 2);
        TaskDurationTrendRecord day = new(new DateOnly(2026, 10, 30), 4, 8, 4, 2, 0.5, 2);

        var actual = Capture(() => TaskDurationStatsTextView.Print([record], since, [day]));

        Assert.Equal(string.Join(Environment.NewLine,
            "",
            "Task duration stats (since 2026-10-30T08:00:00.0000000+00:00, 1 group(s)):",
            "  Developer/Complex OpenAI/model-a: tasks=4 attempts=8 findings=2 attemptsPerTask=2.0 legit median=0s p90=n/a overhead median=42.5s failureRate=50% realFailureRate=10% environmentalRate=25% manufacturedFixedRate=15% unknownEraRate=0%",
            "",
            "Daily trend:",
            "  2026-10-30: tasks=4 attempts=8 findings=2 attemptsPerTask=2.0 failureRate=50%",
            "", ""), actual);
    }

    [Fact]
    public void Print_InsufficientSamplesAndEmptyTrend_PreservesExactText()
    {
        TaskDurationStatsRecord record = new(AgentRole.Tester, TaskComplexity.Simple,
            null, null, 2, 3, 0, null, null, null, 0);

        var actual = Capture(() => TaskDurationStatsTextView.Print([record], trend: []));

        Assert.Equal(string.Join(Environment.NewLine,
            "",
            "Task duration stats (all time, 1 group(s)):",
            "  Tester/Simple: tasks=2 attempts=3 findings=0 attemptsPerTask=1.5 legit median=n/a (n=2) p90=n/a (n=2) overhead median=n/a (n=2) failureRate=0% realFailureRate=0% environmentalRate=0% manufacturedFixedRate=0% unknownEraRate=0%",
            "", ""), actual);
    }

    private static string Capture(Action print)
    {
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            return AsyncLocalConsoleRouter.Capture(print);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }
}
