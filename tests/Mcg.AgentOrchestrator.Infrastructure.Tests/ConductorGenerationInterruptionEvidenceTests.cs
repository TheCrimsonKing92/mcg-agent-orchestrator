using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: injected readers and clocks; database controls own unique temporary roots.
public sealed class ConductorGenerationInterruptionEvidenceTests
{
    private static readonly DateTimeOffset OwnerStart = DateTimeOffset.Parse("2026-10-19T08:18:07Z");
    private static readonly DateTimeOffset DispatchStart = DateTimeOffset.Parse("2026-10-19T08:34:20Z");
    private static readonly DateTimeOffset End = DateTimeOffset.Parse("2026-10-19T08:37:23Z");
    private readonly TestClock _clock = new(End.AddMinutes(10));

    [Xunit.Theory]
    [Xunit.InlineData(-500, false)]
    [Xunit.InlineData(-120000, false)]
    [Xunit.InlineData(30000, false)]
    [Xunit.InlineData(-500, true)]
    public void TryProve_IncidentAndClosedEdges_EmitsExactReceipt(int offsetMs, bool nullStart)
    {
        var goal = GoalId.New();
        var task = new TaskSpec(TaskId.New(), "Recover worker", AgentRole.Developer);
        var process = Process();
        var owner = new SpawnProcessIdentity(4242, OwnerStart, "dotnet.exe");
        var end = new ConductorGenerationTermination(4242, nullStart ? null : OwnerStart, End, "activation-reverted", "AdmissionStalled");
        var observed = End.AddMilliseconds(offsetMs);
        var reads = 0;
        var evidence = new ConductorGenerationInterruptionEvidence(_clock, () => [end],
            (id, pid, at) =>
            {
                reads++;
                Assert.Equal($"{goal.Value}:{task.Id.Value}", id);
                Assert.Equal(process.ProcessId, pid);
                Assert.Equal(DispatchStart, at);
                return owner;
            }, _ => Heartbeat(observed));

        Assert.True(evidence.TryProve(goal, task, process, out var receipt));
        Assert.Equal(1, reads);
        Assert.Equal($"CONDUCTOR_GENERATION_INTERRUPTED owner_pid=4242 owner_started_at={OwnerStart:O} " +
            $"terminated_at={End:O} cause=activation-reverted heartbeat_at={observed:O} dispatch_started_at={DispatchStart:O}", receipt);
    }

    [Xunit.Theory]
    [Xunit.InlineData("owner-missing")]
    [Xunit.InlineData("termination-missing")]
    [Xunit.InlineData("pid-mismatch")]
    [Xunit.InlineData("start-mismatch")]
    [Xunit.InlineData("dispatch-equal")]
    [Xunit.InlineData("dispatch-before")]
    [Xunit.InlineData("owner-equal")]
    [Xunit.InlineData("heartbeat-before")]
    [Xunit.InlineData("heartbeat-after")]
    [Xunit.InlineData("heartbeat-unavailable")]
    [Xunit.InlineData("heartbeat-null")]
    [Xunit.InlineData("reader-throws")]
    [Xunit.InlineData("owner-throws")]
    [Xunit.InlineData("heartbeat-throws")]
    public void TryProve_MissingOrInvalidEvidence_ReturnsFalseAndEmpty(string fault)
    {
        var owner = new SpawnProcessIdentity(4242, fault == "owner-equal" ? End : OwnerStart, "dotnet.exe");
        var termination = new ConductorGenerationTermination(fault == "pid-mismatch" ? 7 : 4242,
            fault == "start-mismatch" ? OwnerStart.AddMilliseconds(1001) : owner.StartedAt,
            fault == "dispatch-equal" ? DispatchStart : fault == "dispatch-before" ? DispatchStart.AddTicks(-1) : End,
            "activation-reverted", "AdmissionStalled");
        var observed = fault == "heartbeat-before" ? End.AddMinutes(-2).AddMilliseconds(-1) :
            fault == "heartbeat-after" ? End.AddSeconds(30).AddMilliseconds(1) : End.AddSeconds(-1);
        var evidence = new ConductorGenerationInterruptionEvidence(_clock,
            () => fault == "reader-throws" ? throw new IOException("unreadable events") : fault == "termination-missing" ? [] : [termination],
            (_, _, _) => fault == "owner-throws" ? throw new IOException("unreadable owner") : fault == "owner-missing" ? null : owner,
            _ => fault == "heartbeat-throws" ? throw new IOException("unreadable heartbeat") :
                Heartbeat(fault == "heartbeat-null" ? null : observed, fault != "heartbeat-unavailable"));

        Assert.False(evidence.TryProve(GoalId.New(), new(TaskId.New(), "Recover", AgentRole.Developer), Process(), out var receipt));
        Assert.Equal(string.Empty, receipt);
    }

