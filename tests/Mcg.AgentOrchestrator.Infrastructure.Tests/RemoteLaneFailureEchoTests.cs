using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each instance owns a unique ledger directory and fixed timestamps.
public sealed class RemoteLaneFailureEchoTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "remote-failure-echo-" + Guid.NewGuid().ToString("N"));
    private readonly DateTimeOffset _now = DateTimeOffset.Parse("2026-10-08T12:00:00Z");
    private string Ledger => Path.Combine(_root, "health.jsonl");
    private IReadOnlyDictionary<(string Lane, string Filter), RemoteLaneFailureStreak> Read() =>
        new RemoteLaneFailureHistory().Read(Ledger);

    [Xunit.Fact]
    public void RemoteRedThenEcho_CountsOneFailure()
    {
        Append(RemoteLaneOutcomeCode.RemoteRed);
        Append(RemoteLaneOutcomeCode.LateAfterFallback);

        Assert.Equal(1, Read()[("lane", "filter")].Count);
    }

    [Xunit.Fact]
    public void DistinctFailuresWithEchoes_HoldOnlyAtThreeFailures()
    {
        foreach (var attempt in new[] { "a1", "a2" })
        {
            Append(RemoteLaneOutcomeCode.RemoteRed, attempt: attempt);
            Append(RemoteLaneOutcomeCode.LateAfterFallback, attempt: attempt);
        }

        var streak = Read()[("lane", "filter")];
        Assert.Equal(2, streak.Count);
        var decision = Decide(streak);
        Assert.NotEqual("remote-failing", decision.Reason);
        Assert.Equal("fits", decision.Reason);
        Assert.True(decision.Offer);

        Append(RemoteLaneOutcomeCode.RemoteRed, attempt: "a3");
        Append(RemoteLaneOutcomeCode.LateAfterFallback, attempt: "a3");
        streak = Read()[("lane", "filter")];
        Assert.Equal(3, streak.Count);
        decision = Decide(streak);
        Assert.Equal("remote-failing", decision.Reason);
        Assert.False(decision.Offer);
    }

    [Xunit.Theory]
    [Xunit.InlineData((int)RemoteLaneOutcomeCode.LeaseExpired)]
    [Xunit.InlineData((int)RemoteLaneOutcomeCode.TrxIncomplete)]
    [Xunit.InlineData((int)RemoteLaneOutcomeCode.UnexpectedNotExecuted)]
    [Xunit.InlineData((int)RemoteLaneOutcomeCode.LaneTimeout)]
    public void CountedNonLateOutcomeThenEcho_CountsOneFailure(int outcome)
    {
        Append((RemoteLaneOutcomeCode)outcome);
        Append(RemoteLaneOutcomeCode.LateAfterFallback);

        Assert.Equal(1, Read()[("lane", "filter")].Count);
    }

    [Xunit.Fact]
    public void StandaloneLate_CountsOneFailure()
    {
        Append(RemoteLaneOutcomeCode.LateAfterFallback);

        Assert.Equal(new RemoteLaneFailureStreak(1, _now), Read()[("lane", "filter")]);
    }

    [Xunit.Theory]
    [Xunit.InlineData((int)RemoteLaneOutcomeCode.TransportUnavailable)]
    [Xunit.InlineData((int)RemoteLaneOutcomeCode.Unreachable)]
    public void UncountedOutcomeThenLate_CountsOneFailure(int outcome)
    {
        Append((RemoteLaneOutcomeCode)outcome);
        Append(RemoteLaneOutcomeCode.LateAfterFallback);

        Assert.Equal(1, Read()[("lane", "filter")].Count);
    }

    [Xunit.Fact]
    public void DifferentAttemptLate_CountsAnotherFailure()
    {
        Append(RemoteLaneOutcomeCode.RemoteRed);
        Append(RemoteLaneOutcomeCode.LateAfterFallback, attempt: "a2");

        Assert.Equal(2, Read()[("lane", "filter")].Count);
    }

    [Xunit.Fact]
    public void DifferentLaneLate_CountsIndependently()
    {
        Append(RemoteLaneOutcomeCode.RemoteRed);
        Append(RemoteLaneOutcomeCode.LateAfterFallback, lane: "other-lane");

        var history = Read();
        Assert.Equal(1, history[("lane", "filter")].Count);
        Assert.Equal(1, history[("other-lane", "filter")].Count);
    }

    [Xunit.Fact]
    public void Echo_DoesNotMoveNewestFailureAt()
    {
        Append(RemoteLaneOutcomeCode.RemoteRed);
        Append(RemoteLaneOutcomeCode.LateAfterFallback, observedAt: _now.AddHours(2));

        Assert.Equal(new RemoteLaneFailureStreak(1, _now), Read()[("lane", "filter")]);
    }

    [Xunit.Fact]
    public void AcceptedAfterEcho_ResetsThenNextFailureCountsOne()
    {
        Append(RemoteLaneOutcomeCode.RemoteRed);
        Append(RemoteLaneOutcomeCode.LateAfterFallback);
        Append(RemoteLaneOutcomeCode.Accepted);

        Assert.Equal(new RemoteLaneFailureStreak(0, null), Read()[("lane", "filter")]);
        Append(RemoteLaneOutcomeCode.RemoteRed, attempt: "a2", observedAt: _now.AddHours(1));
        Assert.Equal(new RemoteLaneFailureStreak(1, _now.AddHours(1)), Read()[("lane", "filter")]);
    }

    [Xunit.Fact]
    public void EchoAfterAccepted_DoesNotRestartStreak()
    {
        Append(RemoteLaneOutcomeCode.RemoteRed);
        Append(RemoteLaneOutcomeCode.Accepted);
        Append(RemoteLaneOutcomeCode.LateAfterFallback, observedAt: _now.AddHours(2));

        Assert.Equal(new RemoteLaneFailureStreak(0, null), Read()[("lane", "filter")]);
    }

    [Xunit.Fact]
    public void FailureOutsideWindow_DoesNotSuppressLateRow()
    {
        Append(RemoteLaneOutcomeCode.RemoteRed);
        File.AppendAllLines(Ledger, Enumerable.Repeat("malformed", 499));
        Append(RemoteLaneOutcomeCode.LateAfterFallback, observedAt: _now.AddHours(2));

        Assert.Equal(new RemoteLaneFailureStreak(1, _now.AddHours(2)), Read()[("lane", "filter")]);
    }

    [Xunit.Theory]
    [Xunit.InlineData("")]
    [Xunit.InlineData("   ")]
    [Xunit.InlineData(null)]
    public void BlankOrMissingAttempt_CountsBothRows(string? attempt)
    {
        Append(RemoteLaneOutcomeCode.RemoteRed, attempt: attempt ?? "a1");
        Append(RemoteLaneOutcomeCode.LateAfterFallback, attempt: attempt ?? "a1");
        if (attempt is null)
        {
            var rows = File.ReadAllLines(Ledger).Select(line =>
            {
                var row = JsonNode.Parse(line)!.AsObject();
                row.Remove("gate_attempt_id");
                return row.ToJsonString();
            }).ToArray();
            File.WriteAllLines(Ledger, rows);
        }

        Assert.Equal(2, Read()[("lane", "filter")].Count);
    }

    [Xunit.Fact]
    public void LateBeforeFailure_AndRepeatedStandaloneLate_CountNormally()
    {
        Append(RemoteLaneOutcomeCode.LateAfterFallback);
        Append(RemoteLaneOutcomeCode.LateAfterFallback);
        Append(RemoteLaneOutcomeCode.RemoteRed);
        Append(RemoteLaneOutcomeCode.LateAfterFallback);

        Assert.Equal(3, Read()[("lane", "filter")].Count);
    }

    [Xunit.Fact]
    public void MalformedFailure_DoesNotSuppressValidLateRow()
    {
        Append(RemoteLaneOutcomeCode.RemoteRed);
        File.WriteAllText(Ledger, File.ReadAllText(Ledger).Replace("observed_at", "missing_timestamp", StringComparison.Ordinal));
        Append(RemoteLaneOutcomeCode.LateAfterFallback);

        Assert.Equal(1, Read()[("lane", "filter")].Count);
    }

    [Xunit.Fact]
    public void SameAttemptDifferentFilter_EchoIsStillSuppressed()
    {
        Append(RemoteLaneOutcomeCode.RemoteRed);
        Append(RemoteLaneOutcomeCode.LateAfterFallback, filter: "other-filter");

        var history = Read();
        Assert.Equal(1, history[("lane", "filter")].Count);
        Assert.False(history.ContainsKey(("lane", "other-filter")));
    }

    [Xunit.Theory]
    [Xunit.InlineData(" a1 ", 1)]
    [Xunit.InlineData("A1", 2)]
    public void AttemptMatching_TrimsButRemainsCaseSensitive(string attempt, int expected)
    {
        Append(RemoteLaneOutcomeCode.RemoteRed);
        Append(RemoteLaneOutcomeCode.LateAfterFallback, attempt: attempt);

        Assert.Equal(expected, Read()[("lane", "filter")].Count);
    }

    [Xunit.Fact]
    public void RepeatedReads_DoNotRetainSuppressionState()
    {
        var reader = new RemoteLaneFailureHistory();
        Append(RemoteLaneOutcomeCode.RemoteRed);
        Append(RemoteLaneOutcomeCode.LateAfterFallback);
        Assert.Equal(1, reader.Read(Ledger)[("lane", "filter")].Count);

        File.WriteAllLines(Ledger, File.ReadAllLines(Ledger).Skip(1));
        Assert.Equal(1, reader.Read(Ledger)[("lane", "filter")].Count);
    }

    private RemoteLaneOfferDecision Decide(RemoteLaneFailureStreak streak) =>
        RemoteLaneOfferPolicy.Decide(new("lane", 345, 3, 345, 300, 3, 400,
            streak.Count, streak.NewestFailureAt, _now.AddMinutes(1)));

    private void Append(RemoteLaneOutcomeCode outcome, string attempt = "a1", string lane = "lane",
        string filter = "filter", DateTimeOffset? observedAt = null)
    {
        var binding = new RemoteLaneBinding("executor", lane, "commit", "tree", "main", filter, "manifest");
        RemoteExecutorHealthLedger.Append(Ledger,
            new(observedAt ?? _now, binding.ExecutorId, attempt, lane, outcome, null, binding, null));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
