using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: records and loop leases have a unique root; no children or global mutations.
public sealed class ConductorLiveGroupedGateCountTests : ConductorBatchLoopTests
{
    public ConductorLiveGroupedGateCountTests(ITestOutputHelper output) : base(output) { }

    [Xunit.Theory]
    [Xunit.InlineData("train")]
    [Xunit.InlineData("cohort")]
    [Xunit.InlineData("follower")]
    public void RunningAttempt_LiveForeignOwner_CountsAsOne(string kind)
    {
        var root = CreateTempDirectory("live-grouped-gate");
        try
        {
            var now = DateTimeOffset.UnixEpoch;
            var coordinator = new ConductorGroupedGateAttemptCoordinator(
                root, generationId: 111, isProcessAlive: pid => pid == 222, utcNow: () => now);
            var attempt = SaveAttempt(coordinator, root, kind);

            Assert.Equal(now, attempt.StartedAt);
            Assert.NotEqual(coordinator.GenerationId, attempt.OwnerProcessId);
            Assert.Equal(1, ConductorLiveGroupedGateCount.Count(coordinator));
        }
        finally { TryDeleteDirectory(root); }
    }

    [Xunit.Theory]
    [Xunit.InlineData("reconciled")]
    [Xunit.InlineData("owner-dead")]
    [Xunit.InlineData("result")]
    [Xunit.InlineData("exit")]
    [Xunit.InlineData("owner-absent")]
    [Xunit.InlineData("not-running")]
    public void InactiveAttempt_Drain_RelaunchesWithoutWaiting(string condition)
    {
        var root = CreateTempDirectory("inactive-grouped-gate");
        try
        {
            var now = DateTimeOffset.UnixEpoch;
            var coordinator = new ConductorGroupedGateAttemptCoordinator(
                Path.Combine(root, "attempts"), generationId: 111,
                isProcessAlive: pid => condition != "owner-dead" && pid == 222,
                utcNow: () => now);
            var attempt = SaveAttempt(coordinator, root, "train");
            if (condition == "reconciled") attempt = attempt with { ReconciledAt = now };
            if (condition == "owner-absent") attempt = attempt with { OwnerProcessId = 0 };
            if (condition == "not-running") attempt = attempt with { Outcome = "Completed" };
            ConductorGroupedGateAttemptCoordinator.Save(attempt);
            if (condition == "result") File.WriteAllText(attempt.ResultPath, "{}");
            if (condition == "exit") File.WriteAllText(attempt.ExitCodePath, "0");

            Assert.Equal(0, ConductorLiveGroupedGateCount.Count(coordinator));
            var kernel = new AgentOrchestratorKernel();
            CreateVerifiedSimpleGoal(kernel, "Update conductor runtime");
            var driver = LandingDriver();
            var handoffs = 0;
            var sleeps = 0;
            var polls = 0;
            var output = AsyncLocalConsoleRouter.Capture(() => new ConductorBatchLoop(
                utcNow: () => now,
                countLiveGroupedGates: () => { polls++; return ConductorLiveGroupedGateCount.Count(coordinator); },
                selfRelaunchEnabled: true,
                selfRelaunch: _ => { handoffs++; return Handoff(); }).Run(
                    kernel, driver, ConductorAutonomyPolicy.Conservative,
                    Path.Combine(root, "stop"), maxIterations: 5,
                    sleepFunc: _ => { sleeps++; return false; }));

            Assert.Equal(1, handoffs);
            Assert.Equal(1, polls);
            Assert.Equal(0, sleeps);
            Assert.Contains("LOOP_RELAUNCH_REBUILD", output, StringComparison.Ordinal);
            Assert.DoesNotContain("LOOP_RELAUNCH_DRAIN", output, StringComparison.Ordinal);
        }
        finally { TryDeleteDirectory(root); }
    }

    [Xunit.Fact]
    public void CoordinatorNotEnabled_Count_ReturnsZero() =>
        Assert.Equal(0, ConductorLiveGroupedGateCount.Count(null));

    internal static ConductorGroupedGateAttempt SaveAttempt(
        ConductorGroupedGateAttemptCoordinator coordinator, string root, string kind)
    {
        var attempt = coordinator.Create(kind, [], new string('a', 40), new string('b', 40),
            "manifest", "test-gate", root, ConductorAutonomyPolicy.Conservative) with
        {
            OwnerProcessId = 222, OwnerProcessStartedAt = DateTimeOffset.UnixEpoch,
            OwnerExecutablePath = "C:\\dotnet.exe"
        };
        ConductorGroupedGateAttemptCoordinator.Save(attempt);
        return attempt;
    }

    internal static ConductorDriver LandingDriver(Action? onDispatch = null)
    {
        var landedIds = new HashSet<string>(StringComparer.Ordinal);
        return MakeDriver(
            dispatchAndStart: _ => { onDispatch?.Invoke(); return DispatchStartOutcome.Started(); },
            getFacts: goal => landedIds.Contains(goal.Id.Value)
                ? new GoalLifecycleFacts(WorkspaceExists: true, IsMerged: true, IsRecorded: true, IsCleanedUp: true)
                : new GoalLifecycleFacts(WorkspaceExists: true),
            land: goal =>
            {
                landedIds.Add(goal.Id.Value);
                return new LandingResult(goal.Id.Value, goal.Id.Value[..8],
                    new LandingDecision.Promote(), "integration", true, "Landed");
            },
            getLandingFileScopes: _ => ["src/Mcg.AgentOrchestrator.App/Orchestration/ConductorBatchLoop.cs"]);
    }

    internal static ConductorSelfRelaunchResult Handoff() =>
        new(true, null, null, ConductorLoopHandoffResult.StartedProcess(333, "out", "err"));
}
