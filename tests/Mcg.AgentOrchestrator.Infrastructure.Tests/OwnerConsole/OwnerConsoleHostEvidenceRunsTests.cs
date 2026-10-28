// Parallel-safe: fixed JSON events and a fixed now; no files, clock or process.
using System.Text.Json;
using Mcg.AgentOrchestrator.App.OwnerConsole;

public sealed class OwnerConsoleHostEvidenceRunsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RecentStart_WithoutEnd_ReportsClassRemainingRequestsAndAge()
    {
        var run = Assert.Single(OwnerConsoleHostEvidenceRuns.Read([Event("EVIDENCE_START", "a", -5)], Now));
        Assert.Equal("c8cfc17efull", run.GoalId);
        Assert.Equal("ConductorContinuitySupervisorStopHonoredTests, +2", run.Label);
        Assert.Equal(Now.AddMinutes(-5), run.StartedAt);
        Assert.Equal(TimeSpan.FromMinutes(5), run.Age);
    }

    [Fact]
    public void EndedOldAndMalformedEvents_AreExcluded()
    {
        Assert.Empty(OwnerConsoleHostEvidenceRuns.Read([
            Event("EVIDENCE_START", "ended", -5), Event("EVIDENCE_END", "ended", -1),
            Event("EVIDENCE_START", "old", -61), "malformed", "{}",
            "{\"eventKind\":\"EVIDENCE_START\",\"attempt\":\"bad\",\"timestamp\":\"invalid\"}",
            Event("EVIDENCE_START", "", -5)
        ], Now));
    }

    [Fact]
    public void WindowBoundaryLabelsAndInterleavedAttempts_KeepIndependentRuns()
    {
        var runs = OwnerConsoleHostEvidenceRuns.Read([
            Event("EVIDENCE_END", "ended-first", -1), Event("EVIDENCE_START", "ended-first", -3),
            Event("EVIDENCE_START", "boundary", -60, ["PlainClass"]),
            Event("EVIDENCE_START", "future", 1, [])
        ], Now);
        Assert.Equal(new[] { "PlainClass", "" }, runs.Select(run => run.Label));
        Assert.Equal(new[] { TimeSpan.FromMinutes(60), TimeSpan.FromMinutes(-1) }, runs.Select(run => run.Age));
    }

    [Fact]
    public void Parse_StartEvent_PreservesAttemptIdentityAndOrdinal()
    {
        var entry = Assert.IsType<OwnerConsoleHostEvidenceRuns.Entry>(OwnerConsoleHostEvidenceRuns.Parse(
            JsonSerializer.Serialize(new { eventKind = "EVIDENCE_START", attempt = "running",
                goalId = "goal", ordinal = 3, timestamp = Now.AddMinutes(-2) })));

        var run = Assert.Single(OwnerConsoleHostEvidenceRuns.Fold([entry], Now));

        Assert.Equal("running", run.Attempt);
        Assert.Equal(3, run.Ordinal);
        Assert.Equal(Now.AddMinutes(-2), run.StartedAt);
        Assert.Null(entry.End);
    }

    [Fact]
    public void LatestEnded_SameGoal_UsesLastEventRatherThanTimestamp()
    {
        var first = End("first", "goal", Now.AddMinutes(-1), 120, "passed", 12);
        var last = End("last", "goal", Now.AddMinutes(-5), 390, "failed", 163);

        var previous = OwnerConsoleHostEvidenceRuns.LatestEnded([first, last], "goal", "running");

        Assert.Equal(new OwnerConsoleEvidenceAttemptEnd("goal", "last", 390, "failed", 163), previous);
    }

    [Fact]
    public void LatestEnded_OtherGoalAndRunningAttempt_AreExcluded()
    {
        var expected = End("previous", "goal", Now.AddMinutes(-3), 390, "failed", 163);
        var other = End("newer-other", "another-goal", Now.AddMinutes(-2), 60, "passed", 1);
        var running = End("running", "goal", Now.AddMinutes(-1), 30, "passed", 2);

        Assert.Equal(expected,
            OwnerConsoleHostEvidenceRuns.LatestEnded([expected, other, running], "goal", "running"));
        Assert.Null(OwnerConsoleHostEvidenceRuns.LatestEnded([other, running], "goal", "running"));
        Assert.Null(OwnerConsoleHostEvidenceRuns.LatestEnded([], "goal", "running"));
    }

    [Theory]
    [InlineData("unknown", "unknown")]
    [InlineData(-1, -1)]
    [InlineData(null, null)]
    public void LatestEnded_UnavailableSummary_DoesNotBorrowFromOlderEvent(
        object? duration, object? count)
    {
        var older = End("older", "goal", Now.AddMinutes(-3), 390, "failed", 163);
        var latest = End("latest", "goal", Now.AddMinutes(-1), duration, "inconclusive", count);

        var previous = Assert.IsType<OwnerConsoleEvidenceAttemptEnd>(
            OwnerConsoleHostEvidenceRuns.LatestEnded([older, latest], "goal", "running"));

        Assert.Equal("latest", previous.Attempt);
        Assert.Equal("inconclusive", previous.Outcome);
        Assert.Null(previous.DurationSeconds);
        Assert.Null(previous.TestsExecuted);
    }

    [Fact]
    public void Fold_EndWithoutGoal_StillClosesMatchingRun()
    {
        var start = OwnerConsoleHostEvidenceRuns.Parse(Event("EVIDENCE_START", "a", -5));
        var end = OwnerConsoleHostEvidenceRuns.Parse("{\"eventKind\":\"EVIDENCE_END\",\"attempt\":\"a\"}");

        Assert.NotNull(end);
        Assert.Empty(OwnerConsoleHostEvidenceRuns.Fold([start, end], Now));
        Assert.Null(OwnerConsoleHostEvidenceRuns.LatestEnded([end.End!], "c8cfc17efull", "running"));
    }

    private static OwnerConsoleEvidenceAttemptEnd End(string attempt, string goalId, DateTimeOffset timestamp,
        object? duration, string outcome, object? count) =>
        Assert.IsType<OwnerConsoleHostEvidenceRuns.Entry>(OwnerConsoleHostEvidenceRuns.Parse(JsonSerializer.Serialize(new
        {
            eventKind = "EVIDENCE_END", attempt, goalId, timestamp, duration_s = duration,
            outcome, tests_executed = count
        }))).End!;

    private static string Event(string kind, string attempt, int minutes, string[]? requests = null) =>
        JsonSerializer.Serialize(new { timestamp = Now.AddMinutes(minutes), eventKind = kind, goalId = "c8cfc17efull",
            attempt, detail = (string?)null, member_requests = requests ??
                ["Infrastructure.Tests:ConductorContinuitySupervisorStopHonoredTests", "Infrastructure.Tests:Second", "Core.Tests:Third"] });
}
