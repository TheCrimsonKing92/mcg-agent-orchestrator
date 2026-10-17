using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: immutable samples and a unique conduct log, no global probe overrides.
public sealed class ShardCompleteLoadFieldsTests
{
    [Fact]
    public void ShardOutcomeTokens_AvailableLoad_PrecedesUnchangedOutcomeSuffix()
    {
        var progress = Progress("shard-complete", new(GateLoadSample.Available(2), GateLoadSample.Available(3),
            GateLoadSample.Available(4), GateLoadSample.Available(25), GateLoadSample.Available(8), GateLoadSample.Available(1000)));
        var expected = " " + GateLoadContextProbe.FormatProgressTokens(progress.LoadContext) + " outcome=passed failed=0";
        Assert.Equal(expected, progress.ShardOutcomeTokens);
        Assert.Contains("load_concurrent_gates=2 load_inflight_paid_workers=3 load_concurrent_shards=4", expected);
        Assert.Contains("load_cpu_percent=25 load_processor_count=8 load_cpu_window_ms=1000", expected);
        Assert.EndsWith(" outcome=passed failed=0", progress.ShardOutcomeTokens);
        var detail = WriteDetail(progress);
        Assert.Contains(expected, detail);
        Assert.EndsWith(" outcome=passed failed=0", detail);
    }

    [Fact]
    public void ShardOutcomeTokens_UnavailableLoad_PrintsUnavailable()
    {
        var sample = GateLoadSample.Unavailable("fixture");
        var progress = Progress("shard-complete", new(sample, sample, sample, sample, sample, sample));
        Assert.Equal(6, progress.ShardOutcomeTokens.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Count(token => token.StartsWith("load_", StringComparison.Ordinal) && token.EndsWith("=unavailable", StringComparison.Ordinal)));
        Assert.Equal(" outcome=passed failed=0", (progress with { LoadContext = null }).ShardOutcomeTokens);
    }

    [Theory]
    [InlineData("shard-start")]
    [InlineData("verification-check")]
    public void ShardOutcomeTokens_OtherPhases_DoNotAddLoadFields(string phase)
    {
        var sample = GateLoadSample.Available(3);
        var progress = Progress(phase, new(sample, sample, sample, sample, sample, sample));
        Assert.Equal(string.Empty, progress.ShardOutcomeTokens);
        Assert.DoesNotContain("load_", WriteDetail(progress));
    }

    [Fact]
    public void SlowdownProgress_LongNames_BoundsDetailAndContainsSinkFailures()
    {
        var previous = Enumerable.Range(0, 3).Select(_ => new GateLaneBaselineStore.Run(DateTimeOffset.UnixEpoch, 60000,
            new Dictionary<string, double> { [new string('A', 200) + ".Commit"] = 4000 })).ToArray();
        var result = GateLaneSlowdownDiff.Compute(120000,
            new(new Dictionary<string, double> { [new string('A', 200) + ".Commit"] = 12000 }), previous);
        AcceptanceGateProgress? emitted = null;
        GateLaneSlowdownDiff.TryEmitProgress(result, new string('1', 32), new string('L', 200), TimeSpan.FromSeconds(120),
            progress => emitted = progress);
        Assert.NotNull(emitted);
        Assert.Equal("lane-slowdown", emitted.Phase);
        Assert.True(emitted.CurrentTarget.Length <= 80);
        var detail = WriteDetail(emitted);
        Assert.Contains("ratio=2.00", detail);
        Assert.Contains("median_wall_ms=60000", detail);
        Assert.Contains("top=+1", detail);
        Assert.True(detail.Length <= 400, detail);
        var calls = 0;
        GateLaneSlowdownDiff.TryEmitProgress(result, null, "Alpha", TimeSpan.FromSeconds(120), _ =>
        {
            calls++;
            throw new IOException("injected progress sink failure");
        });
        Assert.Equal(1, calls);
    }

    internal static string WriteDetail(AcceptanceGateProgress progress)
    {
        var directory = Path.Combine(Path.GetTempPath(), "load-fields-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var member = new AcceptanceCohortMemberBinding(new GoalId(new string('1', 32)), new string('a', 40),
                new string('b', 40), ["src/A.cs"], ["resource:a"], ChangeRiskTier.Behavior,
                ConductorTransitionDecision.Auto, "Clean", "NoConflictsDetected");
            var peer = new AcceptanceCohortMemberBinding(new GoalId(new string('2', 32)), new string('e', 40),
                new string('f', 40), ["src/B.cs"], ["resource:b"], ChangeRiskTier.Behavior,
                ConductorTransitionDecision.Auto, "Clean", "NoConflictsDetected");
            var members = new[] { member, peer };
            var identity = AcceptanceCohortIdentity.Create(members, new string('c', 40), new string('d', 40), "manifest-v1");
            var path = Path.Combine(directory, "conduct-events.log");
            ConductorDriver.AppendCohortGateProgressEvents(new ConductEventLogWriter(path), identity, members, progress);
            var lines = File.ReadAllLines(path);
            Assert.Equal(members.Length, lines.Length);
            return Mcg.AgentOrchestrator.App.OwnerConsole.OwnerConsoleGateProgress.ParseEvent(lines[0])!.Detail;
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static AcceptanceGateProgress Progress(string phase, GateLoadContext load) =>
        new(new string('1', 32), phase, "infrastructure tests: Alpha", null, null, null, DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(60), 0, "none", load, Outcome: "passed");
}
