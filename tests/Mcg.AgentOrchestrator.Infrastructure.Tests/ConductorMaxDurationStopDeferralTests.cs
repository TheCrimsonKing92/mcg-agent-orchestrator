using Mcg.AgentOrchestrator.App.Orchestration;

public sealed class ConductorMaxDurationStopDeferralTests
{
    private static readonly DateTimeOffset DeferralStartedAt =
        new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

    [Xunit.Fact]
    public void DecideWithoutAttemptsStopsImmediately()
    {
        var verdict = ConductorMaxDurationStopDeferral.Decide(
            DeferralStartedAt,
            DeferralStartedAt,
            [],
            TimeSpan.FromMinutes(40));

        Assert.Equal(ConductorMaxDurationStopVerdictKind.Stop, verdict.Kind);
        Assert.Empty(verdict.Attempts);
    }

    [Xunit.Fact]
    public void DecideBeforeCeilingDefersAndOrdersAttempts()
    {
        var later = Attempt("attempt-b", DeferralStartedAt.AddMinutes(-2));
        var earlier = Attempt("attempt-a", DeferralStartedAt.AddMinutes(-3));

        var verdict = ConductorMaxDurationStopDeferral.Decide(
            DeferralStartedAt.AddMinutes(39),
            DeferralStartedAt,
            [later, earlier],
            TimeSpan.FromMinutes(40));

        Assert.Equal(ConductorMaxDurationStopVerdictKind.Defer, verdict.Kind);
        Assert.Equal(["attempt-a", "attempt-b"], verdict.Attempts.Select(attempt => attempt.AttemptId));
    }

    [Xunit.Theory]
    [Xunit.InlineData(0)]
    [Xunit.InlineData(-1)]
    public void DecideAtNonPositiveCeilingExpires(int ceilingSeconds)
    {
        var verdict = ConductorMaxDurationStopDeferral.Decide(
            DeferralStartedAt,
            DeferralStartedAt,
            [Attempt("attempt-a", DeferralStartedAt.AddMinutes(-1))],
            TimeSpan.FromSeconds(ceilingSeconds));

        Assert.Equal(ConductorMaxDurationStopVerdictKind.StopExpired, verdict.Kind);
        Assert.Equal("attempt-a", Assert.Single(verdict.Attempts).AttemptId);
    }

    [Xunit.Fact]
    public void DecideAtCeilingExpires()
    {
        var verdict = ConductorMaxDurationStopDeferral.Decide(
            DeferralStartedAt.AddMinutes(40),
            DeferralStartedAt,
            [Attempt("attempt-a", DeferralStartedAt.AddMinutes(-1))],
            TimeSpan.FromMinutes(40));

        Assert.Equal(ConductorMaxDurationStopVerdictKind.StopExpired, verdict.Kind);
    }

    [Xunit.Fact]
    public void DecideAfterClockMovesBackwardStillDefers()
    {
        var verdict = ConductorMaxDurationStopDeferral.Decide(
            DeferralStartedAt.AddMinutes(-1),
            DeferralStartedAt,
            [Attempt("attempt-a", DeferralStartedAt.AddMinutes(-2))],
            TimeSpan.FromMinutes(40));

        Assert.Equal(ConductorMaxDurationStopVerdictKind.Defer, verdict.Kind);
    }

    [Xunit.Fact]
    public void SnapshotFailureDefersUntilCeilingThenExpiresLoudly()
    {
        var failure = new InvalidDataException("attempt metadata is unreadable");

        var deferred = ConductorMaxDurationStopDeferral.Decide(
            DeferralStartedAt.AddMinutes(39),
            DeferralStartedAt,
            [],
            TimeSpan.FromMinutes(40),
            failure);
        var expired = ConductorMaxDurationStopDeferral.Decide(
            DeferralStartedAt.AddMinutes(40),
            DeferralStartedAt,
            [],
            TimeSpan.FromMinutes(40),
            failure);
        var detail = ConductorMaxDurationStopDeferral.FormatStopDetail(
            TimeSpan.FromHours(12),
            expired,
            failure);

        Assert.Equal(ConductorMaxDurationStopVerdictKind.Defer, deferred.Kind);
        Assert.Equal(ConductorMaxDurationStopVerdictKind.StopExpired, expired.Kind);
        Assert.Contains("deferralExpired=true", detail, StringComparison.Ordinal);
        Assert.Contains("inflightStateUnavailable=true", detail, StringComparison.Ordinal);
    }

    private static ConductorMaxDurationInFlightAttempt Attempt(string id, DateTimeOffset startedAt) =>
        new(id, ["goal-a"], startedAt);
}
