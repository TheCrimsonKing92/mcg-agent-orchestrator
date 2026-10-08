using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each instance owns a unique ledger directory and fixed timestamps.
public sealed class RemoteLaneFailureHistoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "remote-failure-history-" + Guid.NewGuid().ToString("N"));
    private readonly DateTimeOffset _now = DateTimeOffset.Parse("2026-10-08T12:00:00Z");
    private string Ledger => Path.Combine(_root, "health.jsonl");
    private IReadOnlyDictionary<(string Lane, string Filter), RemoteLaneFailureStreak> Read() =>
        new RemoteLaneFailureHistory().Read(Ledger);

    [Xunit.Fact]
    public void FailuresAfterAccepted_CountThreeAndReportNewestTimestamp()
    {
        Append(RemoteLaneOutcomeCode.Accepted, _now.AddHours(-4));
        Append(RemoteLaneOutcomeCode.RemoteRed, _now.AddHours(-3));
        Append(RemoteLaneOutcomeCode.RemoteRed, _now.AddHours(-1));
        Append(RemoteLaneOutcomeCode.RemoteRed, _now.AddHours(-2));

        Assert.Equal(new RemoteLaneFailureStreak(3, _now.AddHours(-1)), Read()[("lane", "filter")]);
    }

    [Xunit.Fact]
    public void AcceptedAfterFailures_ClearsCountAndNewestTimestampInAppendOrder()
    {
        Append(RemoteLaneOutcomeCode.RemoteRed, _now);
        Append(RemoteLaneOutcomeCode.RemoteRed, _now);
        Append(RemoteLaneOutcomeCode.Accepted, _now.AddDays(-1));

        Assert.Equal(new RemoteLaneFailureStreak(0, null), Read()[("lane", "filter")]);
    }

    [Xunit.Theory]
    [Xunit.InlineData((int)RemoteLaneOutcomeCode.NotEligibleExclusiveResource)]
    [Xunit.InlineData((int)RemoteLaneOutcomeCode.ShadowSkippedNoExecutor)]
    [Xunit.InlineData((int)RemoteLaneOutcomeCode.CancelledAfterGrace)]
    [Xunit.InlineData((int)RemoteLaneOutcomeCode.TransportUnavailable)]
    public void IgnoredOutcome_BetweenFailuresNeitherCountsNorResets(int outcome)
    {
        Append(RemoteLaneOutcomeCode.RemoteRed, _now.AddHours(-2));
        Append((RemoteLaneOutcomeCode)outcome, _now);
        Append(RemoteLaneOutcomeCode.RemoteRed, _now.AddHours(-1));

        Assert.Equal(new RemoteLaneFailureStreak(2, _now.AddHours(-1)), Read()[("lane", "filter")]);
    }

    [Xunit.Fact]
    public void DifferentBindings_KeepStreaksSeparate()
    {
        Append(RemoteLaneOutcomeCode.RemoteRed, _now);
        Append(RemoteLaneOutcomeCode.RemoteRed, _now);
        Append(RemoteLaneOutcomeCode.RemoteRed, _now, filter: "other-filter");
        Append(RemoteLaneOutcomeCode.RemoteRed, _now, lane: "other-lane");
        Append(RemoteLaneOutcomeCode.Accepted, _now, filter: "other-filter");

        var history = Read();
        Assert.Equal(2, history[("lane", "filter")].Count);
        Assert.Equal(new RemoteLaneFailureStreak(0, null), history[("lane", "other-filter")]);
        Assert.Equal(1, history[("other-lane", "filter")].Count);
    }

    [Xunit.Fact]
    public void TailWithoutAccepted_CountsOnlyLast500LinesAndAllFailureKinds()
    {
        for (var index = 0; index < 3; index++) Append(RemoteLaneOutcomeCode.RemoteRed, _now.AddDays(-1));
        File.AppendAllLines(Ledger, Enumerable.Repeat("malformed", 494));
        foreach (var outcome in new[] { RemoteLaneOutcomeCode.RemoteRed, RemoteLaneOutcomeCode.LateAfterFallback,
            RemoteLaneOutcomeCode.TrxIncomplete, RemoteLaneOutcomeCode.LeaseExpired,
            RemoteLaneOutcomeCode.LaneTimeout, RemoteLaneOutcomeCode.UnexpectedNotExecuted })
            Append(outcome, _now);

        Assert.Equal(new RemoteLaneFailureStreak(6, _now), Read()[("lane", "filter")]);
    }

    [Xunit.Fact]
    public void MissingLedger_ReturnsEmptyHistory() => Assert.Empty(Read());

    [Xunit.Fact]
    public void MalformedAndUnknownRows_DoNotChangeStreak()
    {
        Append(RemoteLaneOutcomeCode.RemoteRed, _now);
        var valid = File.ReadAllText(Ledger).Trim();
        File.AppendAllLines(Ledger,
        [
            "malformed", "null", "{}",
            valid.Replace("remote-red", "future-outcome", StringComparison.Ordinal),
            valid.Replace("observed_at", "missing_timestamp", StringComparison.Ordinal),
            valid.Replace("remote-red", "REMOTE-RED", StringComparison.Ordinal)
        ]);

        Assert.Equal(new RemoteLaneFailureStreak(2, _now), Read()[("lane", "filter")]);
        File.AppendAllLines(Ledger, [valid.Replace("remote-red", "ACCEPTED", StringComparison.Ordinal)]);
        Assert.Equal(new RemoteLaneFailureStreak(0, null), Read()[("lane", "filter")]);
    }

    private void Append(RemoteLaneOutcomeCode outcome, DateTimeOffset observedAt,
        string lane = "lane", string filter = "filter")
    {
        var binding = new RemoteLaneBinding("executor", lane, "commit", "tree", "main", filter, "manifest");
        RemoteExecutorHealthLedger.Append(Ledger,
            new(observedAt, binding.ExecutorId, "seed-attempt", lane, outcome, null, binding, null));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
