using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: pure inputs and a fixed clock value; no shared state.
public sealed class ProgramHealthEvaluatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 6, 0, 0, TimeSpan.Zero);
    private static ProgramHealthReceiptObservation Receipt(int age, string id = "merge-train-receipt-1234567890", string head = "main") =>
        new(id, Now.AddMinutes(-age), head, ["4edbbf63355d4bbfb92658e1face8fe5", "69a480fd32864f29a474d741e87a8ecf"]);
    private static ProgramHealthSnapshot Board(int stale = 20, int verified = 0, params ProgramHealthReceiptObservation[] receipts) =>
        new(Now, Now.AddMinutes(-stale), Now.AddDays(-1), "main", verified, receipts);
    private static ProgramHealthAssessment Assess(ProgramHealthSnapshot snapshot) => ProgramHealthEvaluator.Evaluate(snapshot, new());

    [Theory]
    [InlineData(29, false)]
    [InlineData(30, true)]
    public void HeldReceiptCrossesAtThirtyMinutes(int age, bool tripped)
    {
        var receipt = Receipt(age);
        var result = Assess(Board(20, 0, receipt));
        Assert.Equal(tripped, result.Tripped);
        Assert.Equal(tripped ? ["held-receipt"] : Array.Empty<string>(), result.Conditions);
        Assert.Equal(tripped ? receipt.ReceiptId : "", result.TopBlocker);
        Assert.Equal(tripped ? age : 0, result.TopBlockerMinutes);
    }

    [Fact]
    public void ObsoleteAndFutureReceiptsAreNotCurrent()
    {
        var result = Assess(Board(600, 0, Receipt(600, head: "old"), Receipt(-1)));
        Assert.False(result.Tripped);
        Assert.Equal(0, result.CurrentReceipts);
    }

    [Theory]
    [InlineData(119, false)]
    [InlineData(120, true)]
    public void VerifiedGoalsCrossAtTwoHoursAndNewLandingClears(int stale, bool tripped)
    {
        var snapshot = Board(stale, 1);
        var result = Assess(snapshot);
        Assert.Equal(tripped, result.Tripped);
        Assert.Equal(tripped ? "verified-goals" : "", result.TopBlocker);
        Assert.Equal(tripped ? stale : 0, result.TopBlockerMinutes);
        Assert.False(Assess(snapshot with { LastLandingAt = Now }).Tripped);
    }

    [Fact]
    public void ObservedSinceIsTheBaselineWithoutALandingAndSkewIsClamped()
    {
        var snapshot = Board(0, 1) with { LastLandingAt = null, ObservedSince = Now.AddMinutes(-120) };
        Assert.Equal("no-landing|verified-goals", Assess(snapshot).Key);
        Assert.Equal(120, Assess(snapshot).MinutesSinceLanding);
        Assert.Equal(0, Assess(snapshot with { ObservedSince = Now.AddMinutes(5) }).MinutesSinceLanding);
    }

    [Fact]
    public void CustomThresholdsMoveBothCrossings()
    {
        var thresholds = new ProgramHealthThresholds(TimeSpan.FromMinutes(60), TimeSpan.FromMinutes(10));
        ProgramHealthAssessment Evaluate(int stale, int age) =>
            ProgramHealthEvaluator.Evaluate(Board(stale, 1, Receipt(age)), thresholds);
        Assert.False(Evaluate(59, 9).Tripped);
        Assert.Equal(["held-receipt"], Evaluate(59, 10).Conditions);
        Assert.Equal(["no-landing"], Evaluate(60, 9).Conditions);
        Assert.Equal(["held-receipt", "no-landing"], Evaluate(60, 10).Conditions);
    }

    [Fact]
    public void KeyOrderOldestTieBreakAndReceiptDeduplicationAreStable()
    {
        var a = Receipt(75, "a");
        var b = Receipt(75, "b");
        var result = Assess(Board(120, 2, b, a, a));
        Assert.Equal("held-receipt,no-landing|a", result.Key);
        Assert.Equal(2, result.CurrentReceipts);
        Assert.Equal(2, result.OverThreshold);
        Assert.Equal(75, result.TopBlockerMinutes);
        Assert.Equal("Nothing landed for 2 h 0 min; 2 passed trains held, oldest a for 75 min (4edbbf63,69a480fd).", result.Diagnosis);
    }

    [Fact]
    public void FormatsAreExactWithUtcSecondsAndDiagnosisLast()
    {
        var snapshot = Board(95, 4, Receipt(30));
        var result = Assess(snapshot);
        const string diagnosis = "Nothing landed for 1 h 35 min; 1 passed train held, oldest 12345678 for 30 min (4edbbf63,69a480fd).";
        Assert.Equal(diagnosis, result.Diagnosis);
        Assert.Equal("PROGRAM_HEALTH_ALERT conditions=held-receipt topBlocker=merge-train-receipt-1234567890 minutesSinceLanding=95 lastLanding=2026-10-15T04:25:00Z currentReceipts=1 overThreshold=1 verifiedGoals=4 diagnosis=" + diagnosis,
            ProgramHealthEvaluator.FormatAlert(snapshot, result));
        var cleared = Assess(snapshot with { LastLandingAt = Now, Receipts = [] });
        Assert.Equal("PROGRAM_HEALTH_CLEARED previous=held-receipt|merge-train-receipt-1234567890 minutesSinceLanding=0 currentReceipts=0 verifiedGoals=4",
            ProgramHealthEvaluator.FormatCleared(result.Key, cleared));
        Assert.Contains(" lastLanding=none ", ProgramHealthEvaluator.FormatAlert(snapshot with { LastLandingAt = null }, result));
        Assert.Equal(ProgramHealthEvaluator.FormatAlert(snapshot, result),
            ProgramHealthEvaluator.FormatAlert(snapshot with { LastLandingAt = snapshot.LastLandingAt!.Value.ToOffset(TimeSpan.FromHours(5)).AddTicks(5) }, result));
    }

    [Theory]
    [InlineData(1, 1, "1 Verified goal and 1 passed train")]
    [InlineData(2, 2, "2 Verified goals and 2 passed trains")]
    [InlineData(0, 0, "0 Verified goals and 0 passed trains")]
    public void WaitingDiagnosisPluralizesFromCounts(int goals, int trains, string words)
    {
        var receipts = Enumerable.Range(0, trains).Select(index => Receipt(1, index.ToString())).ToArray();
        Assert.Equal("Nothing landed for 2 h 0 min; " + words + " waiting.", Assess(Board(120, goals, receipts)).Diagnosis);
    }

    [Fact]
    public void HealthyBoardDoesNotTrip() => Assert.False(Assess(Board(20, 3, Receipt(25))).Tripped);

    [Theory]
    [InlineData(120, false)]
    [InlineData(600, false)]
    [InlineData(4320, false)]
    [InlineData(120, true)]
    [InlineData(600, true)]
    [InlineData(4320, true)]
    public void EmptyBoardNeverTrips(int stale, bool noLanding)
    {
        var snapshot = Board(stale) with { ObservedSince = Now.AddMinutes(-stale) };
        if (noLanding) snapshot = snapshot with { LastLandingAt = null };
        var result = Assess(snapshot);
        Assert.False(result.Tripped);
        Assert.Empty(result.Key);
    }
}
