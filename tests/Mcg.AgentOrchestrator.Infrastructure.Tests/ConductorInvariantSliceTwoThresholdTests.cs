using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: pure observations and an injected timeline; no shared state or I/O.
public sealed class ConductorInvariantSliceTwoThresholdTests
{
    [Fact]
    public void Thresholds_SliceTwo_MatchPublishedContract()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), ConductorInvariantThresholds.FinishedUnreconciled);
        Assert.Equal(TimeSpan.FromMinutes(15), ConductorInvariantThresholds.RelaunchWithoutActivation);
        Assert.Equal(TimeSpan.FromMinutes(30), ConductorInvariantThresholds.GenerationBehindMain);
        Assert.Equal(TimeSpan.FromHours(24), ConductorInvariantThresholds.FinishedAttemptScanWindow);
        Assert.Equal("finished-unreconciled", ConductorInvariantThresholds.FinishedUnreconciledKind);
        Assert.Equal("relaunch-without-activation", ConductorInvariantThresholds.RelaunchWithoutActivationKind);
        Assert.Equal("generation-behind-main", ConductorInvariantThresholds.GenerationBehindMainKind);
    }

    [Theory]
    [InlineData("finished-unreconciled", 5, "attempt")]
    [InlineData("relaunch-without-activation", 15, "goal")]
    [InlineData("generation-behind-main", 30, "bbbbbbbbbbbb")]
    public void Evaluate_ExactThresholdAndOneSecondOver_UsesStrictBoundary(string kind,
        int minutes, string record)
    {
        var start = DateTimeOffset.Parse("2026-10-16T21:17:00Z");
        ConductorInvariantObservation observation = new(start.AddMinutes(minutes), [], null, [],
            kind == "finished-unreconciled" ? [new("attempt", "goal", "gate", start.AddMinutes(-1), start)] : [],
            kind == "finished-unreconciled" ? null : new(start, "goal", null,
                kind == "generation-behind-main" ? start.AddMinutes(2) : null, new string('a', 40)), new string('b', 40));
        var atThreshold = ConductorInvariantWatchdog.Evaluate(observation, new Dictionary<string, DateTimeOffset>());
        Assert.Empty(atThreshold.Raise);
        Assert.Empty(atThreshold.Clear);
        Assert.Empty(atThreshold.Active);
        var over = ConductorInvariantWatchdog.Evaluate(observation with { Now = observation.Now.AddSeconds(1) }, atThreshold.Active);
        var breach = Assert.Single(over.Raise);
        Assert.Equal(kind, breach.Kind);
        Assert.Equal(record, breach.Record);
        Assert.Equal(start, breach.Since);
        Assert.Empty(over.Clear);
        Assert.Equal(start, Assert.Single(over.Active).Value);
    }

    [Fact]
    public void Evaluate_OldFourArguments_PreservesSliceOneRaiseClearAndActive()
    {
        var since = DateTimeOffset.Parse("2026-10-16T21:17:00Z");
        var refusals = Enumerable.Range(0, 5).Select(index =>
            new ConductorInvariantRefusal("members", "identity-changed field=main", since.AddSeconds(index))).ToArray();
        ConductorInvariantObservation legacy = new(since.AddMinutes(11), [new("goal", since)], null, refusals);
        var active = new Dictionary<string, DateTimeOffset> { ["gate-idle-while-ready|old-goal"] = since.AddMinutes(-1) };
        var result = ConductorInvariantWatchdog.Evaluate(legacy, active);
        Assert.Equal(new[] { "gate-idle-while-ready|goal", "repeated-refusal|members" }, result.Raise.Select(b => b.Key));
        Assert.All(result.Raise, breach => Assert.Equal(since, breach.Since));
        Assert.Equal(new ConductorInvariantBreach("gate-idle-while-ready", "old-goal", since.AddMinutes(-1), "recovered"),
            Assert.Single(result.Clear));
        Assert.Equal(result.Raise.Select(b => b.Key).Order(), result.Active.Keys.Order());
        Assert.All(result.Active, pair => Assert.Equal(since, pair.Value));

        var explicitEmpty = ConductorInvariantWatchdog.Evaluate(legacy with
            { FinishedAttempts = [], Relaunch = ConductorRelaunchObservation.Empty }, active);
        Assert.Equal(result.Raise, explicitEmpty.Raise);
        Assert.Equal(result.Clear, explicitEmpty.Clear);
        Assert.Equal(result.Active.OrderBy(pair => pair.Key), explicitEmpty.Active.OrderBy(pair => pair.Key));
        var stable = ConductorInvariantWatchdog.Evaluate(legacy, result.Active);
        Assert.Empty(stable.Raise);
        Assert.Empty(stable.Clear);
        Assert.Equal(result.Active.OrderBy(pair => pair.Key), stable.Active.OrderBy(pair => pair.Key));
        var cleared = ConductorInvariantWatchdog.Evaluate(new(legacy.Now, [], null, []), result.Active);
        Assert.Empty(cleared.Raise);
        Assert.Equal(result.Active.Keys.Order(), cleared.Clear.Select(b => b.Key).Order());
        Assert.Empty(cleared.Active);
    }

    [Theory]
    [InlineData(null, "aaaa")]
    [InlineData("bbbb", null)]
    [InlineData("  ", "aaaa")]
    [InlineData(" AAAAaaaaaaaa ", "aaaa")]
    [InlineData("aaaa", "AAAAAAAAAAAA")]
    public void Evaluate_UnknownOrMatchingPrefixBuilds_DoesNotRaise(string? main, string? running)
    {
        var start = DateTimeOffset.UnixEpoch;
        var result = ConductorInvariantWatchdog.Evaluate(new(start.AddHours(1), [], null, [],
            Relaunch: new(start, "goal", null, start.AddMinutes(2), running), MainSha: main),
            new Dictionary<string, DateTimeOffset>());
        Assert.Empty(result.Raise);
        Assert.Empty(result.Active);
    }
}
