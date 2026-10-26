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

    private static string Event(string kind, string attempt, int minutes, string[]? requests = null) =>
        JsonSerializer.Serialize(new { timestamp = Now.AddMinutes(minutes), eventKind = kind, goalId = "c8cfc17efull",
            attempt, detail = (string?)null, member_requests = requests ??
                ["Infrastructure.Tests:ConductorContinuitySupervisorStopHonoredTests", "Infrastructure.Tests:Second", "Core.Tests:Third"] });
}
