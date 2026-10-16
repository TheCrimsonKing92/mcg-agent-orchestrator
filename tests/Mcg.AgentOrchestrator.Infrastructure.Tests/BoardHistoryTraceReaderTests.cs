using Mcg.AgentOrchestrator.App.Simulation;
using Microsoft.Data.Sqlite;

public sealed class BoardHistoryTraceReaderTests
{
    [Fact]
    public void Reads_real_receipts_epochs_and_landings_without_changing_inputs()
    {
        using var fixture = new BoardSimulationFixture();
        fixture.WriteF1();
        fixture.WriteAttempt(BoardSimulationFixture.Gate(GateRunKind.Solo, "A", "00:00", "00:01", GateRunOutcome.Failed)
            with { BeginAt = BoardSimulationFixture.Start.AddHours(-2), EndAt = BoardSimulationFixture.Start.AddHours(-1) }, "old");
        var bad = Path.Combine(fixture.DirectoryPath, "acceptance-gate-attempts", "broken.attempt.json");
        File.WriteAllText(bad, "{broken");
        var before = fixture.Snapshot();

        var trace = BoardHistoryTraceReader.Read(fixture.DirectoryPath, BoardSimulationFixture.Start, BoardSimulationFixture.Start.AddDays(1));

        Assert.Equal(before, fixture.Snapshot());
        Assert.Equal([GateRunKind.Solo, GateRunKind.Train, GateRunKind.Cohort, GateRunKind.Solo], trace.GateRuns.Select(g => g.Kind));
        Assert.Equal([GateRunOutcome.Failed, GateRunOutcome.Passed, GateRunOutcome.Failed, GateRunOutcome.Passed], trace.GateRuns.Select(g => g.Outcome));
        Assert.Equal(BoardSimulationFixture.At("01:10"), trace.GateRuns.Single(g => g.Kind == GateRunKind.Train).BeginAt);
        foreach (var expected in BoardSimulationFixture.F1().Epochs)
        {
            var actual = Assert.Single(trace.Epochs, e => e.GoalId == expected.GoalId);
            Assert.Equal(expected.ReadyAt, actual.ReadyAt);
            Assert.Equal(expected.LeaveAt, actual.LeaveAt);
            Assert.Equal(expected.CandidateRevision, actual.CandidateRevision);
        }
        Assert.Equal(BoardSimulationFixture.F1().Landings, trace.Landings);
        Assert.Contains(trace.Skipped, s => s.Path == bad && s.Reason.Length > 0);
        Assert.Single(trace.Skipped);
    }

    [Fact]
    public void Missing_stores_and_directories_create_nothing()
    {
        using var fixture = new BoardSimulationFixture();
        foreach (var path in new[] { fixture.DirectoryPath, Path.Combine(fixture.DirectoryPath, "missing") })
        {
            var trace = BoardHistoryTraceReader.Read(path, BoardSimulationFixture.Start, BoardSimulationFixture.Start.AddDays(1));
            Assert.Empty(trace.GateRuns);
            Assert.Empty(trace.Epochs);
            Assert.Empty(trace.Landings);
            Assert.Empty(trace.Skipped);
        }
        Assert.Empty(Directory.GetFileSystemEntries(fixture.DirectoryPath));
    }

    [Fact]
    public void Null_train_payload_is_reported_without_losing_other_inputs_or_writing()
    {
        using var fixture = new BoardSimulationFixture();
        fixture.WriteF1();
        var path = Path.Combine(fixture.DirectoryPath, "merge-train-acceptance.db");
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
               { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE merge_train_receipts SET payload_json='null'";
            Assert.Equal(1, command.ExecuteNonQuery());
        }
        var before = fixture.Snapshot();
        var trace = BoardHistoryTraceReader.Read(fixture.DirectoryPath, BoardSimulationFixture.Start, BoardSimulationFixture.Start.AddDays(1));
        Assert.Equal(3, trace.GateRuns.Count);
        Assert.StartsWith(path + "#", Assert.Single(trace.Skipped).Path);
        Assert.Contains("Malformed train receipt payload", trace.Skipped[0].Reason);
        Assert.Equal(before, fixture.Snapshot());
    }

    [Fact]
    public void Heartbeat_fallback_window_edges_event_time_precedence_and_bad_lines_are_observable()
    {
        using var fixture = new BoardSimulationFixture();
        var start = BoardSimulationFixture.Start;
        var gate = BoardSimulationFixture.Gate(GateRunKind.Solo, "A", "00:00", "00:20", GateRunOutcome.Failed)
            with { BeginAt = start.AddMinutes(-10), EndAt = start };
        fixture.WriteAttempt(gate, "boundary", heartbeatOnly: true);
        var path = fixture.WriteEvent(BoardSimulationFixture.Id('A'), "TaskVerified", start.AddMinutes(-11));
        var text = $$"""
            {"cursor":1,"timestamp":"2026-10-14T23:40:00+00:00","goalId":"{{BoardSimulationFixture.Id('A')}}","eventType":"TaskVerified","progressKind":"TaskVerificationRecorded","message":"Dispatch execution passed (0).","occurredAt":"2026-10-14T23:49:00+00:00","taskId":"t1"}
            """ + Environment.NewLine;
        File.WriteAllText(path, text + "{broken" + Environment.NewLine);
        File.SetLastWriteTimeUtc(path, start.UtcDateTime);
        var old = fixture.WriteEvent(BoardSimulationFixture.Id('B'), "GoalLanded", start.AddMinutes(1));
        File.SetLastWriteTimeUtc(old, start.AddSeconds(-1).UtcDateTime);
        var trace = BoardHistoryTraceReader.Read(fixture.DirectoryPath, start, start.AddDays(1));
        Assert.Single(trace.GateRuns);
        Assert.Equal(start, trace.GateRuns[0].EndAt);
        Assert.Equal(start.AddMinutes(-11), Assert.Single(trace.Epochs).ReadyAt);
        Assert.Empty(trace.Landings);
        Assert.Contains(trace.Skipped, s => s.Path == path + ":2");
    }
}
