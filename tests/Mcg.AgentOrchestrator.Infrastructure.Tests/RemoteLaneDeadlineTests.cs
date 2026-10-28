using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Infrastructure;
using Check = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

// Scenario-owned roots, injected clocks/executors, and leased build slots.
[Xunit.Collection(TestCollections.JobAccounting)]
public sealed class RemoteLaneDeadlineTests
{
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromMinutes(40);

    [Theory]
    [InlineData(2000, 2400)]
    [InlineData(600, 900)]
    [InlineData(100, 300)]
    [InlineData(600.125, 900.1875)]
    public void Resolve_MeasuredEstimate_UsesCapMultiplierAndFloor(double remote, double expected)
    {
        Assert.Equal(TimeSpan.FromSeconds(expected), RemoteLaneDeadline.Resolve(Decision(remote), CheckTimeout));
    }

    [Theory]
    [InlineData(0, 0, 2400)]
    [InlineData(2, 2, 2400)]
    [InlineData(3, 2, 900)]
    [InlineData(2, 3, 900)]
    public void Resolve_EitherSampleThresholdPermitsShortening(int local, int remote, double expected)
    {
        Assert.Equal(TimeSpan.FromSeconds(expected),
            RemoteLaneDeadline.Resolve(Decision(600, local, remote), CheckTimeout));
    }

    [Fact]
    public void Resolve_CheckTimeoutBelowFloor_StillCapsDeadline()
    {
        Assert.Equal(TimeSpan.FromMinutes(2),
            RemoteLaneDeadline.Resolve(Decision(100), TimeSpan.FromMinutes(2)));
    }

