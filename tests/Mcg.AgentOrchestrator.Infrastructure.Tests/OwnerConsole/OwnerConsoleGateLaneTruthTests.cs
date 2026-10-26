// Parallel-safe: immutable fixtures and an injected clock; no process or shared files.
using System.Text.Json;
using Mcg.AgentOrchestrator.App.OwnerConsole;

public sealed class OwnerConsoleGateLaneTruthTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 14, 0, 0, TimeSpan.Zero);
    private const string X = "11111111-gate-20261016123751767-1a60276764bb4900b24d041376df0054";
    private const string Y = "33333333-gate-20261016130459792-7ddfa3e3956f43859c142a709da0ed41";
    private const string Focused = "reviewer focused evidence: Infrastructure.Tests FullyQualifiedName~Sample.Tests.FailsOnPurpose";
    private static readonly OwnerConsoleGateManifestLane[] Manifest = [new("A", 100), new("B", 200), new("C", 300)];

    [Fact]
    public void SettledChecks_ShowVerdictWhileUnfinishedCheckRemainsRunning()
    {
        var gate = Build(Fixture());
        var cli = Row(gate, "cli tests");
        Assert.Equal(OwnerConsoleGateLaneState.Done, cli.State);
        Assert.Equal("passed", cli.Outcome);
        Assert.StartsWith("+ cli tests 95s", OwnerConsoleGateFormatter.LaneLine(cli));
        var baseline = Row(gate, "core tests [trusted main baseline]");
        Assert.Equal(OwnerConsoleGateLaneState.Done, baseline.State);
        Assert.Equal("failed", baseline.Outcome);
        Assert.StartsWith("x ", OwnerConsoleGateFormatter.LaneLine(baseline));
        var execution = Row(gate, "execution tests");
        Assert.Equal(OwnerConsoleGateLaneState.Running, execution.State);
        Assert.StartsWith("> ", OwnerConsoleGateFormatter.LaneLine(execution));
    }

    [Fact]
    public void RemoteClaim_ShowsExecutorAndElapsedBeforeCompletion()
    {
        var gate = Build(Fixture().Where(item => item.Timestamp <= Now.AddSeconds(-600)));
        var remote = Row(gate, "C");
        Assert.Equal(OwnerConsoleGateLaneState.Running, remote.State);
        Assert.Equal("mcg-exec-2", remote.Location);
        Assert.Equal(TimeSpan.FromSeconds(600), remote.Elapsed);
        Assert.StartsWith("> ", OwnerConsoleGateFormatter.LaneLine(remote));
    }

    [Theory]
    [InlineData("failed", "x ")]
    [InlineData("passed", "+ ")]
    [InlineData(null, "* ")]
    public void RemoteCompletion_UsesOutcomeOrKeepsUnknown(string? outcome, string marker)
    {
        var events = Fixture().Select(item => item.Detail.Contains("phase=remote-shard-complete", StringComparison.Ordinal)
            ? item with { Detail = item.Detail.Replace(" outcome=failed failed=1", outcome is null ? "" : $" outcome={outcome} failed=1", StringComparison.Ordinal) }
            : item);
        var remote = Row(Build(events), "C");
        Assert.Equal(OwnerConsoleGateLaneState.Done, remote.State);
        Assert.Equal(outcome ?? "unknown", remote.Outcome);
        Assert.StartsWith(marker, OwnerConsoleGateFormatter.LaneLine(remote));
        if (outcome is null) Assert.EndsWith("done", OwnerConsoleGateFormatter.LaneLine(remote));
    }

    [Fact]
    public void ConcurrentTrains_BindRemoteReceiptsToTheirOwnAttempt()
    {
        var events = Fixture().Concat(new[]
        {
            Event(-800, "acceptance-cohort", "ACCEPTANCE_COHORT_ENTRY members=33333333,44444444"),
            Progress(-790, "verification-check", "cli tests", 0, Heartbeat(Y, "cli-tests"), attempt: Y),
            Progress(-200, "remote-shard-complete", "infrastructure tests: C", 290000,
                "build-slots/activity-1.heartbeat.json", " outcome=passed failed=0", Y)
        });
        var first = Build(events);
        var second = Assert.Single(first.OtherLiveGates!);
        Assert.Equal("mcg-exec-1", Row(first, "C").Location);
        Assert.Equal("mcg-exec-2", Row(second, "C").Location);
        Assert.Equal(["11111111", "22222222"], first.Members.Select(member => member.Id));
        Assert.Equal(["33333333", "44444444"], second.Members.Select(member => member.Id));
        Assert.DoesNotContain(second.Lanes, row => row.IsAttribution);

        var attribution = Row(first, Focused);
        Assert.True(attribution.IsAttribution);
        Assert.False(attribution.IsManifestLane);
        Assert.StartsWith("> confirming failure: Infrastructure.Tests FullyQualifiedName~Sample.Tests.FailsOnPurpose",
            OwnerConsoleGateFormatter.LaneLine(attribution));
        var cli = Row(first, "cli tests");
        Assert.False(cli.IsAttribution);
        Assert.Equal("cli tests", cli.Target);
        Assert.Equal(Heartbeat(X, "cli-tests"), cli.HeartbeatPath);
    }

    [Fact]
    public void LegacyReceipt_KeepsGoalAndStartTimeBinding()
    {
        var events = Fixture().Where(item => item.EventKind != "remote-lane").Concat(new[]
        {
            Event(-950, "remote-lane", "REMOTE_LANE executor=before-start lane=\"infrastructure tests: C\" attempt=1"),
            Event(-100, "remote-lane", "REMOTE_LANE executor=legacy-executor lane=\"infrastructure tests: C\" attempt=1"),
            Event(-90, "remote-lane", "REMOTE_LANE executor=other-goal lane=\"infrastructure tests: C\" attempt=1", "33333333")
        });
        Assert.Equal("legacy-executor", Row(Build(events), "C").Location);
    }

    [Fact]
    public void UnknownAttempt_IsRejectedUnlessGateHasNoAttemptEvidence()
    {
        var events = Fixture().Where(item => item.EventKind != "remote-lane").Append(
            Event(-100, "remote-lane", $"REMOTE_LANE executor=unbound-executor lane=\"infrastructure tests: C\" attempt={Y}"));
        Assert.Equal("mcg-exec-2", Row(Build(events), "C").Location);
        var withoutIds = events.Select(item => item with { Detail = item.Detail.Replace(X, "legacy", StringComparison.Ordinal) });
        Assert.Equal("unbound-executor", Row(Build(withoutIds), "C").Location);
    }

    [Fact]
    public void HeartbeatMetadata_UsesNewestAttemptPathAcrossPathSeparators()
    {
        var heartbeat = Heartbeat(X.ToUpperInvariant(), "cli-new").Replace('/', '\\');
        var events = Fixture().Append(Progress(-50, "verification-check", "cli tests", 0, heartbeat))
            .Append(Progress(-40, "check-complete", "cli tests", 95000, "build-slots/activity-0.heartbeat.json", " outcome=passed failed=0"));
        Assert.Equal(heartbeat, Row(Build(events), "cli tests").HeartbeatPath);
        Assert.Equal("mcg-exec-1", Row(Build(events), "C").Location);
    }

    [Theory]
    [InlineData("reviewer focused evidence: ")]
    [InlineData("reviewer mapped project evidence: ")]
    public void FailedAttribution_DoesNotMarkOtherwisePassedGateFailing(string prefix)
    {
        var passed = Fixture().Where(item => item.Detail.Contains("ACCEPTANCE_COHORT_ENTRY", StringComparison.Ordinal) ||
            item.Detail.Contains("phase=check-complete", StringComparison.Ordinal)).Select(item =>
                item with { Detail = item.Detail.Replace("outcome=failed", "outcome=passed", StringComparison.Ordinal) }).ToArray();
        var gate = Build(passed);
        Assert.False(gate.Failing);
        Assert.All(gate.Lanes.Where(row => row.State == OwnerConsoleGateLaneState.Done), row => Assert.Equal("passed", row.Outcome));
        Assert.Equal("GATE train | elapsed 15m", OwnerConsoleGateFormatter.Lines(gate)[0]);

        var attribution = prefix + "Sample.Tests.FailsOnPurpose";
        gate = Build(passed.Append(Progress(-30, "check-complete", attribution, 1000,
            Heartbeat(X, "attribution"), " outcome=failed failed=1")));
        Assert.True(Row(gate, attribution).IsAttribution);
        Assert.Equal("failed", Row(gate, attribution).Outcome);
        Assert.False(gate.Failing);
        Assert.Equal("GATE train | elapsed 15m", OwnerConsoleGateFormatter.Lines(gate)[0]);
        Assert.StartsWith("x confirming failure: Sample.Tests.FailsOnPurpose", OwnerConsoleGateFormatter.LaneLine(Row(gate, attribution)));
    }

    [Fact]
    public void FailedSettledCheck_MarksGateHeaderFailing()
    {
        var events = Fixture().Where(item => item.Detail.Contains("ACCEPTANCE_COHORT_ENTRY", StringComparison.Ordinal) ||
            item.Detail.Contains("phase=check-complete", StringComparison.Ordinal));
        var gate = Build(events);
        Assert.Single(gate.Lanes.Where(row => row.State == OwnerConsoleGateLaneState.Done && row.Outcome == "failed"));
        Assert.True(gate.Failing);
        Assert.Equal("GATE train | elapsed 15m | FAILING", OwnerConsoleGateFormatter.Lines(gate)[0]);
    }

    private static OwnerConsoleGateProgress Build(IEnumerable<OwnerConductEvent> events) =>
        OwnerConsoleGateProgress.Build(Now, events, Manifest, []);
    private static OwnerConsoleGateLane Row(OwnerConsoleGateProgress gate, string name) => Assert.Single(gate.Lanes, row => row.Name == name);
    private static string Heartbeat(string attempt, string slug) =>
        $"C:/repo/.orchestrator/acceptance-gate-attempts/{attempt[..8]}/{attempt}.{slug}-a9356bc231b0f67f.gate-heartbeat.json";
    private static OwnerConductEvent Event(int age, string kind, string detail, string? goal = null) =>
        OwnerConsoleGateProgress.ParseEvent(JsonSerializer.Serialize(new { timestamp = Now.AddSeconds(age), eventKind = kind, goalId = goal, detail }))!;
    private static OwnerConductEvent Progress(int age, string phase, string target, int elapsed, string heartbeat,
        string extra = "", string attempt = X)
    {
        var members = attempt == Y ? "33333333,44444444" : "11111111,22222222";
        return Event(age, "gate-progress", $"PHASE_PROGRESS goal={attempt[..8]} phase={phase} elapsed_ms={elapsed} target=\"{target}\" child_pid=unknown output_bytes=0 heartbeat={heartbeat}{extra} train=merge-train-v2-653fcc0a member={attempt[..8]} members={members} scope=merge-train");
    }
    private static OwnerConductEvent[] Fixture() =>
    [
        Event(-900, "acceptance-cohort", "ACCEPTANCE_COHORT_ENTRY members=11111111,22222222"),
        Progress(-880, "verification-check", "cli tests", 0, Heartbeat(X, "cli-tests")),
        Progress(-700, "check-complete", "cli tests", 95000, Heartbeat(X, "cli-tests"), " outcome=passed failed=0"),
        Progress(-650, "check-complete", "core tests [trusted main baseline]", 30000, Heartbeat(X, "core-tests-trusted-main-baseline"), " outcome=failed failed=0"),
        Progress(-640, "verification-check", "execution tests", 0, Heartbeat(X, "execution-tests")),
        Progress(-600, "remote-shard-started", "infrastructure tests: C", 0, "build-slots/activity-0.heartbeat.json", " executor=mcg-exec-2"),
        Progress(-300, "remote-shard-complete", "infrastructure tests: C", 290000, "build-slots/activity-0.heartbeat.json", " outcome=failed failed=1"),
        Progress(-60, "verification-check", Focused, 0, Heartbeat(X, "reviewer-focused-evidence")),
        Event(-305, "remote-lane", $"REMOTE_LANE executor=mcg-exec-1 lane=\"infrastructure tests: C\" attempt={X} outcome=accepted reason=none"),
        Event(-205, "remote-lane", $"REMOTE_LANE executor=mcg-exec-2 lane=\"infrastructure tests: C\" attempt={Y} outcome=accepted reason=none")
    ];
}
