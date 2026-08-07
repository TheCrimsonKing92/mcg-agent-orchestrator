using Mcg.AgentOrchestrator.App.Orchestration;

public sealed class RetryLoopPolicyTests
{
    public static TheoryData<double, double> ClampCases => new()
    {
        { 0, 5 },
        { -1, 5 },
        { 0.5, 5 },
        { 5, 5 },
        { 15, 15 },
        { 30, 30 }
    };

    [Xunit.Theory]
    [Xunit.MemberData(nameof(ClampCases))]
    public void ClampIntervalEnforcesFiveSecondFloor(double computedSeconds, double expectedSeconds)
    {
        var actual = RetryLoopPolicy.ClampInterval(
            TimeSpan.FromSeconds(computedSeconds),
            TimeSpan.FromSeconds(ConductorBatchLoop.WatchStopPollIntervalSeconds));

        Xunit.Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), actual);
    }

    [Xunit.Fact]
    public void WriteBackoffIsPositiveMonotonicAndCappedForEveryJitterExtreme()
    {
        foreach (var jitter in new[] { 0d, 1d })
        {
            var waits = Enumerable.Range(1, 12)
                .Select(retry => RetryLoopPolicy.GetWriteDelay(retry, jitter))
                .ToArray();

            Xunit.Assert.All(waits, wait => Xunit.Assert.True(wait > TimeSpan.Zero));
            Xunit.Assert.All(waits, wait => Xunit.Assert.True(wait <= RetryLoopPolicy.MaximumWriteDelay));
            for (var index = 1; index < waits.Length; index++)
                Xunit.Assert.True(waits[index] >= waits[index - 1]);
        }
    }

    [Xunit.Fact]
    public void DiagnosticCoalescingIsBoundedAndKeyedPerGoal()
    {
        var now = DateTimeOffset.UnixEpoch;
        var coalescer = new RetryDiagnosticCoalescer(() => now);
        var first = new RetryDiagnosticKey("EVENT", "goal-a", "busy");
        var second = new RetryDiagnosticKey("EVENT", "goal-b", "busy");
        var lines = new List<string>();

        for (var attempt = 1; attempt <= 1000; attempt++)
        {
            foreach (var key in new[] { first, second })
            {
                var line = coalescer.Observe(key, $"EVENT goal={key.Goal} attempt={attempt}");
                if (line is not null)
                    lines.Add(line);
            }
            now = now.AddMilliseconds(100);
        }

        lines.Add(coalescer.Complete(first)!);
        lines.Add(coalescer.Complete(second)!);
        Xunit.Assert.True(lines.Count <= 16, $"Expected bounded diagnostics, observed {lines.Count} lines.");
        Xunit.Assert.Contains(lines, line => line.Contains("goal=goal-a", StringComparison.Ordinal));
        Xunit.Assert.Contains(lines, line => line.Contains("goal=goal-b", StringComparison.Ordinal));
        Xunit.Assert.Equal(2, lines.Count(line => line.StartsWith("RETRY_TERMINAL_SUMMARY", StringComparison.Ordinal)));
    }

    [Xunit.Fact]
    public void CompletedConditionGetsFreshVerbatimBudgetWhenItRecurs()
    {
        var coalescer = new RetryDiagnosticCoalescer(() => DateTimeOffset.UnixEpoch);
        var key = new RetryDiagnosticKey("EVENT", "goal", "condition");
        for (var attempt = 0; attempt < 4; attempt++)
            _ = coalescer.Observe(key, $"first-run-{attempt}");

        Xunit.Assert.NotNull(coalescer.Complete(key));
        Xunit.Assert.Equal("second-run", coalescer.Observe(key, "second-run"));
    }
}