    [Fact]
    public void Resolve_MissingDecision_KeepsCheckTimeout()
    {
        Assert.Equal(CheckTimeout, RemoteLaneDeadline.Resolve(null, CheckTimeout));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1)]
    public void Resolve_InvalidEstimate_KeepsCheckTimeout(double remote)
    {
        Assert.Equal(CheckTimeout, RemoteLaneDeadline.Resolve(Decision(remote), CheckTimeout));
    }

    [Fact]
    public async Task NeverFinishingLane_TimesOutOnlyAfterMeasuredDeadline()
    {
        using var fixture = new Fixture();
        var deadline = RemoteLaneDeadline.Resolve(Decision(600), CheckTimeout);
        var handle = new FakeRemoteLaneExecutor.Handle(fixture.Clock.GetUtcNow());
        var polledAtDeadline = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = fixture.Clock.GetUtcNow();
        var polls = 0;
        handle.OnPolled = () =>
        {
            // Advance after the first timeout comparison; only a later poll can prove equality survived.
            if (Interlocked.Increment(ref polls) == 1) fixture.Clock.Advance(deadline);
            else if (fixture.Clock.GetUtcNow() - started == deadline) polledAtDeadline.TrySetResult();
        };
        fixture.Executor.Submit = (_, _) => Task.FromResult(new RemoteLaneSubmission(handle));
        using var stop = new CancellationTokenSource();
        var running = fixture.RunAsync(deadline, stop.Token);
        try
        {
            await Event(fixture.Executor.Submitted.Task, "executor submission");
            await Event(handle.Polled.Task, "first executor poll");
            await Event(polledAtDeadline.Task, "executor poll at exact deadline");
            Assert.False(running.IsCompleted);

            fixture.Clock.Advance(TimeSpan.FromSeconds(1));
            await Event(running, "LaneTimeout fallback after deadline");
            var outcome = await running;
            Assert.Null(outcome.Accepted);
            Assert.Equal(RemoteLaneOutcomeCode.LaneTimeout,
                Assert.Single(RemoteExecutorHealthLedger.ReadAll(fixture.Ledger)).Outcome);
            Assert.Equal(TimeSpan.FromSeconds(901), fixture.Clock.GetUtcNow() - started);
            Assert.True(fixture.Clock.GetUtcNow() - started < CheckTimeout);
            Assert.True(handle.IsAbandoned);
        }
        finally
        {
            stop.Cancel();
            try { await Event(running, "remote run cleanup"); }
            catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task ShortenedDeadline_PreservesCheckTimeoutTestHostBudget()
    {
        using var fixture = new Fixture();
        var deadline = RemoteLaneDeadline.Resolve(Decision(600), CheckTimeout);
        await fixture.RunAsync(deadline, CancellationToken.None);
        var request = Assert.Single(fixture.Executor.Requests);
        Assert.Equal(TimeSpan.FromSeconds(900), deadline);
        Assert.Equal(RemoteLaneTestHostBudget.Resolve(CheckTimeout), request.TestHostTimeoutSeconds);
        Assert.Equal(2100, request.TestHostTimeoutSeconds);
        Assert.NotEqual(RemoteLaneTestHostBudget.Resolve(deadline), request.TestHostTimeoutSeconds);
    }

    [Theory]
    [InlineData(600, "900")]
    [InlineData(600.125, "900.1875")]
    public async Task PolicyLine_ReportsSeparateDeadlineAndCheckTimeout(double remote, string deadline)
    {
        using var scenario = new GoalAcceptanceVerifierDotnetBuildSlotTestsRemoteSlotInstrumentation.Scenario(1);
        var manifestPath = Path.Combine(scenario.Root, "config", "acceptance-manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!;
        manifest["checks"]![0]!["timeoutMinutes"] = 40;
        manifest["engine"]!["infrastructureTestLanes"]![0]!["estimatedSerialSeconds"] = 1000;
        File.WriteAllText(manifestPath, manifest.ToJsonString());
        var history = Path.Combine(scenario.Root, "offer-history.jsonl");
        File.WriteAllText(history, "");
        RemoteLaneOfferSeeding.SeedRemote(history, scenario.Lane(0), "FullyQualifiedName~Lane0Tests", remote);

        Assert.True((await scenario.RunAsync()).Passed);

        var line = Assert.Single(scenario.Progress, value => value.StartsWith("REMOTE_LANE_POLICY ", StringComparison.Ordinal));
        var remoteField = remote == 600 ? "600" : "600.125";
        Assert.Equal($"REMOTE_LANE_POLICY lane=\"infrastructure tests: Lane 0\" decision=offer reason=fits L=1000 Ln=0 R={remoteField} Rn=3 F=1000 Fn=0 DeadlineSeconds={deadline} CheckTimeoutSeconds=2400", line);
    }

    private static RemoteLaneOfferDecision Decision(double remote, int localSamples = 0, int remoteSamples = 3) =>
        new(true, "fits", 600, remote, new("infrastructure tests: Deadline", 600, localSamples, 600,
            remote, remoteSamples, 1000));

    private static async Task Event(Task task, string name)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (TimeoutException) { throw new TimeoutException("Missing event: " + name); }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "remote-deadline-" + Guid.NewGuid().ToString("N"));
        private readonly RemoteLaneExecutorEntry _entry = new("executor", 3600, RunnerAlias: "runner-host");
        private readonly Check _check = new()
        {
            Name = "infrastructure tests: Deadline", Type = "dotnet-test",
            Project = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
            Arguments = ["--filter", "FullyQualifiedName~RemoteLaneDeadlineTests"]
        };
        private readonly RemoteLaneCoordinator _coordinator;
        internal FakeRemoteLaneExecutor Executor { get; } = new();
        internal ManualRemoteLaneClock Clock { get; } = new();
        internal string Ledger => RemoteExecutorHealthLedger.ResolveStorePath(_root);

        internal Fixture()
        {
            Directory.CreateDirectory(_root);
            _coordinator = new(new([_entry], [_check.Name], null),
                new RemoteLaneCandidateIdentity("attempt", "goal", "commit", "tree", "main", "manifest"),
                _root, null, Executor, Clock, TimeSpan.FromMilliseconds(1), null);
            Assert.True(_coordinator.IsEligible(_check));
            Assert.Equal(_entry, _coordinator.TryClaimIdleExecutor());
        }

        internal Task<RemoteLaneOutcome> RunAsync(TimeSpan deadline, CancellationToken token) =>
            _coordinator.RunRemoteAsync(_check, _entry, CheckTimeout, deadline, token);

        public void Dispose()
        {
            _coordinator.Dispose();
            Directory.Delete(_root, recursive: true);
        }
    }
}
