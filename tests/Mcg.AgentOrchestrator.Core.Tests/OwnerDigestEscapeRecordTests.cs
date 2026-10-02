using Mcg.AgentOrchestrator.Core;

public sealed class OwnerDigestEscapeRecordTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 28, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = Start.AddDays(1);

    [Fact(DisplayName = "A recorded defect overrides a passing canary and changes the correct rate")]
    public void RecordReclassifiesOneOfTwoPassingLandings()
    {
        var record = new OwnerDigestEscapeRecord("a", "fix", Start.AddHours(20), "Feature never worked");
        var result = Build([record]);

        Assert.Equal("escape", result.Goals[0].LandingStatus);
        Assert.Equal("record", result.Goals[0].EscapeSource);
        Assert.Equal("correct", result.Goals[1].LandingStatus);
        Assert.Null(result.Goals[1].EscapeSource);
        Assert.Equal(1, result.Totals.Escapes);
        Assert.Equal(1, result.Totals.CorrectLandings);
        Assert.Equal(0.5, result.Totals.CorrectLandingRate);
        Assert.Equal(record, Assert.Single(result.Escapes!));
        Assert.Equal("not tracked", result.Reverts);
    }

    [Theory(DisplayName = "Records at or after the window end cannot change historical results")]
    [InlineData(0)]
    [InlineData(1)]
    public void CutoffExcludesLaterRecords(int hoursAfterEnd)
    {
        var result = Build([new("a", null, End.AddHours(hoursAfterEnd), "Later defect")]);

        Assert.Equal("correct", result.Goals[0].LandingStatus);
        Assert.Null(result.Goals[0].EscapeSource);
        Assert.Null(result.Escapes);
        Assert.Equal(0, result.Totals.Escapes);
        Assert.Equal(1, result.Totals.CorrectLandingRate);
    }

    [Fact(DisplayName = "A failed canary still causes an escape without an operator record")]
    public void FailedCanaryKeepsItsSource()
    {
        var result = OwnerDigestReport.Build([new("a", Start.AddHours(8), "sha-a", [])],
            [new("sha-a", Start.AddHours(9), false)], new FixedClock(End), Start, End);

        var row = Assert.Single(result.Goals);
        Assert.Equal("escape", row.LandingStatus);
        Assert.Equal("canary", row.EscapeSource);
        Assert.Equal(1, result.Totals.Escapes);
        Assert.Null(result.Escapes);
    }

    [Fact(DisplayName = "Records override pending canaries and multiple defects count as one escape")]
    public void PendingAndDuplicateRecordsCountOnce()
    {
        var result = OwnerDigestReport.Build([new("a", Start.AddHours(8), "sha-a", [])], [],
            new FixedClock(End), Start, End, escapeRecords:
            [new("A", null, Start.AddHours(7), "First"), new("a", "fix", Start.AddHours(20), "Second")]);

        var row = Assert.Single(result.Goals);
        Assert.Equal("escape", row.LandingStatus);
        Assert.Equal("record", row.EscapeSource);
        Assert.Equal(1, result.Totals.Escapes);
        Assert.Equal(0, result.Totals.Pending);
        Assert.Equal(2, result.Escapes!.Count);
    }

    [Fact(DisplayName = "A failed canary and a recorded defect retain both escape sources")]
    public void CanaryAndRecordAreBothNamed()
    {
        var result = OwnerDigestReport.Build([new("a", Start.AddHours(8), "sha-a", [])],
            [new("sha-a", Start.AddHours(9), false)], new FixedClock(End), Start, End,
            escapeRecords: [new("a", null, Start.AddHours(20), "Defect")]);

        Assert.Equal("canary,record", Assert.Single(result.Goals).EscapeSource);
        Assert.Equal(1, result.Totals.Escapes);
    }

    [Fact(DisplayName = "Only records belonging to landings inside the digest window are visible")]
    public void OtherLandingsDoNotPopulateView()
    {
        var result = Build([new("outside", null, Start.AddHours(20), "Outside landing")]);
        Assert.Null(result.Escapes);
        Assert.All(result.Goals, row => Assert.Equal("correct", row.LandingStatus));
    }

    private static OwnerDigestResult Build(IReadOnlyList<OwnerDigestEscapeRecord> records) =>
        OwnerDigestReport.Build(
            [new("a", Start.AddHours(8), "sha-a", []), new("b", Start.AddHours(12), "sha-b", []),
             new("outside", Start.AddHours(-1), "prior", [])],
            [new("sha-a", Start.AddHours(9), true), new("sha-b", Start.AddHours(13), true)],
            new FixedClock(End), Start, End, escapeRecords: records);

    private sealed record FixedClock(DateTimeOffset UtcNow) : IClock;
}
