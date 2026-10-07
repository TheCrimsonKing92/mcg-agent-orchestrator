using Mcg.AgentOrchestrator.Core;

public sealed class RemoteExecutorReportTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 6, 0, 0, 0, TimeSpan.Zero);
    private static RemoteExecutorOutcomeRow Row(int minute, string outcome, string id = "one", string? reason = null) =>
        new(Start.AddMinutes(minute), id, $"attempt-{minute}", "lane", outcome, reason);

    [Xunit.Fact]
    public void AttemptsExcludeDiagnosticsAndRecentRowsAreOrderedLimitedAndTruncated()
    {
        var report = RemoteExecutorReport.Build(["zero", "one"],
            [Row(0, "accepted"), Row(1, "unreachable"), Row(2, "accepted", reason: new string('x', 200)),
             Row(3, "late-after-fallback"), Row(4, "not-eligible-exclusive-resource"), Row(0, "remote-red", "red")],
            [], last: 2);
        Assert.Equal(new[] { "one", "red", "zero" }, report.Executors.Select(row => row.ExecutorId));
        var row = report.Executors[0];
        Assert.Equal(3, row.Attempts);
        Assert.Equal(2, row.OutcomeCounts["accepted"]);
        Assert.Equal(1, row.OutcomeCounts["unreachable"]);
        Assert.Equal(2, row.OutcomeCounts.Count);
        Assert.Equal(2d / 3, row.AcceptedShare);
        Assert.Equal(new[] { "attempt-2", "attempt-1" }, row.RecentAttempts.Select(attempt => attempt.GateAttemptId));
        Assert.Equal(160, row.RecentAttempts[0].Reason!.Length);
        Assert.Equal(Start.AddMinutes(2), row.LastSuccessAt);
        Assert.Null(report.Executors[1].LastSuccessAt);
        Assert.Equal(0d, report.Executors[1].AcceptedShare);
        var zero = report.Executors[2];
        Assert.Equal(0, zero.Attempts);
        Assert.Null(zero.AcceptedShare);
        Assert.Null(zero.LastSuccessAt);
        Assert.Empty(zero.RecentAttempts);
    }

    [Xunit.Fact]
    public void SinceLimitsAttemptsButKeepsWholeLedgerSuccessAndProbe()
    {
        var probe = new RemoteExecutorProbeRow(Start, "one", true, 0, false, "Ready", true, [], "");
        var older = probe with { ObservedAt = Start.AddDays(-1), TaskState = "Disabled" };
        var report = RemoteExecutorReport.Build([], [Row(0, "accepted"), Row(2, "unreachable")],
            [probe, older, probe with { ExecutorId = "probe-only" }], Start.AddMinutes(1), 10, 2, 3);
        var row = report.Executors[0];
        Assert.Equal(1, row.Attempts);
        Assert.Equal(1, row.OutcomeCounts["unreachable"]);
        Assert.False(row.OutcomeCounts.ContainsKey("accepted"));
        Assert.Equal(0d, row.AcceptedShare);
        Assert.Equal(Start, row.LastSuccessAt);
        Assert.Equal("attempt-2", Assert.Single(row.RecentAttempts).GateAttemptId);
        Assert.Equal(probe, row.LastProbe);
        Assert.Equal("probe-only", report.Executors[1].ExecutorId);
        Assert.Equal(2, report.UnreadableOutcomeLines);
        Assert.Equal(3, report.UnreadableProbeLines);
    }
}
