using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: all time/process probes are injected and every artifact/lease is test-owned.
public sealed class ConductorBatchLoopSelfRelaunchDrainLiveGateTests : ConductorBatchLoopTests
{
    public ConductorBatchLoopSelfRelaunchDrainLiveGateTests(ITestOutputHelper output) : base(output) { }

    [Xunit.Theory]
    [Xunit.InlineData("train", false, false)]
    [Xunit.InlineData("train", true, false)]
    [Xunit.InlineData("cohort", false, false)]
    [Xunit.InlineData("train", false, true)]
    public void LiveGate_Publication_RelaunchesOnNextPoll(
        string kind, bool publishExit, bool runningDispatch)
    {
        var result = RunScenario(kind, publishExit, runningDispatch);

        Assert.Equal(1, result.Handoffs);
        Assert.Equal(5, result.HandoffAtSleep);
        Assert.Equal(6, result.Polls);
        Assert.Equal(0, result.Admissions);
        Assert.All(result.DrainWaits, interval =>
            Assert.Equal(TimeSpan.FromSeconds(ConductorBatchLoop.WatchStopPollIntervalSeconds), interval));
        var drains = result.Output.Split(Environment.NewLine)
            .Where(line => line.Contains("LOOP_RELAUNCH_DRAIN", StringComparison.Ordinal)).ToArray();
        Assert.Equal(5, drains.Length);
        Assert.All(drains, line => Assert.Contains(
            $"active={(runningDispatch ? 1 : 0)} admitting=false liveGates=1", line, StringComparison.Ordinal));
        Assert.True(result.Output.LastIndexOf("LOOP_RELAUNCH_DRAIN", StringComparison.Ordinal) <
                    result.Output.IndexOf("LOOP_RELAUNCH_REBUILD", StringComparison.Ordinal));
        Assert.DoesNotContain("LOOP_RELAUNCH_ROLLBACK", result.Output, StringComparison.Ordinal);
        Assert.Equal(runningDispatch ? 1 : 0, result.Detached);
        if (runningDispatch)
        {
            Assert.True(result.Output.IndexOf("LOOP_RELAUNCH_DETACH", StringComparison.Ordinal) <
                        result.Output.LastIndexOf("LOOP_RELAUNCH_DRAIN", StringComparison.Ordinal));
        }
    }

    [Xunit.Fact]
    public void NeverPublishingGate_LiveIdleBound_RollsBackAndDefersRelaunch()
    {
        var result = RunScenario("train", publishExit: false, runningDispatch: false, neverPublish: true);

        Assert.Equal(0, result.Handoffs);
        var rollback = result.Output.IndexOf("LOOP_RELAUNCH_ROLLBACK", StringComparison.Ordinal);
        Assert.True(rollback >= 0, "The live-idle bound must emit the drain rollback.");
        Assert.Equal(1, CountOccurrences(result.Output, "LOOP_RELAUNCH_ROLLBACK"));
        Assert.Contains("phase=drain", result.Output, StringComparison.Ordinal);
        Assert.Contains($"within_{(int)DispatchRecoveryPolicy.DefaultLiveIdleTimeout.TotalMinutes}_minutes", result.Output, StringComparison.Ordinal);
        Assert.Equal(DispatchRecoveryPolicy.DefaultLiveIdleTimeout, result.RollbackAt);
        Assert.True(result.Admissions > 0, "The deferred relaunch must permit a tick before re-arming.");
        Assert.Contains("LOOP_RELAUNCH_DRAIN", result.Output[(rollback + 1)..], StringComparison.Ordinal);
        Assert.Contains("active=0 admitting=false liveGates=1", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("LOOP_RELAUNCH_REBUILD", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("LOOP_RELAUNCH_DETACH", result.Output, StringComparison.Ordinal);
    }

    private static ScenarioResult RunScenario(
        string kind, bool publishExit, bool runningDispatch, bool neverPublish = false)
    {
        var root = CreateTempDirectory("relaunch-live-gate");
        try
        {
            var stopPath = Path.Combine(root, "stop");
            var eventPath = Path.Combine(root, "events.log");
            var started = DateTimeOffset.UnixEpoch;
            var now = started;
            var coordinator = new ConductorGroupedGateAttemptCoordinator(
                Path.Combine(root, "attempts"), generationId: 111,
                isProcessAlive: pid => pid == 222, utcNow: () => now);
            var attempt = ConductorLiveGroupedGateCountTests.SaveAttempt(coordinator, root, kind);
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
            var drainWaits = new List<TimeSpan>();
            TimeSpan? rollbackAt = null;
            var output = AsyncLocalConsoleRouter.Capture(() => new ConductorBatchLoop(
                utcNow: () => now,
                readRelaunchDrainCap: () => null,
                conductEventLogWriter: new ConductEventLogWriter(eventPath, utcNow: () => now),
                countLiveGroupedGates: () =>
                {
                    if (++polls == 1)
                        GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "Queued during drain");
                    return ConductorLiveGroupedGateCount.Count(coordinator);
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
                    handoffs++;
                    handoffAtSleep = sleeps;
                    return ConductorLiveGroupedGateCountTests.Handoff();
                }).Run(kernel, driver, ConductorAutonomyPolicy.Conservative, stopPath,
                    maxIterations: 10, watchInterval: TimeSpan.FromSeconds(1), keepAliveWhenIdle: true,
                    sleepFunc: interval =>
                    {
                        sleeps++;
                        var events = File.ReadAllText(eventPath);
                        var rollback = events.IndexOf("LOOP_RELAUNCH_ROLLBACK", StringComparison.Ordinal);
                        if (rollback >= 0)
                        {
                            rollbackAt ??= now - started;
                            if (events.IndexOf("LOOP_RELAUNCH_DRAIN", rollback, StringComparison.Ordinal) >= 0)
                                File.WriteAllText(stopPath, "stop after deferred drain re-arms");
                        }
                        if (rollback < 0) drainWaits.Add(interval);
                        now += TimeSpan.FromMinutes(neverPublish ? 5 : 1);
                        if (!neverPublish && sleeps == 5)
                            File.WriteAllText(publishExit ? attempt.ExitCodePath : attempt.ResultPath,
                                publishExit ? "0" : "{\"Verdict\":\"passed\"}");
                        // A broken hold/defer path must fail instead of leaving the acceptance host hung.
                        if (sleeps >= 20) File.WriteAllText(stopPath, "deferred drain did not re-arm");
                        return false;
                    }));
            return new(output, handoffs, handoffAtSleep, polls, detached, rollbackAt, admissions, drainWaits);
        }
        finally { TryDeleteDirectory(root); }
    }

    private sealed record ScenarioResult(
        string Output, int Handoffs, int HandoffAtSleep, int Polls, int Detached, TimeSpan? RollbackAt,
        int Admissions, IReadOnlyList<TimeSpan> DrainWaits);
}