    [Xunit.Theory]
    [Xunit.InlineData(-1000)]
    [Xunit.InlineData(1000)]
    public void TryProve_OneSecondOwnerTolerance_IsInclusive(int offsetMs)
    {
        var evidence = new ConductorGenerationInterruptionEvidence(_clock,
            () => [new(4242, OwnerStart.AddMilliseconds(offsetMs), End, "tick-stall", null)],
            (_, _, _) => new(4242, OwnerStart, "dotnet.exe"), _ => Heartbeat(End));
        Assert.True(evidence.TryProve(GoalId.New(), new(TaskId.New(), "Recover", AgentRole.Developer), Process(), out _));
    }

    [Xunit.Fact]
    public void TryProve_EarliestMatchOutsideWindow_DoesNotUseLaterMatch()
    {
        var evidence = new ConductorGenerationInterruptionEvidence(_clock,
            () => [new(4242, OwnerStart, End.AddMinutes(5), "tick-stall", null), new(4242, OwnerStart, End, "tick-stall", null)],
            (_, _, _) => new(4242, OwnerStart, "dotnet.exe"), _ => Heartbeat(End.AddMinutes(5)));
        Assert.False(evidence.TryProve(GoalId.New(), new(TaskId.New(), "Recover", AgentRole.Developer), Process(), out var receipt));
        Assert.Empty(receipt);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task ReadTerminations_PagesEntireHistory_AndRejectsMalformedPayload(bool malformed)
    {
        var root = Path.Combine(Path.GetTempPath(), "generation-evidence", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "state.db");
            Assert.Empty(ConductorGenerationInterruptionEvidence.ReadTerminations(path));
            Assert.False(File.Exists(Path.Combine(root, "run-events.db")));
            var store = new SqliteRunEventStore(Path.Combine(root, "run-events.db"));
            for (var i = 0; i < 501; i++)
                await store.AppendAsync(new(RunEventTypes.ConductorSupervision, null, "generation-terminated", "forced-stop", "fixture",
                    JsonSerializer.Serialize(new { processId = i, processStartedAt = OwnerStart, terminatedAt = End, cause = "tick-stall", reason = (string?)null }), _clock.UtcNow));
            var rows = ConductorGenerationInterruptionEvidence.ReadTerminations(path);
            Assert.Equal(501, rows.Count);
            Assert.Equal(500, rows[^1].ProcessId);
            if (malformed)
            {
                await store.AppendAsync(new(RunEventTypes.ConductorSupervision, null, "generation-terminated", "forced-stop", "broken", "{", _clock.UtcNow));
                Assert.Empty(ConductorGenerationInterruptionEvidence.ReadTerminations(path));
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static TaskProcessRecord Process() => new(999999, "codex exec", "fixture", "out", "err", "exit", DispatchStart, null, null);
    private static DispatchHeartbeatStatus Heartbeat(DateTimeOffset? at, bool available = true) =>
        new("heartbeat", available, available ? null : "missing", 999999, null, [], "running", at, at, null, null, 0, 0);
    private sealed class TestClock(DateTimeOffset now) : IClock { public DateTimeOffset UtcNow => now; }
}
