using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class AcceptanceEngineMainSuspectReleaseTests
{
    internal const string S = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    internal const string T = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    internal const string U = "cccccccccccccccccccccccccccccccccccccccc";
    internal static readonly string[] Tests = ["Fixture.Shared.FailsA", "Fixture.Shared.FailsB"];

    [Fact]
    public async Task PassedProbe_ReleasesOnlyAfterMainMovesAndRecordsEvidence()
    {
        using var fixture = new Fixture();
        await fixture.FailAsync();
        fixture.Tip.Value = S;
        Assert.Equal(MainSuspectReleaseCheckOutcome.SuspectStillTip, await fixture.Release.CheckAsync());
        Assert.Empty(fixture.Probe.Calls);
        Assert.Equal(AcceptanceEngineHealth.Unhealthy, (await fixture.Circuit.ReadAsync()).Health);

        fixture.Tip.Value = T;
        Assert.Equal(MainSuspectReleaseCheckOutcome.Released, await fixture.Release.CheckAsync());

        var call = Assert.Single(fixture.Probe.Calls);
        Assert.Equal(T, call.Tip);
        Assert.Equal(Tests, call.Tests);
        var cleared = Assert.Single((await fixture.Events.ReadAllAsync()).Where(item => item.Kind == PostLandingCanaryEventKind.Cleared));
        Assert.Contains(S, cleared.Payload.OperatorNote!);
        Assert.Contains(T, cleared.Payload.OperatorNote!);
        foreach (var test in Tests) Assert.Contains(test, cleared.Payload.OperatorNote!);
        Assert.Contains("probe=probe-receipt", cleared.Payload.OperatorNote!);
        Assert.Equal(AcceptanceEngineHealth.Healthy, (await fixture.Circuit.ReadAsync()).Health);
        Assert.Equal($"CANARY_GATE sha={T} result=released reason=main-moved suspect={S}", Assert.Single(fixture.Lines));
        await fixture.Release.CheckAsync();
        Assert.Single(fixture.Probe.Calls);
        Assert.Single(fixture.Lines);
    }

    [Fact]
    public async Task FailedProbe_RetiresTipButProbesFurtherTipOnce()
    {
        using var fixture = new Fixture(MainSuspectReleaseProbeOutcome.Failed);
        await fixture.FailAsync();
        Assert.Equal(MainSuspectReleaseCheckOutcome.StillFailing, await fixture.Release.CheckAsync());
        Assert.Equal(AcceptanceEngineHealth.Unhealthy, (await fixture.Circuit.ReadAsync()).Health);
        Assert.Equal($"CANARY_GATE sha={T} result=still-failing suspect={S}", Assert.Single(fixture.Lines));
        Assert.Equal(MainSuspectReleaseCheckOutcome.AlreadyResolvedForTip, await fixture.Release.CheckAsync());
        Assert.Single(fixture.Probe.Calls);
        Assert.Single(fixture.Lines);
        fixture.Tip.Value = U;
        await fixture.Release.CheckAsync();
        await fixture.Release.CheckAsync();
        Assert.Equal(new[] { T, U }, fixture.Probe.Calls.Select(call => call.Tip));
        Assert.Equal(2, fixture.Lines.Count);
        Assert.Empty((await fixture.Events.ReadAllAsync()).Where(item => item.Kind == PostLandingCanaryEventKind.Cleared));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnavailableProbe_RetriesOnLaterChecksUpToNamedBound(bool throws)
    {
        using var fixture = new Fixture(MainSuspectReleaseProbeOutcome.CouldNotRun);
        await fixture.FailAsync();
        if (throws) fixture.Probe.Handler = (_, _, _) => throw new InvalidOperationException("controlled probe fault");
        for (var attempt = 1; attempt <= AcceptanceEngineMainSuspectRelease.MaxReleaseProbeAttemptsPerTip; attempt++)
        {
            Assert.Equal(MainSuspectReleaseCheckOutcome.Deferred, await fixture.Release.CheckAsync());
            Assert.Equal(attempt, fixture.Probe.Calls.Count);
            Assert.Contains($"result=release-probe-deferred suspect={S} attempt={attempt}", fixture.Lines[^1]);
            Assert.Equal(AcceptanceEngineHealth.Unhealthy, (await fixture.Circuit.ReadAsync()).Health);
        }
        for (var extra = 0; extra < 2; extra++)
            Assert.Equal(MainSuspectReleaseCheckOutcome.AttemptsExhausted, await fixture.Release.CheckAsync());
        Assert.Equal(AcceptanceEngineMainSuspectRelease.MaxReleaseProbeAttemptsPerTip, fixture.Probe.Calls.Count);
        Assert.Equal(fixture.Probe.Calls.Count, fixture.Lines.Count);
        Assert.Empty((await fixture.Events.ReadAllAsync()).Where(item => item.Kind == PostLandingCanaryEventKind.Cleared));
        fixture.Tip.Value = U;
        Assert.Equal(MainSuspectReleaseCheckOutcome.Deferred, await fixture.Release.CheckAsync());
        Assert.Equal(U, fixture.Probe.Calls[^1].Tip);
        Assert.Contains("attempt=1", fixture.Lines[^1]);
    }

    [Theory]
    [InlineData("reject")]
    [InlineData("empty-receipt")]
    [InlineData("timeout")]
    [InlineData("infrastructure-error")]
    [InlineData("evaluated-artifact-failure")]
    public async Task OtherFailureReasons_AreNeverProbedOrCleared(string reason)
    {
        using var fixture = new Fixture();
        await fixture.FailAsync(reason: reason);
        await AssertIneligible(fixture);
    }

    [Fact]
    public async Task MixedFailures_DisableReleaseOfEntireCircuit()
    {
        using var fixture = new Fixture();
        await fixture.FailAsync();
        await fixture.FailAsync(U, "reject");
        await AssertIneligible(fixture);
    }

    [Fact]
    public async Task LegacyMainSuspectWithoutStructuredTests_IsNotProbed()
    {
        using var fixture = new Fixture();
        await fixture.FailAsync(tests: null);
        await AssertIneligible(fixture);
    }

    [Fact]
    public async Task MultipleSuspects_RequireMovedTipAndProbeUnion()
    {
        using var fixture = new Fixture();
        await fixture.FailAsync();
        await fixture.FailAsync(tests: [Tests[1], "Fixture.Other.FailsC"], sha: U);
        fixture.Tip.Value = U;
        Assert.Equal(MainSuspectReleaseCheckOutcome.SuspectStillTip, await fixture.Release.CheckAsync());
        Assert.Empty(fixture.Probe.Calls);
        fixture.Tip.Value = T;
        Assert.Equal(MainSuspectReleaseCheckOutcome.Released, await fixture.Release.CheckAsync());
        Assert.Equal(new[] { "Fixture.Other.FailsC", Tests[0], Tests[1] }, Assert.Single(fixture.Probe.Calls).Tests);
        var cleared = Assert.Single((await fixture.Events.ReadAllAsync()).Where(item => item.Kind == PostLandingCanaryEventKind.Cleared));
        Assert.Contains(S, cleared.Payload.OperatorNote!);
        Assert.Contains(U, cleared.Payload.OperatorNote!);
    }

    [Fact]
    public async Task ProbeInFlight_IsNotDuplicatedAndDoesNotConsumeAttempt()
    {
        using var fixture = new Fixture();
        await fixture.FailAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<MainSuspectReleaseProbeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Probe.Handler = (_, _, _) => { entered.SetResult(); return finish.Task; };
        var first = fixture.Release.CheckAsync();
        await entered.Task;
        try
        {
            Assert.Equal(MainSuspectReleaseCheckOutcome.InFlight, await fixture.Release.CheckAsync());
            Assert.Single(fixture.Probe.Calls);
            Assert.Empty(fixture.Lines);
            Assert.Equal(AcceptanceEngineHealth.Unhealthy, (await fixture.Circuit.ReadAsync()).Health);
        }
        finally { finish.SetResult(new(MainSuspectReleaseProbeOutcome.CouldNotRun, "receipt", "unavailable")); }
        Assert.Equal(MainSuspectReleaseCheckOutcome.Deferred, await first);
        Assert.Contains("attempt=1", Assert.Single(fixture.Lines));
    }

    [Fact]
    public async Task MainMovesDuringProbe_DoesNotClearAndProbesNewTip()
    {
        using var fixture = new Fixture();
        await fixture.FailAsync();
        fixture.Probe.Handler = (_, _, _) =>
        {
            fixture.Tip.Value = U;
            return Task.FromResult(new MainSuspectReleaseProbeResult(MainSuspectReleaseProbeOutcome.Passed, "receipt"));
        };
        Assert.Equal(MainSuspectReleaseCheckOutcome.TipMovedDuringProbe, await fixture.Release.CheckAsync());
        Assert.Equal(AcceptanceEngineHealth.Unhealthy, (await fixture.Circuit.ReadAsync()).Health);
        Assert.Empty(fixture.Lines);
        fixture.Probe.Handler = (_, _, _) => Task.FromResult(new MainSuspectReleaseProbeResult(MainSuspectReleaseProbeOutcome.Passed, "receipt-U"));
        Assert.Equal(MainSuspectReleaseCheckOutcome.Released, await fixture.Release.CheckAsync());
        Assert.Equal(new[] { T, U }, fixture.Probe.Calls.Select(call => call.Tip));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TripChangesDuringProbe_DoesNotAppendAutomaticClear(bool operatorClear)
    {
        using var fixture = new Fixture();
        await fixture.FailAsync();
        fixture.Probe.Handler = async (_, _, _) =>
        {
            if (operatorClear) fixture.Circuit.Clear("operator repair");
            else await fixture.FailAsync(U, "reject");
            return new(MainSuspectReleaseProbeOutcome.Passed, "receipt");
        };
        Assert.Equal(MainSuspectReleaseCheckOutcome.ReceiptsChangedDuringProbe, await fixture.Release.CheckAsync());
        Assert.Empty(fixture.Lines);
        var clears = (await fixture.Events.ReadAllAsync()).Where(item => item.Kind == PostLandingCanaryEventKind.Cleared).ToArray();
        if (operatorClear) Assert.Equal("operator repair", Assert.Single(clears).Payload.OperatorNote);
        else Assert.Empty(clears);
    }

    [Fact]
    public async Task EmergencyCircuit_IsNeverReleasedEvenIfSetDuringProbe()
    {
        using var fixture = new Fixture();
        await fixture.FailAsync();
        fixture.Probe.Handler = (_, _, _) =>
        {
            PostLandingCanaryEmergencyCircuit.Signal(fixture.Events.Identity, T, "emergency", DateTimeOffset.UtcNow);
            return Task.FromResult(new MainSuspectReleaseProbeResult(MainSuspectReleaseProbeOutcome.Passed, "receipt"));
        };
        Assert.Equal(MainSuspectReleaseCheckOutcome.ReceiptsChangedDuringProbe, await fixture.Release.CheckAsync());
        await fixture.Release.CheckAsync();
        Assert.Single(fixture.Probe.Calls);
        Assert.NotNull(PostLandingCanaryEmergencyCircuit.TryRead(fixture.Events.Identity));
        Assert.Empty((await fixture.Events.ReadAllAsync()).Where(item => item.Kind == PostLandingCanaryEventKind.Cleared));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnreadableMainTip_DefersEvaluationWithoutProbeOrLog(bool throws)
    {
        using var fixture = new Fixture();
        await fixture.FailAsync();
        fixture.Tip.Value = null;
        fixture.Tip.Throws = throws;
        Assert.Equal(MainSuspectReleaseCheckOutcome.TipUnreadable, await fixture.Release.CheckAsync());
        Assert.Empty(fixture.Probe.Calls);
        Assert.Empty(fixture.Lines);
        Assert.Equal(AcceptanceEngineHealth.Unhealthy, (await fixture.Circuit.ReadAsync()).Health);
    }

    [Fact]
    public async Task UnavailableProbe_EmitsOneLineForMultilineRunnerDiagnostic()
    {
        using var fixture = new Fixture();
        await fixture.FailAsync();
        fixture.Probe.Handler = (_, _, _) => Task.FromResult(new MainSuspectReleaseProbeResult(
            MainSuspectReleaseProbeOutcome.CouldNotRun, "receipt", "runner failed\r\nsecond diagnostic"));
        Assert.Equal(MainSuspectReleaseCheckOutcome.Deferred, await fixture.Release.CheckAsync());
        var line = Assert.Single(fixture.Lines);
        Assert.Contains("reason=runner_failed_second_diagnostic", line);
        Assert.DoesNotContain('\r', line);
        Assert.DoesNotContain('\n', line);
    }

    private static async Task AssertIneligible(Fixture fixture)
    {
        Assert.Equal(MainSuspectReleaseCheckOutcome.NotEligible, await fixture.Release.CheckAsync());
        Assert.Empty(fixture.Probe.Calls);
        Assert.Empty(fixture.Lines);
        Assert.Equal(AcceptanceEngineHealth.Unhealthy, (await fixture.Circuit.ReadAsync()).Health);
        Assert.Empty((await fixture.Events.ReadAllAsync()).Where(item => item.Kind == PostLandingCanaryEventKind.Cleared));
    }

    internal sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "main-suspect-release-tests", Guid.NewGuid().ToString("N"));
        internal Fixture(MainSuspectReleaseProbeOutcome outcome = MainSuspectReleaseProbeOutcome.Passed)
        {
            Directory.CreateDirectory(_root);
            var path = Path.Combine(_root, "events.db");
            Events = new(new SqliteRunEventStore(path), path);
            Circuit = new(Events);
            Probe = new(outcome);
            Release = new(Events, Circuit, Tip, Probe, Lines.Add);
        }
        internal PostLandingCanaryEventStore Events { get; }
        internal AcceptanceEngineCircuitBreaker Circuit { get; }
        internal TipReader Tip { get; } = new();
        internal ProbeStub Probe { get; }
        internal List<string> Lines { get; } = [];
        internal AcceptanceEngineMainSuspectRelease Release { get; }
        internal Task FailAsync(string sha = S, string reason = "main-suspect") => FailAsync(Tests, sha, reason);
        internal async Task FailAsync(IReadOnlyList<string>? tests, string sha = S, string reason = "main-suspect")
        {
            var now = DateTimeOffset.UtcNow;
            await Events.AppendOnceAsync(PostLandingCanaryEventKind.Failed,
                new(PostLandingCanaryEventPayload.CanaryTag, sha, [], reason, tests?.Count ?? 0,
                    "shared failures", null, now, SharedFailingTests: tests),
                $"failure:{Guid.NewGuid():N}", now);
        }
        public void Dispose()
        {
            PostLandingCanaryEmergencyCircuit.Clear(Events.Identity);
            Directory.Delete(_root, recursive: true);
        }
    }

    internal sealed class TipReader : IMainTipReader
    {
        internal string? Value { get; set; } = T;
        internal bool Throws { get; set; }
        public string? ReadMainTip() => Throws ? throw new IOException("controlled tip failure") : Value;
    }

    internal sealed class ProbeStub(MainSuspectReleaseProbeOutcome outcome) : IMainSuspectReleaseProbe
    {
        internal List<(string Tip, string[] Tests)> Calls { get; } = [];
        internal Func<string, IReadOnlyList<string>, CancellationToken, Task<MainSuspectReleaseProbeResult>>? Handler { get; set; }
        public Task<MainSuspectReleaseProbeResult> RunAsync(string tip, IReadOnlyList<string> tests, CancellationToken cancellationToken)
        {
            Calls.Add((tip, tests.ToArray()));
            return Handler?.Invoke(tip, tests, cancellationToken) ?? Task.FromResult(new MainSuspectReleaseProbeResult(outcome, "probe-receipt"));
        }
    }
}
