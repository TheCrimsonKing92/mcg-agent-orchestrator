using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsInvalidatedAttemptGraceStop(ITestOutputHelper output)
    : ConductorBatchLoopTests(output)
{
    [Fact(Timeout = 30000)]
    [Trait("Category", "CrossTick")]
    public void LiveInvalidatedOwnerPastGraceIsStoppedOnceAndRegatedNextTick()
    {
        using var fixture = new Fixture(landFirst: true);
        fixture.LandAndInvalidate();
        var invalidatedAt = ReadAttempt(fixture.Attempt.MetadataPath).InvalidatedAt;
        Assert.Equal(fixture.Now, invalidatedAt);

        fixture.Now = fixture.Now.AddSeconds(120);
        fixture.Tick();
        Assert.Empty(fixture.StoppedPids);
        Assert.Empty(fixture.StopEvents);

        fixture.Now = fixture.Now.AddSeconds(1);
        fixture.Tick();
        Assert.Equal([Fixture.OwnerPid], fixture.StoppedPids);
        var stopEvent = Assert.Single(fixture.StopEvents);
        Assert.Contains($"attempt={fixture.Attempt.AttemptId} ", stopEvent);
        Assert.Contains($"ownerPid={Fixture.OwnerPid} descendantPids={Fixture.ChildPid} ", stopEvent);
        Assert.Contains($"invalidatedAt={invalidatedAt:O} waitedSeconds=121.000 graceSeconds=120 outcome=stopped", stopEvent);
        Assert.Equal(GoalStatus.Verifying, fixture.Waiting.Status);

        // A stop request is not exit confirmation. Keep the owner alive over another tick.
        fixture.Now = fixture.Now.AddSeconds(10);
        fixture.Tick();
        Assert.Single(fixture.StoppedPids);
        Assert.Single(fixture.StopEvents);
        Assert.Equal(GoalStatus.Verifying, fixture.Waiting.Status);
        Assert.Equal(0, fixture.Waiting.ConsecutiveAcceptanceIdentityStaleCount);

        fixture.ExitOwner();
        fixture.Tick();
        fixture.AssertRegated();
        Assert.Single(fixture.StoppedPids);
        Assert.Single(fixture.StopEvents);
    }

    [Fact(Timeout = 30000)]
    [Trait("Category", "CrossTick")]
    public void InvalidatedOwnerExitingWithinGraceIsNeverStopped()
    {
        using var fixture = new Fixture(landFirst: true);
        fixture.LandAndInvalidate();
        fixture.Now = fixture.Now.AddSeconds(119);
        fixture.Tick();
        Assert.Equal(GoalStatus.Verifying, fixture.Waiting.Status);

        fixture.ExitOwner();
        fixture.Tick();
        fixture.AssertRegated();
        // Observing the same dead owner beyond the deadline must remain harmless.
        fixture.Now = fixture.Now.AddSeconds(10);
        Assert.False(fixture.Coordinator.TryGetLiveInvalidatedAttempt(fixture.Waiting.Id.Value, out _));
        Assert.Empty(fixture.StoppedPids);
        Assert.Empty(fixture.StopEvents);
    }

    [Fact(Timeout = 30000)]
    [Trait("Category", "CrossTick")]
    public void LiveNonInvalidatedAttemptIsNeverStopped()
    {
        using var fixture = new Fixture(landFirst: false);
        fixture.Tick();
        fixture.Now = fixture.Now.AddMinutes(10);
        fixture.Tick();

        Assert.Equal(GoalStatus.Verifying, fixture.Waiting.Status);
        var current = ReadAttempt(fixture.Attempt.MetadataPath);
        Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Running, current.Outcome);
        Assert.Null(current.SupersedingMainHeadSha);
        Assert.Null(current.InvalidatedAt);
        Assert.False(fixture.Coordinator.TryGetLiveInvalidatedAttempt(fixture.Waiting.Id.Value, out _));
        Assert.Single(fixture.Coordinator.GetCapacityReservingAttempts([fixture.Waiting.Id.Value]));
        Assert.Empty(fixture.StoppedPids);
        Assert.Empty(fixture.StopEvents);
    }

    private sealed class Fixture : IDisposable
    {
        internal const int OwnerPid = 7105;
        internal const int ChildPid = 7106;
        private const string Scope = "src/Mcg.AgentOrchestrator.App/Orchestration/ConductorBatchLoop.cs";
        private const string OldMain = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private const string NewMain = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        private const string Branch = "cccccccccccccccccccccccccccccccccccccccc";
        private readonly string _root = CreateTempDirectory("mcg-invalidated-grace");
        private readonly AgentOrchestratorKernel _kernel = new();
        private readonly ConductorAutonomyPolicy _policy = ConductorAutonomyPolicy.Conservative with { AcceptanceWidth = 2 };
        private readonly ConductorDriver _driver;
        private readonly ConductorBatchLoop _loop;
        private readonly List<string> _lines = [];
        private bool _alive = true;
        internal DateTimeOffset Now = new(2026, 10, 30, 14, 0, 0, TimeSpan.Zero);
        internal Goal Waiting { get; }
        internal ConductorParallelAcceptanceAttempt Attempt { get; }
        internal ConductorParallelAcceptanceAttemptCoordinator Coordinator { get; }
        internal List<int> StoppedPids { get; } = [];
        internal IEnumerable<string> StopEvents => _lines.Where(line => line.StartsWith("INVALIDATED_ATTEMPT_STOP ", StringComparison.Ordinal));

        internal Fixture(bool landFirst)
        {
            var landing = landFirst ? CreateVerifiedSimpleGoal(_kernel, "Land before invalidation") : null;
            Waiting = CreateVerifiedSimpleGoal(_kernel, "Wait for owner exit before re-gating");
            var ownerPath = Path.Combine(_root, "dotnet.exe");
            var ownerStartedAt = Now.AddSeconds(-10);
            var starter = new ConductorParallelAcceptanceAttemptCoordinator(_root, TrunkBranchName.Default, utcNow: () => Now,
                isProcessAlive: _ => _alive, recentHeartbeatGrace: TimeSpan.FromHours(1),
                launchOwnedProcess: _ => new(OwnerPid, ownerStartedAt, ownerPath));
            var candidate = ConductorParallelAcceptanceCandidate.Create(Waiting, landFirst ? 1 : 0, [Scope], Branch, OldMain);
            Attempt = starter.Evaluate(candidate, _policy, PassingRun).Attempt;
            // Reuse the live waiting attempt, but let the unrelated landing goal finish its gate inline.
            Coordinator = new(_root, TrunkBranchName.Default, utcNow: () => Now, runInline: true,
                isProcessAlive: _ => _alive, recentHeartbeatGrace: TimeSpan.FromHours(1));
            var main = OldMain;
            _driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                getLandingFileScopes: goal => goal.Id == landing?.Id ? ["docs/architecture.md"] : [Scope],
                getAcceptanceSlotCount: _ => 2,
                runAcceptanceWithSlot: (_, _) => new(true, [], BranchHeadSha: Branch, MainHeadSha: OldMain),
                resolveAcceptanceHeads: _ => (Branch, main),
                land: goal =>
                {
                    main = NewMain;
                    _kernel.CompleteGoal(goal.Id, "Landed");
                    return new(goal.Id.Value, goal.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "Landed", Branch);
                }, parallelAcceptanceAttemptCoordinator: Coordinator, utcNow: () => Now);
            var processStop = new InvalidatedAcceptanceAttemptProcessStop(
                inspect: pid => new(pid, 1, "dotnet", ownerPath, ownerStartedAt,
                    $"dotnet {ConductorParallelAcceptanceAttemptCoordinator.OwnedProcessSubcommandName} {Attempt.MetadataPath}",
                    ProcessInspectionStatus.Available),
                descendants: _ => [ChildPid], stop: pid => { StoppedPids.Add(pid); return true; });
            _loop = new(utcNow: () => Now) { InvalidatedAttemptStop = processStop.Stop };
        }

        internal void LandAndInvalidate()
        {
            Tick();
            Assert.Equal(NewMain, ReadAttempt(Attempt.MetadataPath).SupersedingMainHeadSha);
            Assert.True(Coordinator.TryGetLiveInvalidatedAttempt(Waiting.Id.Value, out _));
            Assert.Equal(GoalStatus.Verifying, Waiting.Status);
        }

        internal void Tick() => _loop.Run(_kernel, _driver, _policy, Path.Combine(_root, "stop"),
            maxIterations: 1, onTick: tick => _lines.AddRange(tick.ProgressLines ?? []));

        internal void ExitOwner()
        {
            _alive = false;
            File.WriteAllText(Attempt.ExitCodePath, "1");
        }

        internal void AssertRegated()
        {
            Assert.Equal(GoalStatus.Verified, Waiting.Status);
            Assert.Equal(1, Waiting.ConsecutiveAcceptanceIdentityStaleCount);
            Assert.Equal(Attempt.AttemptId, Waiting.LastAcceptanceIdentityStaleAttemptId);
            Assert.Empty(Coordinator.GetCapacityReservingAttempts([Waiting.Id.Value]));
        }

        public void Dispose() => TryDeleteDirectory(_root);
    }
}
