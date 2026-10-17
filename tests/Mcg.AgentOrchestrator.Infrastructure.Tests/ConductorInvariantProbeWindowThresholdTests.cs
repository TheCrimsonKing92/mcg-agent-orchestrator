using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: pure watchdog inputs and injected times only.
public sealed class ConductorInvariantProbeWindowThresholdTests
{
    [Fact]
    public void Thresholds_ProbeCeiling_ComposesCompletionAndSilenceWindows()
    {
        Assert.Equal(TimeSpan.FromMinutes(75), ConductorInvariantThresholds.ProbeWindowCeiling);
        Assert.Equal(AcceptanceAdmissionPathSet.CompletionTimeout +
            ConductorInvariantThresholds.RelaunchWithoutActivation, ConductorInvariantThresholds.ProbeWindowCeiling);
    }

    [Fact]
    public void Evaluate_CeilingBoundary_RaisesOnlyOneSecondOverDespiteProgress()
    {
        var start = DateTimeOffset.UnixEpoch;
        var handoff = start.AddMinutes(1);
        var boundary = handoff.AddMinutes(75);
        ConductorInvariantObservation observation = new(boundary, [], null, [],
            Relaunch: new(start, "goal", boundary, null, null, HandoffAt: handoff));
        var at = ConductorInvariantWatchdog.Evaluate(observation, new Dictionary<string, DateTimeOffset>());
        Assert.Empty(at.Raise);
        Assert.Empty(at.Clear);
        Assert.Empty(at.Active);

        var over = ConductorInvariantWatchdog.Evaluate(observation with { Now = boundary.AddSeconds(1) }, at.Active);
        var breach = Assert.Single(over.Raise);
        Assert.Equal("relaunch-without-activation", breach.Kind);
        Assert.Equal("goal", breach.Record);
        Assert.Equal(handoff + AcceptanceAdmissionPathSet.CompletionTimeout, breach.Since);
        Assert.Contains($"handoffAt:{handoff:O}", breach.Evidence);
        Assert.Empty(over.Clear);
        Assert.Equal(new KeyValuePair<string, DateTimeOffset>(breach.Key, breach.Since), Assert.Single(over.Active));
    }

    [Theory]
    [InlineData("finished-unreconciled", 5, "attempt")]
    [InlineData("relaunch-without-activation", 15, "goal")]
    [InlineData("generation-behind-main", 30, "bbbbbbbbbbbb")]
    public void Evaluate_NullHandoff_PreservesSliceTwoRaiseClearAndActive(
        string kind, int minutes, string record)
    {
        // Preserve the exact threshold inputs and expectations of slice two.
        var start = DateTimeOffset.Parse("2026-10-16T21:17:00Z");
        ConductorInvariantObservation observation = new(start.AddMinutes(minutes), [], null, [],
            kind == "finished-unreconciled" ? [new("attempt", "goal", "gate", start.AddMinutes(-1), start)] : [],
            kind == "finished-unreconciled" ? null : new(start, "goal", null,
                kind == "generation-behind-main" ? start.AddMinutes(2) : null,
                new string('a', 40), HandoffAt: null), new string('b', 40));
        var at = ConductorInvariantWatchdog.Evaluate(observation, new Dictionary<string, DateTimeOffset>());
        Assert.Empty(at.Raise);
        Assert.Empty(at.Clear);
        Assert.Empty(at.Active);
        var overObservation = observation with { Now = observation.Now.AddSeconds(1) };
        var over = ConductorInvariantWatchdog.Evaluate(overObservation, at.Active);
        var breach = Assert.Single(over.Raise);
        Assert.Equal(kind, breach.Kind);
        Assert.Equal(record, breach.Record);
        Assert.Equal(start, breach.Since);
        Assert.Empty(over.Clear);
        Assert.Equal(new KeyValuePair<string, DateTimeOffset>(kind + "|" + record, start), Assert.Single(over.Active));
        if (kind == "relaunch-without-activation")
            Assert.Equal($"scheduledAt:{start:O},lastLiveGateAt:none,silentFor:15m", breach.Evidence);

        var stable = ConductorInvariantWatchdog.Evaluate(overObservation, over.Active);
        Assert.Empty(stable.Raise);
        Assert.Empty(stable.Clear);
        Assert.Equal(over.Active.OrderBy(pair => pair.Key), stable.Active.OrderBy(pair => pair.Key));
        var cleared = ConductorInvariantWatchdog.Evaluate(new(overObservation.Now, [], null, []), over.Active);
        Assert.Empty(cleared.Raise);
        Assert.Equal(new ConductorInvariantBreach(kind, record, start, "recovered"), Assert.Single(cleared.Clear));
        Assert.Empty(cleared.Active);
    }

    [Theory]
    [InlineData(null, "aaaa")]
    [InlineData("bbbb", null)]
    [InlineData("  ", "aaaa")]
    [InlineData(" AAAAaaaaaaaa ", "aaaa")]
    [InlineData("aaaa", "AAAAAAAAAAAA")]
    public void Evaluate_NullHandoffAndUnknownOrMatchingBuilds_RemainsQuiet(
        string? main, string? running)
    {
        var start = DateTimeOffset.UnixEpoch;
        var result = ConductorInvariantWatchdog.Evaluate(new(start.AddHours(1), [], null, [],
            Relaunch: new(start, "goal", null, start.AddMinutes(2), running, HandoffAt: null), MainSha: main),
            new Dictionary<string, DateTimeOffset>());
        Assert.Empty(result.Raise);
        Assert.Empty(result.Clear);
        Assert.Empty(result.Active);
    }
}
