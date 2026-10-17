using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: the repository, loop artifacts and process/clock observations are test-owned.
public sealed class RelaunchDrainLegacyHolderControlTests : ConductorBatchLoopTests
{
    public RelaunchDrainLegacyHolderControlTests(ITestOutputHelper output) : base(output) { }

    [Fact]
    public void LegacyHolder_PublicationHandsOffOnFirstFollowingPoll()
    {
        var result = RunScenario(durable: false, runningDispatch: false);

        Assert.Equal(1, result.Handoffs);
        Assert.Equal(5, result.HandoffAtSleep);
        Assert.Equal(6, result.Polls);
        var drains = DrainLines(result.Output);
        Assert.Equal(5, drains.Length);
        Assert.All(drains, line => Assert.Contains("active=0 admitting=false liveGates=1", line, StringComparison.Ordinal));
        Assert.DoesNotContain("LOOP_RELAUNCH_DETACH", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("LOOP_RELAUNCH_ROLLBACK", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void LeasedV1_RunningDispatchStillWaitsForDetachCap()
    {
        var result = RunScenario(durable: true, runningDispatch: true);

        Assert.Equal(1, result.Handoffs);
        Assert.Equal(1, result.Detached);
        Assert.True(result.DetachedBeforeHandoff);
        Assert.Equal(3, result.HandoffAtSleep);
        var drains = DrainLines(result.Output);
        Assert.NotEmpty(drains);
        Assert.All(drains, line =>
        {
            Assert.Contains("active=1", line, StringComparison.Ordinal);
            Assert.Contains("liveGates=0", line, StringComparison.Ordinal);
        });
        var detach = result.Output.IndexOf("LOOP_RELAUNCH_DETACH", StringComparison.Ordinal);
        var rebuild = result.Output.IndexOf("LOOP_RELAUNCH_REBUILD", StringComparison.Ordinal);
        Assert.True(detach >= 0 && rebuild > detach, "Dispatch detach must precede rebuild.");
        Assert.DoesNotContain("LOOP_RELAUNCH_ROLLBACK", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void NeverPublishingLegacyHolder_RevertsAtLiveIdleBoundAndRearms()
    {
        var result = RunScenario(durable: false, runningDispatch: false, neverPublish: true);

        Assert.Equal(0, result.Handoffs);
        var rollback = Assert.Single(result.Output.Split(Environment.NewLine),
            line => line.Contains("LOOP_RELAUNCH_ROLLBACK", StringComparison.Ordinal));
        Assert.Contains("phase=drain", rollback, StringComparison.Ordinal);
        Assert.Contains($"within_{(int)DispatchRecoveryPolicy.DefaultLiveIdleTimeout.TotalMinutes}_minutes",
            rollback, StringComparison.Ordinal);
        Assert.Equal(DispatchRecoveryPolicy.DefaultLiveIdleTimeout, result.RollbackAt);
        Assert.True(result.Admissions > 0);
        var rollbackIndex = result.Output.IndexOf("LOOP_RELAUNCH_ROLLBACK", StringComparison.Ordinal);
        Assert.Contains("LOOP_RELAUNCH_DRAIN", result.Output[(rollbackIndex + 1)..], StringComparison.Ordinal);
        Assert.All(DrainLines(result.Output),
            line => Assert.Contains("active=0 admitting=false liveGates=1", line, StringComparison.Ordinal));
        Assert.DoesNotContain("LOOP_RELAUNCH_REBUILD", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("LOOP_RELAUNCH_DETACH", result.Output, StringComparison.Ordinal);
    }

    private static string[] DrainLines(string output) => output.Split(Environment.NewLine)
        .Where(line => line.Contains("LOOP_RELAUNCH_DRAIN", StringComparison.Ordinal)).ToArray();

    private static ScenarioResult RunScenario(bool durable, bool runningDispatch, bool neverPublish = false)
    {
        using var scenario = new RelaunchDrainV1GroupedGateScenarioTests.Scenario();
        var root = CreateTempDirectory("relaunch-holder-controls");
        try
        {
            var now = DateTimeOffset.UnixEpoch;
            var started = now;
            var stopPath = Path.Combine(root, "stop");
            var eventPath = Path.Combine(root, "events.log");
            var exits = new AcceptanceHolderExitFixture();
            using var fixture = durable
                ? new ConductorGroupedGateAttemptRelaunchDrainCountTests.DurableHolderFixture(scenario.AttemptRoot, "cohort")
                : null;
            var coordinator = fixture?.Coordinator ?? new ConductorGroupedGateAttemptCoordinator(
                scenario.AttemptRoot, generationId: 111, isProcessAlive: pid => pid == 222,
                classifyHolder: exits.Classify, utcNow: () => now);
            var attempt = fixture?.Attempt ?? ConductorLiveGroupedGateCountTests.SaveAttempt(coordinator, root, "cohort");
            Assert.Equal(durable ? 1 : 0, attempt.HolderContractVersion);
            var gateDriver = scenario.Driver(coordinator);
            var kernel = new AgentOrchestratorKernel();
            CreateVerifiedSimpleGoal(kernel, "Update conductor runtime");
            if (runningDispatch)
            {
                var worker = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "Existing worker");
                var task = worker.Tasks.Single();
                const string command = "worker Developer";
                kernel.RecordTaskDispatch(worker.Id, task.Id,
                    new TaskDispatchRecord("test-worker", command, root, now, BaseCommit: "abc123"));
                kernel.RecordTaskProcessStarted(worker.Id, task.Id, new TaskProcessRecord(
                    111, command, root, Path.Combine(root, "out"), Path.Combine(root, "err"),
                    Path.Combine(root, "exit"), now, null, null,
                    OwnedProcessIds: [111], ProcessIdentityStartedAt: now));
            }
            var admissions = 0;
            var driver = ConductorLiveGroupedGateCountTests.LandingDriver(() => admissions++);
            var polls = 0;
            var sleeps = 0;
            var handoffs = 0;
            var handoffAtSleep = -1;
            var detached = 0;
            var detachedBeforeHandoff = false;
            var published = false;
            TimeSpan? rollbackAt = null;
            var output = AsyncLocalConsoleRouter.Capture(() => new ConductorBatchLoop(
                utcNow: () => now,
                readRelaunchDrainCap: () => null,
                conductEventLogWriter: new ConductEventLogWriter(eventPath, utcNow: () => now),
                countLiveGroupedGates: () =>
                {
                    if (++polls == 1)
                        GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "Queued during drain");
                    return gateDriver.CountLiveGroupedGateAttempts(relaunchBlockingOnly: true);
                },
                detachGoalRunningDispatches: (_, goal) =>
                {
                    foreach (var task in goal.Tasks.Where(task => task.LastProcess is
                                 { IsRunning: true, WasGracefullyDetachedByConductor: false }))
                    {
                        kernel.RecordTaskProcessGracefullyDetached(goal.Id, task.Id,
                            task.LastProcess! with { WasGracefullyDetachedByConductor = true });
                        detached++;
                    }
                },
                selfRelaunchEnabled: true,
                selfRelaunch: _ =>
                {
                    if (!durable) Assert.True(published, "Legacy holder must publish before handoff.");
                    if (runningDispatch) Assert.Equal(1, detached);
                    detachedBeforeHandoff = detached > 0;
                    handoffs++;
                    handoffAtSleep = sleeps;
                    return ConductorLiveGroupedGateCountTests.Handoff();
                }).Run(kernel, driver, ConductorAutonomyPolicy.Conservative, stopPath,
                    maxIterations: 10, watchInterval: TimeSpan.FromSeconds(1), keepAliveWhenIdle: true,
                    sleepFunc: _ =>
                    {
                        Assert.Equal(0, handoffs);
                        sleeps++;
                        var events = File.ReadAllText(eventPath);
                        var rollback = events.IndexOf("LOOP_RELAUNCH_ROLLBACK", StringComparison.Ordinal);
                        if (rollback >= 0)
                        {
                            rollbackAt ??= now - started;
                            if (events.IndexOf("LOOP_RELAUNCH_DRAIN", rollback, StringComparison.Ordinal) >= 0)
                                File.WriteAllText(stopPath, "stop after deferred drain re-arms");
                        }
                        now += TimeSpan.FromMinutes(neverPublish ? 5 : 1);
                        if (!durable && !neverPublish && sleeps == 5)
                        {
                            File.WriteAllText(attempt.ResultPath, "{\"Status\":\"completed\"}");
                            File.WriteAllText(attempt.ExitCodePath, "0");
                            exits.RecordExit(attempt);
                            published = true;
                        }
                        // Bound a defective state machine by poll count, with no wall-clock pacing.
                        if (sleeps >= 20) File.WriteAllText(stopPath, "deferred drain did not re-arm");
                        return false;
                    }));
            return new(output, handoffs, handoffAtSleep, polls, detached, detachedBeforeHandoff, rollbackAt, admissions);
        }
        finally { TryDeleteDirectory(root); }
    }

    private sealed record ScenarioResult(string Output, int Handoffs, int HandoffAtSleep,
        int Polls, int Detached, bool DetachedBeforeHandoff, TimeSpan? RollbackAt, int Admissions);
}
