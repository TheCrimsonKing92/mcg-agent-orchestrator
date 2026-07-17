using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Dashboard.Api;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

/// <summary>
/// Unit tests for ConductorBatchLoop covering: loop scheduling (cap, hold/advance),
/// auto-retry-recover, auto-retry-escalate, and kill-switch.
/// END-marker parsing tests live in ChaosGateTests.
/// </summary>
[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static IReadOnlyList<AgentDefinition> DefaultAgents() => AgentCatalog.Default().Agents;

    private static (AgentOrchestratorKernel Kernel, Goal Goal) SimpleGoal(string objective = "Test goal")
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), objective);
        return (kernel, goal);
    }

    private static void PassVerification(AgentOrchestratorKernel kernel, Goal goal, TaskSpec task)
    {
        PassVerificationAt(kernel, goal, task, DateTimeOffset.UtcNow);
    }

    private static void PassVerificationAt(AgentOrchestratorKernel kernel, Goal goal, TaskSpec task, DateTimeOffset completedAt)
    {
        var dispatch = new TaskDispatchRecord("test-worker", "test.exe", "C:\\tmp", DateTimeOffset.UtcNow);
        kernel.RecordTaskDispatch(goal.Id, task.Id, dispatch);
        var verification = new TaskVerificationRecord("test.exe", "C:\\tmp", 0, "ok", "", completedAt);
        kernel.RecordTaskVerification(goal.Id, task.Id, verification);
    }

    private static Exception SqliteBusy() =>
        new InvalidOperationException("SQLite Error 5: 'database is locked'.");

    private static GoalWorktreeRebaseResult DefaultRebaseSuccess() =>
        new(GoalWorktreeRebaseStatus.AlreadyFastForwardable, "goal/test", "OK", [], null);

    private static ConductorDriver MakeDriver(
        Func<Goal, GoalLifecycleFacts>? getFacts = null,
        Func<int>? getRunningCount = null,
        Func<Goal, string>? createWorkspace = null,
        Func<Goal, DispatchStartOutcome>? dispatchAndStart = null,
        Func<Goal, bool>? runAcceptance = null,
        Func<Goal, int?, AcceptanceVerificationSummary>? runAcceptanceWithSlot = null,
        Func<Goal, GoalWorktreeRebaseResult>? rebaseOntoMain = null,
        Func<Goal, LandingResult>? land = null,
        Action<Goal>? record = null,
        Func<Goal, GoalWorktreeRemoveResult>? cleanup = null,
        Action<Goal, GoalLifecycleState, string>? writeEscalation = null,
        Func<Goal, ChangeRiskTier?>? classifyRisk = null,
        Func<GoalId, TaskId, string, TaskSpec>? retryTask = null,
        Func<GoalId, TaskId, IReadOnlyList<string>, int>? recordCriterionRetryFeedback = null,
        Action<Goal, string>? recordMissingBranchRetirement = null,
        Func<Goal, IReadOnlyList<string>>? getLandingFileScopes = null,
        ConductorParallelAcceptanceAttemptCoordinator? parallelAcceptanceAttemptCoordinator = null) =>
        new ConductorDriver(
            getFacts ?? (_ => GoalLifecycleFacts.None),
            getRunningCount ?? (() => 0),
            createWorkspace ?? (_ => "/tmp/workspace"),
            dispatchAndStart ?? (_ => DispatchStartOutcome.Started()),
            null,
            null,
            goal => (runAcceptance ?? (_ => true))(goal)
                ? AcceptanceVerificationSummary.PassedWithNoUnmetCriteria
                : AcceptanceVerificationSummary.Failed,
            null,
            retryTask,
            null,
            recordCriterionRetryFeedback,
            null,
            rebaseOntoMain ?? (_ => DefaultRebaseSuccess()),
            land is null
                ? ((g, _) => new LandingResult(g.Id.Value, g.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "Landed"))
                : ((g, _) => land(g)),
            null,
            record ?? (_ => { }),
            cleanup ?? (_ => new GoalWorktreeRemoveResult("Workspace cleaned up.", null, [], null)),
            writeEscalation ?? ((_, _, _) => { }),
            classifyRisk ?? (_ => null),
            recordMissingBranchRetirement: recordMissingBranchRetirement,
            getLandingFileScopes: getLandingFileScopes,
            runAcceptanceVerificationWithSlot: runAcceptanceWithSlot,
            parallelAcceptanceAttemptCoordinator: parallelAcceptanceAttemptCoordinator);

    // Returns a path to a stop file that does NOT exist yet.
    private static string NoStopPath() =>
        Path.Combine(Path.GetTempPath(), $"conduct-stop-{Guid.NewGuid():N}.txt");

    private static Goal CreateVerifiedSimpleGoal(AgentOrchestratorKernel kernel, string objective)
    {
        var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), objective);
        PassVerification(kernel, goal, goal.Tasks.Single());
        return goal;
    }

    [Xunit.Fact(DisplayName = "BatchLoop_runs_disjoint_gate_ready_acceptance_concurrently_on_distinct_slots")]
    public void BatchLoopRunsDisjointGateReadyAcceptanceConcurrentlyOnDistinctSlots()
    {
        var kernel = new AgentOrchestratorKernel();
        var goalA = CreateVerifiedSimpleGoal(kernel, "Update src/Mcg.AgentOrchestrator.App/Orchestration/A.cs");
        var goalB = CreateVerifiedSimpleGoal(kernel, "Update src/Mcg.AgentOrchestrator.App/Orchestration/B.cs");
        using var release = new ManualResetEventSlim(false);
        using var bothStarted = new CountdownEvent(2);
        using var bothFinished = new CountdownEvent(2);
        var running = 0;
        var maxRunning = 0;
        var slots = new ConcurrentDictionary<string, int?>();
        var rebaseCounts = new ConcurrentDictionary<string, int>();
        var landOrder = new List<string>();
        var landed = new HashSet<string>(StringComparer.Ordinal);
        object gate = new();
        var attemptRoot = CreateTempDirectory("mcg-conductor-acceptance-attempts");
        Action waitForAttempts = () => { };

        try
        {
            var driver = MakeDriver(
                getFacts: goal => landed.Contains(goal.Id.Value)
                    ? new GoalLifecycleFacts(WorkspaceExists: true, IsMerged: true, IsRecorded: true, IsCleanedUp: true)
                    : new GoalLifecycleFacts(WorkspaceExists: true),
                rebaseOntoMain: goal =>
                {
                    rebaseCounts.AddOrUpdate(goal.Id.Value, 1, (_, count) => count + 1);
                    return DefaultRebaseSuccess();
                },
                runAcceptanceWithSlot: (goal, slot) =>
                {
                    slots[goal.Id.Value] = slot;
                    lock (gate)
                    {
                        running++;
                        maxRunning = Math.Max(maxRunning, running);
                    }

                    bothStarted.Signal();
                    Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
                    lock (gate)
                    {
                        running--;
                    }

                    bothFinished.Signal();
                    return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria;
                },
                land: goal =>
                {
                    lock (landOrder)
                    {
                        landOrder.Add(goal.Id.Value);
                        landed.Add(goal.Id.Value);
                    }

                    return new LandingResult(goal.Id.Value, goal.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "ok");
                },
                getLandingFileScopes: goal => goal.Id == goalA.Id
                    ? ["src/Mcg.AgentOrchestrator.App/Orchestration/A.cs"]
                    : ["src/Mcg.AgentOrchestrator.App/Orchestration/B.cs"],
                parallelAcceptanceAttemptCoordinator: ThreadedAcceptanceAttemptCoordinator(attemptRoot, out waitForAttempts));

            BatchTickSummary? startTick = null;
            var startClock = Stopwatch.StartNew();
            var startSummary = new ConductorBatchLoop().Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 1,
                onTick: t => startTick = t);
            startClock.Stop();

            Assert.True(startClock.Elapsed < TimeSpan.FromSeconds(1), $"background acceptance start tick took {startClock.Elapsed}.");
            Assert.Equal(0, startSummary.Advanced);
            Assert.Equal(2, startSummary.Held);
            Assert.True(bothStarted.Wait(TimeSpan.FromSeconds(5)));
            release.Set();
            Assert.True(bothFinished.Wait(TimeSpan.FromSeconds(5)));

            BatchTickSummary? reconcileTick = null;
            var totalAdvanced = 0;
            for (var tick = 0; tick < 8 && totalAdvanced < 2; tick++)
            {
                var reconcileSummary = new ConductorBatchLoop().Run(
                    kernel,
                    driver,
                    ConductorAutonomyPolicy.Conservative,
                    NoStopPath(),
                    maxIterations: 1,
                    onTick: t => reconcileTick = t);
                totalAdvanced += reconcileSummary.Advanced;
                Thread.Sleep(50);
            }

            Assert.Equal(2, totalAdvanced);
            Assert.Equal(2, maxRunning);
            Assert.Equal(2, slots.Values.Where(slot => slot.HasValue).Select(slot => slot!.Value).Distinct().Count());
            Assert.Equal(2, rebaseCounts[goalA.Id.Value]);
            Assert.Equal(2, rebaseCounts[goalB.Id.Value]);
            Assert.Equal(2, landOrder.Count);
            Assert.Contains(goalA.Id.Value, landOrder);
            Assert.Contains(goalB.Id.Value, landOrder);
            Assert.All(
                new[] { ReadLatestAttempt(attemptRoot, goalA), ReadLatestAttempt(attemptRoot, goalB) },
                attempt =>
                {
                    Assert.True(attempt.OwnerProcessId > 0);
                    using var heartbeat = JsonDocument.Parse(File.ReadAllText(attempt.HeartbeatPath));
                    Assert.Equal(attempt.OwnerProcessId, heartbeat.RootElement.GetProperty("childPid").GetInt32());
                    Assert.Contains(
                        heartbeat.RootElement.GetProperty("ownedPids").EnumerateArray(),
                        pid => pid.GetInt32() == attempt.OwnerProcessId);
                });
            Assert.Contains(startTick!.ProgressLines!, line => line.Contains("ACCEPTANCE", StringComparison.Ordinal) && line.Contains("result=started", StringComparison.Ordinal));
            Assert.Contains(reconcileTick!.ProgressLines!, line => line.Contains("ACCEPTANCE", StringComparison.Ordinal) && line.Contains("result=passed", StringComparison.Ordinal));
        }
        finally
        {
            waitForAttempts();
            TryDeleteDirectory(attemptRoot);
        }
    }

    [Xunit.Fact(DisplayName = "BatchLoop_serializes_overlapping_gate_ready_acceptance")]
    public void BatchLoopSerializesOverlappingGateReadyAcceptance()
    {
        var kernel = new AgentOrchestratorKernel();
        var goalA = CreateVerifiedSimpleGoal(kernel, "Update src/Mcg.AgentOrchestrator.App/Orchestration/Same.cs");
        var goalB = CreateVerifiedSimpleGoal(kernel, "Also update src/Mcg.AgentOrchestrator.App/Orchestration/Same.cs");
        var running = 0;
        var overlapped = false;
        var slots = new ConcurrentQueue<int?>();
        var landed = new HashSet<string>(StringComparer.Ordinal);
        var attemptRoot = CreateTempDirectory("mcg-conductor-acceptance-attempts");
        Action waitForAttempts = () => { };

        try
        {
            var driver = MakeDriver(
                getFacts: goal => landed.Contains(goal.Id.Value)
                    ? new GoalLifecycleFacts(WorkspaceExists: true, IsMerged: true, IsRecorded: true, IsCleanedUp: true)
                    : new GoalLifecycleFacts(WorkspaceExists: true),
                runAcceptanceWithSlot: (_, slot) =>
                {
                    slots.Enqueue(slot);
                    if (Interlocked.Increment(ref running) > 1)
                    {
                        overlapped = true;
                    }

                    Thread.Sleep(25);
                    Interlocked.Decrement(ref running);
                    return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria;
                },
                land: goal =>
                {
                    landed.Add(goal.Id.Value);
                    return new LandingResult(goal.Id.Value, goal.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "ok");
                },
                getLandingFileScopes: _ => ["src/Mcg.AgentOrchestrator.App/Orchestration/Same.cs"],
                parallelAcceptanceAttemptCoordinator: ThreadedAcceptanceAttemptCoordinator(attemptRoot, out waitForAttempts));

            var totalAdvanced = 0;
            var totalHeld = 0;
            for (var tick = 0; tick < 8 && totalAdvanced < 2; tick++)
            {
                var summary = new ConductorBatchLoop().Run(
                    kernel,
                    driver,
                    ConductorAutonomyPolicy.Conservative,
                    NoStopPath(),
                    maxIterations: 1);
                totalAdvanced += summary.Advanced;
                totalHeld += summary.Held;
                Thread.Sleep(50);
            }

            Assert.Equal(2, totalAdvanced);
            Assert.True(totalHeld >= 2);
            Assert.False(overlapped);
            Assert.Equal(2, slots.Count);
            Assert.All(slots, slot => Assert.True(slot.HasValue));
        }
        finally
        {
            waitForAttempts();
            TryDeleteDirectory(attemptRoot);
        }
    }

    [Xunit.Fact(DisplayName = "BatchLoop_failing_parallel_acceptance_does_not_block_sibling_landing")]
    public void BatchLoopFailingParallelAcceptanceDoesNotBlockSiblingLanding()
    {
        var kernel = new AgentOrchestratorKernel();
        var failing = CreateVerifiedSimpleGoal(kernel, "Update src/Mcg.AgentOrchestrator.App/Orchestration/Failing.cs");
        var passing = CreateVerifiedSimpleGoal(kernel, "Update src/Mcg.AgentOrchestrator.App/Orchestration/Passing.cs");
        var landed = new List<string>();

        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            runAcceptanceWithSlot: (goal, _) => goal.Id == failing.Id
                ? AcceptanceVerificationSummary.Failed
                : AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
            land: goal =>
            {
                landed.Add(goal.Id.Value);
                return new LandingResult(goal.Id.Value, goal.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "ok");
            },
            writeEscalation: (_, _, _) => { },
            getLandingFileScopes: goal => goal.Id == failing.Id
                ? ["src/Mcg.AgentOrchestrator.App/Orchestration/Failing.cs"]
                : ["src/Mcg.AgentOrchestrator.App/Orchestration/Passing.cs"]);

        var summary = new ConductorBatchLoop().Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 1,
            maxVerifyRetries: 0);

        Assert.Equal(1, summary.Advanced);
        Assert.Equal(1, summary.Escalated);
        Assert.Equal([passing.Id.Value], landed);
    }

    [Xunit.Fact(DisplayName = "BatchLoop_slot_path_unmet_acceptance_retries_with_concrete_feedback")]
    public void BatchLoopSlotPathUnmetAcceptanceRetriesWithConcreteFeedback()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateVerifiedSimpleGoal(kernel, "Update src/Mcg.AgentOrchestrator.App/Orchestration/RetryEvidence.cs");
        var passing = CreateVerifiedSimpleGoal(kernel, "Update src/Mcg.AgentOrchestrator.App/Orchestration/PassingRetryEvidence.cs");
        var task = goal.Tasks.Single();
        string? retryMessage = null;
        int? observedSlot = null;
        var unmet = new AcceptanceCheckResult(
            "command-exit dotnet test --filter SlotRetryEvidence",
            false,
            1,
            string.Join(Environment.NewLine,
            [
                "src/RetryEvidence.cs(4,5): error CS0103: The name 'missing' does not exist in the current context",
                "[xUnit.net 00:00:02.00]     Mcg.AgentOrchestrator.Tests.SlotRetryEvidenceTests.ReportsFailingTest [FAIL]",
            ]),
            ResultSummary: "slot acceptance failed");

        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            runAcceptanceWithSlot: (candidate, slot) =>
            {
                if (candidate.Id == goal.Id)
                {
                    observedSlot = slot;
                    return new AcceptanceVerificationSummary(true, [unmet]);
                }

                return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria;
            },
            retryTask: (goalId, taskId, message) =>
            {
                retryMessage = message;
                return kernel.RetryTask(goalId, taskId, message);
            },
            recordCriterionRetryFeedback: kernel.RecordCriterionRetryFeedback,
            getLandingFileScopes: candidate => candidate.Id == goal.Id
                ? ["src/Mcg.AgentOrchestrator.App/Orchestration/RetryEvidence.cs"]
                : ["src/Mcg.AgentOrchestrator.App/Orchestration/PassingRetryEvidence.cs"]);

        var summary = new ConductorBatchLoop().Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 1);
        var brief = kernel.BuildTaskBrief(goal.Id, task.Id);

        Assert.Equal(2, summary.Advanced);
        Assert.NotNull(observedSlot);
        Assert.Equal(WorkTaskStatus.Assigned, task.Status);
        Assert.Equal(WorkTaskStatus.Completed, passing.Tasks.Single().Status);
        Assert.Contains("src/RetryEvidence.cs(4,5): error CS0103", retryMessage!, StringComparison.Ordinal);
        Assert.Contains("SlotRetryEvidenceTests.ReportsFailingTest [FAIL]", brief.Content, StringComparison.Ordinal);
        Assert.Contains("failed check: command-exit dotnet test --filter SlotRetryEvidence (exit code 1)", brief.Content, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "BatchLoop_parallel_acceptance_slot_exhaustion_queues_extra_goal")]
    public void BatchLoopParallelAcceptanceSlotExhaustionQueuesExtraGoal()
    {
        var kernel = new AgentOrchestratorKernel();
        var goals = Enumerable.Range(0, DotnetBuildEnvironmentManager.StableSlotCount + 1)
            .Select(index => CreateVerifiedSimpleGoal(
                kernel,
                $"Update src/Mcg.AgentOrchestrator.App/Orchestration/Slot{index}.cs"))
            .ToArray();
        using var release = new ManualResetEventSlim(false);
        using var firstWaveStarted = new CountdownEvent(DotnetBuildEnvironmentManager.StableSlotCount);
        var running = 0;
        var maxRunning = 0;
        var slots = new ConcurrentQueue<int?>();
        object gate = new();
        var landed = new HashSet<string>(StringComparer.Ordinal);
        var attemptRoot = CreateTempDirectory("mcg-conductor-acceptance-attempts");
        Action waitForAttempts = () => { };

        try
        {
            var driver = MakeDriver(
                getFacts: goal => landed.Contains(goal.Id.Value)
                    ? new GoalLifecycleFacts(WorkspaceExists: true, IsMerged: true, IsRecorded: true, IsCleanedUp: true)
                    : new GoalLifecycleFacts(WorkspaceExists: true),
                runAcceptanceWithSlot: (_, slot) =>
                {
                    slots.Enqueue(slot);
                    lock (gate)
                    {
                        running++;
                        maxRunning = Math.Max(maxRunning, running);
                    }

                    if (slot.HasValue && !release.IsSet)
                    {
                        firstWaveStarted.Signal();
                        Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
                    }

                    lock (gate)
                    {
                        running--;
                    }

                    return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria;
                },
                getLandingFileScopes: goal =>
                {
                    var index = Array.FindIndex(goals, candidate => candidate.Id == goal.Id);
                    return [$"src/Mcg.AgentOrchestrator.App/Orchestration/Slot{index}.cs"];
                },
                land: goal =>
                {
                    landed.Add(goal.Id.Value);
                    return new LandingResult(goal.Id.Value, goal.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "ok");
                },
                parallelAcceptanceAttemptCoordinator: ThreadedAcceptanceAttemptCoordinator(attemptRoot, out waitForAttempts));

            BatchTickSummary? firstTick = null;
            var firstSummary = new ConductorBatchLoop().Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 1,
                onTick: t => firstTick = t);

            Assert.Equal(0, firstSummary.Advanced);
            Assert.Equal(DotnetBuildEnvironmentManager.StableSlotCount + 1, firstSummary.Held);
            Assert.True(firstWaveStarted.Wait(TimeSpan.FromSeconds(5)));
            release.Set();
            Thread.Sleep(100);

            var totalAdvanced = 0;
            var deferredGoalStartedOnFirstFreeTick = false;
            for (var tick = 0; tick < 8 && totalAdvanced < DotnetBuildEnvironmentManager.StableSlotCount + 1; tick++)
            {
                BatchTickSummary? tickSummary = null;
                var summary = new ConductorBatchLoop().Run(
                    kernel,
                    driver,
                    ConductorAutonomyPolicy.Conservative,
                    NoStopPath(),
                    maxIterations: 1,
                    onTick: t => tickSummary = t);
                totalAdvanced += summary.Advanced;
                if (tick == 0)
                {
                    var deferredPrefix = goals[^1].Id.Value[..8];
                    deferredGoalStartedOnFirstFreeTick = tickSummary?.ProgressLines?.Any(line =>
                        line.Contains($"ACCEPTANCE goal={deferredPrefix}", StringComparison.Ordinal) &&
                        line.Contains("result=started", StringComparison.Ordinal)) == true;
                }

                Thread.Sleep(50);
            }

            Assert.Equal(DotnetBuildEnvironmentManager.StableSlotCount + 1, totalAdvanced);
            Assert.True(deferredGoalStartedOnFirstFreeTick);
            Assert.Equal(DotnetBuildEnvironmentManager.StableSlotCount, maxRunning);
            Assert.Equal(DotnetBuildEnvironmentManager.StableSlotCount, slots.Where(slot => slot.HasValue).Select(slot => slot!.Value).Distinct().Count());
            Assert.DoesNotContain(slots, slot => !slot.HasValue);
            Assert.Contains(firstTick!.ProgressLines!, line =>
                line.Contains("ADMISSION", StringComparison.Ordinal) &&
                line.Contains("reason=parallel-acceptance-slot-cap", StringComparison.Ordinal));
            Assert.DoesNotContain(firstTick.ProgressLines!, line =>
                line.Contains("reason=reserved-gate-slot", StringComparison.Ordinal));
        }
        finally
        {
            waitForAttempts();
            TryDeleteDirectory(attemptRoot);
        }
    }

    [Xunit.Fact(DisplayName = "BatchLoop_slots_busy_gate_retries_and_lands_on_later_tick")]
    public void BatchLoopSlotsBusyGateRetriesAndLandsOnLaterTick()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateVerifiedSimpleGoal(kernel, "Update src/Mcg.AgentOrchestrator.App/Orchestration/SlotsBusy.cs");
        var attempts = 0;
        var escalations = 0;

        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            runAcceptanceWithSlot: (_, _) =>
            {
                attempts++;
                if (attempts == 1)
                {
                    throw new DotnetBuildSlotsBusyException(new DotnetBuildLeaseAcquisition.SlotsBusy(
                        "goal-slots-busy",
                        Enumerable.Range(0, DotnetBuildEnvironmentManager.StableSlotCount)
                            .Select(slot => new DotnetBuildStableSlotWait(slot, 1000 + slot))
                            .ToArray()));
                }

                return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria;
            },
            writeEscalation: (_, _, _) => escalations++);

        var ticks = new List<BatchTickSummary>();
        var summary = new ConductorBatchLoop().Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 2,
            watchInterval: TimeSpan.FromMilliseconds(1),
            sleepFunc: _ => false,
            onTick: ticks.Add);

        Assert.Equal(2, attempts);
        Assert.Equal(1, summary.Held);
        Assert.Equal(1, summary.Advanced);
        Assert.Equal(0, summary.Escalated);
        Assert.Equal(0, escalations);
        Assert.Contains(ticks[0].ProgressLines!, line =>
            line.Contains("GOAL", StringComparison.Ordinal) &&
            line.Contains("result=held", StringComparison.Ordinal));
        Assert.Contains(ticks[1].ProgressLines!, line =>
            line.Contains("GOAL", StringComparison.Ordinal) &&
            line.Contains("result=executed", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "BatchLoop_build_lock_blocked_gate_retries_and_lands_on_later_tick")]
    public void BatchLoopBuildLockBlockedGateRetriesAndLandsOnLaterTick()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateVerifiedSimpleGoal(kernel, "Update src/Mcg.AgentOrchestrator.App/Orchestration/BuildLock.cs");
        var attempts = 0;
        var escalations = 0;

        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            runAcceptanceWithSlot: (_, _) =>
            {
                attempts++;
                if (attempts == 1)
                {
                    throw new BuildLockBlockedException(new BuildLockAttribution(
                        @"C:\mcg-dotnet-isolated\slots\slot-0\artifacts\bin\Mcg.AgentOrchestrator.Core.dll",
                        [new BuildLockHolder(null, "unknown-probe-timeout", null, false)],
                        "handle64-timeout",
                        "acceptance-output",
                        "classify-build-lock"));
                }

                return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria;
            },
            writeEscalation: (_, _, _) => escalations++);

        var ticks = new List<BatchTickSummary>();
        var summary = new ConductorBatchLoop().Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 2,
            watchInterval: TimeSpan.FromMilliseconds(1),
            sleepFunc: _ => false,
            onTick: ticks.Add);

        Assert.Equal(2, attempts);
        Assert.Equal(1, summary.Held);
        Assert.Equal(1, summary.Advanced);
        Assert.Equal(0, summary.Escalated);
        Assert.Equal(0, escalations);
        Assert.Contains(ticks[0].ProgressLines!, line =>
            line.Contains("GOAL", StringComparison.Ordinal) &&
            line.Contains("result=held", StringComparison.Ordinal));
        Assert.Contains(ticks[1].ProgressLines!, line =>
            line.Contains("GOAL", StringComparison.Ordinal) &&
            line.Contains("result=executed", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "ParallelAcceptance_stale_running_attempt_cannot_overwrite_terminal_stale_outcome")]
    public void ParallelAcceptanceStaleRunningAttemptCannotOverwriteTerminalStaleOutcome()
    {
        var (_, goal) = SimpleGoal("Update src/Mcg.AgentOrchestrator.App/Orchestration/Stale.cs");
        var attemptRoot = CreateTempDirectory("mcg-conductor-acceptance-attempts");
        var launches = new ConcurrentDictionary<string, ConductorParallelAcceptanceOwnedProcessLaunch>();
        var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
            attemptRoot,
            isProcessAlive: _ => true,
            launchOwnedProcess: launch =>
            {
                launches[launch.Attempt.AttemptId] = launch;
                return new ConductorParallelAcceptanceOwnedProcessLaunchResult(7001 + launches.Count);
            });
        var candidateA = ConductorParallelAcceptanceCandidate.Create(goal, 0, ["src/Stale.cs"], "branch-a", "main-a");
        var candidateB = ConductorParallelAcceptanceCandidate.Create(goal, 0, ["src/Stale.cs"], "branch-b", "main-a");

        try
        {
            var first = coordinator.Evaluate(candidateA, ConductorAutonomyPolicy.Conservative, PassingRun);
            var second = coordinator.Evaluate(candidateB, ConductorAutonomyPolicy.Conservative, PassingRun);
            launches[first.Attempt.AttemptId].ExecuteInCurrentProcess(first.Attempt.OwnerProcessId);
            var moved = coordinator.Evaluate(candidateB, ConductorAutonomyPolicy.Conservative, PassingRun);
            var stale = ReadAttempt(first.Attempt.MetadataPath);

            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Started, first.Kind);
            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Running, second.Kind);
            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Started, moved.Kind);
            Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.StaleCandidate, stale.Outcome);
            Assert.Contains("candidate branch/main SHA moved", stale.Detail, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(attemptRoot);
        }
    }

    [Xunit.Fact(DisplayName = "ParallelAcceptance_rebased_attempt_updates_candidate_key_before_reconciliation")]
    public void ParallelAcceptanceRebasedAttemptUpdatesCandidateKeyBeforeReconciliation()
    {
        var (_, goal) = SimpleGoal("Update src/Mcg.AgentOrchestrator.App/Orchestration/Rebased.cs");
        var attemptRoot = CreateTempDirectory("mcg-conductor-acceptance-attempts");
        var launches = new ConcurrentDictionary<string, ConductorParallelAcceptanceOwnedProcessLaunch>();
        var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
            attemptRoot,
            isProcessAlive: _ => true,
            launchOwnedProcess: launch =>
            {
                launches[launch.Attempt.AttemptId] = launch;
                return new ConductorParallelAcceptanceOwnedProcessLaunchResult(7005 + launches.Count);
            });
        var preRebase = ConductorParallelAcceptanceCandidate.Create(goal, 0, ["src/Rebased.cs"], "branch-before", "main-a");
        var postRebase = ConductorParallelAcceptanceCandidate.Create(goal, 0, ["src/Rebased.cs"], "branch-after", "main-a");

        try
        {
            var started = coordinator.Evaluate(
                preRebase,
                ConductorAutonomyPolicy.Conservative,
                (candidate, _) => ConductorParallelAcceptanceRunResult.Accepted(
                    postRebase,
                    AcceptanceVerificationSummary.PassedWithNoUnmetCriteria));
            launches[started.Attempt.AttemptId].ExecuteInCurrentProcess(started.Attempt.OwnerProcessId);
            var completed = coordinator.Evaluate(postRebase, ConductorAutonomyPolicy.Conservative, PassingRun);
            var persisted = ReadAttempt(started.Attempt.MetadataPath);

            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Completed, completed.Kind);
            Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Passed, completed.Attempt.Outcome);
            Assert.Equal("branch-after", completed.Attempt.BranchHeadSha);
            Assert.Equal("main-a", completed.Attempt.MainHeadSha);
            Assert.Equal("branch-after", completed.Run!.Candidate.BranchHeadSha);
            Assert.Null(persisted.ReconciledAt);
            Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Passed, persisted.Outcome);
            Assert.Equal("branch-after", persisted.BranchHeadSha);
        }
        finally
        {
            TryDeleteDirectory(attemptRoot);
        }
    }

    [Xunit.Fact(DisplayName = "ParallelAcceptance_stale_terminal_without_run_attempt_does_not_block_moved_candidate")]
    public void ParallelAcceptanceStaleTerminalWithoutRunAttemptDoesNotBlockMovedCandidate()
    {
        var (_, goal) = SimpleGoal("Update src/Mcg.AgentOrchestrator.App/Orchestration/StaleTerminal.cs");
        var attemptRoot = CreateTempDirectory("mcg-conductor-acceptance-attempts");
        var launchAttempts = 0;
        var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
            attemptRoot,
            launchOwnedProcess: _ =>
            {
                launchAttempts++;
                if (launchAttempts == 1)
                {
                    throw new InvalidOperationException("spawn failed");
                }

                return new ConductorParallelAcceptanceOwnedProcessLaunchResult(7010);
            });
        var candidateA = ConductorParallelAcceptanceCandidate.Create(
            goal,
            0,
            ["src/StaleTerminal.cs"],
            "branch-a",
            "main-a");
        var candidateB = ConductorParallelAcceptanceCandidate.Create(
            goal,
            0,
            ["src/StaleTerminal.cs"],
            "branch-b",
            "main-a");

        try
        {
            var failed = coordinator.Evaluate(candidateA, ConductorAutonomyPolicy.Conservative, PassingRun);
            var moved = coordinator.Evaluate(candidateB, ConductorAutonomyPolicy.Conservative, PassingRun);
            var stale = ReadAttempt(failed.Attempt.MetadataPath);

            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.TerminalWithoutRun, failed.Kind);
            Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.LaunchFailed, failed.Attempt.Outcome);
            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Started, moved.Kind);
            Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.StaleCandidate, stale.Outcome);
            Assert.Contains("candidate branch/main SHA moved", stale.Detail, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(attemptRoot);
        }
    }

    [Xunit.Fact(DisplayName = "ParallelAcceptance_running_attempt_without_live_process_records_process_died")]
    public void ParallelAcceptanceRunningAttemptWithoutLiveProcessRecordsProcessDied()
    {
        var (_, goal) = SimpleGoal("Update src/Mcg.AgentOrchestrator.App/Orchestration/Dead.cs");
        var attemptRoot = CreateTempDirectory("mcg-conductor-acceptance-attempts");
        var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
            attemptRoot,
            isProcessAlive: _ => false,
            launchOwnedProcess: _ => new ConductorParallelAcceptanceOwnedProcessLaunchResult(7010));
        var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, ["src/Dead.cs"], "branch", "main");

        try
        {
            coordinator.Evaluate(candidate, ConductorAutonomyPolicy.Conservative, PassingRun);
            var terminal = coordinator.Evaluate(candidate, ConductorAutonomyPolicy.Conservative, PassingRun);

            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.TerminalWithoutRun, terminal.Kind);
            Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.ProcessDied, terminal.Attempt.Outcome);
        }
        finally
        {
            TryDeleteDirectory(attemptRoot);
        }
    }

    [Xunit.Fact(DisplayName = "ParallelAcceptance_reconciliation_preserves_typed_terminal_outcome")]
    public void ParallelAcceptanceReconciliationPreservesTypedTerminalOutcome()
    {
        var (_, goal) = SimpleGoal("Update src/Mcg.AgentOrchestrator.App/Orchestration/Reconciled.cs");
        var attemptRoot = CreateTempDirectory("mcg-conductor-acceptance-attempts");
        var launches = new ConcurrentDictionary<string, ConductorParallelAcceptanceOwnedProcessLaunch>();
        var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
            attemptRoot,
            isProcessAlive: _ => true,
            launchOwnedProcess: launch =>
            {
                launches[launch.Attempt.AttemptId] = launch;
                return new ConductorParallelAcceptanceOwnedProcessLaunchResult(7025 + launches.Count);
            });
        var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, ["src/Reconciled.cs"], "branch", "main");

        try
        {
            var started = coordinator.Evaluate(candidate, ConductorAutonomyPolicy.Conservative, PassingRun);
            launches[started.Attempt.AttemptId].ExecuteInCurrentProcess(started.Attempt.OwnerProcessId);
            var completed = coordinator.Evaluate(candidate, ConductorAutonomyPolicy.Conservative, PassingRun);
            coordinator.MarkReconciled(completed.Attempt);
            var reconciled = ReadAttempt(completed.Attempt.MetadataPath);
            var next = coordinator.Evaluate(candidate, ConductorAutonomyPolicy.Conservative, PassingRun);

            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Completed, completed.Kind);
            Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Passed, reconciled.Outcome);
            Assert.NotNull(reconciled.ReconciledAt);
            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Started, next.Kind);
            Assert.NotEqual(completed.Attempt.AttemptId, next.Attempt.AttemptId);
        }
        finally
        {
            TryDeleteDirectory(attemptRoot);
        }
    }

    [Xunit.Fact(DisplayName = "ParallelAcceptance_corrupt_result_artifact_records_corrupt_outcome")]
    public void ParallelAcceptanceCorruptResultArtifactRecordsCorruptOutcome()
    {
        var (_, goal) = SimpleGoal("Update src/Mcg.AgentOrchestrator.App/Orchestration/Corrupt.cs");
        var attemptRoot = CreateTempDirectory("mcg-conductor-acceptance-attempts");
        var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
            attemptRoot,
            isProcessAlive: _ => false,
            launchOwnedProcess: _ => new ConductorParallelAcceptanceOwnedProcessLaunchResult(7020));
        var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, ["src/Corrupt.cs"], "branch", "main");

        try
        {
            var started = coordinator.Evaluate(candidate, ConductorAutonomyPolicy.Conservative, PassingRun);
            File.WriteAllText(started.Attempt.ResultPath, "{ not-json");
            var terminal = coordinator.Evaluate(candidate, ConductorAutonomyPolicy.Conservative, PassingRun);

            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.TerminalWithoutRun, terminal.Kind);
            Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.CorruptArtifacts, terminal.Attempt.Outcome);
        }
        finally
        {
            TryDeleteDirectory(attemptRoot);
        }
    }

    [Xunit.Fact(DisplayName = "ParallelAcceptance_launch_failure_records_typed_terminal_outcome")]
    public void ParallelAcceptanceLaunchFailureRecordsTypedTerminalOutcome()
    {
        var (_, goal) = SimpleGoal("Update src/Mcg.AgentOrchestrator.App/Orchestration/LaunchFailed.cs");
        var attemptRoot = CreateTempDirectory("mcg-conductor-acceptance-attempts");
        var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
            attemptRoot,
            launchOwnedProcess: _ => throw new InvalidOperationException("spawn failed"));
        var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, ["src/LaunchFailed.cs"], "branch", "main");

        try
        {
            var terminal = coordinator.Evaluate(candidate, ConductorAutonomyPolicy.Conservative, PassingRun);

            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.TerminalWithoutRun, terminal.Kind);
            Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.LaunchFailed, terminal.Attempt.Outcome);
            Assert.Contains("spawn failed", terminal.Attempt.Detail, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(attemptRoot);
        }
    }

    [Xunit.Fact(DisplayName = "ParallelAcceptance_durable_passed_attempt_survives_locked_receipt_artifact")]
    public void ParallelAcceptanceDurablePassedAttemptSurvivesLockedReceiptArtifact()
    {
        var (_, goal) = SimpleGoal("Update src/Mcg.AgentOrchestrator.App/Orchestration/LockedReceipt.cs");
        var attemptRoot = CreateTempDirectory("mcg-conductor-acceptance-attempts");
        var launchAttempts = new ConcurrentDictionary<string, ConductorParallelAcceptanceOwnedProcessLaunch>();
        var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
            attemptRoot,
            isProcessAlive: _ => true,
            launchOwnedProcess: launch =>
            {
                launchAttempts[launch.Attempt.AttemptId] = launch;
                return new ConductorParallelAcceptanceOwnedProcessLaunchResult(7031);
            });
        var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, ["src/LockedReceipt.cs"], "branch", "main");

        try
        {
            var started = coordinator.Evaluate(candidate, ConductorAutonomyPolicy.Conservative, PassingRun);
            launchAttempts[started.Attempt.AttemptId].ExecuteInCurrentProcess(started.Attempt.OwnerProcessId);
            using var lockedResult = new FileStream(started.Attempt.ResultPath, FileMode.Open, FileAccess.Read, FileShare.None);

            var completed = coordinator.Evaluate(candidate, ConductorAutonomyPolicy.Conservative, PassingRun);

            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Completed, completed.Kind);
            Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Passed, completed.Attempt.Outcome);
            Assert.True(completed.Run!.Acceptance!.Passed);
        }
        finally
        {
            TryDeleteDirectory(attemptRoot);
        }
    }

    [Xunit.Fact(DisplayName = "ParallelAcceptance_records_and_prunes_attempt_trx_with_attempt_artifacts")]
    public void ParallelAcceptanceRecordsAndPrunesAttemptTrxWithAttemptArtifacts()
    {
        var (_, goal) = SimpleGoal("Update src/Mcg.AgentOrchestrator.App/Orchestration/AttemptTrx.cs");
        var attemptRoot = CreateTempDirectory("mcg-conductor-acceptance-attempts");
        var now = new DateTimeOffset(2026, 7, 17, 12, 0, 0, TimeSpan.Zero);
        var tick = 0;
        var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
            attemptRoot,
            utcNow: () => now.AddMinutes(tick++),
            runInline: true);
        var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, ["src/AttemptTrx.cs"], "branch", "main");
        string? firstAttemptPath = null;
        string? firstTrxPath = null;
        ConductorParallelAcceptanceAttempt? latestAttempt = null;

        try
        {
            for (var index = 0; index < 22; index++)
            {
                var completed = coordinator.Evaluate(
                    candidate,
                    ConductorAutonomyPolicy.Conservative,
                    (runCandidate, _) =>
                    {
                        var prefix = Environment.GetEnvironmentVariable(GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable);
                        Assert.False(string.IsNullOrWhiteSpace(prefix));
                        var trxPath = prefix + ".dotnet-test.trx";
                        File.WriteAllText(trxPath, "trx");
                        return ConductorParallelAcceptanceRunResult.Accepted(
                            runCandidate,
                            new AcceptanceVerificationSummary(true, [], TestResultPaths: [trxPath]));
                    });

                Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Completed, completed.Kind);
                var persisted = ReadAttempt(completed.Attempt.MetadataPath);
                Assert.NotNull(persisted.TestResultPaths);
                Assert.Single(persisted.TestResultPaths!);
                Assert.True(File.Exists(persisted.TestResultPaths![0]));
                using var resultJson = JsonDocument.Parse(File.ReadAllText(completed.Attempt.ResultPath));
                Assert.Equal(
                    persisted.TestResultPaths![0],
                    resultJson.RootElement.GetProperty("acceptance").GetProperty("testResultPaths")[0].GetString());

                firstAttemptPath ??= completed.Attempt.MetadataPath;
                firstTrxPath ??= persisted.TestResultPaths![0];
                latestAttempt = completed.Attempt;
                coordinator.MarkReconciled(completed.Attempt);
            }

            Assert.NotNull(firstAttemptPath);
            Assert.NotNull(firstTrxPath);
            Assert.False(File.Exists(firstAttemptPath!));
            Assert.False(File.Exists(firstTrxPath!));
            Assert.NotNull(latestAttempt);
            Assert.True(File.Exists(latestAttempt!.MetadataPath));
        }
        finally
        {
            TryDeleteDirectory(attemptRoot);
        }
    }

    [Xunit.Fact(DisplayName = "BatchLoop_transient_launch_failure_retries_before_escalating_at_cap")]
    public void BatchLoopTransientLaunchFailureRetriesBeforeEscalatingAtCap()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateVerifiedSimpleGoal(kernel, "Update src/Mcg.AgentOrchestrator.App/Orchestration/TransientLaunch.cs");
        var attemptRoot = CreateTempDirectory("mcg-conductor-acceptance-attempts");
        var escalations = new List<string>();
        var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
            attemptRoot,
            launchOwnedProcess: _ => throw new IOException("The process cannot access the file 'attempt.out.log' because it is being used by another process"));
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            writeEscalation: (_, _, reason) => escalations.Add(reason),
            getLandingFileScopes: _ => ["src/Mcg.AgentOrchestrator.App/Orchestration/TransientLaunch.cs"],
            parallelAcceptanceAttemptCoordinator: coordinator);

        try
        {
            var summary = new ConductorBatchLoop().Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: ConductorBatchLoop.ParallelAcceptanceTransientFailureCap,
                watchInterval: TimeSpan.FromMilliseconds(1),
                sleepFunc: _ => false);
            var latest = ReadLatestAttempt(attemptRoot, goal);

            Assert.Equal(ConductorBatchLoop.ParallelAcceptanceTransientFailureCap - 1, summary.Held);
            Assert.Equal(1, summary.Escalated);
            Assert.Equal(ConductorBatchLoop.ParallelAcceptanceTransientFailureCap, latest.TransientFailureCount);
            Assert.Single(escalations);
            Assert.Contains("background acceptance launch-failed", escalations.Single(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDeleteDirectory(attemptRoot);
        }
    }

    [Xunit.Fact(DisplayName = "BatchLoop_verified_goal_with_running_task_does_not_start_acceptance_attempt")]
    public void BatchLoopVerifiedGoalWithRunningTaskDoesNotStartAcceptanceAttempt()
    {
        var taskId = TaskId.New().Value;
        var kernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot(
            [
                new GoalSnapshot(
                    "goal-running-task",
                    "Update src/Mcg.AgentOrchestrator.App/Orchestration/RunningTask.cs",
                    GoalStatus.Verified,
                    [
                        new TaskSnapshot(taskId, "Still running.", AgentRole.Reviewer, WorkTaskStatus.Running, null, null, null, [], null, null)
                    ],
                    [])
            ],
            []));
        var goal = kernel.Goals.Single();
        var launches = 0;
        var attemptRoot = CreateTempDirectory("mcg-conductor-acceptance-attempts");
        var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
            attemptRoot,
            launchOwnedProcess: _ =>
            {
                launches++;
                return new ConductorParallelAcceptanceOwnedProcessLaunchResult(7032);
            });
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            runAcceptanceWithSlot: (_, _) => throw new InvalidOperationException("acceptance should not run for an incomplete task"),
            getLandingFileScopes: _ => ["src/Mcg.AgentOrchestrator.App/Orchestration/RunningTask.cs"],
            parallelAcceptanceAttemptCoordinator: coordinator);

        try
        {
            var summary = new ConductorBatchLoop().Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 1);

            Assert.Equal(0, launches);
            Assert.Equal(1, summary.Held);
        }
        finally
        {
            TryDeleteDirectory(attemptRoot);
        }
    }

    [Xunit.Fact(DisplayName = "BatchLoop_named_failing_checks_route_parallel_acceptance_to_retry_without_rerun")]
    public void BatchLoopNamedFailingChecksRouteParallelAcceptanceToRetryWithoutRerun()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateVerifiedSimpleGoal(kernel, "Update src/Mcg.AgentOrchestrator.App/Orchestration/NamedFailure.cs");
        var task = goal.Tasks.Single();
        var attempts = 0;
        string? retryMessage = null;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            runAcceptanceWithSlot: (_, _) =>
            {
                attempts++;
                return new AcceptanceVerificationSummary(
                    false,
                    [],
                    "WorkerSandboxPreparer_second_round_reuses_prep_receipt failed",
                    ["WorkerSandboxPreparer_second_round_reuses_prep_receipt"]);
            },
            retryTask: (goalId, taskId, message) =>
            {
                retryMessage = message;
                return kernel.RetryTask(goalId, taskId, message);
            },
            recordCriterionRetryFeedback: kernel.RecordCriterionRetryFeedback,
            getLandingFileScopes: _ => ["src/Mcg.AgentOrchestrator.App/Orchestration/NamedFailure.cs"]);

        var summary = new ConductorBatchLoop().Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 1);

        Assert.Equal(1, attempts);
        Assert.Equal(1, summary.Advanced);
        Assert.Equal(WorkTaskStatus.Assigned, task.Status);
        Assert.Contains("WorkerSandboxPreparer_second_round_reuses_prep_receipt", retryMessage!, StringComparison.Ordinal);
        Assert.Contains("acceptance failed checks", retryMessage!, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "ParallelAcceptance_fast_child_terminal_outcome_survives_parent_pid_update")]
    public void ParallelAcceptanceFastChildTerminalOutcomeSurvivesParentPidUpdate()
    {
        var (_, goal) = SimpleGoal("Update src/Mcg.AgentOrchestrator.App/Orchestration/FastCancel.cs");
        var attemptRoot = CreateTempDirectory("mcg-conductor-acceptance-attempts");
        var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
            attemptRoot,
            launchOwnedProcess: launch =>
            {
                launch.ExecuteInCurrentProcess(7030);
                return new ConductorParallelAcceptanceOwnedProcessLaunchResult(7030);
            });
        var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, ["src/FastCancel.cs"], "branch", "main");

        try
        {
            var started = coordinator.Evaluate(
                candidate,
                ConductorAutonomyPolicy.Conservative,
                (_, _) => throw new OperationCanceledException("operator cancelled"));
            var persisted = ReadAttempt(started.Attempt.MetadataPath);

            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Started, started.Kind);
            Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Cancelled, persisted.Outcome);
            Assert.Equal(7030, persisted.OwnerProcessId);
        }
        finally
        {
            TryDeleteDirectory(attemptRoot);
        }
    }

    [Xunit.Fact(DisplayName = "ParallelAcceptance_background_cancellation_and_build_blocks_are_typed")]
    public void ParallelAcceptanceBackgroundCancellationAndBuildBlocksAreTyped()
    {
        AssertBackgroundOutcome(
            "Cancel.cs",
            (_, _) => throw new OperationCanceledException("operator cancelled"),
            ConductorParallelAcceptanceAttemptOutcome.Cancelled);
        AssertBackgroundOutcome(
            "SlotsBusyTyped.cs",
            (_, _) => throw new DotnetBuildSlotsBusyException(new DotnetBuildLeaseAcquisition.SlotsBusy(
                "typed-test",
                [new DotnetBuildStableSlotWait(0, 7100)])),
            ConductorParallelAcceptanceAttemptOutcome.BlockedBuildSlot);
        AssertBackgroundOutcome(
            "BuildLockTyped.cs",
            (_, _) => throw new BuildLockBlockedException(new BuildLockAttribution(
                @"C:\mcg-dotnet-isolated\slots\slot-0\artifacts\bin\Mcg.AgentOrchestrator.Core.dll",
                [new BuildLockHolder(null, "unknown-probe-timeout", null, false)],
                "handle64-timeout",
                "acceptance-output",
                "classify-build-lock")),
            ConductorParallelAcceptanceAttemptOutcome.BlockedBuildLock);
    }

    [Xunit.Fact(DisplayName = "BatchLoop_parallel_acceptance_serves_oldest_verified_goal_first")]
    public void BatchLoopParallelAcceptanceServesOldestVerifiedGoalFirst()
    {
        using var _ = IsolatedDotnetRootScope();
        var kernel = new AgentOrchestratorKernel();
        var newer = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
            kernel,
            DefaultAgents(),
            "Update src/Mcg.AgentOrchestrator.App/Orchestration/Newer.cs");
        var older = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
            kernel,
            DefaultAgents(),
            "Update src/Mcg.AgentOrchestrator.App/Orchestration/Older.cs");
        var now = DateTimeOffset.UtcNow;
        PassVerificationAt(kernel, newer, newer.Tasks.Single(), now.AddMinutes(1));
        PassVerificationAt(kernel, older, older.Tasks.Single(), now);
        var attemptRoot = CreateTempDirectory("mcg-conductor-acceptance-attempts");
        var acceptanceOrder = new List<string>();

        try
        {
            var driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                runAcceptanceWithSlot: (goal, _) =>
                {
                    acceptanceOrder.Add(goal.Id.Value);
                    return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria;
                },
                land: goal => new LandingResult(goal.Id.Value, goal.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "ok"),
                getLandingFileScopes: goal => goal.Id == older.Id
                    ? ["src/Mcg.AgentOrchestrator.App/Orchestration/Older.cs"]
                    : ["src/Mcg.AgentOrchestrator.App/Orchestration/Newer.cs"],
                parallelAcceptanceAttemptCoordinator: new ConductorParallelAcceptanceAttemptCoordinator(
                    attemptRoot,
                    runInline: true));

            var summary = new ConductorBatchLoop().Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 1);

            Assert.Equal(2, summary.Advanced);
            Assert.Equal([older.Id.Value, newer.Id.Value], acceptanceOrder);
        }
        finally
        {
            TryDeleteDirectory(attemptRoot);
        }
    }

    [Xunit.Fact(DisplayName = "BatchLoop_parallel_acceptance_bounded_overtake_defers_newer_after_cap")]
    public void BatchLoopParallelAcceptanceBoundedOvertakeDefersNewerAfterCap()
    {
        using var _ = IsolatedDotnetRootScope();
        var kernel = new AgentOrchestratorKernel();
        var older = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
            kernel,
            DefaultAgents(),
            "Update src/Mcg.AgentOrchestrator.App/Orchestration/OldestBlocked.cs");
        var newer = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
            kernel,
            DefaultAgents(),
            "Update src/Mcg.AgentOrchestrator.App/Orchestration/NewerOvertake.cs");
        var now = DateTimeOffset.UtcNow;
        PassVerificationAt(kernel, older, older.Tasks.Single(), now);
        PassVerificationAt(kernel, newer, newer.Tasks.Single(), now.AddMinutes(1));
        var attemptRoot = CreateTempDirectory("mcg-conductor-acceptance-attempts");
        var launched = 0;
        var livePids = new ConcurrentDictionary<int, byte>();
        var nextPid = 8600;

        try
        {
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                attemptRoot,
                isProcessAlive: pid => livePids.ContainsKey(pid),
                launchOwnedProcess: _ =>
                {
                    var pid = Interlocked.Increment(ref nextPid);
                    livePids[pid] = 0;
                    launched++;
                    return new ConductorParallelAcceptanceOwnedProcessLaunchResult(pid);
                });
            var driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                getLandingFileScopes: goal =>
                {
                    if (goal.Id == older.Id)
                    {
                        throw new IOException("oldest scope temporarily unavailable");
                    }

                    return ["src/Mcg.AgentOrchestrator.App/Orchestration/NewerOvertake.cs"];
                },
                parallelAcceptanceAttemptCoordinator: coordinator);

            var output = AsyncLocalConsoleRouter.Capture(() =>
            {
                var summary = new ConductorBatchLoop().Run(
                    kernel,
                    driver,
                    ConductorAutonomyPolicy.Conservative,
                    NoStopPath(),
                    maxIterations: 2);

                Assert.Equal(0, summary.Advanced);
                Assert.True(summary.Held >= 2);
            });

            Assert.Equal(1, launched);
            Assert.Contains("reason=parallel-acceptance-fairness", output, StringComparison.Ordinal);
            Assert.Contains($"oldest={older.Id.Value[..8]}", output, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(attemptRoot);
        }
    }

    [Xunit.Fact(DisplayName = "BatchLoop_parallel_acceptance_replays_lease_receipts_to_conduct_events")]
    public void BatchLoopParallelAcceptanceReplaysLeaseReceiptsToConductEvents()
    {
        using var _ = IsolatedDotnetRootScope();
        var root = CreateTempDirectory("mcg-conduct-events-acceptance-lease");
        var logPath = Path.Combine(root, ".orchestrator", "logs", ConductEventLogWriter.CurrentFileName);
        var writer = new ConductEventLogWriter(logPath);
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateVerifiedSimpleGoal(kernel, "Update src/Mcg.AgentOrchestrator.App/Orchestration/LeaseEvents.cs");
        var attemptRoot = CreateTempDirectory("mcg-conductor-acceptance-attempts");

        try
        {
            var driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                runAcceptanceWithSlot: (_, _) => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
                getLandingFileScopes: _ => ["src/Mcg.AgentOrchestrator.App/Orchestration/LeaseEvents.cs"],
                parallelAcceptanceAttemptCoordinator: new ConductorParallelAcceptanceAttemptCoordinator(
                    attemptRoot,
                    runInline: true));

            new ConductorBatchLoop(conductEventLogWriter: writer).Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 1);

            var records = File.ReadAllLines(logPath)
                .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(line, new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
                .ToArray();

            Assert.Contains(records, record =>
                record.EventKind == "acceptance-lease" &&
                record.GoalId == goal.Id.Value[..8] &&
                record.Detail.Contains("ACCEPTANCE_LEASE_ACQUIRE", StringComparison.Ordinal));
            Assert.Contains(records, record =>
                record.EventKind == "acceptance-lease" &&
                record.GoalId == goal.Id.Value[..8] &&
                record.Detail.Contains("ACCEPTANCE_LEASE_RELEASE", StringComparison.Ordinal));
        }
        finally
        {
            TryDeleteDirectory(attemptRoot);
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "BatchLoop_parallel_acceptance_cancelled_terminal_attempt_is_held_not_escalated")]
    public void BatchLoopParallelAcceptanceCancelledTerminalAttemptIsHeldNotEscalated()
    {
        using var _ = IsolatedDotnetRootScope();
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateVerifiedSimpleGoal(kernel, "Update src/Mcg.AgentOrchestrator.App/Orchestration/CancelReplay.cs");
        var attemptRoot = CreateTempDirectory("mcg-conductor-acceptance-attempts");
        var escalated = false;

        try
        {
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                attemptRoot,
                launchOwnedProcess: launch =>
                {
                    launch.ExecuteInCurrentProcess(8701);
                    return new ConductorParallelAcceptanceOwnedProcessLaunchResult(8701);
                });
            var driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                runAcceptanceWithSlot: (_, _) => throw new OperationCanceledException("goal parked"),
                writeEscalation: (_, _, _) => escalated = true,
                getLandingFileScopes: _ => ["src/Mcg.AgentOrchestrator.App/Orchestration/CancelReplay.cs"],
                parallelAcceptanceAttemptCoordinator: coordinator);

            var output = AsyncLocalConsoleRouter.Capture(() =>
            {
                var summary = new ConductorBatchLoop().Run(
                    kernel,
                    driver,
                    ConductorAutonomyPolicy.Conservative,
                    NoStopPath(),
                    maxIterations: 2);

                Assert.Equal(0, summary.Escalated);
                Assert.True(summary.Held >= 1);
            });

            Assert.False(escalated);
            Assert.Contains("result=cancelled", output, StringComparison.Ordinal);
            Assert.DoesNotContain("result=escalated", output, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(attemptRoot);
        }
    }

    [Xunit.Fact(DisplayName = "ParallelAcceptance_attempt_holds_stable_slot_lease_until_terminal")]
    public void ParallelAcceptanceAttemptHoldsStableSlotLeaseUntilTerminal()
    {
        using var _ = IsolatedDotnetRootScope();
        var (_, goal) = SimpleGoal("Update src/Mcg.AgentOrchestrator.App/Orchestration/HoldLease.cs");
        var attemptRoot = CreateTempDirectory("mcg-conductor-acceptance-attempts");
        var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(attemptRoot, runInline: true);
        var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, ["src/HoldLease.cs"], "branch", "main");
        DotnetBuildLeaseAcquisition? reacquireWhileRunning = null;

        try
        {
            var output = AsyncLocalConsoleRouter.Capture(() =>
            {
                var decision = coordinator.Evaluate(
                    candidate,
                    ConductorAutonomyPolicy.Conservative,
                    (attemptCandidate, _, stableSlotLease, _) =>
                    {
                        Assert.NotNull(stableSlotLease);
                        reacquireWhileRunning = DotnetBuildEnvironmentManager.TryAcquireLeaseExecutionLock(
                            stableSlotLease.Environment,
                            TimeSpan.Zero);
                        return PassingRun(attemptCandidate, ConductorAutonomyPolicy.Conservative);
                    });

                Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Completed, decision.Kind);
                Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Passed, decision.Attempt.Outcome);
            });

            Assert.IsType<DotnetBuildLeaseAcquisition.SlotsBusy>(reacquireWhileRunning);
            Assert.Contains("ACCEPTANCE_LEASE_ACQUIRE", output);
            Assert.Contains("ACCEPTANCE_LEASE_HANDOFF", output);
            Assert.Contains("ACCEPTANCE_LEASE_RELEASE", output);
            Assert.Equal(1, CountOccurrences(output, "ACCEPTANCE_LEASE_RELEASE"));
        }
        finally
        {
            TryDeleteDirectory(attemptRoot);
        }
    }

    [Xunit.Fact(DisplayName = "ParallelAcceptance_second_attempt_yields_without_running_while_first_holds_slot")]
    public void ParallelAcceptanceSecondAttemptYieldsWithoutRunningWhileFirstHoldsSlot()
    {
        using var _ = IsolatedDotnetRootScope();
        var (_, goalA) = SimpleGoal("Update src/Mcg.AgentOrchestrator.App/Orchestration/HoldA.cs");
        var (_, goalB) = SimpleGoal("Update src/Mcg.AgentOrchestrator.App/Orchestration/HoldB.cs");
        var attemptRoot = CreateTempDirectory("mcg-conductor-acceptance-attempts");
        using var releaseFirst = new ManualResetEventSlim(false);
        using var firstHasLease = new ManualResetEventSlim(false);
        var coordinator = ThreadedAcceptanceAttemptCoordinator(attemptRoot, out var waitForAttempts);
        var candidateA = ConductorParallelAcceptanceCandidate.Create(goalA, 0, ["src/HoldA.cs"], "branch-a", "main");
        var candidateB = ConductorParallelAcceptanceCandidate.Create(goalB, 0, ["src/HoldB.cs"], "branch-b", "main");
        var secondRan = false;

        try
        {
            var first = coordinator.Evaluate(
                candidateA,
                ConductorAutonomyPolicy.Conservative,
                (attemptCandidate, _, stableSlotLease, _) =>
                {
                    Assert.NotNull(stableSlotLease);
                    firstHasLease.Set();
                    Assert.True(releaseFirst.Wait(TimeSpan.FromSeconds(5)));
                    return PassingRun(attemptCandidate, ConductorAutonomyPolicy.Conservative);
                });
            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Started, first.Kind);
            Assert.True(firstHasLease.Wait(TimeSpan.FromSeconds(5)));

            var second = coordinator.Evaluate(
                candidateB,
                ConductorAutonomyPolicy.Conservative,
                (attemptCandidate, attemptPolicy, _, _) =>
                {
                    secondRan = true;
                    return PassingRun(attemptCandidate, attemptPolicy);
                });
            var blocked = WaitForAttemptOutcome(coordinator, candidateB, ConductorParallelAcceptanceAttemptOutcome.BlockedBuildSlot);

            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Started, second.Kind);
            Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.BlockedBuildSlot, blocked.Attempt.Outcome);
            Assert.False(secondRan);
            releaseFirst.Set();
        }
        finally
        {
            releaseFirst.Set();
            waitForAttempts();
            TryDeleteDirectory(attemptRoot);
        }
    }

    [Xunit.Fact(DisplayName = "ParallelAcceptance_attempt_yields_to_live_cli_holder_and_reclaims_dead_holder")]
    public void ParallelAcceptanceAttemptYieldsToLiveCliHolderAndReclaimsDeadHolder()
    {
        using var _ = IsolatedDotnetRootScope();
        var (_, liveGoal) = SimpleGoal("Update src/Mcg.AgentOrchestrator.App/Orchestration/LiveCli.cs");
        var (_, deadGoal) = SimpleGoal("Update src/Mcg.AgentOrchestrator.App/Orchestration/DeadCli.cs");
        var attemptRoot = CreateTempDirectory("mcg-conductor-acceptance-attempts");
        var liveCandidate = ConductorParallelAcceptanceCandidate.Create(liveGoal, 0, ["src/LiveCli.cs"], "branch-live", "main");
        var deadCandidate = ConductorParallelAcceptanceCandidate.Create(deadGoal, 0, ["src/DeadCli.cs"], "branch-dead", "main");
        var liveEnvironment = DotnetBuildEnvironmentManager.CreateStableSlotAttempt(0);
        var liveRan = false;
        var deadRan = false;

        try
        {
            using (DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(liveEnvironment))
            {
                var liveCoordinator = new ConductorParallelAcceptanceAttemptCoordinator(attemptRoot, runInline: true);
                var liveBlocked = liveCoordinator.Evaluate(
                    liveCandidate,
                    ConductorAutonomyPolicy.Conservative,
                    (attemptCandidate, attemptPolicy, _, _) =>
                    {
                        liveRan = true;
                        return PassingRun(attemptCandidate, attemptPolicy);
                    });
                Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.BlockedBuildSlot, liveBlocked.Attempt.Outcome);
                Assert.False(liveRan);
            }

            File.WriteAllText(liveEnvironment.ExecutionLockPath, "999999");
            var deadCoordinator = new ConductorParallelAcceptanceAttemptCoordinator(attemptRoot, runInline: true);
            var reclaimed = deadCoordinator.Evaluate(
                deadCandidate,
                ConductorAutonomyPolicy.Conservative,
                (attemptCandidate, attemptPolicy, stableSlotLease, _) =>
                {
                    Assert.NotNull(stableSlotLease);
                    deadRan = true;
                    return PassingRun(attemptCandidate, attemptPolicy);
                });

            Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Passed, reclaimed.Attempt.Outcome);
            Assert.True(deadRan);
        }
        finally
        {
            TryDeleteDirectory(attemptRoot);
        }
    }

    [Xunit.Fact(DisplayName = "ParallelAcceptance_cancelled_attempt_records_cancelled_and_releases_lease")]
    public void ParallelAcceptanceCancelledAttemptRecordsCancelledAndReleasesLease()
    {
        using var _ = IsolatedDotnetRootScope();
        var (_, goal) = SimpleGoal("Update src/Mcg.AgentOrchestrator.App/Orchestration/CancelAttempt.cs");
        var attemptRoot = CreateTempDirectory("mcg-conductor-acceptance-attempts");
        var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(attemptRoot, runInline: true);
        var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, ["src/CancelAttempt.cs"], "branch", "main");
        DotnetBuildEnvironment? leasedEnvironment = null;

        try
        {
            var output = AsyncLocalConsoleRouter.Capture(() =>
            {
                var decision = coordinator.Evaluate(
                    candidate,
                    ConductorAutonomyPolicy.Conservative,
                    (_, _, stableSlotLease, _) =>
                    {
                        leasedEnvironment = stableSlotLease?.Environment;
                        throw new OperationCanceledException("goal parked");
                    });

                Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Cancelled, decision.Attempt.Outcome);
            });

            Assert.NotNull(leasedEnvironment);
            Assert.Contains("ACCEPTANCE_LEASE_RELEASE", output);
            using var reacquired = DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(leasedEnvironment, TimeSpan.Zero);
        }
        finally
        {
            TryDeleteDirectory(attemptRoot);
        }
    }

    [Xunit.Fact(DisplayName = "ParallelAcceptance_serialized_early_done_replays_parent_missing_branch_retirement")]
    public void ParallelAcceptanceSerializedEarlyDoneReplaysParentMissingBranchRetirement()
    {
        var root = CreateSeededGitRepository();
        var attemptRoot = CreateTempDirectory("mcg-conductor-acceptance-attempts");
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateVerifiedSimpleGoal(kernel, "Update src/Mcg.AgentOrchestrator.App/Orchestration/MissingReplay.cs");
            var policy = ConductorAutonomyPolicy.Conservative;
            var candidate = ConductorParallelAcceptanceCandidate.Create(
                goal,
                0,
                ["src/Mcg.AgentOrchestrator.App/Orchestration/MissingReplay.cs"]);
            var startCoordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                attemptRoot,
                isProcessAlive: _ => true,
                launchOwnedProcess: _ => new ConductorParallelAcceptanceOwnedProcessLaunchResult(7101));
            var started = startCoordinator.Evaluate(candidate, policy, PassingRun);
            var detail = "child observed missing branch before landing";
            var childCoordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                attemptRoot,
                isProcessAlive: _ => false);
            childCoordinator.RunAttemptForTests(
                started.Attempt,
                candidate,
                policy,
                (attemptCandidate, attemptPolicy) => ConductorParallelAcceptanceRunResult.Early(
                    attemptCandidate,
                    new ConductorAdvanceResult(
                        attemptCandidate.Goal.Id.Value,
                        attemptCandidate.GoalPrefix,
                        attemptPolicy.Name,
                        new ConductorAdvanceOutcome.Done(GoalLifecycleState.CleanedUp)),
                    ConductorParallelAcceptanceEarlyOutcome.MissingBranchRetired(GoalLifecycleState.CleanedUp, detail)));

            var retiredDetails = new List<string>();
            var escalationReasons = new List<string>();
            var parentCoordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                attemptRoot,
                isProcessAlive: _ => false,
                launchOwnedProcess: _ => throw new InvalidOperationException("parent should reconcile, not launch"));
            var driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                writeEscalation: (_, _, reason) => escalationReasons.Add(reason),
                recordMissingBranchRetirement: (retiredGoal, retirementDetail) =>
                {
                    retiredDetails.Add(retirementDetail);
                    GoalOperationJournal.RecordTerminalDisposition(
                        root,
                        retiredGoal,
                        new GoalTerminalDisposition(GoalTerminalDispositionKind.Retired, retirementDetail));
                    kernel.CompleteGoal(retiredGoal.Id, retirementDetail);
                },
                getLandingFileScopes: _ => ["src/Mcg.AgentOrchestrator.App/Orchestration/MissingReplay.cs"],
                parallelAcceptanceAttemptCoordinator: parentCoordinator);

            var summary = new ConductorBatchLoop().Run(
                kernel,
                driver,
                policy,
                NoStopPath(),
                maxIterations: 1);
            var reconciled = ReadAttempt(started.Attempt.MetadataPath);

            Assert.Equal(1, summary.Done);
            Assert.Empty(escalationReasons);
            Assert.Equal([detail], retiredDetails);
            Assert.Equal(GoalStatus.Completed, kernel.GetGoal(goal.Id).Status);
            Assert.True(GoalOperationJournal.HasRetiredTerminalDisposition(GoalOperationJournal.Read(root, goal.Id)));
            Assert.NotNull(reconciled.ReconciledAt);
        }
        finally
        {
            TryDeleteDirectory(attemptRoot);
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "ParallelAcceptance_serialized_early_escalation_replays_parent_escalation_record")]
    public void ParallelAcceptanceSerializedEarlyEscalationReplaysParentEscalationRecord()
    {
        var attemptRoot = CreateTempDirectory("mcg-conductor-acceptance-attempts");
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateVerifiedSimpleGoal(kernel, "Update src/Mcg.AgentOrchestrator.App/Orchestration/EscalateReplay.cs");
            var policy = ConductorAutonomyPolicy.Conservative;
            var candidate = ConductorParallelAcceptanceCandidate.Create(
                goal,
                0,
                ["src/Mcg.AgentOrchestrator.App/Orchestration/EscalateReplay.cs"]);
            var startCoordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                attemptRoot,
                isProcessAlive: _ => true,
                launchOwnedProcess: _ => new ConductorParallelAcceptanceOwnedProcessLaunchResult(7102));
            var started = startCoordinator.Evaluate(candidate, policy, PassingRun);
            var detail = "pre-landing rebase conflict (src/EscalateReplay.cs); use 'workspace rebase' to resolve";
            var childCoordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                attemptRoot,
                isProcessAlive: _ => false);
            childCoordinator.RunAttemptForTests(
                started.Attempt,
                candidate,
                policy,
                (attemptCandidate, attemptPolicy) => ConductorParallelAcceptanceRunResult.Early(
                    attemptCandidate,
                    new ConductorAdvanceResult(
                        attemptCandidate.Goal.Id.Value,
                        attemptCandidate.GoalPrefix,
                        attemptPolicy.Name,
                        new ConductorAdvanceOutcome.Escalated(GoalLifecycleState.Verified, detail)),
                    ConductorParallelAcceptanceEarlyOutcome.PreLandingEscalated(GoalLifecycleState.Verified, detail)));

            var retired = false;
            var escalationReasons = new List<string>();
            var parentCoordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                attemptRoot,
                isProcessAlive: _ => false,
                launchOwnedProcess: _ => throw new InvalidOperationException("parent should reconcile, not launch"));
            var driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                writeEscalation: (_, state, reason) =>
                {
                    Assert.Equal(GoalLifecycleState.Verified, state);
                    escalationReasons.Add(reason);
                },
                recordMissingBranchRetirement: (_, _) => retired = true,
                getLandingFileScopes: _ => ["src/Mcg.AgentOrchestrator.App/Orchestration/EscalateReplay.cs"],
                parallelAcceptanceAttemptCoordinator: parentCoordinator);

            var summary = new ConductorBatchLoop().Run(
                kernel,
                driver,
                policy,
                NoStopPath(),
                maxIterations: 1);
            var reconciled = ReadAttempt(started.Attempt.MetadataPath);

            Assert.Equal(1, summary.Escalated);
            Assert.False(retired);
            Assert.Equal([detail], escalationReasons);
            Assert.NotNull(reconciled.ReconciledAt);
        }
        finally
        {
            TryDeleteDirectory(attemptRoot);
        }
    }

    // Creates a stop file and returns its path.
    private static string ExistingStopPath()
    {
        var path = NoStopPath();
        File.WriteAllText(path, "stop");
        return path;
    }

    private static string CreateSeededGitRepository()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mcg-batch-loop-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        RunGit(path, "init");
        RunGit(path, "checkout", "-b", "main");
        RunGit(path, "config", "user.email", "tests@example.com");
        RunGit(path, "config", "user.name", "Batch Loop Tests");
        File.WriteAllText(Path.Combine(path, "seed.txt"), "seed");
        RunGit(path, "add", "-A");
        RunGit(path, "commit", "-m", "Seed");
        return path;
    }

    private static string CreateTempDirectory(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static IDisposable IsolatedDotnetRootScope() =>
        new EnvVarScope(
            DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable,
            Path.Combine(Path.GetTempPath(), $"{DotnetBuildEnvironmentManager.RootDirectoryName}-batch-loop-{Guid.NewGuid():N}"));

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private sealed class EnvVarScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _previousValue;
        private readonly string? _value;

        public EnvVarScope(string name, string? value)
        {
            _name = name;
            _value = value;
            _previousValue = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(_name, _previousValue);
            if (!string.IsNullOrWhiteSpace(_value) && Directory.Exists(_value))
            {
                try { Directory.Delete(_value, recursive: true); }
                catch { }
            }
        }
    }

    private static ConductorParallelAcceptanceAttemptCoordinator ThreadedAcceptanceAttemptCoordinator(
        string attemptRoot,
        out Action waitForAttempts)
    {
        var nextPid = 8000;
        var alive = new ConcurrentDictionary<int, byte>();
        var threads = new ConcurrentBag<Thread>();
        waitForAttempts = () =>
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            foreach (var thread in threads)
            {
                var remaining = deadline - DateTime.UtcNow;
                Assert.True(
                    remaining > TimeSpan.Zero && thread.Join(remaining),
                    $"acceptance attempt thread {thread.Name} did not finish before cleanup");
            }
        };

        return new ConductorParallelAcceptanceAttemptCoordinator(
            attemptRoot,
            isProcessAlive: pid => alive.ContainsKey(pid),
            launchOwnedProcess: launch =>
            {
                var pid = Interlocked.Increment(ref nextPid);
                var thread = new Thread(() =>
                {
                    alive[pid] = 0;
                    try
                    {
                        launch.ExecuteInCurrentProcess(pid);
                    }
                    finally
                    {
                        alive.TryRemove(pid, out _);
                    }
                })
                {
                    IsBackground = true,
                    Name = $"acceptance-attempt-test-{pid}"
                };
                threads.Add(thread);
                thread.Start();
                return new ConductorParallelAcceptanceOwnedProcessLaunchResult(pid);
            });
    }

    private static ConductorParallelAcceptanceRunResult PassingRun(
        ConductorParallelAcceptanceCandidate candidate,
        ConductorAutonomyPolicy _) =>
        ConductorParallelAcceptanceRunResult.Accepted(candidate, AcceptanceVerificationSummary.PassedWithNoUnmetCriteria);

    private static ConductorParallelAcceptanceAttempt ReadLatestAttempt(string attemptRoot, Goal goal) =>
        Directory.EnumerateFiles(Path.Combine(attemptRoot, goal.Id.Value), "*.attempt.json")
            .Select(ReadAttempt)
            .OrderByDescending(attempt => attempt.StartedAt)
            .First();

    private static ConductorParallelAcceptanceAttempt ReadAttempt(string path) =>
        JsonSerializer.Deserialize<ConductorParallelAcceptanceAttempt>(
            File.ReadAllText(path),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private static void AssertBackgroundOutcome(
        string fileName,
        Func<ConductorParallelAcceptanceCandidate, ConductorAutonomyPolicy, ConductorParallelAcceptanceRunResult> run,
        ConductorParallelAcceptanceAttemptOutcome expected)
    {
        var (_, goal) = SimpleGoal($"Update src/Mcg.AgentOrchestrator.App/Orchestration/{fileName}");
        var attemptRoot = CreateTempDirectory("mcg-conductor-acceptance-attempts");
        var coordinator = ThreadedAcceptanceAttemptCoordinator(attemptRoot, out var waitForAttempts);
        var candidate = ConductorParallelAcceptanceCandidate.Create(
            goal,
            0,
            [$"src/Mcg.AgentOrchestrator.App/Orchestration/{fileName}"],
            "branch",
            "main");

        try
        {
            var started = coordinator.Evaluate(candidate, ConductorAutonomyPolicy.Conservative, run);
            var terminal = WaitForAttemptOutcome(coordinator, candidate, expected);

            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Started, started.Kind);
            Assert.Equal(expected, terminal.Attempt.Outcome);
        }
        finally
        {
            waitForAttempts();
            TryDeleteDirectory(attemptRoot);
        }
    }

    private static ConductorParallelAcceptanceAttemptDecision WaitForAttemptOutcome(
        ConductorParallelAcceptanceAttemptCoordinator coordinator,
        ConductorParallelAcceptanceCandidate candidate,
        ConductorParallelAcceptanceAttemptOutcome expected)
    {
        for (var i = 0; i < 50; i++)
        {
            var decision = coordinator.Evaluate(candidate, ConductorAutonomyPolicy.Conservative, PassingRun);
            if (decision.Attempt.Outcome == expected)
            {
                return decision;
            }

            Thread.Sleep(20);
        }

        var latest = coordinator.Evaluate(candidate, ConductorAutonomyPolicy.Conservative, PassingRun);
        Assert.Equal(expected, latest.Attempt.Outcome);
        return latest;
    }

    private static ConductLoopHandoffOptions HandoffOptions(
        string root,
        IReadOnlyList<string>? args = null,
        string? stopFilePath = null,
        string? runEventStorePath = null,
        int renewalCount = 0,
        int maxRenewals = ConductorLoopHandoff.DefaultMaxRenewalsWithoutLanding,
        Action? release = null,
        TimeSpan verificationTimeout = default,
        Func<ConductLoopHandoffOptions, long, bool>? loopStartProbe = null) =>
        new(
            Args: args ?? ["conduct", "--loop", "--watch", "--max-duration", "14400"],
            ExecutionDirectory: root,
            OrchestratorDirectory: Path.Combine(root, ".orchestrator"),
            LogDirectory: Path.Combine(root, ".orchestrator", "logs"),
            RunEventStorePath: runEventStorePath ?? Path.Combine(root, ".orchestrator", "run-events.db"),
            StopFilePath: stopFilePath ?? Path.Combine(root, ConductorBatchLoop.StopFileName),
            RenewalCount: renewalCount,
            MaxRenewals: maxRenewals,
            ReleaseCurrentLease: release ?? (() => { }),
            VerificationTimeout: verificationTimeout,
            LoopStartProbe: loopStartProbe);

    private static void RunGit(string workingDirectory, params string[] args)
    {
        var result = GitCli.Run(workingDirectory, args);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {result.Error}");
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }

    private static AgentDefinition[] BuildAgents(params AgentRole[] roles)
    {
        var capability = ModelCapability.Text | ModelCapability.Code | ModelCapability.ToolUse;
        return roles
            .Select(role => new AgentDefinition(
                AgentId.New(),
                role.ToString(),
                role,
                new ModelProfile("OpenAI", "test", capability, SubscriptionMode.ApiKey)))
            .ToArray();
    }

    [Xunit.Fact(DisplayName = "BatchLoop_max_duration_starts_handoff")]
    public void BatchLoopMaxDurationStartsHandoff()
    {
        var (kernel, _) = SimpleGoal();
        ConductorLoopHandoffRequest? request = null;
        var summary = new ConductorBatchLoop(
            handoffOnMaxDuration: handoffRequest =>
            {
                request = handoffRequest;
                return ConductorLoopHandoffResult.StartedProcess(1234, "out.log", "err.log");
            }).Run(
            kernel,
            MakeDriver(),
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxDuration: TimeSpan.Zero);

        Assert.NotNull(request);
        Assert.True(summary.Handoff?.Started);
        Assert.Equal(1234, summary.Handoff.ProcessId);
    }

    [Xunit.Fact(DisplayName = "BatchLoop_stop_file_does_not_handoff")]
    public void BatchLoopStopFileDoesNotHandoff()
    {
        var (kernel, _) = SimpleGoal();
        var called = false;

        var summary = new ConductorBatchLoop(
            handoffOnMaxDuration: _ =>
            {
                called = true;
                return ConductorLoopHandoffResult.StartedProcess(1234, "out.log", "err.log");
            }).Run(
            kernel,
            MakeDriver(),
            ConductorAutonomyPolicy.Conservative,
            ExistingStopPath(),
            maxDuration: TimeSpan.FromSeconds(1));

        Assert.True(summary.StopRequested);
        Assert.False(called);
        Assert.Null(summary.Handoff);
    }

    [Xunit.Fact(DisplayName = "BatchLoop_no_progress_exit_does_not_handoff")]
    public void BatchLoopNoProgressExitDoesNotHandoff()
    {
        var (kernel, _) = SimpleGoal();
        var called = false;

        new ConductorBatchLoop(
            handoffOnMaxDuration: _ =>
            {
                called = true;
                return ConductorLoopHandoffResult.StartedProcess(1234, "out.log", "err.log");
            }).Run(
            kernel,
            MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                getRunningCount: () => ConductorAutonomyPolicy.Conservative.MaxConcurrentPaidWorkers),
            ConductorAutonomyPolicy.Conservative,
            NoStopPath());

        Assert.False(called);
    }

    [Xunit.Fact(DisplayName = "BatchLoop_max_duration_detaches_running_dispatches_before_handoff")]
    public void BatchLoopMaxDurationDetachesRunningDispatchesBeforeHandoff()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        StartProcess(kernel, goal, task, DateTimeOffset.UtcNow, "base");
        var detached = 0;
        var cancelled = 0;

        var summary = new ConductorBatchLoop(
            reapGoalRunningDispatches: (_, _) => cancelled++,
            detachGoalRunningDispatches: (_, _) => detached++,
            handoffOnMaxDuration: _ => ConductorLoopHandoffResult.StartedProcess(1234, "out.log", "err.log")).Run(
            kernel,
            MakeDriver(),
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxDuration: TimeSpan.Zero);

        Assert.True(summary.Handoff?.Started);
        Assert.Equal(1, detached);
        Assert.Equal(0, cancelled);
    }

    [Xunit.Fact(DisplayName = "ConductorLoopLease_refuses_second_loop")]
    public void ConductorLoopLeaseRefusesSecondLoop()
    {
        var root = CreateTempDirectory("mcg-conduct-loop-lease");
        try
        {
            var orchestrator = Path.Combine(root, ".orchestrator");
            using var lease = ConductorLoopLease.Acquire(orchestrator);

            Assert.Throws<InvalidOperationException>(() => ConductorLoopLease.Acquire(orchestrator));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "ConductorLoopHandoff_stop_file_suppresses_successor")]
    public void ConductorLoopHandoffStopFileSuppressesSuccessor()
    {
        var root = CreateTempDirectory("mcg-conduct-loop-stop-handoff");
        try
        {
            var stopFile = Path.Combine(root, ConductorBatchLoop.StopFileName);
            File.WriteAllText(stopFile, "stop");
            var result = ConductorLoopHandoff.TryStartSuccessor(
                HandoffOptions(root, stopFilePath: stopFile),
                new ConductorLoopHandoffRequest(0, TimeSpan.Zero, 0),
                _ => throw new InvalidOperationException("launch should not run"));

            Assert.False(result.Started);
            Assert.Equal("stop-file", result.Reason);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "ConductorLoopHandoff_stops_at_renewal_cap_without_landing")]
    public void ConductorLoopHandoffStopsAtRenewalCapWithoutLanding()
    {
        var root = CreateTempDirectory("mcg-conduct-loop-renewal-cap");
        try
        {
            var result = ConductorLoopHandoff.TryStartSuccessor(
                HandoffOptions(root, renewalCount: 6, maxRenewals: 6),
                new ConductorLoopHandoffRequest(0, TimeSpan.Zero, 0),
                _ => throw new InvalidOperationException("launch should not run"));

            Assert.False(result.Started);
            Assert.Contains("renewal-cap", result.Reason, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "ConductorLoopHandoff_launches_successor_with_incremented_batch_fresh_logs_and_renewal_arg")]
    public void ConductorLoopHandoffLaunchesSuccessorWithIncrementedBatchFreshLogsAndRenewalArg()
    {
        var root = CreateTempDirectory("mcg-conduct-loop-happy-handoff");
        var previousName = Environment.GetEnvironmentVariable("MCG_ORCHESTRATOR_CONDUCT_BATCH_NAME");
        try
        {
            Environment.SetEnvironmentVariable("MCG_ORCHESTRATOR_CONDUCT_BATCH_NAME", "batch25");
            var released = false;
            ConductLoopLaunchRequest? launchRequest = null;
            var result = ConductorLoopHandoff.TryStartSuccessor(
                HandoffOptions(
                    root,
                    args: ["conduct", "--loop", "--watch", "--max-duration", "14400", ConductorLoopHandoff.RenewalCountFlag, "4"],
                    renewalCount: 4,
                    release: () => released = true),
                new ConductorLoopHandoffRequest(12, TimeSpan.FromHours(4), 0),
                request =>
                {
                    launchRequest = request;
                    return new ConductLoopLaunchResult(4567, request.StdoutPath, request.StderrPath);
                },
                (_, _) =>
                {
                    File.WriteAllText(launchRequest!.StdoutPath, "LOOP_START");
                    return new ConductLoopHandoffVerification(true, true, true, "processAlive=true stdoutLogExists=true loopStartJournaled=true");
                });

            Assert.True(result.Started);
            Assert.True(released);
            Assert.Equal("batch26", launchRequest!.Name);
            Assert.Contains("operator-batch26-", Path.GetFileName(launchRequest.StdoutPath), StringComparison.Ordinal);
            Assert.DoesNotContain(ConductorLoopHandoff.RenewalCountFlag, launchRequest.Args);
            Assert.Equal(5, launchRequest.RenewalCount);
            Assert.Equal(4567, result.ProcessId);
            Assert.Contains("guard=lease-released-before-launch", result.VerificationOutcome, StringComparison.Ordinal);
            Assert.Contains("spawnPath=injected", result.VerificationOutcome, StringComparison.Ordinal);
            Assert.Contains("loopStartJournaled=true", result.VerificationOutcome, StringComparison.Ordinal);

            var records = new SqliteRunEventStore(Path.Combine(root, ".orchestrator", "run-events.db"))
                .ReadSinceAsync()
                .GetAwaiter()
                .GetResult();
            var handoffEvent = Assert.Single(records.Where(record => record.Operation == "LOOP_HANDOFF"));
            Assert.Equal("Started", handoffEvent.Status);
            Assert.Contains(Path.GetFullPath(launchRequest.StdoutPath), handoffEvent.Detail, StringComparison.Ordinal);
            Assert.Contains(Path.GetFullPath(launchRequest.StderrPath), handoffEvent.Detail, StringComparison.Ordinal);
            Assert.Contains("guard=lease-released-before-launch", handoffEvent.Detail, StringComparison.Ordinal);
            Assert.Contains("spawnPath=injected", handoffEvent.Detail, StringComparison.Ordinal);
            Assert.Contains("loopStartJournaled=true", handoffEvent.Detail, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MCG_ORCHESTRATOR_CONDUCT_BATCH_NAME", previousName);
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "BatchLoop_handoff_renewal_cap_resets_for_landing_adopted_by_reconcile")]
    public void BatchLoopHandoffRenewalCapResetsForLandingAdoptedByReconcile()
    {
        var root = CreateTempDirectory("mcg-conduct-loop-adopted-landing");
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateVerifiedSimpleGoal(kernel, "Adopted landing");
            var start = DateTimeOffset.Parse("2026-07-11T00:00:00Z");
            var nowCalls = 0;
            DateTimeOffset UtcNow() => nowCalls++ switch
            {
                0 => start,
                1 => start,
                _ => start.AddSeconds(2)
            };

            var reconciled = false;
            ConductLoopLaunchRequest? launchRequest = null;
            ConductorLoopHandoffRequest? handoffRequest = null;
            var summary = new ConductorBatchLoop(
                measuredSweep: loopKernel =>
                {
                    if (!reconciled)
                    {
                        reconciled = true;
                        loopKernel.CompleteGoal(goal.Id, "Test fixture: landing adopted during first reconcile.");
                    }

                    return null;
                },
                handoffOnMaxDuration: request =>
                {
                    handoffRequest = request;
                    return ConductorLoopHandoff.TryStartSuccessor(
                        HandoffOptions(root, renewalCount: 6, maxRenewals: 6),
                        request,
                        launch =>
                        {
                            launchRequest = launch;
                            return new ConductLoopLaunchResult(4567, launch.StdoutPath, launch.StderrPath);
                        },
                        (_, _) =>
                        {
                            File.WriteAllText(launchRequest!.StdoutPath, "LOOP_START");
                            return new ConductLoopHandoffVerification(true, true, true, "processAlive=true stdoutLogExists=true loopStartJournaled=true");
                        });
                },
                utcNow: UtcNow).Run(
                kernel,
                MakeDriver(),
                ConductorAutonomyPolicy.Conservative,
                Path.Combine(root, ConductorBatchLoop.StopFileName),
                watchInterval: TimeSpan.FromSeconds(1),
                sleepFunc: _ => false,
                maxDuration: TimeSpan.FromSeconds(1),
                keepAliveWhenIdle: true);

            Assert.True(summary.Handoff?.Started);
            Assert.NotNull(handoffRequest);
            Assert.Equal(0, handoffRequest!.Done);
            Assert.Equal(1, handoffRequest.LandedGoalDelta);
            Assert.DoesNotContain(ConductorLoopHandoff.RenewalCountFlag, launchRequest!.Args);
            Assert.Equal(0, launchRequest.RenewalCount);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "ConductorLoopHandoff_journal_failure_is_loud_and_still_launches_successor")]
    public void ConductorLoopHandoffJournalFailureIsLoudAndStillLaunchesSuccessor()
    {
        var root = CreateTempDirectory("mcg-conduct-loop-journal-failure");
        try
        {
            string outText = "";
            var errorText = CaptureConsoleError(() =>
            {
                outText = CaptureConsole(() =>
                {
                    var launched = false;

                    var result = ConductorLoopHandoff.TryStartSuccessor(
                        HandoffOptions(root, runEventStorePath: root),
                        new ConductorLoopHandoffRequest(12, TimeSpan.FromHours(4), 0),
                        request =>
                        {
                            launched = true;
                            return new ConductLoopLaunchResult(4567, request.StdoutPath, request.StderrPath);
                        },
                        (_, _) =>
                        {
                            return new ConductLoopHandoffVerification(true, true, true, "processAlive=true stdoutLogExists=true loopStartJournaled=true");
                        });

                    Assert.True(result.Started);
                    Assert.True(launched);
                });
            });

            Assert.Contains("LOOP_HANDOFF_JOURNAL_FAILED", outText, StringComparison.Ordinal);
            Assert.Contains("LOOP_HANDOFF_JOURNAL_FAILED", errorText, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "ConductorLoopHandoff_does_not_retry_while_successor_is_alive")]
    public void ConductorLoopHandoffDoesNotRetryWhileSuccessorIsAlive()
    {
        var root = CreateTempDirectory("mcg-conduct-loop-slow-handoff");
        try
        {
            string outText = "";
            var errorText = CaptureConsoleError(() =>
            {
                outText = CaptureConsole(() =>
                {
                    var scaledOldTimeout = TimeSpan.FromMilliseconds(500);
                    var scaledLoopStartDelay = TimeSpan.FromMilliseconds(750);
                    var stopwatch = Stopwatch.StartNew();
                    var attempts = 0;
                    var result = ConductorLoopHandoff.TryStartSuccessor(
                        HandoffOptions(
                            root,
                            verificationTimeout: scaledOldTimeout,
                            loopStartProbe: (_, _) => stopwatch.Elapsed >= scaledLoopStartDelay),
                        new ConductorLoopHandoffRequest(12, TimeSpan.FromHours(4), 0),
                        request =>
                        {
                            attempts++;
                            File.WriteAllText(request.StdoutPath, "successor booting");
                            return new ConductLoopLaunchResult(Environment.ProcessId, request.StdoutPath, request.StderrPath);
                        });

                    Assert.True(result.Started);
                    Assert.Equal(1, attempts);
                    Assert.Contains("processAlive=true", result.VerificationOutcome, StringComparison.Ordinal);
                    Assert.Contains("loopStartJournaled=true", result.VerificationOutcome, StringComparison.Ordinal);
                    Assert.Contains("terminalReason=loop-start", result.VerificationOutcome, StringComparison.Ordinal);
                });
            });

            Assert.Contains("LOOP_HANDOFF_PENDING", outText, StringComparison.Ordinal);
            Assert.DoesNotContain("LOOP_HANDOFF_FAILED", outText, StringComparison.Ordinal);
            Assert.DoesNotContain("LOOP_HANDOFF_FAILED", errorText, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "ConductorLoopHandoff_slow_boot_successor_succeeds_without_retry")]
    public void ConductorLoopHandoffSlowBootSuccessorSucceedsWithoutRetry()
    {
        var root = CreateTempDirectory("mcg-conduct-loop-delayed-handoff");
        try
        {
            var oldFixedStartupWindow = TimeSpan.FromSeconds(10);
            var loopStartDelay = TimeSpan.FromMilliseconds(10250);
            var stopwatch = Stopwatch.StartNew();
            var attempts = 0;
            var result = ConductorLoopHandoff.TryStartSuccessor(
                HandoffOptions(
                    root,
                    verificationTimeout: TimeSpan.FromSeconds(12),
                    loopStartProbe: (_, _) => stopwatch.Elapsed >= loopStartDelay),
                new ConductorLoopHandoffRequest(12, TimeSpan.FromHours(4), 0),
                request =>
                {
                    attempts++;
                    File.WriteAllText(request.StdoutPath, "successor booting");
                    return new ConductLoopLaunchResult(Environment.ProcessId, request.StdoutPath, request.StderrPath);
                });

            Assert.True(result.Started);
            Assert.Equal(1, attempts);
            Assert.Equal(Environment.ProcessId, result.ProcessId);
            Assert.True(stopwatch.Elapsed > oldFixedStartupWindow);
            Assert.Contains("loopStartJournaled=true", result.VerificationOutcome, StringComparison.Ordinal);
            Assert.Contains("terminalReason=loop-start", result.VerificationOutcome, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "ConductorLoopHandoff_dead_successor_fails_with_own_evidence")]
    public void ConductorLoopHandoffDeadSuccessorFailsWithOwnEvidence()
    {
        var root = CreateTempDirectory("mcg-conduct-loop-failed-handoff");
        try
        {
            string outText = "";
            var errorText = CaptureConsoleError(() =>
            {
                outText = CaptureConsole(() =>
                {
                    var attempts = 0;
                    var result = ConductorLoopHandoff.TryStartSuccessor(
                        HandoffOptions(root),
                        new ConductorLoopHandoffRequest(12, TimeSpan.FromHours(4), 0),
                        request =>
                        {
                            attempts++;
                            return new ConductLoopLaunchResult(int.MaxValue, request.StdoutPath, request.StderrPath);
                        });

                    Assert.False(result.Started);
                    Assert.True(result.Failed);
                    Assert.Equal(1, attempts);
                    Assert.Equal("successor-child-dead", result.Reason);
                    Assert.Contains("attempt=1", result.VerificationOutcome, StringComparison.Ordinal);
                    Assert.Contains($"pid={int.MaxValue}", result.VerificationOutcome, StringComparison.Ordinal);
                    Assert.Contains("guard=lease-released-before-launch", result.VerificationOutcome, StringComparison.Ordinal);
                    Assert.Contains("spawnPath=injected", result.VerificationOutcome, StringComparison.Ordinal);
                    Assert.Contains("loopStartJournaled=false", result.VerificationOutcome, StringComparison.Ordinal);
                    Assert.Contains("terminalReason=child-dead", result.VerificationOutcome, StringComparison.Ordinal);
                });
            });

            Assert.Contains("LOOP_HANDOFF_FAILED", outText, StringComparison.Ordinal);
            Assert.Contains("LOOP_HANDOFF_FAILED", errorText, StringComparison.Ordinal);

            var records = new SqliteRunEventStore(Path.Combine(root, ".orchestrator", "run-events.db"))
                .ReadSinceAsync()
                .GetAwaiter()
                .GetResult();
            Assert.Equal(1, records.Count(record => record.Operation == "LOOP_HANDOFF" && record.Status == "Failed"));
            Assert.Contains(records, record =>
                record.Operation == "LOOP_HANDOFF" &&
                record.Status == "Failed" &&
                record.Detail is not null &&
                record.Detail.Contains($"pid={int.MaxValue}", StringComparison.Ordinal) &&
                record.Detail.Contains("terminalReason=child-dead", StringComparison.Ordinal));
            Assert.Contains(records, record =>
                record.Operation == "LOOP_HANDOFF" &&
                record.Status == "Escalated" &&
                record.Detail is not null &&
                record.Detail.Contains("successor-child-dead", StringComparison.Ordinal));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "BatchLoop_handoff_failure_emits_failed_event_not_skipped")]
    public void BatchLoopHandoffFailureEmitsFailedEventNotSkipped()
    {
        var (kernel, _) = SimpleGoal();
        var root = CreateTempDirectory("mcg-conduct-loop-failed-event");
        var logPath = Path.Combine(root, ".orchestrator", "logs", ConductEventLogWriter.CurrentFileName);
        var writer = new ConductEventLogWriter(logPath);
        try
        {
            var summary = new ConductorBatchLoop(
                handoffOnMaxDuration: _ => ConductorLoopHandoffResult.FailedStart(
                    "successor-verification-failed",
                    Path.Combine(root, "successor.out.log"),
                    Path.Combine(root, "successor.err.log"),
                    "processAlive=false stdoutLogExists=false loopStartJournaled=false"),
                conductEventLogWriter: writer)
                .Run(
                    kernel,
                    MakeDriver(),
                    ConductorAutonomyPolicy.Conservative,
                    NoStopPath(),
                    maxDuration: TimeSpan.Zero);

            Assert.True(summary.Handoff?.Failed);
            var log = File.ReadAllText(logPath);
            Assert.Contains("LOOP_HANDOFF_FAILED", log, StringComparison.Ordinal);
            Assert.Contains("successor.out.log", log, StringComparison.Ordinal);
            Assert.Contains("loopStartJournaled=false", log, StringComparison.Ordinal);
            Assert.DoesNotContain("LOOP_HANDOFF_SKIPPED", log, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "ConductorLoopHandoff_windows_launcher_uses_direct_process_with_explicit_log_handles")]
    public void ConductorLoopHandoffWindowsLauncherUsesDirectProcessWithExplicitLogHandles()
    {
        var commandLine = ConductorLoopHandoff.BuildWindowsProcessCommandLine(
            ["dotnet", @"C:\repo\src\Mcg.AgentOrchestrator.App.dll", "conduct", "--loop", "--watch"]);

        Assert.Contains(@"""dotnet"" ""C:\repo\src\Mcg.AgentOrchestrator.App.dll"" ""conduct"" ""--loop"" ""--watch""", commandLine, StringComparison.Ordinal);
        Assert.DoesNotContain("Start-Process", commandLine, StringComparison.Ordinal);
        Assert.DoesNotContain("cmd.exe", commandLine, StringComparison.OrdinalIgnoreCase);

        var source = File.ReadAllText(Path.Combine(InfrastructureTestSupport.FindRepositoryRoot(), "src", "Mcg.AgentOrchestrator.App", "Orchestration", "ConductorLoopHandoff.cs"));
        Assert.Contains("CreateBreakawayFromJob", source, StringComparison.Ordinal);
        Assert.Contains("CreateInheritedOutputFile", source, StringComparison.Ordinal);
        Assert.Contains("UseStdHandles", source, StringComparison.Ordinal);
        Assert.Contains("bInheritHandles: true", source, StringComparison.Ordinal);
        Assert.Contains("ProcThreadAttributeHandleList", source, StringComparison.Ordinal);
        Assert.Contains("ExtendedStartupInfoPresent", source, StringComparison.Ordinal);
        Assert.DoesNotContain("WindowsCreationFlags.CreateNoWindow", source, StringComparison.Ordinal);
        Assert.DoesNotContain("WindowsCreationFlags.DetachedProcess", source, StringComparison.Ordinal);

        var jobSource = File.ReadAllText(Path.Combine(InfrastructureTestSupport.FindRepositoryRoot(), "src", "Mcg.AgentOrchestrator.Infrastructure", "Processes", "OwnedProcessGroup.cs"));
        Assert.Contains("JobObjectLimitBreakawayOk", jobSource, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "ConductorLoopHandoff_windows_launcher_inherits_redirected_stdout_handle")]
    public void ConductorLoopHandoffWindowsLauncherInheritsRedirectedStdoutHandle()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateTempDirectory("mcg-conduct-loop-stdout-handoff");
        int? processId = null;
        try
        {
            var stdoutPath = Path.Combine(root, "successor.out.log");
            var stderrPath = Path.Combine(root, "successor.err.log");
            var stdoutMarker = "handoff-stdout-marker-" + Guid.NewGuid().ToString("N");
            var stderrMarker = "handoff-stderr-marker-" + Guid.NewGuid().ToString("N");
            var scriptPath = Path.Combine(root, "write-marker.cmd");
            File.WriteAllText(scriptPath, $"@echo {stdoutMarker}{Environment.NewLine}@echo {stderrMarker} 1>&2{Environment.NewLine}");
            var cmdPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "cmd.exe");
            var result = ConductorLoopHandoff.LaunchDetachedWindows(
                new ConductLoopLaunchRequest("batch1", [], stdoutPath, stderrPath, root, 0),
                [
                    cmdPath,
                    "/d",
                    "/c",
                    scriptPath
                ]);

            Assert.True(result.ProcessId > 0);
            processId = result.ProcessId;
            Assert.True(WaitUntil(
                () => File.Exists(stdoutPath) && ReadAllTextShared(stdoutPath).Contains(stdoutMarker, StringComparison.Ordinal),
                TimeSpan.FromSeconds(10)),
                $"stdout did not contain marker. child={DescribeProcess(processId.Value)} stdout={TryReadAllTextShared(stdoutPath)} stderr={TryReadAllTextShared(stderrPath)}");
            Assert.True(WaitUntil(
                () => File.Exists(stderrPath) && ReadAllTextShared(stderrPath).Contains(stderrMarker, StringComparison.Ordinal),
                TimeSpan.FromSeconds(10)),
                $"stderr did not contain marker. child={DescribeProcess(processId.Value)} stdout={TryReadAllTextShared(stdoutPath)} stderr={TryReadAllTextShared(stderrPath)}");
        }
        finally
        {
            if (processId is { } pid)
            {
                TryKillProcess(pid);
            }

            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "ConductorLoopHandoff_successor_survives_parent_job_exit_and_emits_loop_start")]
    public void ConductorLoopHandoffSuccessorSurvivesParentJobExitAndEmitsLoopStart()
    {
        if (!OperatingSystem.IsWindows())
        {
            // The production failure mode is Windows job-object kill-on-close inheritance.
            return;
        }

        var root = CreateTempDirectory("mcg-conduct-loop-runtime-handoff");
        var appAssembly = typeof(ConductorBatchLoop).Assembly.Location;
        Process? parent = null;
        int? successorPid = null;
        try
        {
            var startInfo = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = root
            };
            startInfo.ArgumentList.Add(appAssembly);
            startInfo.ArgumentList.Add("conduct");
            startInfo.ArgumentList.Add("--loop");
            startInfo.ArgumentList.Add("--daemon");
            startInfo.ArgumentList.Add("--watch");
            startInfo.ArgumentList.Add("--poll-seconds");
            startInfo.ArgumentList.Add("1");
            startInfo.ArgumentList.Add("--max-duration");
            startInfo.ArgumentList.Add("3");
            startInfo.ArgumentList.Add("--quiet");
            startInfo.Environment[OrchestratorWorkspace.RepoRootEnvironmentVariable] = root;
            startInfo.Environment[OrchestratorProjectRegistry.RegistryHomeEnvironmentVariable] = Path.Combine(root, "project-registry");
            startInfo.Environment["MCG_ORCHESTRATOR_CONDUCT_BATCH_NAME"] = "batch98";

            parent = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start parent conductor process.");
            var stdoutTask = parent.StandardOutput.ReadToEndAsync();
            var stderrTask = parent.StandardError.ReadToEndAsync();
            using (var parentJob = OwnedProcessGroup.Attach(parent))
            {
                Assert.True(parent.WaitForExit(30000), "Parent conductor did not reach max-duration handoff.");
                parentJob.Dispose();
            }

            var stdout = stdoutTask.GetAwaiter().GetResult();
            var stderr = stderrTask.GetAwaiter().GetResult();
            Assert.Equal(0, parent.ExitCode);
            successorPid = ParseHandoffProcessId(stdout);

            Assert.True(IsProcessRunning(successorPid.Value), $"Successor pid {successorPid.Value} did not survive parent job close. stdout={stdout} stderr={stderr}");
            Assert.Contains("guard=lease-released-before-launch", stdout, StringComparison.Ordinal);
            Assert.Contains("spawnPath=windows-createprocess", stdout, StringComparison.Ordinal);
            Assert.Contains("breakawayRequested=true", stdout, StringComparison.Ordinal);
            Assert.Contains("breakawaySucceeded=true", stdout, StringComparison.Ordinal);

            var logDirectory = Path.Combine(root, ".orchestrator", "logs");
            var conductEventsPath = Path.Combine(logDirectory, ConductEventLogWriter.CurrentFileName);
            Assert.True(WaitUntil(() =>
                File.Exists(conductEventsPath) &&
                ReadAllTextShared(conductEventsPath).Contains("LOOP_START", StringComparison.Ordinal),
                TimeSpan.FromSeconds(15)), $"Successor did not journal LOOP_START. stdout={stdout} stderr={stderr}");

            Assert.True(WaitUntil(
                () => Directory.GetFiles(logDirectory, "operator-batch99-*.out.log").Length > 0,
                TimeSpan.FromSeconds(10)),
                $"Successor did not create batch99 stdout log. stdout={stdout} stderr={stderr}");
        }
        finally
        {
            File.WriteAllText(Path.Combine(root, ConductorBatchLoop.StopFileName), "stop");
            if (successorPid is { } pid && !WaitUntil(() => !IsProcessRunning(pid), TimeSpan.FromSeconds(10)))
            {
                TryKillProcess(pid);
            }

            if (parent is not null)
            {
                TryKillProcess(parent.Id);
                parent.Dispose();
            }

            TryDeleteDirectory(root);
        }
    }

    private static int ParseHandoffProcessId(string output)
    {
        foreach (var line in output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.Contains("LOOP_HANDOFF tick=", StringComparison.Ordinal) ||
                !TryReadTokenValue(line, "pid=", out var pidText) ||
                !int.TryParse(pidText, out var pid))
            {
                continue;
            }

            return pid;
        }

        throw new InvalidOperationException("Parent conductor output did not include a LOOP_HANDOFF pid.");
    }

    private static bool TryReadTokenValue(string line, string token, out string value)
    {
        var start = line.IndexOf(token, StringComparison.Ordinal);
        if (start < 0)
        {
            value = string.Empty;
            return false;
        }

        start += token.Length;
        var end = line.IndexOf(' ', start);
        value = end < 0 ? line[start..] : line[start..end];
        return value.Length > 0;
    }

    private static bool WaitUntil(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (predicate())
            {
                return true;
            }

            Thread.Sleep(50);
        }

        return predicate();
    }

    private static string ReadAllTextShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string TryReadAllTextShared(string path)
    {
        try
        {
            return File.Exists(path) ? ReadAllTextShared(path) : "<missing>";
        }
        catch (Exception ex)
        {
            return $"<{ex.GetType().Name}:{ex.Message}>";
        }
    }

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static void TryKillProcess(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (ArgumentException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static string DescribeProcess(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (!process.WaitForExit(100))
            {
                return $"pid={processId} running";
            }

            return $"pid={processId} exit={process.ExitCode}";
        }
        catch (ArgumentException)
        {
            return $"pid={processId} missing";
        }
        catch (InvalidOperationException)
        {
            return $"pid={processId} unavailable";
        }
    }

    private static void StartProcess(
        AgentOrchestratorKernel kernel,
        Goal goal,
        TaskSpec task,
        DateTimeOffset dispatchedAt,
        string baseCommit,
        int processId = 111)
    {
        var root = Path.Combine(Path.GetTempPath(), $"mcg-watch-progress-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var command = $"worker {task.RequiredRole}";
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("test-worker", command, root, dispatchedAt, BaseCommit: baseCommit));
        kernel.RecordTaskProcessStarted(goal.Id, task.Id, new TaskProcessRecord(
            processId,
            command,
            root,
            Path.Combine(root, "out.log"),
            Path.Combine(root, "err.log"),
            Path.Combine(root, "exit.txt"),
            dispatchedAt,
            null,
            null,
            OwnedProcessIds: [processId]));
    }

    private static void CompleteDispatchedTask(
        AgentOrchestratorKernel kernel,
        Goal goal,
        TaskSpec task,
        DateTimeOffset completedAt,
        string resultCommit)
    {
        kernel.RecordDispatchResultCommit(goal.Id, task.Id, resultCommit);
        var process = kernel.GetTask(goal.Id, task.Id).LastProcess!;
        kernel.RecordTaskProcessRefreshed(
            goal.Id,
            task.Id,
            process with { CompletedAt = completedAt, ExitCode = 0 },
            null);
        kernel.RecordTaskVerification(
            goal.Id,
            task.Id,
            new TaskVerificationRecord("manual", process.WorkingDirectory, 0, "ok", "", completedAt));
    }

    private static ConductorAutonomyPolicy PolicyEscalatingAtVerified()
    {
        var transitionMap = ConductorAutonomyPolicy.Conservative.TransitionMap.ToDictionary();
        transitionMap[GoalLifecycleState.Verified] = ConductorTransitionDecision.Escalate;
        return ConductorAutonomyPolicy.Conservative with
        {
            Name = "VerifiedEscalates",
            TransitionMap = transitionMap
        };
    }

    private static ConductorWatchProgressReporter FakeWatchReporter(
        DateTimeOffset now,
        long stdoutBytes,
        long stderrBytes,
        TimeSpan idle,
        IReadOnlyList<int> ownedPids,
        IReadOnlyCollection<int> alivePids,
        IReadOnlyList<string> files) =>
        new(
            readHeartbeat: (process, observedAt) => Heartbeat(process, observedAt, stdoutBytes, stderrBytes, idle, ownedPids),
            readChanges: (_, _) => new DispatchLiveChangeSnapshot(files, files.Take(3).ToArray(), Math.Max(0, files.Count - 3)),
            isProcessAlive: alivePids.Contains,
            now: () => now);

    private static DispatchHeartbeatStatus Heartbeat(
        TaskProcessRecord process,
        DateTimeOffset observedAt,
        long stdoutBytes,
        long stderrBytes,
        TimeSpan idle,
        IReadOnlyList<int> ownedPids) =>
        new(
            BackgroundDispatchRunner.GetHeartbeatPath(process),
            true,
            null,
            process.ProcessId,
            null,
            ownedPids,
            "running",
            observedAt,
            observedAt - idle,
            TimeSpan.Zero,
            idle,
            stdoutBytes,
            stderrBytes);

    // ── Loop scheduling: loop advances while progress, stops when held ────

    [Xunit.Fact(DisplayName = "BatchLoop_goal_advances_then_stops_when_held")]
    public void BatchLoop_GoalAdvancesThenStopsWhenHeld()
    {
        var (kernel, _) = SimpleGoal();
        var advanceCalls = 0;

        // getFacts controls state: first call → Created (no workspace), subsequent → WorkspaceReady
        // getRunningCount escalates to cap on 3rd advance, causing Held
        var driver = MakeDriver(
            getFacts: _ => advanceCalls < 1 ? GoalLifecycleFacts.None : new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningCount: () => advanceCalls >= 2 ? ConductorAutonomyPolicy.Conservative.MaxConcurrentPaidWorkers : 0,
            createWorkspace: _ => { advanceCalls++; return "/tmp/ws"; },
            dispatchAndStart: _ => { advanceCalls++; return DispatchStartOutcome.Started(); });

        var stopFile = NoStopPath();
        var summary = new ConductorBatchLoop().Run(kernel, driver, ConductorAutonomyPolicy.Conservative, stopFile, maxIterations: 5);

        // Tick 1: Created → workspace created (Executed), advanceCalls=1
        // Tick 2: WorkspaceReady, runningCount<cap → dispatch (Executed), advanceCalls=2
        // Tick 3: WorkspaceReady, runningCount≥cap → Held → loop stops
        Assert.Equal(3, summary.Ticks);
        Assert.Equal(2, summary.Advanced);
        Assert.Equal(0, summary.Escalated);
        Assert.False(summary.StopRequested);
    }

    [Xunit.Fact(DisplayName = "BatchLoop_active_goal_advances_alongside_many_completed_goals")]
    public void BatchLoopActiveGoalAdvancesAlongsideManyCompletedGoals()
    {
        var kernel = new AgentOrchestratorKernel();
        var completedGoalIds = new HashSet<GoalId>();
        for (var i = 0; i < 100; i++)
        {
            var completed = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), $"completed goal {i}");
            PassVerification(kernel, completed, completed.Tasks.Single());
            kernel.CompleteGoal(completed.Id, "Test fixture: historical goal already landed, recorded, and cleaned up.");
            completedGoalIds.Add(completed.Id);
        }

        var active = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "active conductor goal");
        var createdWorkspaces = new List<GoalId>();
        var driver = MakeDriver(
            getFacts: goal => completedGoalIds.Contains(goal.Id)
                ? new GoalLifecycleFacts(IsMerged: true, IsRecorded: true, IsCleanedUp: true)
                : GoalLifecycleFacts.None,
            createWorkspace: goal =>
            {
                createdWorkspaces.Add(goal.Id);
                return "/tmp/workspace";
            });

        var summary = new ConductorBatchLoop().Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 1);

        Assert.Equal(1, summary.Advanced);
        Assert.Contains(active.Id, createdWorkspaces);
        Assert.DoesNotContain(createdWorkspaces, completedGoalIds.Contains);
    }

    [Xunit.Fact(DisplayName = "TerminalGoalSweep_skips_unchanged_terminal_goals_and_reports_cache_hits")]
    public void TerminalGoalSweepSkipsUnchangedTerminalGoalsAndReportsCacheHits()
    {
        var root = CreateTempDirectory("mcg-terminal-sweep-cache");
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var cancelled = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "historical cancelled goal");
            kernel.ReportTaskProgress(cancelled.Id, cancelled.Tasks.Single().Id, WorkTaskStatus.Cancelled, "Test fixture: task cancelled.");
            kernel.CancelGoal(cancelled.Id, "Test fixture: terminal and already clean.");
            var active = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "active conductor goal");
            var cache = new TerminalGoalSweepCache();
            BatchTickSummary? secondTick = null;

            var driver = MakeDriver(
                getFacts: goal => goal.Id == active.Id && goal.Tasks.Single().LastDispatch is not null
                    ? new GoalLifecycleFacts(WorkspaceExists: true)
                    : GoalLifecycleFacts.None,
                createWorkspace: _ => "/tmp/workspace",
                dispatchAndStart: goal =>
                {
                    var task = goal.Tasks.Single(task => task.Status == WorkTaskStatus.Assigned);
                    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("test-worker", "test.exe", "C:\\tmp", DateTimeOffset.UtcNow));
                    return DispatchStartOutcome.Started();
                });

            new ConductorBatchLoop(measuredSweep: loopKernel => TerminalGoalSweep.Run(loopKernel, root, cache: cache)).Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 2,
                onTick: tick =>
                {
                    if (tick.Tick == 2)
                    {
                        secondTick = tick;
                    }
                });

            Assert.Contains(secondTick!.ProgressLines!, line =>
                line.StartsWith("PHASE_TIMING tick=2 phase=sweep ", StringComparison.Ordinal) &&
                line.Contains("sweep_cache_hits=1", StringComparison.Ordinal));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "TerminalGoalSweep_goal_write_invalidates_terminal_cache_entry")]
    public void TerminalGoalSweepGoalWriteInvalidatesTerminalCacheEntry()
    {
        var root = CreateTempDirectory("mcg-terminal-sweep-invalidate");
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var cancelled = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "historical cancelled goal");
            kernel.ReportTaskProgress(cancelled.Id, cancelled.Tasks.Single().Id, WorkTaskStatus.Cancelled, "Test fixture: task cancelled.");
            kernel.CancelGoal(cancelled.Id, "Test fixture: terminal and already clean.");
            var cache = new TerminalGoalSweepCache();

            var first = TerminalGoalSweep.Run(kernel, root, cache: cache);
            var second = TerminalGoalSweep.Run(kernel, root, cache: cache);
            kernel.RecordGoalPolicyDecision(cancelled.Id, "Test fixture: goal write invalidates terminal sweep cache.");
            var third = TerminalGoalSweep.Run(kernel, root, cache: cache);

            Assert.Equal(1, first.CacheMissCount);
            Assert.Equal(1, second.CacheHitCount);
            Assert.Equal(0, third.CacheHitCount);
            Assert.Equal(1, third.CacheMissCount);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "TerminalGoalSweep_cache_persists_across_cache_instances")]
    public void TerminalGoalSweepCachePersistsAcrossCacheInstances()
    {
        var root = CreateSeededGitRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var cancelled = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "historical durable cache goal");
            kernel.ReportTaskProgress(cancelled.Id, cancelled.Tasks.Single().Id, WorkTaskStatus.Cancelled, "Test fixture: task cancelled.");
            kernel.CancelGoal(cancelled.Id, "Test fixture: terminal and already clean.");

            var first = TerminalGoalSweep.Run(kernel, root, cache: new TerminalGoalSweepCache());
            var second = TerminalGoalSweep.Run(kernel, root, cache: new TerminalGoalSweepCache());

            Assert.Equal(1, first.CacheMissCount);
            Assert.Equal(0, first.CacheHitCount);
            Assert.Equal(0, second.CacheMissCount);
            Assert.Equal(1, second.CacheHitCount);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "TerminalGoalSweep_durable_cache_invalidates_when_branch_evidence_changes")]
    public void TerminalGoalSweepDurableCacheInvalidatesWhenBranchEvidenceChanges()
    {
        var root = CreateSeededGitRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var cancelled = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "historical branch evidence goal");
            kernel.ReportTaskProgress(cancelled.Id, cancelled.Tasks.Single().Id, WorkTaskStatus.Cancelled, "Test fixture: task cancelled.");
            kernel.CancelGoal(cancelled.Id, "Test fixture: terminal and already clean.");

            var first = TerminalGoalSweep.Run(kernel, root, cache: new TerminalGoalSweepCache());
            var second = TerminalGoalSweep.Run(kernel, root, cache: new TerminalGoalSweepCache());
            RunGit(root, "branch", GoalWorktrees.BranchName(cancelled.Id));
            var third = TerminalGoalSweep.Run(kernel, root, cache: new TerminalGoalSweepCache());

            Assert.Equal(1, first.CacheMissCount);
            Assert.Equal(1, second.CacheHitCount);
            Assert.Equal(0, third.CacheHitCount);
            Assert.Equal(1, third.CacheMissCount);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "TerminalGoalSweep_corrupt_durable_cache_falls_back_to_rebuild")]
    public void TerminalGoalSweepCorruptDurableCacheFallsBackToRebuild()
    {
        var root = CreateTempDirectory("mcg-terminal-sweep-corrupt-cache");
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var cancelled = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "historical corrupt cache goal");
            kernel.ReportTaskProgress(cancelled.Id, cancelled.Tasks.Single().Id, WorkTaskStatus.Cancelled, "Test fixture: task cancelled.");
            kernel.CancelGoal(cancelled.Id, "Test fixture: terminal and already clean.");
            var cachePath = Path.Combine(root, ".orchestrator", "terminal-goal-sweep-cache.json");
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            File.WriteAllText(cachePath, "{not-json");

            var first = TerminalGoalSweep.Run(kernel, root, cache: new TerminalGoalSweepCache());
            var second = TerminalGoalSweep.Run(kernel, root, cache: new TerminalGoalSweepCache());

            Assert.Equal(1, first.CacheMissCount);
            Assert.Equal(0, first.CacheHitCount);
            Assert.Equal(0, second.CacheMissCount);
            Assert.Equal(1, second.CacheHitCount);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "TerminalGoalSweep_pending_cleanup_blocker_is_not_cache_skipped")]
    public void TerminalGoalSweepPendingCleanupBlockerIsNotCacheSkipped()
    {
        var root = CreateTempDirectory("mcg-terminal-sweep-cleanup-blocker");
        var originalDeleteDirectoryForCleanup = GoalWorktrees.DeleteDirectoryForCleanup;
        var originalWarnings = GoalWorktrees.CleanupWarningSink;
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var cancelled = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "historical cleanup blocker goal");
            kernel.ReportTaskProgress(cancelled.Id, cancelled.Tasks.Single().Id, WorkTaskStatus.Cancelled, "Test fixture: task cancelled.");
            kernel.CancelGoal(cancelled.Id, "Test fixture: terminal cleanup blocker remains pending.");
            var contextPath = Path.Combine(root, ".orchestrator-context", cancelled.Id.Value);
            Directory.CreateDirectory(contextPath);
            File.WriteAllText(Path.Combine(contextPath, "digest.md"), "digest");
            var attempts = 0;
            var cache = new TerminalGoalSweepCache();

            GoalWorktrees.DeleteDirectoryForCleanup = path =>
            {
                if (path.Equals(contextPath, StringComparison.OrdinalIgnoreCase))
                {
                    attempts++;
                    return GoalWorktreeDeleteResult.Failed(
                        GoalWorktreeDeleteFailureKind.Unknown,
                        "Directory deletion failed.");
                }

                return originalDeleteDirectoryForCleanup(path);
            };
            GoalWorktrees.CleanupWarningSink = _ => { };

            var first = TerminalGoalSweep.Run(kernel, root, cache: cache);
            var second = TerminalGoalSweep.Run(kernel, root, cache: cache);

            Assert.Equal(1, first.CacheMissCount);
            Assert.Equal(0, second.CacheHitCount);
            Assert.Equal(1, second.CacheMissCount);
            Assert.Equal(1, attempts);
            Assert.Contains(first.Goals, goal =>
                goal.GoalId == cancelled.Id &&
                goal.Blockers.Any(blocker => blocker.Kind == "owned-ephemeral-cleanup-needed"));
            Assert.Contains(second.Goals, goal =>
                goal.GoalId == cancelled.Id &&
                goal.Blockers.Any(blocker => blocker.Kind == "owned-ephemeral-cleanup-needed"));
        }
        finally
        {
            GoalWorktrees.DeleteDirectoryForCleanup = originalDeleteDirectoryForCleanup;
            GoalWorktrees.CleanupWarningSink = originalWarnings;
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "TerminalGoalSweep_cache_preserves_verified_missing_branch_repair")]
    public void TerminalGoalSweepCachePreservesVerifiedMissingBranchRepair()
    {
        var root = CreateSeededGitRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var verified = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "Verified missing branch goal");
            PassVerification(kernel, verified, verified.Tasks.Single());
            var cache = new TerminalGoalSweepCache();

            var sweep = TerminalGoalSweep.Run(kernel, root, cache: cache);

            Assert.Contains(sweep.Goals, goal =>
                goal.GoalId == verified.Id &&
                goal.Repairs.Any(repair => repair.Kind == "missing-branch-retired"));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "BatchLoop_dependent_goal_advances_when_completed_dependency_is_metadata_only")]
    public void BatchLoopDependentGoalAdvancesWhenCompletedDependencyIsMetadataOnly()
    {
        var completedDependencyId = GoalId.New();
        var kernel = new AgentOrchestratorKernel();
        var active = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "active dependent goal");
        kernel.ReplaceWithSnapshot(kernel.ExportSnapshot() with
        {
            Goals = kernel.ExportSnapshot().Goals
                .Select(goal => goal.Id == active.Id.Value
                    ? goal with { DependsOn = [completedDependencyId.Value] }
                    : goal)
                .ToArray()
        });
        kernel.MarkKnownCompletedDependencyGoals([completedDependencyId]);

        var createdWorkspaces = new List<GoalId>();
        var driver = MakeDriver(createWorkspace: goal =>
        {
            createdWorkspaces.Add(goal.Id);
            return "/tmp/workspace";
        });

        var summary = new ConductorBatchLoop().Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 1);

        Assert.Equal(1, summary.Advanced);
        Assert.Contains(active.Id, createdWorkspaces);
        Assert.Equal(0, summary.Held);
    }

    // ── Dynamic goal pickup: a goal ingested mid-run via the sweep is driven ──

    [Xunit.Fact(DisplayName = "BatchLoop_picks_up_a_goal_ingested_mid_run_via_the_sweep")]
    public void BatchLoop_PicksUpGoalIngestedMidRunViaSweep()
    {
        var kernel = new AgentOrchestratorKernel();
        var goalA = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "goal A");

        // A store snapshot carrying a brand-new goal B, simulating a goal submitted after the loop loaded.
        var store = new AgentOrchestratorKernel();
        var goalB = GoalLifecycleCommands.CreateAndActivateSimpleGoal(store, DefaultAgents(), "goal B");
        var snapshot = store.ExportSnapshot();

        var dispatchedIds = new List<string>();
        var ingestedOnce = false;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningCount: () => 0,
            dispatchAndStart: g =>
            {
                var task = g.Tasks.First(t => t.Status == WorkTaskStatus.Assigned);
                kernel.RecordTaskDispatch(g.Id, task.Id, new TaskDispatchRecord("test-worker", "test.exe", "C:\\tmp", DateTimeOffset.UtcNow));
                dispatchedIds.Add(g.Id.Value);
                return DispatchStartOutcome.Started();
            });

        // The sweep ingests B on the first tick — exactly how the live --loop pulls in newly-submitted goals.
        Action<AgentOrchestratorKernel> sweep = loopKernel =>
        {
            if (!ingestedOnce) { loopKernel.IngestNewGoals(snapshot); ingestedOnce = true; }
        };

        new ConductorBatchLoop(sweep).Run(
            kernel, driver, ConductorAutonomyPolicy.Conservative, NoStopPath(), maxIterations: 3);

        Assert.True(dispatchedIds.Contains(goalA.Id.Value));
        Assert.True(dispatchedIds.Contains(goalB.Id.Value)); // B was picked up mid-run and driven
    }

    [Xunit.Fact(DisplayName = "BatchLoop_refreshes_persisted_completion_and_dispatches_next_assigned_role")]
    public void BatchLoopRefreshesPersistedCompletionAndDispatchesNextAssignedRole()
    {
        var developerId = TaskId.New().Value;
        var testerId = TaskId.New().Value;
        var kernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot(
            [
                new GoalSnapshot(
                    "goal-role-handoff",
                    "Dispatch next role after persisted completion",
                    GoalStatus.Active,
                    [
                        new TaskSnapshot(developerId, "Implement.", AgentRole.Developer, WorkTaskStatus.Assigned, null, null, null, [], null, null),
                        new TaskSnapshot(testerId, "Test.", AgentRole.Tester, WorkTaskStatus.Assigned, null, null, null, [], null, null)
                    ],
                    [])
            ],
            []));
        var store = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot(
            [
                new GoalSnapshot(
                    "goal-role-handoff",
                    "Dispatch next role after persisted completion",
                    GoalStatus.Active,
                    [
                        new TaskSnapshot(developerId, "Implement.", AgentRole.Developer, WorkTaskStatus.Completed, null, null, null, [], null, null),
                        new TaskSnapshot(testerId, "Test.", AgentRole.Tester, WorkTaskStatus.Assigned, null, null, null, [], null, null)
                    ],
                    [])
            ],
            []));
        AgentRole? dispatchedRole = null;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: goal =>
            {
                var task = goal.Tasks.First(task => task.Status == WorkTaskStatus.Assigned);
                dispatchedRole = task.RequiredRole;
                kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("stub-worker", "stub", "C:\\tmp", DateTimeOffset.UtcNow));
                return DispatchStartOutcome.Started();
            });

        new ConductorBatchLoop(loopKernel => loopKernel.RefreshTrackedGoals(store.ExportSnapshot())).Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 1);

        var goal = kernel.Goals.Single();
        Assert.Equal(AgentRole.Tester, dispatchedRole);
        Assert.Equal(WorkTaskStatus.Completed, goal.Tasks.Single(task => task.Id.Value == developerId).Status);
        Assert.Equal(WorkTaskStatus.Running, goal.Tasks.Single(task => task.Id.Value == testerId).Status);
    }

    [Xunit.Fact(DisplayName = "BatchLoop_keepAliveWhenIdle_stays_running_and_drives_a_goal_that_arrives_later")]
    public void BatchLoop_KeepAliveStaysRunningAndDrivesLaterGoal()
    {
        var kernel = new AgentOrchestratorKernel(); // starts with NO goals — a one-shot loop would exit immediately

        var store = new AgentOrchestratorKernel();
        var goalB = GoalLifecycleCommands.CreateAndActivateSimpleGoal(store, DefaultAgents(), "goal B");
        var snapshot = store.ExportSnapshot();

        var dispatchedIds = new List<string>();
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningCount: () => 0,
            dispatchAndStart: g =>
            {
                var task = g.Tasks.First(t => t.Status == WorkTaskStatus.Assigned);
                kernel.RecordTaskDispatch(g.Id, task.Id, new TaskDispatchRecord("test-worker", "test.exe", "C:\\tmp", DateTimeOffset.UtcNow));
                dispatchedIds.Add(g.Id.Value);
                return DispatchStartOutcome.Started();
            });

        // The goal arrives on the 2nd sweep — i.e. AFTER the loop has already gone idle at least once.
        var sweeps = 0;
        Action<AgentOrchestratorKernel> sweep = loopKernel =>
        {
            sweeps++;
            if (sweeps == 2) { loopKernel.IngestNewGoals(snapshot); }
        };

        // sleepFunc bounds the test: it returns "stop" after a handful of idle/no-progress sleeps, so the
        // loop terminates even though keep-alive would otherwise poll forever on an empty backlog.
        var sleepCalls = 0;
        Func<TimeSpan, bool> sleepFunc = _ => ++sleepCalls >= 6;

        new ConductorBatchLoop(sweep).Run(
            kernel, driver, ConductorAutonomyPolicy.Conservative, NoStopPath(),
            maxIterations: 10,
            watchInterval: TimeSpan.FromSeconds(1),
            sleepFunc: sleepFunc,
            keepAliveWhenIdle: true);

        // Without keep-alive the loop would have exited on the first empty tick and never seen B.
        Assert.True(dispatchedIds.Contains(goalB.Id.Value));
    }

    // ── Loop scheduling: concurrent cap limits active dispatches ─────────

    [Xunit.Fact(DisplayName = "BatchLoop_cap_holds_third_goal_when_two_already_dispatched")]
    public void BatchLoop_CapHoldsThirdGoalWhenTwoAlreadyDispatched()
    {
        var kernel = new AgentOrchestratorKernel();
        // 3 goals all in WorkspaceReady state
        for (var i = 0; i < 3; i++)
            GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), $"goal {i}");

        var dispatched = 0;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningCount: () => dispatched,
            dispatchAndStart: g =>
            {
                var task = g.Tasks.First(t => t.Status == WorkTaskStatus.Assigned);
                kernel.RecordTaskDispatch(g.Id, task.Id, new TaskDispatchRecord("test-worker", "test.exe", "C:\\tmp", DateTimeOffset.UtcNow));
                dispatched++;
                return DispatchStartOutcome.Started();
            });

        var stopFile = NoStopPath();
        var policy = ConductorAutonomyPolicy.Conservative;
        var expectedDispatches = Math.Min(3, policy.MaxConcurrentPaidWorkers);
        var summary = new ConductorBatchLoop().Run(kernel, driver, policy, stopFile);

        Assert.True(summary.Advanced >= expectedDispatches);
        Assert.Equal(expectedDispatches, dispatched);
    }

    // ── Persistence: a loop dispatch must be durable across reload ────────
    // Regression for the autonomy-blocker found 2026-06-18. `conduct --loop[ --watch]` runs the
    // ENTIRE loop inside one state transaction (CliPersistentStateRunner.TransactAsync), which
    // only commits when the command returns. ConductorBatchLoop.Run never persists per tick, so
    // a long-running watch loop never commits its dispatches and a killed loop rolls them all
    // back. Observed live: Researcher 5825a584 ran four times on disk (exit 0 each) yet the
    // timeline shows zero TaskDispatchRecorded/TaskProcessStarted events after the Planner — so
    // every tick re-dispatched it and the goal could never advance.
    // The fix runs the loop outside the transaction and checkpoints each tick via persistTick, so a
    // started dispatch is durable the moment its tick completes. This test fails if that wiring breaks.
    [Xunit.Fact(DisplayName = "ConductorBatchLoop_persists_each_tick_so_dispatch_survives_reload")]
    public async Task LoopPersistsEachTickDispatchSurvivesReload()
    {
        var db = Path.Combine(Path.GetTempPath(), $"mcg-loop-persist-{Guid.NewGuid():N}.db");
        var repo = new SqliteOrchestratorStateRepository(db);

        // Seed a single-task goal and commit it.
        GoalId goalId = default;
        TaskId taskId = default;
        await repo.TransactAsync((k, _) =>
        {
            var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(k, DefaultAgents(), "Durable dispatch goal");
            goalId = goal.Id;
            taskId = goal.Tasks.Single().Id;
            return Task.FromResult((true, true));
        });

        // Drive the loop as the fixed conduct --loop path does: load the kernel, then run the loop
        // outside the wrapping transaction so each tick can persist independently.
        var kernel = await repo.LoadAsync();
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: g =>
            {
                var task = g.Tasks.Single();
                kernel.RecordTaskDispatch(g.Id, task.Id,
                    new TaskDispatchRecord("claude-cli", "claude -p plan", "C:\\wt", DateTimeOffset.UtcNow));
                kernel.RecordTaskProcessStarted(g.Id, task.Id,
                    new TaskProcessRecord(4242, "claude -p plan", "C:\\wt", "out.log", "err.log", "exit.txt",
                        DateTimeOffset.UtcNow, null, null));
                return DispatchStartOutcome.Started();
            });

        // The loop runs outside any transaction and checkpoints each tick via persistTick. The
        // process then "dies" with no final save, so durability must come from the per-tick save.
        new ConductorBatchLoop().Run(kernel, driver, ConductorAutonomyPolicy.Conservative, NoStopPath(),
            maxIterations: 1,
            persistTick: k => repo.SaveAsync(k).GetAwaiter().GetResult());

        // The dispatch the loop performed must survive so reconcile can recognize it instead of
        // re-dispatching the same task forever.
        // If LastProcess is null after reload, the loop's dispatch was never persisted — so the
        // conductor re-dispatches the same task on every tick and the goal can never advance.
        var reloaded = await repo.LoadAsync();
        var task = reloaded.GetTask(goalId, taskId);
        Assert.True(task.LastProcess is not null);
    }

    [Xunit.Fact(DisplayName = "ConductorBatchLoop_critical_dispatch_start_retries_and_persists_dispatch_records_before_slot_release")]
    public async Task CriticalDispatchStartRetriesAndPersistsDispatchRecordsBeforeSlotRelease()
    {
        var db = Path.Combine(Path.GetTempPath(), $"mcg-loop-dispatch-start-{Guid.NewGuid():N}.db");
        var repo = new SqliteOrchestratorStateRepository(db);

        GoalId goalId = default;
        TaskId taskId = default;
        await repo.TransactAsync((k, _) =>
        {
            var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(k, DefaultAgents(), "Critical dispatch start goal");
            goalId = goal.Id;
            taskId = goal.Tasks.Single().Id;
            return Task.FromResult((true, true));
        });

        var kernel = await repo.LoadAsync();
        var attempts = 0;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: g =>
            {
                var task = g.Tasks.Single();
                kernel.RecordTaskDispatch(g.Id, task.Id,
                    new TaskDispatchRecord("claude-cli", "claude -p plan", "C:\\wt", DateTimeOffset.UtcNow));
                kernel.RecordTaskProcessStarted(g.Id, task.Id,
                    new TaskProcessRecord(4242, "claude -p plan", "C:\\wt", "out.log", "err.log", "exit.txt",
                        DateTimeOffset.UtcNow, null, null));
                ConductorBatchLoop.PersistCriticalDispatchStartOrThrow(
                    (checkpoint, changedGoalIds) =>
                    {
                        attempts++;
                        if (attempts < 3)
                            throw SqliteBusy();
                        var changed = changedGoalIds.Select(id => id.Value).ToHashSet(StringComparer.Ordinal);
                        var snaps = checkpoint.ExportSnapshot().Goals
                            .Where(goal => changed.Contains(goal.Id))
                            .ToArray();
                        repo.SaveGoalSnapshotsAsync(snaps, CancellationToken.None).GetAwaiter().GetResult();
                    },
                    kernel,
                    g.Id,
                    task.Id);
                return DispatchStartOutcome.Started();
            });

        new ConductorBatchLoop().Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 1,
            persistGoalTick: (_, _) => { });

        var reloaded = await repo.LoadAsync();
        var task = reloaded.GetTask(goalId, taskId);
        Assert.Equal(3, attempts);
        Assert.True(task.LastDispatch is not null);
        Assert.True(task.LastProcess is not null);
        Assert.Equal(1, reloaded.Goals.Single(g => g.Id == goalId).Timeline.Count(evt =>
            evt.TaskId == taskId && evt.Kind == ProgressKind.TaskDispatchRecorded));
        Assert.Equal(1, reloaded.Goals.Single(g => g.Id == goalId).Timeline.Count(evt =>
            evt.TaskId == taskId && evt.Kind == ProgressKind.TaskProcessStarted));
    }

    [Xunit.Fact(DisplayName = "ConductorBatchLoop_final_checkpoint_preserves_cli_retry_written_after_last_tick")]
    public async Task FinalCheckpointPreservesCliRetryWrittenAfterLastTick()
    {
        var db = Path.Combine(Path.GetTempPath(), $"mcg-loop-final-checkpoint-merge-{Guid.NewGuid():N}.db");
        var repo = new SqliteOrchestratorStateRepository(db);
        var developerTaskId = TaskId.New();
        var testerTaskId = TaskId.New();
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Protect final checkpoint retry", [
            new TaskSpec(developerTaskId, "Implement final checkpoint persistence", AgentRole.Developer),
            new TaskSpec(testerTaskId, "Test final checkpoint persistence", AgentRole.Tester)
        ]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        kernel.RecordTaskVerification(
            goal.Id,
            testerTaskId,
            new TaskVerificationRecord("manual", "C:\\repo", 0, "passed", "", DateTimeOffset.UtcNow));
        await repo.SaveAsync(kernel);

        kernel = await repo.LoadAsync();
        var baselines = kernel.ExportSnapshot().Goals.ToDictionary(snapshot => snapshot.Id, StringComparer.Ordinal);
        var persistCalls = 0;
        var injectedRetry = false;
        void PersistGoalTick(AgentOrchestratorKernel checkpoint, IReadOnlyCollection<GoalId> changedGoalIds)
        {
            persistCalls++;
            if (persistCalls == 2 && !injectedRetry)
            {
                injectedRetry = true;
                repo.TransactGoalAsync(goal.Id, (storedSnapshot, _) =>
                {
                    var storedKernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([storedSnapshot!], []));
                    storedKernel.RetryTask(goal.Id, testerTaskId, "operator retry between last tick and final checkpoint");
                    return Task.FromResult((
                        true,
                        storedKernel.ExportSnapshot().Goals.Single(),
                        true));
                }).GetAwaiter().GetResult();
            }

            var changed = changedGoalIds.Select(id => id.Value).ToHashSet(StringComparer.Ordinal);
            var requests = checkpoint.ExportSnapshot().Goals
                .Where(snapshot => changed.Contains(snapshot.Id))
                .Select(snapshot => new GoalSnapshotSaveRequest(baselines[snapshot.Id], snapshot))
                .ToArray();
            var results = repo.SaveGoalSnapshotsWithMergeAsync(requests).GetAwaiter().GetResult();
            foreach (var result in results)
            {
                if (result.PersistedSnapshot is not null)
                    baselines[result.GoalId] = result.PersistedSnapshot;
            }
        }

        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: g =>
            {
                kernel.RecordTaskDispatch(g.Id, developerTaskId,
                    new TaskDispatchRecord("claude-cli", "claude -p work", "C:\\wt", DateTimeOffset.UtcNow));
                kernel.RecordTaskProcessStarted(g.Id, developerTaskId,
                    new TaskProcessRecord(4242, "claude -p work", "C:\\wt", "out.log", "err.log", "exit.txt",
                        DateTimeOffset.UtcNow, null, null));
                return DispatchStartOutcome.Started();
            });

        new ConductorBatchLoop().Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 1,
            persistGoalTick: PersistGoalTick);

        var reloaded = await repo.LoadAsync();
        var developerTask = reloaded.GetTask(goal.Id, developerTaskId);
        var testerTask = reloaded.GetTask(goal.Id, testerTaskId);
        Assert.True(injectedRetry);
        Assert.True(persistCalls >= 2);
        Assert.Equal(WorkTaskStatus.Running, developerTask.Status);
        Assert.NotNull(developerTask.LastProcess);
        Assert.Equal(WorkTaskStatus.Assigned, testerTask.Status);
        Assert.Contains(reloaded.GetGoal(goal.Id).Timeline, evt =>
            evt.TaskId == testerTaskId &&
            evt.Kind == ProgressKind.TaskRetried &&
            evt.Message.Contains("operator retry between last tick and final checkpoint", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "ConductorBatchLoop_critical_dispatch_start_exhaustion_persists_neither_record")]
    public async Task CriticalDispatchStartExhaustionPersistsNeitherRecord()
    {
        var db = Path.Combine(Path.GetTempPath(), $"mcg-loop-dispatch-start-fail-{Guid.NewGuid():N}.db");
        var repo = new SqliteOrchestratorStateRepository(db);

        GoalId goalId = default;
        TaskId taskId = default;
        await repo.TransactAsync((k, _) =>
        {
            var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(k, DefaultAgents(), "Critical dispatch start failure goal");
            goalId = goal.Id;
            taskId = goal.Tasks.Single().Id;
            return Task.FromResult((true, true));
        });

        var kernel = await repo.LoadAsync();
        var task = kernel.GetTask(goalId, taskId);
        kernel.RecordTaskDispatch(goalId, taskId,
            new TaskDispatchRecord("claude-cli", "claude -p plan", "C:\\wt", DateTimeOffset.UtcNow));
        kernel.RecordTaskProcessStarted(goalId, taskId,
            new TaskProcessRecord(4242, "claude -p plan", "C:\\wt", "out.log", "err.log", "exit.txt",
                DateTimeOffset.UtcNow, null, null));

        var attempts = 0;
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ConductorBatchLoop.PersistCriticalDispatchStartOrThrow(
                (_, _) =>
                {
                    attempts++;
                    throw SqliteBusy();
                },
                kernel,
                goalId,
                task.Id));

        var reloaded = await repo.LoadAsync();
        var reloadedTask = reloaded.GetTask(goalId, taskId);
        Assert.Contains("DISPATCH_RECORD_WRITE_FAILED", ex.Message, StringComparison.Ordinal);
        Assert.Equal(ConductorBatchLoop.DefaultMaxBusyWriteAttempts, attempts);
        Assert.True(reloadedTask.LastDispatch is null);
        Assert.True(reloadedTask.LastProcess is null);
    }

    // ── Auto-retry: transient acceptance flake recovers on retry ─────────

    [Xunit.Fact(DisplayName = "BatchLoop_AutoRetry_flakeOnFirstAttempt_recoverOnRetry")]
    public void BatchLoop_AutoRetry_FlakeOnFirstAttemptRecoverOnRetry()
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single()); // goal → Completed → Verified state

        var attempts = 0;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true), // IsMerged=false → Verified state
            runAcceptance: _ => { attempts++; return attempts > 1; }, // fail 1st, pass on retry
            writeEscalation: (_, _, _) => { });

        var stopFile = NoStopPath();
        // 1 tick with maxIterations=1: initial fail → 1 retry (passes) → Advanced
        var summary = new ConductorBatchLoop().Run(kernel, driver, ConductorAutonomyPolicy.Conservative, stopFile, maxIterations: 1);

        Assert.Equal(2, attempts);      // initial + 1 retry
        Assert.Equal(1, summary.Retried);
        Assert.Equal(0, summary.Escalated);
        Assert.Equal(1, summary.Advanced);
    }

    // ── Auto-retry: persistent failure escalates after N retries ─────────

    [Xunit.Fact(DisplayName = "BatchLoop_AutoRetry_persistentFailureEscalatesAfterNRetries")]
    public void BatchLoop_AutoRetry_PersistentFailureEscalatesAfterNRetries()
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());

        var attempts = 0;
        var escalationWritten = false;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            runAcceptance: _ => { attempts++; return false; }, // always fail
            writeEscalation: (_, _, _) => { escalationWritten = true; });

        var stopFile = NoStopPath();
        // maxVerifyRetries=2 → 1 initial + 2 retries = 3 total acceptance calls before escalation
        var summary = new ConductorBatchLoop().Run(kernel, driver, ConductorAutonomyPolicy.Conservative, stopFile,
            maxIterations: 1, maxVerifyRetries: 2);

        Assert.Equal(3, attempts);      // 1 initial + 2 retries
        Assert.Equal(2, summary.Retried);
        Assert.True(escalationWritten);
        Assert.Equal(1, summary.Escalated);
    }

    [Xunit.Fact(DisplayName = "BatchLoop_skips_prior_verified_acceptance_escalation_without_spending_retry_budget")]
    public void BatchLoopSkipsPriorVerifiedAcceptanceEscalationWithoutSpendingRetryBudget()
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());
        kernel.RecordGoalPolicyDecision(goal.Id, "Batch loop tick 1: escalated at Verified — Acceptance verification failed");

        var acceptanceAttempts = 0;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptance: _ =>
            {
                acceptanceAttempts++;
                return false;
            });

        var summary = new ConductorBatchLoop().Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 1,
            maxVerifyRetries: 2);

        Assert.Equal(0, acceptanceAttempts);
        Assert.Equal(0, summary.Retried);
        Assert.Equal(1, summary.Escalated);
        Assert.Equal(0, summary.Advanced);
    }

    [Xunit.Fact(DisplayName = "BatchLoop_treats_cleaned_up_goal_as_done_despite_prior_verified_acceptance_escalation")]
    public void BatchLoopTreatsCleanedUpGoalAsDoneDespitePriorVerifiedAcceptanceEscalation()
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());
        kernel.RecordGoalPolicyDecision(goal.Id, "Batch loop tick 1: escalated at Verified — Acceptance verification failed");

        var acceptanceAttempts = 0;
        var ticks = new List<BatchTickSummary>();
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(IsMerged: true, IsRecorded: true, IsCleanedUp: true),
            runAcceptance: _ =>
            {
                acceptanceAttempts++;
                return false;
            });

        var summary = new ConductorBatchLoop().Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 1,
            maxVerifyRetries: 2,
            onTick: ticks.Add);

        Assert.Equal(0, acceptanceAttempts);
        Assert.Equal(0, summary.Retried);
        Assert.Equal(0, summary.Escalated);
        Assert.Equal(0, summary.Advanced);
        Assert.Empty(ticks);
    }

    [Xunit.Fact(DisplayName = "BatchLoop_operator_retry_clears_prior_verified_acceptance_escalation")]
    public void BatchLoopOperatorRetryClearsPriorVerifiedAcceptanceEscalation()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        PassVerification(kernel, goal, task);
        kernel.RecordGoalPolicyDecision(goal.Id, "Batch loop tick 1: escalated at Verified — Acceptance verification failed");
        kernel.RetryTask(goal.Id, task.Id, "Operator retry after fixing acceptance failure.");

        var dispatches = 0;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: g =>
            {
                dispatches++;
                kernel.RecordTaskDispatch(g.Id, task.Id,
                    new TaskDispatchRecord("test-worker", "test.exe", "C:\\goal", DateTimeOffset.UtcNow));
                return DispatchStartOutcome.Started();
            });

        var summary = new ConductorBatchLoop().Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 1);

        Assert.Equal(1, dispatches);
        Assert.Equal(1, summary.Advanced);
        Assert.Equal(0, summary.Escalated);
        Assert.Equal(0, summary.Retried);
    }

    // ── Regression: flake recovery followed by ownership-blocked dispatch must not escalate ──
    // Before Fix 1+4, the conductor auto-recovered a watchdog-reaped Developer dispatch via
    // _retryTask (setting the task to Assigned), then on the NEXT tick called _dispatchAndStart
    // which returned EmptyBatch (high-risk ownership under Conservative policy). The empty-batch
    // path immediately escalated → SetAside(LifecycleEscalation) → permanently stuck, even though
    // `readiness` said "Proceed". Fix 1: dispatch in the SAME tick as recovery. Fix 4: on
    // EmptyBatch with assigned tasks, return Held instead of Escalate.

    [Xunit.Fact(DisplayName = "BatchLoop_flake_recovery_then_empty_batch_holds_not_escalates_no_paid_worker")]
    public void BatchLoopFlakeRecoveryThenEmptyBatchHoldsNotEscalatesNoPaidWorker()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("src/Mcg.AgentOrchestrator.Core/Fix something");
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var planner = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Planner);
        var researcher = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Researcher);
        var developer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Developer);
        var now = DateTimeOffset.UtcNow;

        // Advance Planner/Researcher to Completed so Developer is the current stage.
        kernel.ReportTaskProgress(goal.Id, planner.Id, WorkTaskStatus.Completed, "Planner done.");
        kernel.ReportTaskProgress(goal.Id, researcher.Id, WorkTaskStatus.Completed, "Researcher done.");

        // Simulate a watchdog reap: Developer was dispatched but produced zero-byte stdout (empty flake).
        kernel.RecordTaskDispatch(goal.Id, developer.Id,
            new TaskDispatchRecord("test-worker", "dev.exe", "C:\\goal", now));
        kernel.RecordDispatchExecutionResult(goal.Id, developer.Id,
            new TaskVerificationRecord("dev.exe", "C:\\goal", 1, "", "", now));

        Assert.Equal(WorkTaskStatus.Failed, kernel.GetTask(goal.Id, developer.Id).Status);
        Assert.Equal(1, developer.EmptyOutputRetryCount);

        var escalated = false;
        var dispatchAttempts = 0;
        var paidWorkerCount = 0;

        // _dispatchAndStart returns EmptyBatch to simulate high-risk ownership blocking
        // under a Conservative policy (the typical trigger for this bug).
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningCount: () => paidWorkerCount,
            dispatchAndStart: _ =>
            {
                dispatchAttempts++;
                return DispatchStartOutcome.EmptyBatch(
                    "No tasks dispatched; all ready tasks require operator approval: high-risk ownership (src/Mcg.AgentOrchestrator.Core/)");
            },
            writeEscalation: (_, _, _) => { escalated = true; },
            retryTask: (gid, tid, msg) => kernel.RetryTask(gid, tid, msg));

        // Run one tick: Failed state → flake recovery → immediate dispatch attempt → EmptyBatch → Held.
        var summary = new ConductorBatchLoop().Run(
            kernel, driver, ConductorAutonomyPolicy.Conservative,
            NoStopPath(), maxIterations: 1);

        Assert.False(escalated); // must not escalate when empty batch follows flake recovery
        Assert.Equal(0, paidWorkerCount); // no paid worker started (dispatch returned EmptyBatch)
        Assert.Equal(1, dispatchAttempts); // dispatch was attempted in the same tick as recovery (Fix 1)
        Assert.Equal(0, summary.Escalated);
        Assert.Equal(WorkTaskStatus.Assigned, kernel.GetTask(goal.Id, developer.Id).Status);
    }

    [Xunit.Fact(DisplayName = "BatchLoop_readmits_awaiting_clarification_goal_when_blocker_clears")]
    public void BatchLoopReadmitsAwaitingClarificationGoalWhenBlockerClears()
    {
        var (kernel, goal) = SimpleGoal();
        var hasOpenClarification = true;
        var workspaceCreates = 0;
        var escalations = 0;

        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(HasOpenClarification: hasOpenClarification),
            createWorkspace: _ =>
            {
                workspaceCreates++;
                return "C:\\goal";
            },
            writeEscalation: (_, _, _) => escalations++);

        var summary = new ConductorBatchLoop().Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 2,
            watchInterval: TimeSpan.FromMilliseconds(1),
            sleepFunc: _ =>
            {
                hasOpenClarification = false;
                return false;
            });

        Assert.Equal(2, summary.Ticks);
        Assert.Equal(1, summary.Escalated);
        Assert.Equal(1, summary.Advanced);
        Assert.Equal(1, escalations);
        Assert.Equal(1, workspaceCreates);
    }

    [Xunit.Fact(DisplayName = "BatchLoop_readmits_escalated_goal_when_task_state_changes_mid_run")]
    public void BatchLoopReadmitsEscalatedGoalWhenTaskStateChangesMidRun()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Failed, "Needs operator repair.");
        var repaired = false;
        var escalations = 0;
        var workspaceCreates = 0;

        var driver = MakeDriver(
            createWorkspace: _ =>
            {
                workspaceCreates++;
                return "C:\\goal";
            },
            writeEscalation: (_, _, _) => escalations++);

        var summary = new ConductorBatchLoop().Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 2,
            watchInterval: TimeSpan.FromMilliseconds(1),
            sleepFunc: _ =>
            {
                if (!repaired)
                {
                    kernel.RetryTask(goal.Id, task.Id, "Operator repaired failed task.");
                    repaired = true;
                }

                return false;
            });

        Assert.Equal(2, summary.Ticks);
        Assert.Equal(1, summary.Escalated);
        Assert.Equal(1, summary.Advanced);
        Assert.Equal(1, escalations);
        Assert.Equal(1, workspaceCreates);
        Assert.Contains(goal.Timeline, evt =>
            evt.Kind == ProgressKind.GoalPolicyDecision &&
            evt.Message.Contains("re-admitted escalated goal after state changed", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "BatchLoop_keeps_unchanged_escalated_goal_set_aside_without_reescalating")]
    public void BatchLoopKeepsUnchangedEscalatedGoalSetAsideWithoutReescalating()
    {
        var kernel = new AgentOrchestratorKernel();
        var failedGoal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "failed goal");
        var heldGoal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "held goal");
        var failedTask = failedGoal.Tasks.Single();
        kernel.ReportTaskProgress(failedGoal.Id, failedTask.Id, WorkTaskStatus.Failed, "Needs operator repair.");
        var escalations = 0;
        var heldAttempts = 0;

        var driver = MakeDriver(
            getFacts: goal => goal.Id == heldGoal.Id
                ? new GoalLifecycleFacts(WorkspaceExists: true)
                : GoalLifecycleFacts.None,
            dispatchAndStart: goal =>
            {
                if (goal.Id == heldGoal.Id)
                {
                    heldAttempts++;
                    return DispatchStartOutcome.EmptyBatch("Held for operator approval.");
                }

                return DispatchStartOutcome.Started();
            },
            writeEscalation: (_, _, _) => escalations++);

        var summary = new ConductorBatchLoop().Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 3,
            watchInterval: TimeSpan.FromMilliseconds(1),
            sleepFunc: _ => false);

        Assert.Equal(3, summary.Ticks);
        Assert.Equal(1, summary.Escalated);
        Assert.Equal(1, escalations);
        Assert.Equal(3, heldAttempts);
    }

    [Xunit.Fact(DisplayName = "BatchLoop_keeps_unresolved_clarification_set_aside_without_reescalating")]
    public void BatchLoopKeepsUnresolvedClarificationSetAsideWithoutReescalating()
    {
        var kernel = new AgentOrchestratorKernel();
        var blockedGoal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "blocked goal");
        var activeGoal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "active goal");
        var workspaces = new HashSet<string>(StringComparer.Ordinal);
        var blockedClarificationPolls = 0;
        var escalations = 0;
        var dispatches = 0;

        var driver = MakeDriver(
            getFacts: goal =>
            {
                if (goal.Id == blockedGoal.Id)
                {
                    blockedClarificationPolls++;
                    return new GoalLifecycleFacts(HasOpenClarification: true);
                }

                return new GoalLifecycleFacts(WorkspaceExists: workspaces.Contains(goal.Id.Value));
            },
            createWorkspace: goal =>
            {
                workspaces.Add(goal.Id.Value);
                return "C:\\active";
            },
            dispatchAndStart: goal =>
            {
                if (goal.Id == activeGoal.Id)
                {
                    var task = goal.Tasks.Single();
                    kernel.RecordTaskDispatch(goal.Id, task.Id,
                        new TaskDispatchRecord("test-worker", "test.exe", "C:\\active", DateTimeOffset.UtcNow));
                    kernel.RecordTaskProcessStarted(goal.Id, task.Id,
                        new TaskProcessRecord(123, "test.exe", "C:\\active", "out.log", "err.log", "exit.txt",
                            DateTimeOffset.UtcNow, null, null));
                    dispatches++;
                }

                return DispatchStartOutcome.Started();
            },
            writeEscalation: (_, _, _) => escalations++);

        var summary = new ConductorBatchLoop().Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 3,
            watchInterval: TimeSpan.FromMilliseconds(1),
            sleepFunc: _ => false);

        Assert.Equal(3, summary.Ticks);
        Assert.Equal(1, summary.Escalated);
        Assert.Equal(1, escalations);
        Assert.Equal(1, dispatches);
        Assert.True(blockedClarificationPolls >= 3);
    }

    [Xunit.Fact(DisplayName = "BatchLoop_escalated_goal_reaps_only_its_owned_running_dispatches")]
    public void BatchLoopEscalatedGoalReapsOnlyItsOwnedRunningDispatches()
    {
        var kernel = new AgentOrchestratorKernel();
        var escalatedGoal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "escalated goal");
        var otherGoal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "other goal");
        var escalatedTask = escalatedGoal.Tasks.Single();
        var otherTask = otherGoal.Tasks.Single();
        var now = DateTimeOffset.UtcNow;

        kernel.RecordTaskDispatch(escalatedGoal.Id, escalatedTask.Id,
            new TaskDispatchRecord("test-worker", "test.exe", "C:\\escalated", now));
        kernel.RecordTaskProcessStarted(escalatedGoal.Id, escalatedTask.Id,
            new TaskProcessRecord(111, "test.exe", "C:\\escalated", "out.log", "err.log", "exit.txt",
                now, null, null, OwnedProcessIds: [111, 222]));
        kernel.RecordTaskDispatch(otherGoal.Id, otherTask.Id,
            new TaskDispatchRecord("test-worker", "other.exe", "C:\\other", now));
        kernel.RecordTaskProcessStarted(otherGoal.Id, otherTask.Id,
            new TaskProcessRecord(333, "other.exe", "C:\\other", "out.log", "err.log", "exit.txt",
                now, null, null, OwnedProcessIds: [333]));

        var killed = new List<int>();
        var runner = new BackgroundDispatchRunner(
            isStillRunning: _ => false,
            tryKillOwnedProcess: pid =>
            {
                killed.Add(pid);
                return true;
            });
        var driver = MakeDriver(getFacts: _ => throw new InvalidOperationException("policy gate"));

        var summary = new ConductorBatchLoop(
            reapGoalRunningDispatches: (loopKernel, goal) => runner.CancelRunningProcessesForGoal(loopKernel, goal.Id)).Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                onlyGoalId: escalatedGoal.Id.Value);

        Assert.Equal(1, summary.Escalated);
        Xunit.Assert.Equal([111, 222], killed);
        Assert.True(kernel.GetTask(escalatedGoal.Id, escalatedTask.Id).LastProcess!.WasCancelled);
        Assert.False(kernel.GetTask(otherGoal.Id, otherTask.Id).LastProcess!.WasCancelled);
    }

    // ── Kill-switch: stop file present → loop exits before first tick ─────

    [Xunit.Fact(DisplayName = "BatchLoop_KillSwitch_stopFilePresent_exitsBeforeAnyAdvance")]
    public void BatchLoop_KillSwitch_StopFilePresentExitsBeforeAnyAdvance()
    {
        var (kernel, _) = SimpleGoal();
        var advanceCalled = false;
        var driver = MakeDriver(createWorkspace: _ => { advanceCalled = true; return "/tmp/ws"; });

        var stopFile = ExistingStopPath();
        try
        {
            var summary = new ConductorBatchLoop().Run(kernel, driver, ConductorAutonomyPolicy.Conservative, stopFile);

            Assert.Equal(0, summary.Ticks);
            Assert.True(summary.StopRequested);
            Assert.False(advanceCalled); // AdvanceOnce must not run when stop signal is present
        }
        finally
        {
            File.Delete(stopFile);
        }
    }

    [Xunit.Fact(DisplayName = "BatchLoop_stop_detaches_watched_goal_running_dispatch")]
    public void BatchLoopStopDetachesWatchedGoalRunningDispatch()
    {
        var kernel = new AgentOrchestratorKernel();
        var watchedGoal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "watched goal");
        var otherGoal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "other goal");
        var watchedTask = watchedGoal.Tasks.Single();
        var otherTask = otherGoal.Tasks.Single();
        var now = DateTimeOffset.UtcNow;

        kernel.RecordTaskDispatch(watchedGoal.Id, watchedTask.Id,
            new TaskDispatchRecord("test-worker", "watch.exe", "C:\\watch", now));
        kernel.RecordTaskProcessStarted(watchedGoal.Id, watchedTask.Id,
            new TaskProcessRecord(444, "watch.exe", "C:\\watch", "out.log", "err.log", "exit.txt",
                now, null, null, OwnedProcessIds: [444]));
        kernel.RecordTaskDispatch(otherGoal.Id, otherTask.Id,
            new TaskDispatchRecord("test-worker", "other.exe", "C:\\other", now));
        kernel.RecordTaskProcessStarted(otherGoal.Id, otherTask.Id,
            new TaskProcessRecord(555, "other.exe", "C:\\other", "out.log", "err.log", "exit.txt",
                now, null, null, OwnedProcessIds: [555]));

        var killed = new List<int>();
        var runner = new BackgroundDispatchRunner(tryKillOwnedProcess: pid =>
        {
            killed.Add(pid);
            return true;
        });
        var stopFile = ExistingStopPath();

        try
        {
            var summary = new ConductorBatchLoop(
                detachGoalRunningDispatches: (loopKernel, goal) => runner.DetachRunningProcessesForGoal(loopKernel, goal.Id)).Run(
                    kernel,
                    MakeDriver(),
                    ConductorAutonomyPolicy.Conservative,
                    stopFile,
                    onlyGoalId: watchedGoal.Id.Value);

            Assert.True(summary.StopRequested);
            Xunit.Assert.Empty(killed);
            Assert.Equal(WorkTaskStatus.Running, kernel.GetTask(watchedGoal.Id, watchedTask.Id).Status);
            Assert.False(kernel.GetTask(watchedGoal.Id, watchedTask.Id).LastProcess!.WasCancelled);
            Assert.False(kernel.GetTask(otherGoal.Id, otherTask.Id).LastProcess!.WasCancelled);
        }
        finally
        {
            File.Delete(stopFile);
        }
    }

    [Xunit.Fact(DisplayName = "BatchLoop_stop_detached_orphan_running_task_is_requeued_and_dispatched")]
    public void BatchLoopStopDetachedOrphanRunningTaskIsRequeuedAndDispatched()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "interrupted goal");
        var task = goal.Tasks.Single();
        var now = DateTimeOffset.UtcNow;

        kernel.RecordTaskDispatch(goal.Id, task.Id,
            new TaskDispatchRecord("test-worker", "first.exe", "C:\\goal", now));
        var firstStdout = Path.Combine(Path.GetTempPath(), $"mcg-first-{Guid.NewGuid():N}.out.log");
        var firstStderr = Path.Combine(Path.GetTempPath(), $"mcg-first-{Guid.NewGuid():N}.err.log");
        var firstExit = Path.Combine(Path.GetTempPath(), $"mcg-first-{Guid.NewGuid():N}.exit.txt");
        kernel.RecordTaskProcessStarted(goal.Id, task.Id,
            new TaskProcessRecord(444, "first.exe", "C:\\goal", firstStdout, firstStderr, firstExit,
                now, null, null, OwnedProcessIds: [444]));

        var runner = new BackgroundDispatchRunner(isStillRunning: _ => false);
        var stopFile = ExistingStopPath();
        try
        {
            new ConductorBatchLoop(
                detachGoalRunningDispatches: (loopKernel, loopGoal) => runner.DetachRunningProcessesForGoal(loopKernel, loopGoal.Id)).Run(
                    kernel,
                    MakeDriver(),
                    ConductorAutonomyPolicy.Conservative,
                    stopFile,
                    onlyGoalId: goal.Id.Value);
        }
        finally
        {
            File.Delete(stopFile);
        }

        Assert.Equal(WorkTaskStatus.Running, kernel.GetTask(goal.Id, task.Id).Status);
        Assert.False(kernel.GetTask(goal.Id, task.Id).LastProcess!.WasCancelled);

        var dispatches = 0;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: g =>
            {
                var ready = g.Tasks.Single(t => t.Status == WorkTaskStatus.Assigned);
                kernel.RecordTaskDispatch(g.Id, ready.Id,
                    new TaskDispatchRecord("test-worker", "second.exe", "C:\\goal", DateTimeOffset.UtcNow));
                kernel.RecordTaskProcessStarted(g.Id, ready.Id,
                    new TaskProcessRecord(777, "second.exe", "C:\\goal", "out2.log", "err2.log", "exit2.txt",
                        DateTimeOffset.UtcNow, null, null, OwnedProcessIds: [777]));
                dispatches++;
                return DispatchStartOutcome.Started();
            });

        var summary = new ConductorBatchLoop(
            recoverInterruptedDispatches: loopKernel => runner.RequeueInterruptedDispatches(loopKernel)).Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 1,
                onlyGoalId: goal.Id.Value);

        Assert.Equal(1, summary.Advanced);
        Assert.Equal(1, dispatches);
        var recoveredTask = kernel.GetTask(goal.Id, task.Id);
        Assert.Equal(WorkTaskStatus.Running, recoveredTask.Status);
        Assert.Equal(777, recoveredTask.LastProcess!.ProcessId);
    }

    [Xunit.Fact(DisplayName = "BatchLoop_max_iterations_detaches_live_dispatch_without_reaping")]
    public void BatchLoopMaxIterationsDetachesLiveDispatchWithoutReaping()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "bounded goal");
        var task = goal.Tasks.Single();
        var now = DateTimeOffset.UtcNow;

        kernel.RecordTaskDispatch(goal.Id, task.Id,
            new TaskDispatchRecord("test-worker", "worker.exe", "C:\\goal", now));
        kernel.RecordTaskProcessStarted(goal.Id, task.Id,
            new TaskProcessRecord(444, "worker.exe", "C:\\goal", "out.log", "err.log", "exit.txt",
                now, null, null, OwnedProcessIds: [444]));

        var killed = new List<int>();
        var detachedGoals = new List<string>();
        var reapedGoals = new List<string>();
        var runner = new BackgroundDispatchRunner(tryKillOwnedProcess: pid =>
        {
            killed.Add(pid);
            return true;
        });

        var summary = new ConductorBatchLoop(
            reapGoalRunningDispatches: (loopKernel, loopGoal) =>
            {
                reapedGoals.Add(loopGoal.Id.Value);
                runner.CancelRunningProcessesForGoal(loopKernel, loopGoal.Id);
            },
            detachGoalRunningDispatches: (loopKernel, loopGoal) =>
            {
                detachedGoals.Add(loopGoal.Id.Value);
                runner.DetachRunningProcessesForGoal(loopKernel, loopGoal.Id);
            }).Run(
                kernel,
                MakeDriver(),
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 0,
                onlyGoalId: goal.Id.Value);

        Assert.Equal(0, summary.Ticks);
        Assert.Empty(reapedGoals);
        Assert.Empty(killed);
        Xunit.Assert.Equal([goal.Id.Value], detachedGoals);
        Assert.Equal(WorkTaskStatus.Running, kernel.GetTask(goal.Id, task.Id).Status);
        Assert.False(kernel.GetTask(goal.Id, task.Id).LastProcess!.WasCancelled);
    }

    [Xunit.Fact(DisplayName = "BatchLoop_max_duration_detaches_live_dispatch_without_reaping")]
    public void BatchLoopMaxDurationDetachesLiveDispatchWithoutReaping()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "duration bounded goal");
        var task = goal.Tasks.Single();
        var now = DateTimeOffset.UtcNow;

        kernel.RecordTaskDispatch(goal.Id, task.Id,
            new TaskDispatchRecord("test-worker", "worker.exe", "C:\\goal", now));
        kernel.RecordTaskProcessStarted(goal.Id, task.Id,
            new TaskProcessRecord(555, "worker.exe", "C:\\goal", "out.log", "err.log", "exit.txt",
                now, null, null, OwnedProcessIds: [555]));

        var killed = new List<int>();
        var detachedGoals = new List<string>();
        var reapedGoals = new List<string>();
        var runner = new BackgroundDispatchRunner(tryKillOwnedProcess: pid =>
        {
            killed.Add(pid);
            return true;
        });

        var summary = new ConductorBatchLoop(
            reapGoalRunningDispatches: (loopKernel, loopGoal) =>
            {
                reapedGoals.Add(loopGoal.Id.Value);
                runner.CancelRunningProcessesForGoal(loopKernel, loopGoal.Id);
            },
            detachGoalRunningDispatches: (loopKernel, loopGoal) =>
            {
                detachedGoals.Add(loopGoal.Id.Value);
                runner.DetachRunningProcessesForGoal(loopKernel, loopGoal.Id);
            }).Run(
                kernel,
                MakeDriver(),
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxDuration: TimeSpan.Zero,
                onlyGoalId: goal.Id.Value);

        Assert.Equal(0, summary.Ticks);
        Assert.Empty(reapedGoals);
        Assert.Empty(killed);
        Xunit.Assert.Equal([goal.Id.Value], detachedGoals);
        Assert.Equal(WorkTaskStatus.Running, kernel.GetTask(goal.Id, task.Id).Status);
        Assert.False(kernel.GetTask(goal.Id, task.Id).LastProcess!.WasCancelled);
    }

    [Xunit.Fact(DisplayName = "BatchLoop_bounded_exit_detached_orphan_running_task_is_requeued_and_dispatched")]
    public void BatchLoopBoundedExitDetachedOrphanRunningTaskIsRequeuedAndDispatched()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "bounded interrupted goal");
        var task = goal.Tasks.Single();
        var now = DateTimeOffset.UtcNow;

        kernel.RecordTaskDispatch(goal.Id, task.Id,
            new TaskDispatchRecord("test-worker", "first.exe", "C:\\goal", now));
        var firstStdout = Path.Combine(Path.GetTempPath(), $"mcg-first-{Guid.NewGuid():N}.out.log");
        var firstStderr = Path.Combine(Path.GetTempPath(), $"mcg-first-{Guid.NewGuid():N}.err.log");
        var firstExit = Path.Combine(Path.GetTempPath(), $"mcg-first-{Guid.NewGuid():N}.exit.txt");
        kernel.RecordTaskProcessStarted(goal.Id, task.Id,
            new TaskProcessRecord(444, "first.exe", "C:\\goal", firstStdout, firstStderr, firstExit,
                now, null, null, OwnedProcessIds: [444]));

        var runner = new BackgroundDispatchRunner(isStillRunning: _ => false);
        new ConductorBatchLoop(
            detachGoalRunningDispatches: (loopKernel, loopGoal) => runner.DetachRunningProcessesForGoal(loopKernel, loopGoal.Id)).Run(
                kernel,
                MakeDriver(),
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 0,
                onlyGoalId: goal.Id.Value);

        Assert.Equal(WorkTaskStatus.Running, kernel.GetTask(goal.Id, task.Id).Status);
        Assert.False(kernel.GetTask(goal.Id, task.Id).LastProcess!.WasCancelled);

        var dispatches = 0;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: g =>
            {
                var ready = g.Tasks.Single(t => t.Status == WorkTaskStatus.Assigned);
                kernel.RecordTaskDispatch(g.Id, ready.Id,
                    new TaskDispatchRecord("test-worker", "second.exe", "C:\\goal", DateTimeOffset.UtcNow));
                kernel.RecordTaskProcessStarted(g.Id, ready.Id,
                    new TaskProcessRecord(777, "second.exe", "C:\\goal", "out2.log", "err2.log", "exit2.txt",
                        DateTimeOffset.UtcNow, null, null, OwnedProcessIds: [777]));
                dispatches++;
                return DispatchStartOutcome.Started();
            });

        var summary = new ConductorBatchLoop(
            recoverInterruptedDispatches: loopKernel => runner.RequeueInterruptedDispatches(loopKernel)).Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 1,
                onlyGoalId: goal.Id.Value);

        Assert.Equal(1, summary.Advanced);
        Assert.Equal(1, dispatches);
        var recoveredTask = kernel.GetTask(goal.Id, task.Id);
        Assert.Equal(WorkTaskStatus.Running, recoveredTask.Status);
        Assert.Equal(777, recoveredTask.LastProcess!.ProcessId);
    }

    [Xunit.Fact(DisplayName = "BatchLoop_prior_cancelled_sdlc_task_is_requeued_before_ready_batch")]
    public void BatchLoopPriorCancelledSdlcTaskIsRequeuedBeforeReadyBatch()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("five stage goal");
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var planner = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Planner);
        var researcher = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Researcher);
        var developer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Developer);
        var now = DateTimeOffset.UtcNow;

        kernel.ReportTaskProgress(goal.Id, planner.Id, WorkTaskStatus.Completed, "Planner done.");
        kernel.ReportTaskProgress(goal.Id, researcher.Id, WorkTaskStatus.Completed, "Researcher done.");
        kernel.RecordTaskDispatch(goal.Id, developer.Id,
            new TaskDispatchRecord("test-worker", "dev.exe", "C:\\goal", now));
        kernel.RecordTaskProcessStarted(goal.Id, developer.Id,
            new TaskProcessRecord(444, "dev.exe", "C:\\goal", "out.log", "err.log", "exit.txt",
                now, null, null, OwnedProcessIds: [444]));
        new BackgroundDispatchRunner(isStillRunning: _ => false, tryKillOwnedProcess: _ => true)
            .CancelRunningProcessesForGoal(kernel, goal.Id);

        Assert.Equal(WorkTaskStatus.Cancelled, kernel.GetTask(goal.Id, developer.Id).Status);

        var dispatches = 0;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: g =>
            {
                var ready = g.Tasks.Single(t => t.Status == WorkTaskStatus.Assigned && t.RequiredRole == AgentRole.Developer);
                kernel.RecordTaskDispatch(g.Id, ready.Id,
                    new TaskDispatchRecord("test-worker", "dev-redo.exe", "C:\\goal", DateTimeOffset.UtcNow));
                kernel.RecordTaskProcessStarted(g.Id, ready.Id,
                    new TaskProcessRecord(777, "dev-redo.exe", "C:\\goal", "out2.log", "err2.log", "exit2.txt",
                        DateTimeOffset.UtcNow, null, null, OwnedProcessIds: [777]));
                dispatches++;
                return DispatchStartOutcome.Started();
            });
        var runner = new BackgroundDispatchRunner(isStillRunning: _ => false);

        var summary = new ConductorBatchLoop(
            recoverInterruptedDispatches: loopKernel => runner.RequeueInterruptedDispatches(loopKernel)).Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 1,
                onlyGoalId: goal.Id.Value);

        Assert.Equal(0, summary.Escalated);
        Assert.Equal(1, dispatches);
        var recoveredDeveloper = kernel.GetTask(goal.Id, developer.Id);
        Assert.Equal(WorkTaskStatus.Running, recoveredDeveloper.Status);
        Assert.Equal(777, recoveredDeveloper.LastProcess!.ProcessId);
    }

    // ── Watch mode: continues when all held instead of breaking ──────────

    [Xunit.Fact(DisplayName = "WatchMode_continuesAfterHeld_thenExitsWhenStopped")]
    public void WatchMode_ContinuesAfterHeld_ThenExitsWhenStopped()
    {
        var kernel = new AgentOrchestratorKernel();
        GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "goal 0");

        // dispatchAndStart records a real dispatch so the goal transitions to Dispatched state,
        // which is always Held on the next tick — no capacity tricks needed.
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: g =>
            {
                var task = g.Tasks.First(t => t.Status == WorkTaskStatus.Assigned);
                kernel.RecordTaskDispatch(g.Id, task.Id,
                    new TaskDispatchRecord("test-worker", "test.exe", "C:\\tmp", DateTimeOffset.UtcNow));
                return DispatchStartOutcome.Started();
            });

        var stopFile = NoStopPath();
        var tickCount = 0;
        var watchInterval = TimeSpan.FromMilliseconds(50); // very short for tests

        // After the 2nd tick (goal held, watch sleeping), create the stop file.
        void OnTick(BatchTickSummary tick)
        {
            tickCount++;
            if (tickCount >= 2)
                File.WriteAllText(stopFile, "stop");
        }

        try
        {
            var summary = new ConductorBatchLoop().Run(
                kernel, driver, ConductorAutonomyPolicy.Conservative, stopFile,
                watchInterval: watchInterval, onTick: OnTick);

            // Tick 1: WorkspaceReady → dispatch (advanced=1).
            // Tick 2: Worker slots full → held → watch sleeps → stop detected.
            Assert.True(summary.Ticks >= 2);
            Assert.True(summary.Advanced >= 1);
            Assert.True(summary.StopRequested);
            Assert.True(tickCount >= 2);
        }
        finally
        {
            if (File.Exists(stopFile)) File.Delete(stopFile);
        }
    }

    [Xunit.Fact(DisplayName = "WatchMode_withoutWatchFlag_breaksWhenAllHeld")]
    public void WatchMode_WithoutWatchFlag_BreaksWhenAllHeld()
    {
        var (kernel, _) = SimpleGoal();
        // getRunningCount is at cap from the start so the goal is held once WorkspaceReady.
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningCount: () => ConductorAutonomyPolicy.Conservative.MaxConcurrentPaidWorkers);

        var stopFile = NoStopPath();
        // No watchInterval → non-watch behavior: exit when all goals are held, not sleep-and-continue.
        var summary = new ConductorBatchLoop().Run(kernel, driver, ConductorAutonomyPolicy.Conservative, stopFile, maxIterations: 10);

        // The loop must exit well before maxIterations once all goals are held.
        Assert.False(summary.StopRequested);   // exited cleanly, not via kill-switch
        Assert.True(summary.Ticks < 10);       // not looping indefinitely (watch-mode would)
        Assert.True(summary.Held >= 1);        // at least one held tick caused the exit
    }

    // ── onTick callback: invoked after each tick ──────────────────────────

    [Xunit.Fact(DisplayName = "OnTick_CalledAfterEachTick_WithCorrectSummary")]
    public void OnTick_CalledAfterEachTick_WithCorrectSummary()
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());

        var tickSummaries = new List<BatchTickSummary>();
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,   // Verified state
            runAcceptance: _ => true,
            land: g => new LandingResult(g.Id.Value, g.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "ok"),
            record: _ => { },
            cleanup: _ => new GoalWorktreeRemoveResult("Workspace cleaned up.", null, [], null));

        var stopFile = NoStopPath();
        var summary = new ConductorBatchLoop().Run(
            kernel, driver, ConductorAutonomyPolicy.Conservative, stopFile,
            maxIterations: 3, onTick: tickSummaries.Add);

        Assert.True(tickSummaries.Count > 0);
        foreach (var t in tickSummaries)
            Assert.True(t.Tick >= 1);
    }

    [Xunit.Fact(DisplayName = "OnTick_WatchSleeping_TrueWhenAllHeldInWatchMode")]
    public void OnTick_WatchSleeping_TrueWhenAllHeldInWatchMode()
    {
        var kernel = new AgentOrchestratorKernel();
        GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "held goal");

        // dispatchAndStart records a real dispatch so the goal transitions to Dispatched state,
        // which is always Held on the next tick — no capacity tricks needed.
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: g =>
            {
                var task = g.Tasks.First(t => t.Status == WorkTaskStatus.Assigned);
                kernel.RecordTaskDispatch(g.Id, task.Id,
                    new TaskDispatchRecord("test-worker", "test.exe", "C:\\tmp", DateTimeOffset.UtcNow));
                return DispatchStartOutcome.Started();
            });

        var stopFile = NoStopPath();
        BatchTickSummary? sleepingTick = null;
        var watchInterval = TimeSpan.FromMilliseconds(30);

        void OnTick(BatchTickSummary tick)
        {
            if (tick.WatchSleeping)
            {
                sleepingTick = tick;
                File.WriteAllText(stopFile, "stop"); // stop after first sleep tick
            }
        }

        try
        {
            new ConductorBatchLoop().Run(
                kernel, driver, ConductorAutonomyPolicy.Conservative, stopFile,
                watchInterval: watchInterval, onTick: OnTick);

            Assert.True(sleepingTick is not null);
            Assert.True(sleepingTick!.WatchSleeping);
            Assert.Equal(0, sleepingTick.Advanced); // all held tick
        }
        finally
        {
            if (File.Exists(stopFile)) File.Delete(stopFile);
        }
    }

    // ── Injectable sleep: sleepFunc is called during watch sleep ─────────

    [Xunit.Fact(DisplayName = "WatchMode_InjectableSleep_SleepFuncCalledAndContinues")]
    public void WatchMode_InjectableSleep_SleepFuncCalledAndContinues()
    {
        var kernel = new AgentOrchestratorKernel();
        GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "held goal");

        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: g =>
            {
                var task = g.Tasks.First(t => t.Status == WorkTaskStatus.Assigned);
                kernel.RecordTaskDispatch(g.Id, task.Id,
                    new TaskDispatchRecord("test-worker", "test.exe", "C:\\tmp", DateTimeOffset.UtcNow));
                // Reflect real start behavior: a started worker moves the goal to Running (which the
                // loop then HOLDS on), not Dispatched (which the hardened conductor now re-starts).
                kernel.RecordTaskProcessStarted(g.Id, task.Id,
                    new TaskProcessRecord(1234, "test.exe", "C:\\tmp", "out.log", "err.log", "exit.txt", DateTimeOffset.UtcNow, null, null));
                return DispatchStartOutcome.Started();
            });

        var stopFile = NoStopPath();
        var sleepCallCount = 0;
        var allIntervalsCorrect = true;

        // Inject a sleep func that records calls and returns false (no stop).
        // Stop via stop file after 2nd sleep call.
        Func<TimeSpan, bool> fakeSleep = interval =>
        {
            sleepCallCount++;
            if (interval != TimeSpan.FromSeconds(ConductorBatchLoop.WatchStopPollIntervalSeconds))
                allIntervalsCorrect = false;
            if (sleepCallCount >= 2)
                File.WriteAllText(stopFile, "stop");
            return File.Exists(stopFile);
        };

        try
        {
            var summary = new ConductorBatchLoop().Run(
                kernel, driver, ConductorAutonomyPolicy.Conservative, stopFile,
                watchInterval: TimeSpan.FromSeconds(15),
                sleepFunc: fakeSleep);

            // Sleep func should have been called at least once (goal dispatched, next tick held)
            Assert.True(sleepCallCount >= 1);
            // Each sleep call should have received the watch interval
            Assert.True(allIntervalsCorrect);
            // Loop should have stopped via the stop file
            Assert.True(summary.StopRequested);
        }
        finally
        {
            if (File.Exists(stopFile)) File.Delete(stopFile);
        }
    }

    [Xunit.Fact(DisplayName = "WatchMode_exit_file_wake_signal_sweeps_before_fallback_timeout")]
    public void WatchModeExitFileWakeSignalSweepsBeforeFallbackTimeout()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mcg-wake-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var exit = Path.Combine(root, "worker.exit.txt");
        var stdout = Path.Combine(root, "worker.out.log");
        var stderr = Path.Combine(root, "worker.err.log");
        File.WriteAllText(stdout, "done");
        File.WriteAllText(stderr, "");

        var (kernel, goal) = SimpleGoal("running goal");
        var task = goal.Tasks.Single();
        var now = DateTimeOffset.UtcNow;
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("test-worker", "test.exe", root, now));
        kernel.RecordTaskProcessStarted(goal.Id, task.Id,
            new TaskProcessRecord(1234, "test.exe", root, stdout, stderr, exit, now, null, null, OwnedProcessIds: [1234]));

        var runner = new BackgroundDispatchRunner(isStillRunning: _ => false);
        var sweepCalls = 0;
        var wakeSignal = new TestWakeSignal(() => File.WriteAllText(exit, "0"));
        var driver = MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true));

        var summary = new ConductorBatchLoop(loopKernel =>
        {
            sweepCalls++;
            runner.SweepExitedProcesses(loopKernel);
        }).Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 2,
            watchInterval: TimeSpan.FromSeconds(ConductorBatchLoop.DefaultWatchIntervalSeconds),
            wakeSignal: wakeSignal);

        Assert.Equal(TimeSpan.FromSeconds(ConductorBatchLoop.WatchStopPollIntervalSeconds), wakeSignal.Timeouts.Single());
        Assert.Contains(wakeSignal.TrackedExitCodePathUpdates.Single(),
            path => string.Equals(path, exit, StringComparison.OrdinalIgnoreCase));
        Assert.True(sweepCalls >= 2);
        Assert.False(kernel.GetTask(goal.Id, task.Id).LastProcess!.IsRunning);
        Assert.False(summary.StopRequested);
    }

    [Xunit.Fact(DisplayName = "BatchLoop_refreshes_exited_readonly_dispatch_before_advance_and_does_not_redispatch_it")]
    public void BatchLoopRefreshesExitedReadonlyDispatchBeforeAdvanceAndDoesNotRedispatchIt()
    {
        var kernel = new AgentOrchestratorKernel();
        var planner = new TaskSpec(TaskId.New(), "Plan the implementation.", AgentRole.Planner);
        var researcher = new TaskSpec(TaskId.New(), "Research constraints.", AgentRole.Researcher);
        var goal = kernel.CreateGoal("Two role handoff", [planner, researcher]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var root = Path.Combine(Path.GetTempPath(), $"mcg-refresh-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var stdout = Path.Combine(root, "planner.out.log");
        var stderr = Path.Combine(root, "planner.err.log");
        var exit = Path.Combine(root, "planner.exit.txt");
        File.WriteAllText(stdout, string.Join(Environment.NewLine,
            "Planner complete.",
            "WORKER_RESULT:",
            "files: none",
            "commands: none",
            "tests: not-run - planning only",
            "commit: none",
            "blockers: none",
            "model_fit: OpenAI/gpt-5.5 - adequate - planning",
            "skills: none",
            "confidence: high",
            "END_WORKER_RESULT"));
        File.WriteAllText(stderr, string.Empty);
        File.WriteAllText(exit, "0");
        var now = DateTimeOffset.UtcNow;
        kernel.RecordTaskDispatch(goal.Id, planner.Id, new TaskDispatchRecord("planner-worker", "planner.exe", root, now));
        var running = new TaskProcessRecord(4242, "planner.exe", root, stdout, stderr, exit, now, null, null);
        kernel.RecordTaskProcessStarted(goal.Id, planner.Id, running);

        var dispatchCalls = new List<TaskId>();
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: g =>
            {
                var next = g.Tasks.Single(task => task.Status == WorkTaskStatus.Assigned);
                dispatchCalls.Add(next.Id);
                kernel.RecordTaskDispatch(g.Id, next.Id, new TaskDispatchRecord("next-worker", "next.exe", root, DateTimeOffset.UtcNow));
                return DispatchStartOutcome.Started();
            });

        var summary = new ConductorBatchLoop(
            refreshGoalDispatchesBeforeAdvance: (loopKernel, loopGoal) => { GoalManagementCommandService.RefreshDispatches(loopKernel, loopGoal); })
            .Run(kernel, driver, ConductorAutonomyPolicy.Conservative, NoStopPath(), maxIterations: 1);

        Assert.Equal(WorkTaskStatus.Completed, planner.Status);
        Assert.Equal(WorkTaskStatus.Running, researcher.Status);
        Assert.Equal([researcher.Id], dispatchCalls);
        Assert.Equal(1, summary.Advanced);
    }

    [Xunit.Fact(DisplayName = "WatchMode_exit_wake_reconciles_before_stop_file_exit")]
    public void WatchModeExitWakeReconcilesBeforeStopFileExit()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mcg-wake-stop-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var exit = Path.Combine(root, "worker.exit.txt");
        var stdout = Path.Combine(root, "worker.out.log");
        var stderr = Path.Combine(root, "worker.err.log");
        var stopFile = NoStopPath();
        File.WriteAllText(stdout, "done");
        File.WriteAllText(stderr, "");

        var (kernel, goal) = SimpleGoal("running goal");
        var task = goal.Tasks.Single();
        var now = DateTimeOffset.UtcNow;
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("test-worker", "test.exe", root, now));
        kernel.RecordTaskProcessStarted(goal.Id, task.Id,
            new TaskProcessRecord(1234, "test.exe", root, stdout, stderr, exit, now, null, null, OwnedProcessIds: [1234]));

        var runner = new BackgroundDispatchRunner(isStillRunning: _ => false);
        var sweepCalls = 0;
        var wakeSignal = new TestWakeSignal(() =>
        {
            File.WriteAllText(exit, "0");
            File.WriteAllText(stopFile, "stop");
        });

        try
        {
            var summary = new ConductorBatchLoop(loopKernel =>
            {
                sweepCalls++;
                runner.SweepExitedProcesses(loopKernel);
            }).Run(
                kernel,
                MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true)),
                ConductorAutonomyPolicy.Conservative,
                stopFile,
                watchInterval: TimeSpan.FromSeconds(ConductorBatchLoop.DefaultWatchIntervalSeconds),
                wakeSignal: wakeSignal);

            Assert.True(summary.StopRequested);
            Assert.True(sweepCalls >= 2);
            Assert.False(kernel.GetTask(goal.Id, task.Id).LastProcess!.IsRunning);
        }
        finally
        {
            if (File.Exists(stopFile)) File.Delete(stopFile);
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact(DisplayName = "WatchMode_no_exit_wake_waits_running_dispatch_fallback_interval")]
    public void WatchModeNoExitWakeWaitsRunningDispatchFallbackInterval()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mcg-wake-no-event-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var exit = Path.Combine(root, "worker.exit.txt");
        var stdout = Path.Combine(root, "worker.out.log");
        var stderr = Path.Combine(root, "worker.err.log");
        File.WriteAllText(stdout, "still running");
        File.WriteAllText(stderr, "");

        var (kernel, goal) = SimpleGoal("running goal");
        var task = goal.Tasks.Single();
        var now = DateTimeOffset.UtcNow;
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("test-worker", "test.exe", root, now));
        kernel.RecordTaskProcessStarted(goal.Id, task.Id,
            new TaskProcessRecord(1234, "test.exe", root, stdout, stderr, exit, now, null, null, OwnedProcessIds: [1234]));

        var wakeSignal = new TestWakeSignal(_ => false);
        try
        {
            var summary = new ConductorBatchLoop().Run(
                kernel,
                MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true)),
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 1,
                watchInterval: TimeSpan.FromSeconds(ConductorBatchLoop.DefaultWatchIntervalSeconds),
                wakeSignal: wakeSignal);

            Assert.Equal(1, summary.Ticks);
            Assert.Equal([TimeSpan.FromSeconds(ConductorBatchLoop.WatchStopPollIntervalSeconds)], wakeSignal.Timeouts);
            Assert.Contains(wakeSignal.TrackedExitCodePathUpdates.Single(),
                path => string.Equals(path, exit, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact(DisplayName = "WatchMode_multiple_exit_wakes_coalesce_into_one_immediate_tick")]
    public void WatchModeMultipleExitWakesCoalesceIntoOneImmediateTick()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mcg-wake-coalesce-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var exitPaths = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), $"running goal {i}");
            var task = goal.Tasks.Single();
            var stdout = Path.Combine(root, $"worker-{i}.out.log");
            var stderr = Path.Combine(root, $"worker-{i}.err.log");
            var exit = Path.Combine(root, $"worker-{i}.exit.txt");
            File.WriteAllText(stdout, "done");
            File.WriteAllText(stderr, "");
            var now = DateTimeOffset.UtcNow;
            kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("test-worker", "test.exe", root, now));
            kernel.RecordTaskProcessStarted(goal.Id, task.Id,
                new TaskProcessRecord(1234 + i, "test.exe", root, stdout, stderr, exit, now, null, null, OwnedProcessIds: [1234 + i]));
            exitPaths.Add(exit);
        }

        var runner = new BackgroundDispatchRunner(isStillRunning: _ => false);
        var wakeSignal = new TestWakeSignal(() =>
        {
            foreach (var exitPath in exitPaths)
            {
                File.WriteAllText(exitPath, "0");
            }
        });

        try
        {
            var summary = new ConductorBatchLoop(loopKernel => runner.SweepExitedProcesses(loopKernel)).Run(
                kernel,
                MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true)),
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 2,
                watchInterval: TimeSpan.FromSeconds(ConductorBatchLoop.DefaultWatchIntervalSeconds),
                wakeSignal: wakeSignal);

            Assert.Equal(1, wakeSignal.SignaledWaits);
            Assert.Single(wakeSignal.Timeouts);
            Assert.Equal(3, wakeSignal.TrackedExitCodePathUpdates.Single().Count);
            Assert.All(exitPaths, exitPath => Assert.Contains(wakeSignal.TrackedExitCodePathUpdates.Single(),
                trackedPath => string.Equals(trackedPath, exitPath, StringComparison.OrdinalIgnoreCase)));
            Assert.False(kernel.Goals.SelectMany(goal => goal.Tasks).Any(task => task.LastProcess is { IsRunning: true }));
            Assert.False(summary.StopRequested);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact(DisplayName = "WatchMode_idle_without_running_workers_keeps_default_fallback")]
    public void WatchModeIdleWithoutRunningWorkersKeepsDefaultFallback()
    {
        var kernel = new AgentOrchestratorKernel();
        var stopFile = NoStopPath();
        var wakeSignal = new TestWakeSignal(waitNumber =>
        {
            if (waitNumber == 3)
            {
                File.WriteAllText(stopFile, "stop");
            }

            return false;
        });

        try
        {
            var summary = new ConductorBatchLoop().Run(
                kernel,
                MakeDriver(),
                ConductorAutonomyPolicy.Conservative,
                stopFile,
                maxIterations: 1,
                watchInterval: TimeSpan.FromSeconds(ConductorBatchLoop.DefaultWatchIntervalSeconds),
                wakeSignal: wakeSignal,
                keepAliveWhenIdle: true);

            Assert.Equal(TimeSpan.FromSeconds(ConductorBatchLoop.DefaultWatchIntervalSeconds), TimeSpan.FromTicks(wakeSignal.Timeouts.Sum(t => t.Ticks)));
            Assert.All(wakeSignal.TrackedExitCodePathUpdates, update => Assert.Empty(update));
            Assert.True(wakeSignal.Timeouts.All(timeout => timeout <= TimeSpan.FromSeconds(ConductorBatchLoop.WatchStopPollIntervalSeconds)));
            Assert.Equal(0, summary.Ticks);
            Assert.True(summary.StopRequested);
        }
        finally
        {
            if (File.Exists(stopFile)) File.Delete(stopFile);
        }
    }

    [Xunit.Fact(DisplayName = "WatchMode_InjectableSleep_StopReturnedFromSleepFunc")]
    public void WatchMode_InjectableSleep_StopReturnedFromSleepFunc()
    {
        var kernel = new AgentOrchestratorKernel();
        GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "held goal");

        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: g =>
            {
                var task = g.Tasks.First(t => t.Status == WorkTaskStatus.Assigned);
                kernel.RecordTaskDispatch(g.Id, task.Id,
                    new TaskDispatchRecord("test-worker", "test.exe", "C:\\tmp", DateTimeOffset.UtcNow));
                return DispatchStartOutcome.Started();
            });

        var stopFile = NoStopPath();

        // Inject a sleep func that immediately signals stop (returns true).
        Func<TimeSpan, bool> fakeSleepStop = _ => true;

        try
        {
            var summary = new ConductorBatchLoop().Run(
                kernel, driver, ConductorAutonomyPolicy.Conservative, stopFile,
                watchInterval: TimeSpan.FromSeconds(15),
                sleepFunc: fakeSleepStop);

            // Loop exits because the sleep func returned true (stop signaled during sleep)
            Assert.True(summary.StopRequested);
        }
        finally
        {
            if (File.Exists(stopFile)) File.Delete(stopFile);
        }
    }

    private sealed class TestWakeSignal : IConductorWakeSignal
    {
        private readonly Func<int, bool>? _wait;
        private int _waits;

        public TestWakeSignal(Action? onFirstWait = null)
            : this(onFirstWait is null
                ? null
                : waitNumber =>
                {
                    if (waitNumber == 1)
                    {
                        onFirstWait();
                        return true;
                    }

                    return false;
                })
        {
        }

        public TestWakeSignal(Func<int, bool>? wait)
        {
            _wait = wait;
        }

        public List<TimeSpan> Timeouts { get; } = [];

        public List<IReadOnlyList<string>> TrackedExitCodePathUpdates { get; } = [];

        public int SignaledWaits { get; private set; }

        public void UpdateTrackedExitArtifacts(IReadOnlyCollection<string> exitCodePaths)
        {
            TrackedExitCodePathUpdates.Add(exitCodePaths.ToArray());
        }

        public bool Wait(TimeSpan timeout)
        {
            Timeouts.Add(timeout);
            var signaled = _wait?.Invoke(++_waits) ?? false;
            if (signaled)
            {
                SignaledWaits++;
            }

            return signaled;
        }

        public void Dispose()
        {
        }
    }

    // ── Progress emission: compact lines emitted to stdout ───────────────

    [Xunit.Fact(DisplayName = "ProgressEmission_CompactLinesIncludeTickAndGoalEvents")]
    public void ProgressEmission_CompactLinesIncludeTickAndGoalEvents()
    {
        var (kernel, _) = SimpleGoal();

        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningCount: () => ConductorAutonomyPolicy.Conservative.MaxConcurrentPaidWorkers);

        var stopFile = NoStopPath();

        var output = AsyncLocalConsoleRouter.Capture(() =>
        {
            new ConductorBatchLoop().Run(
                kernel, driver, ConductorAutonomyPolicy.Conservative, stopFile, maxIterations: 1);
        });

        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        Assert.True(lines.Any(l => l.StartsWith("TICK tick=1 eligible=", StringComparison.Ordinal)));
        Assert.True(lines.Any(l => l.StartsWith("GOAL goal=", StringComparison.Ordinal)));
        Assert.True(lines.Any(l => l.StartsWith("TICK_END tick=1 ", StringComparison.Ordinal)));
    }

    [Xunit.Fact(DisplayName = "ProgressEmission_TickSummaryContainsProgressLines")]
    public void ProgressEmission_TickSummaryContainsProgressLines()
    {
        var (kernel, _) = SimpleGoal();

        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningCount: () => ConductorAutonomyPolicy.Conservative.MaxConcurrentPaidWorkers);

        var stopFile = NoStopPath();
        BatchTickSummary? capturedTick = null;

        new ConductorBatchLoop().Run(
            kernel, driver, ConductorAutonomyPolicy.Conservative, stopFile,
            maxIterations: 1, onTick: t => capturedTick = t);

        Assert.True(capturedTick is not null);
        var lines = capturedTick!.ProgressLines;
        Assert.True(lines is not null && lines.Count > 0);
        Assert.True(lines!.Any(l => l.StartsWith("TICK ", StringComparison.Ordinal)));
        Assert.True(lines.Any(l => l.StartsWith("GOAL ", StringComparison.Ordinal)));
    }

    [Xunit.Fact(DisplayName = "ConductEvents_shared_stream_receives_events_from_sequential_loop_instances")]
    public void ConductEventsSharedStreamReceivesEventsFromSequentialLoopInstances()
    {
        var root = CreateTempDirectory("mcg-conduct-events");
        var logPath = Path.Combine(root, ".orchestrator", "logs", ConductEventLogWriter.CurrentFileName);
        var writer = new ConductEventLogWriter(logPath);
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningCount: () => ConductorAutonomyPolicy.Conservative.MaxConcurrentPaidWorkers);

        string output = AsyncLocalConsoleRouter.Capture(() =>
        {
            var (firstKernel, _) = SimpleGoal("first conduct event stream goal");
            new ConductorBatchLoop(conductEventLogWriter: writer).Run(
                firstKernel, driver, ConductorAutonomyPolicy.Conservative, NoStopPath(), maxIterations: 1);

            var (secondKernel, _) = SimpleGoal("second conduct event stream goal");
            new ConductorBatchLoop(conductEventLogWriter: writer).Run(
                secondKernel, driver, ConductorAutonomyPolicy.Conservative, NoStopPath(), maxIterations: 1);
        });

        Assert.Contains("GOAL goal=", output, StringComparison.Ordinal);

        var records = File.ReadAllLines(logPath)
            .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(line, new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
            .ToArray();

        Assert.True(records.Count(record => record.EventKind == "loop-start") >= 2);
        Assert.Contains(records, record => record.EventKind == "goal" && record.GoalId is not null);
    }

    [Xunit.Fact(DisplayName = "BatchLoop_skips_janitorial_phase_failure_and_journals_event")]
    public void BatchLoopSkipsJanitorialPhaseFailureAndJournalsEvent()
    {
        var root = CreateTempDirectory("mcg-conduct-events-janitorial");
        var logPath = Path.Combine(root, ".orchestrator", "logs", ConductEventLogWriter.CurrentFileName);
        var writer = new ConductEventLogWriter(logPath);
        var kernel = new AgentOrchestratorKernel();
        var driver = MakeDriver();

        var output = AsyncLocalConsoleRouter.Capture(() =>
        {
            var summary = new ConductorBatchLoop(
                measuredSweep: _ => throw new InvalidOperationException("janitorial access denied"),
                conductEventLogWriter: writer).Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 1);

            Assert.False(summary.StopRequested);
        });

        Assert.Contains("LOOP_JANITORIAL_FAILED", output, StringComparison.Ordinal);
        Assert.Contains("LOOP_STOP", output, StringComparison.Ordinal);

        var records = File.ReadAllLines(logPath)
            .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(line, new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
            .ToArray();

        Assert.Contains(records, record =>
            record.EventKind == "loop-janitorial-failure" &&
            record.Detail.Contains("exception=InvalidOperationException", StringComparison.Ordinal) &&
            record.Detail.Contains("janitorial_access_denied", StringComparison.Ordinal));
        Assert.Contains(records, record => record.EventKind == "loop-stop");
    }

    [Xunit.Fact(DisplayName = "BatchLoop_skips_idle_wake_janitorial_failure_and_journals_event")]
    public void BatchLoopSkipsIdleWakeJanitorialFailureAndJournalsEvent()
    {
        var root = CreateTempDirectory("mcg-conduct-events-idle-wake-janitorial");
        var logPath = Path.Combine(root, ".orchestrator", "logs", ConductEventLogWriter.CurrentFileName);
        var writer = new ConductEventLogWriter(logPath);
        var kernel = new AgentOrchestratorKernel();
        var stopFile = NoStopPath();
        var wakeSignal = new TestWakeSignal(() => File.WriteAllText(stopFile, "stop"));

        try
        {
            var output = AsyncLocalConsoleRouter.Capture(() =>
            {
                var summary = new ConductorBatchLoop(
                    measuredSweep: _ => throw new InvalidOperationException("idle wake janitorial access denied"),
                    conductEventLogWriter: writer).Run(
                    kernel,
                    MakeDriver(),
                    ConductorAutonomyPolicy.Conservative,
                    stopFile,
                    maxIterations: 1,
                    watchInterval: TimeSpan.FromSeconds(ConductorBatchLoop.DefaultWatchIntervalSeconds),
                    wakeSignal: wakeSignal,
                    keepAliveWhenIdle: true);

                Assert.True(summary.StopRequested);
            });

            Assert.Contains("LOOP_JANITORIAL_FAILED", output, StringComparison.Ordinal);
            Assert.Contains("LOOP_STOP", output, StringComparison.Ordinal);

            var records = File.ReadAllLines(logPath)
                .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(line, new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
                .ToArray();

            Assert.Contains(records, record =>
                record.EventKind == "loop-janitorial-failure" &&
                record.Detail.Contains("phase=idle-wake-sweep", StringComparison.Ordinal) &&
                record.Detail.Contains("idle_wake_janitorial_access_denied", StringComparison.Ordinal));
            Assert.Contains(records, record => record.EventKind == "loop-stop");
        }
        finally
        {
            if (File.Exists(stopFile)) File.Delete(stopFile);
        }
    }

    [Xunit.Fact(DisplayName = "BatchLoop_skips_watch_wake_janitorial_failure_and_journals_event")]
    public void BatchLoopSkipsWatchWakeJanitorialFailureAndJournalsEvent()
    {
        var root = CreateTempDirectory("mcg-conduct-events-watch-wake-janitorial");
        var logPath = Path.Combine(root, ".orchestrator", "logs", ConductEventLogWriter.CurrentFileName);
        var writer = new ConductEventLogWriter(logPath);
        var (kernel, _) = SimpleGoal("held wake janitorial failure goal");
        var stopFile = NoStopPath();
        var wakeSignal = new TestWakeSignal(() => File.WriteAllText(stopFile, "stop"));
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningCount: () => ConductorAutonomyPolicy.Conservative.MaxConcurrentPaidWorkers);

        try
        {
            var output = AsyncLocalConsoleRouter.Capture(() =>
            {
                var summary = new ConductorBatchLoop(
                    measuredSweep: _ => throw new InvalidOperationException("watch wake janitorial access denied"),
                    conductEventLogWriter: writer).Run(
                    kernel,
                    driver,
                    ConductorAutonomyPolicy.Conservative,
                    stopFile,
                    maxIterations: 1,
                    watchInterval: TimeSpan.FromSeconds(ConductorBatchLoop.DefaultWatchIntervalSeconds),
                    wakeSignal: wakeSignal);

                Assert.True(summary.StopRequested);
            });

            Assert.Contains("LOOP_JANITORIAL_FAILED", output, StringComparison.Ordinal);
            Assert.Contains("LOOP_STOP", output, StringComparison.Ordinal);

            var records = File.ReadAllLines(logPath)
                .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(line, new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
                .ToArray();

            Assert.Contains(records, record =>
                record.EventKind == "loop-janitorial-failure" &&
                record.Detail.Contains("phase=wake-sweep", StringComparison.Ordinal) &&
                record.Detail.Contains("watch_wake_janitorial_access_denied", StringComparison.Ordinal));
            Assert.Contains(records, record => record.EventKind == "loop-stop");
        }
        finally
        {
            if (File.Exists(stopFile)) File.Delete(stopFile);
        }
    }

    [Xunit.Fact(DisplayName = "ConductEvents_shared_stream_records_escalation_reason")]
    public void ConductEventsSharedStreamRecordsEscalationReason()
    {
        var root = CreateTempDirectory("mcg-conduct-events-escalation");
        var logPath = Path.Combine(root, ".orchestrator", "logs", ConductEventLogWriter.CurrentFileName);
        var writer = new ConductEventLogWriter(logPath);
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateVerifiedSimpleGoal(kernel, "escalation event stream goal");
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            runAcceptance: _ => false);

        new ConductorBatchLoop(conductEventLogWriter: writer).Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 1,
            maxVerifyRetries: 0);

        var records = File.ReadAllLines(logPath)
            .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(line, new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
            .ToArray();

        Assert.Contains(records, record =>
            record.EventKind == "goal-escalation" &&
            record.GoalId == goal.Id.Value[..8] &&
            record.Detail.Contains("reason=Acceptance_verification_failed", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "ConductEvents_rollover_preserves_stable_current_filename")]
    public void ConductEventsRolloverPreservesStableCurrentFilename()
    {
        var root = CreateTempDirectory("mcg-conduct-events-rollover");
        var logPath = Path.Combine(root, ".orchestrator", "logs", ConductEventLogWriter.CurrentFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        File.WriteAllText(logPath, new string('x', 128));

        var writer = new ConductEventLogWriter(
            logPath,
            maxBytes: 32,
            utcNow: () => DateTimeOffset.Parse("2026-07-13T02:30:00Z"));

        writer.Append("loop-stop", null, "LOOP_STOP tick=0 reason=test");

        Assert.True(File.Exists(logPath));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(logPath)!, "conduct-events-*.log"));
        var current = JsonSerializer.Deserialize<ConductEventRecord>(
            File.ReadAllText(logPath),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal("loop-stop", current.EventKind);
    }

    [Xunit.Fact(DisplayName = "ProgressEmission_TickSummaryContainsPhaseTimingLines")]
    public void ProgressEmission_TickSummaryContainsPhaseTimingLines()
    {
        var (kernel, _) = SimpleGoal("phase timing dispatch");
        var driver = MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true));
        BatchTickSummary? capturedTick = null;

        new ConductorBatchLoop().Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 1,
            onTick: tick => capturedTick = tick);

        var lines = capturedTick!.ProgressLines!;
        Assert.Contains(lines, line => line.StartsWith("PHASE_TIMING tick=1 phase=sweep ", StringComparison.Ordinal) && line.Contains(" ts=", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.StartsWith("PHASE_TIMING tick=1 phase=prewalk ", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.StartsWith("PHASE_TIMING tick=1 phase=dispatch-prep ", StringComparison.Ordinal) && line.Contains(" task=", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.StartsWith("PHASE_TIMING tick=1 phase=per-goal-walk ", StringComparison.Ordinal) && line.Contains("slowest=", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "ProgressEmission_WatchHeldRunningEmitsOnlyChangedDisposition")]
    public void ProgressEmission_WatchHeldRunningEmitsOnlyChangedDisposition()
    {
        var (kernel, _) = SimpleGoal();

        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningCount: () => ConductorAutonomyPolicy.Conservative.MaxConcurrentPaidWorkers);

        var ticks = new List<BatchTickSummary>();
        var output = AsyncLocalConsoleRouter.Capture(() =>
        {
            new ConductorBatchLoop().Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 4,
                watchInterval: TimeSpan.FromSeconds(1),
                sleepFunc: _ => false,
                onTick: ticks.Add);
        });

        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(4, ticks.Count);
        Assert.Single(lines.Where(l => l.StartsWith("GOAL goal=", StringComparison.Ordinal)));
        Assert.Single(lines.Where(l => l.StartsWith("TICK_END tick=", StringComparison.Ordinal)));
        Assert.DoesNotContain(lines, l => l.StartsWith("TICK_END tick=2 ", StringComparison.Ordinal));
        Assert.True(ticks.Skip(1).All(t =>
            t.ProgressLines is not null &&
            t.ProgressLines.All(line => line.StartsWith("PHASE_TIMING ", StringComparison.Ordinal))));
    }

    [Xunit.Fact(DisplayName = "ConductorTick_includes_operator_disposition_snapshot")]
    public void ConductorTickIncludesOperatorDispositionSnapshot()
    {
        var (kernel, goal) = SimpleGoal("operator disposition tick");
        var driver = MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true));
        BatchTickSummary? captured = null;

        new ConductorBatchLoop().Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 1,
            onTick: tick => captured = tick,
            buildOperatorDispositions: _ =>
            [
                new ConductorOperatorDispositionSnapshot(
                    goal.Id.Value,
                    OperatorDispositionState.Wait,
                    OperatorDispositionConfidence.High,
                    "conductor snapshot",
                    "wait",
                    DateTimeOffset.Parse("2026-07-03T12:10:00Z"),
                    [],
                    [],
                    [])
            ]);

        var snapshot = Assert.Single(captured!.OperatorDispositions!);
        Assert.Equal(goal.Id.Value, snapshot.GoalId);
        Assert.Equal(OperatorDispositionState.Wait, snapshot.State);
        Assert.Equal("conductor snapshot", snapshot.Reason);
    }

    [Xunit.Fact(DisplayName = "WatchProgress_emits_dispatch_role_liveness_bytes_and_files")]
    public void WatchProgressEmitsDispatchRoleLivenessBytesAndFiles()
    {
        var (kernel, goal) = SimpleGoal("watch progress");
        var task = goal.Tasks.First();
        var now = DateTimeOffset.Parse("2026-06-22T12:00:00Z");
        StartProcess(kernel, goal, task, now.AddMinutes(-2), "abc123");
        var reporter = FakeWatchReporter(
            now,
            stdoutBytes: 42,
            stderrBytes: 8,
            idle: TimeSpan.FromSeconds(15),
            ownedPids: [111, 222],
            alivePids: [222],
            files: ["src/A.cs", "src/B.cs", "src/C.cs", "src/D.cs"]);
        var ticks = new List<BatchTickSummary>();

        new ConductorBatchLoop(watchProgressReporter: reporter).Run(
            kernel,
            MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true)),
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 1,
            watchInterval: TimeSpan.FromSeconds(1),
            sleepFunc: _ => true,
            onTick: ticks.Add);

        var line = ticks.Single().ProgressLines!.Single(l => l.StartsWith("WATCH_PROGRESS ", StringComparison.Ordinal));
        Assert.Contains($"goal={goal.Id.Value[..8]}", line);
        Assert.Contains($"role={task.RequiredRole}", line);
        Assert.Contains("task=1/", line);
        Assert.Contains("elapsed=2m0s", line);
        Assert.Contains("liveness=\"alive\"", line);
        Assert.Contains("output_delta=50", line);
        Assert.Contains("last_progress_age=15s", line);
        Assert.Contains("files=4", line);
        Assert.Contains("src/A.cs", line);
        Assert.Contains("+1 more", line);

        var human = ticks.Single().ProgressLines!.Single(l => l.StartsWith($"[{goal.Id.Value[..8]}] {task.RequiredRole}", StringComparison.Ordinal));
        Assert.Contains("(task 1/", human);
        Assert.Contains("running 2m0s", human);
        Assert.Contains("worker pid 222 alive", human);
        Assert.Contains("+42B stdout", human);
        Assert.Contains("last progress 15s ago", human);
        Assert.Contains("4 files changed (src/A.cs, src/B.cs, src/C.cs, +1 more)", human);
    }

    [Xunit.Fact(DisplayName = "WatchProgress_throttles_until_output_or_file_count_changes")]
    public void WatchProgressThrottlesUntilOutputOrFileCountChanges()
    {
        var (kernel, goal) = SimpleGoal("watch throttle");
        var task = goal.Tasks.First();
        var start = DateTimeOffset.Parse("2026-06-22T12:00:00Z");
        StartProcess(kernel, goal, task, start.AddMinutes(-1), "abc123");
        var nowCalls = 0;
        var heartbeatCalls = 0;
        var reporter = new ConductorWatchProgressReporter(
            readHeartbeat: (process, observedAt) =>
            {
                heartbeatCalls++;
                var bytes = heartbeatCalls < 3 ? 10 : 11;
                return Heartbeat(process, observedAt, bytes, 0, TimeSpan.FromSeconds(5), [111]);
            },
            readChanges: (_, _) => new DispatchLiveChangeSnapshot(["src/A.cs"], ["src/A.cs"], 0),
            isProcessAlive: pid => pid == 111,
            now: () => start.AddSeconds(nowCalls++ * 10));
        var ticks = new List<BatchTickSummary>();

        new ConductorBatchLoop(watchProgressReporter: reporter).Run(
            kernel,
            MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true)),
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 3,
            watchInterval: TimeSpan.FromSeconds(1),
            sleepFunc: _ => false,
            onTick: ticks.Add);

        var progressLines = ticks.SelectMany(t => t.ProgressLines ?? []).Where(l => l.StartsWith("WATCH_PROGRESS ", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, progressLines.Length);
        Assert.Contains("output_delta=1", progressLines[1]);
    }

    [Xunit.Fact(DisplayName = "WatchProgress_quiet_suppresses_human_lines_but_keeps_machine_lines")]
    public void WatchProgressQuietSuppressesHumanLinesButKeepsMachineLines()
    {
        var (kernel, goal) = SimpleGoal("watch quiet");
        var task = goal.Tasks.First();
        var now = DateTimeOffset.Parse("2026-06-22T12:00:00Z");
        StartProcess(kernel, goal, task, now.AddMinutes(-1), "abc123");
        var reporter = FakeWatchReporter(now, 10, 0, TimeSpan.FromSeconds(5), [111], [111], ["src/A.cs"]);

        var output = AsyncLocalConsoleRouter.Capture(() =>
        {
            new ConductorBatchLoop(watchProgressReporter: reporter).Run(
                kernel,
                MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true)),
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 1,
                watchInterval: TimeSpan.FromSeconds(1),
                sleepFunc: _ => true,
                quiet: true);
        });

        Assert.Contains("TICK tick=1", output);
        Assert.DoesNotContain("WATCH_PROGRESS", output);
        Assert.DoesNotContain("WATCH_WARNING", output);
    }

    [Xunit.Fact(DisplayName = "WatchProgress_warns_when_owned_pids_are_dead")]
    public void WatchProgressWarnsWhenOwnedPidsAreDead()
    {
        var (kernel, goal) = SimpleGoal("watch warning");
        var task = goal.Tasks.First();
        var now = DateTimeOffset.Parse("2026-06-22T12:00:00Z");
        StartProcess(kernel, goal, task, now.AddMinutes(-1), "abc123");
        var reporter = FakeWatchReporter(now, 10, 0, TimeSpan.FromSeconds(20), [111], [], ["src/A.cs"]);
        var ticks = new List<BatchTickSummary>();

        new ConductorBatchLoop(watchProgressReporter: reporter).Run(
            kernel,
            MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true)),
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 1,
            watchInterval: TimeSpan.FromSeconds(1),
            sleepFunc: _ => true,
            onTick: ticks.Add);

        var warning = ticks.Single().ProgressLines!.Single(l => l.StartsWith("WATCH_WARNING ", StringComparison.Ordinal));
        Assert.Contains($"goal={goal.Id.Value[..8]}", warning);
        Assert.Contains("reason=no-live-worker", warning);

        var human = ticks.Single().ProgressLines!.Single(l => l.StartsWith($"[{goal.Id.Value[..8]}] WARNING:", StringComparison.Ordinal));
        Assert.Contains("no worker progress for 20s", human);
        Assert.Contains("possible stall", human);
    }

    [Xunit.Fact(DisplayName = "WatchProgress_emits_transition_after_role_completion")]
    public void WatchProgressEmitsTransitionAfterRoleCompletion()
    {
        var kernel = new AgentOrchestratorKernel();
        var first = new TaskSpec(TaskId.New(), "Plan", AgentRole.Planner);
        var second = new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer);
        var goal = kernel.CreateGoal("watch transition", [first, second]);
        kernel.ActivateGoal(goal.Id, BuildAgents(AgentRole.Planner, AgentRole.Developer));
        var now = DateTimeOffset.Parse("2026-06-22T12:00:00Z");
        StartProcess(kernel, goal, first, now.AddMinutes(-3), "abc123");
        var calls = 0;
        Action<AgentOrchestratorKernel> sweep = loopKernel =>
        {
            calls++;
            if (calls != 2)
            {
                return;
            }

            loopKernel.RecordDispatchResultCommit(goal.Id, first.Id, "deadbeefcafebabe");
            var firstProcess = loopKernel.GetTask(goal.Id, first.Id).LastProcess!;
            loopKernel.RecordTaskProcessRefreshed(goal.Id, first.Id, firstProcess with { CompletedAt = now, ExitCode = 0 }, null);
            loopKernel.RecordTaskVerification(goal.Id, first.Id, new TaskVerificationRecord("manual", "C:\\repo", 0, "ok", "", now));
            StartProcess(loopKernel, goal, second, now.AddMinutes(-1), "def456", processId: 222);
        };
        var reporter = FakeWatchReporter(now, 10, 0, TimeSpan.FromSeconds(5), [111, 222], [111, 222], ["src/A.cs", "src/B.cs"]);
        var ticks = new List<BatchTickSummary>();

        new ConductorBatchLoop(sweep: sweep, watchProgressReporter: reporter).Run(
            kernel,
            MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true)),
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 2,
            watchInterval: TimeSpan.FromSeconds(1),
            sleepFunc: _ => false,
            onTick: ticks.Add);

        var transition = ticks.SelectMany(t => t.ProgressLines ?? []).Single(l => l.StartsWith("WATCH_TRANSITION ", StringComparison.Ordinal));
        Assert.Contains("Planner=✓", transition);
        Assert.Contains("commit=deadbeefcafe", transition);
        Assert.Contains("files=2", transition);
        Assert.Contains("elapsed=3m0s", transition);
        Assert.Contains("next=Developer", transition);
        Assert.Contains("task=2/2", transition);
        Assert.True(ticks.SelectMany(t => t.ProgressLines ?? []).Any(l => l.Contains("role=Developer", StringComparison.Ordinal)));

        var human = ticks.SelectMany(t => t.ProgressLines ?? []).Single(l => l.StartsWith($"[{goal.Id.Value[..8]}] Planner - committed", StringComparison.Ordinal));
        Assert.Contains("committed deadbee", human);
        Assert.Contains("(2 files changed, 3m0s)", human);
        Assert.Contains("-> Developer dispatched", human);
    }

    [Xunit.Fact(DisplayName = "WatchProgress_emits_final_transition_before_gated_lifecycle_event")]
    public void WatchProgressEmitsFinalTransitionBeforeGatedLifecycleEvent()
    {
        var (kernel, goal) = SimpleGoal("watch final gated transition");
        var task = goal.Tasks.Single();
        var now = DateTimeOffset.Parse("2026-06-22T12:00:00Z");
        StartProcess(kernel, goal, task, now.AddMinutes(-3), "abc123");
        var calls = 0;
        Action<AgentOrchestratorKernel> sweep = loopKernel =>
        {
            calls++;
            if (calls == 2)
            {
                CompleteDispatchedTask(loopKernel, goal, task, now, "feedfacecafebabe");
            }
        };
        var reporter = FakeWatchReporter(now, 10, 0, TimeSpan.FromSeconds(5), [111], [111], ["src/A.cs", "src/B.cs"]);
        var ticks = new List<BatchTickSummary>();

        new ConductorBatchLoop(sweep: sweep, watchProgressReporter: reporter).Run(
            kernel,
            MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true)),
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 2,
            watchInterval: TimeSpan.FromSeconds(1),
            sleepFunc: _ => false,
            onTick: ticks.Add);

        var terminalTick = Assert.Single(ticks.Where(t => (t.ProgressLines ?? [])
            .Any(l => l.StartsWith("WATCH_TRANSITION ", StringComparison.Ordinal))));
        var lines = terminalTick.ProgressLines!.ToList();
        var transitionIndex = lines.FindIndex(l => l.StartsWith("WATCH_TRANSITION ", StringComparison.Ordinal));
        var lifecycleIndex = lines.FindIndex(l => l.StartsWith($"GOAL goal={goal.Id.Value[..8]} ", StringComparison.Ordinal));
        Assert.True(transitionIndex >= 0);
        Assert.True(lifecycleIndex >= 0);
        Assert.True(transitionIndex < lifecycleIndex);

        var transition = lines[transitionIndex];
        Assert.Contains($"{task.RequiredRole}=✓", transition);
        Assert.Contains("commit=feedfacecafe", transition);
        Assert.Contains("files=2", transition);
        Assert.Contains("elapsed=3m0s", transition);
        Assert.Contains("next=acceptance-gate", transition);
        Assert.Contains("task=1/1", transition);
    }

    [Xunit.Fact(DisplayName = "WatchProgress_emits_final_transition_before_ungated_lifecycle_event")]
    public void WatchProgressEmitsFinalTransitionBeforeUngatedLifecycleEvent()
    {
        var (kernel, goal) = SimpleGoal("watch final ungated transition");
        var task = goal.Tasks.Single();
        var now = DateTimeOffset.Parse("2026-06-22T12:00:00Z");
        StartProcess(kernel, goal, task, now.AddMinutes(-3), "abc123");
        var calls = 0;
        Action<AgentOrchestratorKernel> sweep = loopKernel =>
        {
            calls++;
            if (calls == 2)
            {
                CompleteDispatchedTask(loopKernel, goal, task, now, "0123456789abcdef");
            }
        };
        var policy = PolicyEscalatingAtVerified();
        var reporter = FakeWatchReporter(now, 10, 0, TimeSpan.FromSeconds(5), [111], [111], ["src/A.cs"]);
        var ticks = new List<BatchTickSummary>();

        new ConductorBatchLoop(sweep: sweep, watchProgressReporter: reporter).Run(
            kernel,
            MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true)),
            policy,
            NoStopPath(),
            maxIterations: 2,
            watchInterval: TimeSpan.FromSeconds(1),
            sleepFunc: _ => false,
            onTick: ticks.Add);

        var terminalTick = Assert.Single(ticks.Where(t => (t.ProgressLines ?? [])
            .Any(l => l.StartsWith("WATCH_TRANSITION ", StringComparison.Ordinal))));
        var lines = terminalTick.ProgressLines!.ToList();
        var transitionIndex = lines.FindIndex(l => l.StartsWith("WATCH_TRANSITION ", StringComparison.Ordinal));
        var lifecycleIndex = lines.FindIndex(l => l.StartsWith($"GOAL goal={goal.Id.Value[..8]} ", StringComparison.Ordinal));
        Assert.True(transitionIndex >= 0);
        Assert.True(lifecycleIndex >= 0);
        Assert.True(transitionIndex < lifecycleIndex);

        var transition = lines[transitionIndex];
        Assert.Contains($"{task.RequiredRole}=✓", transition);
        Assert.Contains("commit=0123456789ab", transition);
        Assert.Contains("files=1", transition);
        Assert.Contains("elapsed=3m0s", transition);
        Assert.Contains("next=none", transition);
        Assert.Contains("task=1/1", transition);
    }

    [Xunit.Fact(DisplayName = "WatchProgress_uses_operator_supplied_stall_warning_threshold")]
    public void WatchProgressUsesOperatorSuppliedStallWarningThreshold()
    {
        var (kernel, goal) = SimpleGoal("watch custom stall threshold");
        var task = goal.Tasks.First();
        var now = DateTimeOffset.Parse("2026-06-22T12:00:00Z");
        StartProcess(kernel, goal, task, now.AddMinutes(-1), "abc123");
        var reporter = FakeWatchReporter(now, 10, 0, TimeSpan.FromSeconds(45), [111], [111], ["src/A.cs"]);
        var ticks = new List<BatchTickSummary>();

        new ConductorBatchLoop(watchProgressReporter: reporter).Run(
            kernel,
            MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true)),
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 1,
            watchInterval: TimeSpan.FromSeconds(1),
            sleepFunc: _ => true,
            onTick: ticks.Add,
            stallWarningThreshold: TimeSpan.FromSeconds(30));

        var warning = ticks.Single().ProgressLines!.Single(l => l.StartsWith("WATCH_WARNING ", StringComparison.Ordinal));
        Assert.Contains("reason=last-progress-stale", warning);
        Assert.Contains("stall=45s", warning);
    }

    [Xunit.Fact(DisplayName = "WatchProgress_default_stall_threshold_uses_four_poll_intervals_when_larger")]
    public void WatchProgressDefaultStallThresholdUsesFourPollIntervalsWhenLarger()
    {
        var (kernel, goal) = SimpleGoal("watch poll threshold");
        var task = goal.Tasks.First();
        var now = DateTimeOffset.Parse("2026-06-22T12:00:00Z");
        StartProcess(kernel, goal, task, now.AddMinutes(-30), "abc123");
        var reporter = FakeWatchReporter(now, 10, 0, TimeSpan.FromMinutes(12), [111], [111], ["src/A.cs"]);
        var ticks = new List<BatchTickSummary>();

        new ConductorBatchLoop(watchProgressReporter: reporter).Run(
            kernel,
            MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true)),
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 1,
            watchInterval: TimeSpan.FromMinutes(5),
            sleepFunc: _ => true,
            onTick: ticks.Add);

        Assert.DoesNotContain(ticks.Single().ProgressLines!, l => l.StartsWith("WATCH_WARNING ", StringComparison.Ordinal));
        Assert.DoesNotContain(ticks.Single().ProgressLines!, l => l.Contains("WARNING:", StringComparison.Ordinal));
    }

    // ── Fault isolation: a throwing goal is escalated, others still advance ─

    [Xunit.Fact(DisplayName = "BatchLoop_FaultIsolation_ThrowingGoalEscalated_HealthyGoalStillAdvanced")]
    public void BatchLoop_FaultIsolation_ThrowingGoalEscalated_HealthyGoalStillAdvanced()
    {
        var kernel = new AgentOrchestratorKernel();
        var faultyGoal  = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "faulty goal");
        var healthyGoal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "healthy goal");

        var advancedGoalIds = new List<string>();
        var driver = MakeDriver(
            getFacts: g =>
            {
                if (g.Id == faultyGoal.Id)
                    throw new InvalidOperationException("Assigned agent 'missing-agent' was not found.");
                return new GoalLifecycleFacts(WorkspaceExists: true);
            },
            dispatchAndStart: g =>
            {
                advancedGoalIds.Add(g.Id.Value);
                return DispatchStartOutcome.Started();
            });

        var stopFile = NoStopPath();
        var summary = new ConductorBatchLoop().Run(kernel, driver, ConductorAutonomyPolicy.Conservative, stopFile, maxIterations: 1);

        // Faulty goal must be counted escalated and excluded
        Assert.Equal(1, summary.Escalated);
        // Healthy goal must have been advanced
        Assert.True(advancedGoalIds.Contains(healthyGoal.Id.Value));
        // Loop must complete (not throw)
        Assert.Equal(1, summary.Ticks);
    }

    // ── Terminal-goal skip: terminal historical goals not in eligible set ─

    [Xunit.Fact(DisplayName = "BatchLoop_terminal_historical_goals_are_excluded_from_eligible_set")]
    public void BatchLoopTerminalHistoricalGoalsAreExcludedFromEligibleSet()
    {
        var kernel = new AgentOrchestratorKernel();
        var verifiedGoal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "verified goal");
        var cleanedUpGoal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "cleaned-up goal");
        var cancelledGoal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "cancelled goal");
        var supersededGoal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "superseded goal");

        PassVerification(kernel, verifiedGoal, verifiedGoal.Tasks.Single());
        PassVerification(kernel, cleanedUpGoal, cleanedUpGoal.Tasks.Single());
        kernel.CompleteGoal(cleanedUpGoal.Id, "Test fixture: historical goal already landed, recorded, and cleaned up.");
        kernel.CancelGoal(cancelledGoal.Id, "test cancel");
        kernel.SupersedeGoal(supersededGoal.Id, "test supersede");

        var terminalGoalIds = new HashSet<GoalId>
        {
            cleanedUpGoal.Id,
            cancelledGoal.Id,
            supersededGoal.Id
        };
        var driverCalls = new List<GoalId>();
        var advancedGoalIds = new List<GoalId>();
        var driver = MakeDriver(
            getFacts: goal =>
            {
                driverCalls.Add(goal.Id);
                return goal.Id == cleanedUpGoal.Id
                    ? new GoalLifecycleFacts(IsMerged: true, IsRecorded: true, IsCleanedUp: true)
                    : GoalLifecycleFacts.None;
            },
            land: goal =>
            {
                advancedGoalIds.Add(goal.Id);
                return new LandingResult(goal.Id.Value, goal.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "ok");
            },
            record: goal => advancedGoalIds.Add(goal.Id),
            cleanup: goal =>
            {
                advancedGoalIds.Add(goal.Id);
                return new GoalWorktreeRemoveResult("Workspace cleaned up.", null, [], null);
            });

        var summary = new ConductorBatchLoop().Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 3);

        Assert.Equal(3, summary.Ticks);
        Assert.Equal(3, summary.Advanced);
        Assert.Equal(0, summary.Escalated);
        Assert.Contains(verifiedGoal.Id, driverCalls);
        Assert.Empty(advancedGoalIds.Where(terminalGoalIds.Contains));
    }

    [Xunit.Theory(DisplayName = "BatchLoop_stale_terminal_goals_with_assigned_work_are_excluded_from_processing_set")]
    [Xunit.InlineData(GoalStatus.Completed)]
    [Xunit.InlineData(GoalStatus.Cancelled)]
    [Xunit.InlineData(GoalStatus.Failed)]
    public void BatchLoopStaleTerminalGoalsWithAssignedWorkAreExcludedFromProcessingSet(GoalStatus status)
    {
        var kernel = new AgentOrchestratorKernel();
        var staleTask = new TaskSpec(TaskId.New(), "Stale assigned work", AgentRole.Developer);
        var staleGoal = kernel.CreateGoal($"Stale {status}", [staleTask]);
        var activeGoal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "Active conductor goal");
        kernel.ActivateGoal(staleGoal.Id, DefaultAgents());
        kernel = WithGoalStatus(kernel, staleGoal.Id, status);

        var advancedGoalIds = new List<GoalId>();
        var driver = MakeDriver(
            createWorkspace: goal =>
            {
                advancedGoalIds.Add(goal.Id);
                return "/tmp/workspace";
            },
            dispatchAndStart: goal =>
            {
                advancedGoalIds.Add(goal.Id);
                return DispatchStartOutcome.Started();
            });

        var summary = new ConductorBatchLoop().Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 1);

        Assert.Equal(1, summary.Ticks);
        Assert.Equal(1, summary.Advanced);
        Assert.Contains(activeGoal.Id, advancedGoalIds);
        Assert.DoesNotContain(staleGoal.Id, advancedGoalIds);
        Assert.Equal(status, kernel.GetGoal(staleGoal.Id).Status);
        Assert.Equal(WorkTaskStatus.Assigned, kernel.GetTask(staleGoal.Id, staleTask.Id).Status);
    }

    [Xunit.Fact(DisplayName = "BatchLoop_pre_walk_exclusion_avoids_lifecycle_fact_reads_for_inert_goals")]
    public void BatchLoopPreWalkExclusionAvoidsLifecycleFactReadsForInertGoals()
    {
        var kernel = new AgentOrchestratorKernel();
        var cancelled = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "cancelled");
        var superseded = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "superseded");
        var parked = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "parked");
        var staleTask = new TaskSpec(TaskId.New(), "stale assigned", AgentRole.Developer);
        var staleCompleted = kernel.CreateGoal("stale completed", [staleTask]);
        var active = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "active");
        kernel.ActivateGoal(staleCompleted.Id, DefaultAgents());
        kernel.CancelGoal(cancelled.Id, "cancelled");
        kernel.SupersedeGoal(superseded.Id, "superseded");
        kernel.ParkGoal(parked.Id, "parked");
        kernel = WithGoalStatus(kernel, staleCompleted.Id, GoalStatus.Completed);

        var inertGoalIds = new HashSet<GoalId> { cancelled.Id, superseded.Id, parked.Id, staleCompleted.Id };
        var factReads = new List<GoalId>();
        var advancedGoalIds = new List<GoalId>();
        var driver = MakeDriver(
            getFacts: goal =>
            {
                factReads.Add(goal.Id);
                if (inertGoalIds.Contains(goal.Id))
                {
                    throw new InvalidOperationException("Inert goal should have been excluded before lifecycle facts.");
                }

                return GoalLifecycleFacts.None;
            },
            createWorkspace: goal =>
            {
                advancedGoalIds.Add(goal.Id);
                return "/tmp/workspace";
            });

        new ConductorBatchLoop().Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 1);

        Assert.Contains(active.Id, advancedGoalIds);
        Assert.DoesNotContain(factReads, inertGoalIds.Contains);
    }

    [Xunit.Fact(DisplayName = "BatchLoop_reuses_unchanged_cleaned_up_projection_between_watch_ticks")]
    public void BatchLoopReusesUnchangedCleanedUpProjectionBetweenWatchTicks()
    {
        var kernel = new AgentOrchestratorKernel();
        var cleanedUp = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "cleaned up");
        var active = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "active held");
        PassVerification(kernel, cleanedUp, cleanedUp.Tasks.Single());
        kernel.CompleteGoal(cleanedUp.Id, "completed");

        var cleanedFactReads = 0;
        var driver = MakeDriver(
            getFacts: goal =>
            {
                if (goal.Id == cleanedUp.Id)
                {
                    cleanedFactReads++;
                    return new GoalLifecycleFacts(IsMerged: true, IsRecorded: true, IsCleanedUp: true);
                }

                return new GoalLifecycleFacts(WorkspaceExists: true);
            },
            getRunningCount: () => ConductorAutonomyPolicy.Conservative.MaxConcurrentPaidWorkers);

        new ConductorBatchLoop().Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 3,
            watchInterval: TimeSpan.FromSeconds(1),
            sleepFunc: _ => false);

        Assert.Equal(1, cleanedFactReads);
        Assert.Equal(GoalStatus.Completed, kernel.GetGoal(cleanedUp.Id).Status);
        Assert.Equal(GoalStatus.Active, kernel.GetGoal(active.Id).Status);
    }

    [Xunit.Fact(DisplayName = "BatchLoop_parked_goal_is_counted_in_summary_without_escalation_and_resumes_when_active")]
    public void BatchLoopParkedGoalIsCountedInSummaryWithoutEscalationAndResumesWhenActive()
    {
        var kernel = new AgentOrchestratorKernel();
        var parkedGoal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "Parked conductor goal");
        var activeGoal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "Active conductor goal");
        var request = kernel.RequestHumanInput(parkedGoal.Id, null, "Should this continue?");
        kernel.ParkGoal(parkedGoal.Id, "operator deferred");

        Assert.True(request.IsCompleted);
        Assert.DoesNotContain(kernel.HumanInputRequests, candidate => candidate.GoalId == parkedGoal.Id && !candidate.IsCompleted);

        var advancedGoalIds = new List<GoalId>();
        var escalationReasons = new List<string>();
        var driver = MakeDriver(
            createWorkspace: goal =>
            {
                advancedGoalIds.Add(goal.Id);
                return "/tmp/workspace";
            },
            writeEscalation: (_, _, reason) => escalationReasons.Add(reason));

        var parkedOutput = AsyncLocalConsoleRouter.Capture(() =>
        {
            new ConductorBatchLoop().Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 1);
        });

        Assert.Empty(escalationReasons);
        Assert.DoesNotContain(parkedGoal.Id, advancedGoalIds);
        Assert.Contains(activeGoal.Id, advancedGoalIds);
        Assert.Contains("TICK tick=1 eligible=1", parkedOutput);
        Assert.Contains("TICK_EXCLUDED tick=1 kind=parked count=1", parkedOutput);
        Assert.Contains("TICK_END tick=1 advanced=1 held=0 escalated=0 done=0", parkedOutput);
        Assert.DoesNotContain($"GOAL goal={parkedGoal.Id.Value[..8]}", parkedOutput);
        Assert.DoesNotContain("AwaitingHumanInput", parkedOutput, StringComparison.Ordinal);

        kernel = WithGoalStatus(kernel, parkedGoal.Id, GoalStatus.Active);
        advancedGoalIds.Clear();
        escalationReasons.Clear();

        var resumedOutput = AsyncLocalConsoleRouter.Capture(() =>
        {
            new ConductorBatchLoop().Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 1);
        });

        Assert.Empty(escalationReasons);
        Assert.Contains(parkedGoal.Id, advancedGoalIds);
        Assert.Contains($"GOAL goal={parkedGoal.Id.Value[..8]} result=executed state=Created", resumedOutput);
        Assert.DoesNotContain("TICK_EXCLUDED tick=1 kind=parked", resumedOutput);
    }

    [Xunit.Fact(DisplayName = "BatchLoop_retired_verified_goal_is_excluded_from_tick_eligible_set")]
    public void BatchLoopRetiredVerifiedGoalIsExcludedFromTickEligibleSet()
    {
        var root = CreateSeededGitRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var retiredGoal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "Retired missing branch goal");
            var activeGoal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "Active conductor goal");
            PassVerification(kernel, retiredGoal, retiredGoal.Tasks.Single());

            var sweep = TerminalGoalSweep.Run(kernel, root);
            Assert.Contains(sweep.Goals, goal =>
                goal.GoalId == retiredGoal.Id &&
                goal.Repairs.Any(repair => repair.Kind == "missing-branch-retired"));
            Assert.True(GoalOperationJournal.HasRetiredTerminalDisposition(GoalOperationJournal.Read(root, retiredGoal.Id)));

            GoalOperationJournal.Begin(root, retiredGoal, "conductor:cleanup", "Later interrupted cleanup must not erase retirement.");

            var advancedGoalIds = new List<GoalId>();
            var driver = MakeDriver(
                getFacts: goal =>
                {
                    var journal = GoalOperationJournal.Read(root, goal.Id);
                    return new GoalLifecycleFacts(
                        IsMerged: GoalOperationJournal.HasCompletedLandingEvidence(journal),
                        IsRecorded: GoalOperationJournal.HasCompletedRecordEvidence(journal),
                        IsCleanedUp: GoalOperationJournal.HasCompletedCleanupEvidence(journal));
                },
                createWorkspace: goal =>
                {
                    advancedGoalIds.Add(goal.Id);
                    return "/tmp/workspace";
                },
                dispatchAndStart: goal =>
                {
                    advancedGoalIds.Add(goal.Id);
                    return DispatchStartOutcome.Started();
                });

            var output = AsyncLocalConsoleRouter.Capture(() =>
            {
                new ConductorBatchLoop().Run(
                    kernel,
                    driver,
                    ConductorAutonomyPolicy.Conservative,
                    NoStopPath(),
                    maxIterations: 1);
            });

            Assert.Contains("TICK tick=1 eligible=1", output);
            Assert.Contains(activeGoal.Id, advancedGoalIds);
            Assert.DoesNotContain(retiredGoal.Id, advancedGoalIds);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "BatchLoop_verified_missing_branch_is_retired_without_tick_escalation")]
    public void BatchLoopVerifiedMissingBranchIsRetiredWithoutTickEscalation()
    {
        var root = CreateSeededGitRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var missingBranchGoal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "Missing branch pre-landing goal");
            PassVerification(kernel, missingBranchGoal, missingBranchGoal.Tasks.Single());

            var escalationReasons = new List<string>();
            var driver = MakeDriver(
                rebaseOntoMain: goal => new GoalWorktreeRebaseResult(
                    GoalWorktreeRebaseStatus.MissingBranch,
                    GoalWorktrees.BranchName(goal.Id),
                    $"Goal branch {GoalWorktrees.BranchName(goal.Id)} is missing.",
                    [],
                    "goal-recovery"),
                writeEscalation: (_, _, reason) => escalationReasons.Add(reason),
                recordMissingBranchRetirement: (goal, detail) =>
                {
                    GoalOperationJournal.RecordTerminalDisposition(
                        root,
                        goal,
                        new GoalTerminalDisposition(GoalTerminalDispositionKind.Retired, detail));
                    kernel.CompleteGoal(goal.Id, detail);
                });

            var output = AsyncLocalConsoleRouter.Capture(() =>
            {
                new ConductorBatchLoop().Run(
                    kernel,
                    driver,
                    ConductorAutonomyPolicy.Conservative,
                    NoStopPath(),
                    maxIterations: 1);
            });

            Assert.Empty(escalationReasons);
            Assert.Contains("TICK_END tick=1 advanced=0 held=0 escalated=0 done=1", output);
            Assert.DoesNotContain("pre-landing rebase failed", output, StringComparison.Ordinal);
            Assert.Equal(GoalStatus.Completed, kernel.GetGoal(missingBranchGoal.Id).Status);
            Assert.True(GoalOperationJournal.HasRetiredTerminalDisposition(GoalOperationJournal.Read(root, missingBranchGoal.Id)));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    // ── Duration cap: loop exits when max-duration is reached ────────────

    [Xunit.Fact(DisplayName = "MaxDuration_ParameterAcceptedAndLoopExitsCleanly")]
    public void MaxDuration_ParameterAcceptedAndLoopExitsCleanly()
    {
        var (kernel, _) = SimpleGoal();

        // Goal is always held (concurrent cap) so without a cap the loop would exit on first no-progress tick.
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningCount: () => ConductorAutonomyPolicy.Conservative.MaxConcurrentPaidWorkers);

        var stopFile = NoStopPath();

        // maxDuration is generous (won't fire), maxIterations not set.
        // Loop exits on first held tick via the no-watch-mode no-progress path.
        var summary = new ConductorBatchLoop().Run(
            kernel, driver, ConductorAutonomyPolicy.Conservative, stopFile,
            maxDuration: TimeSpan.FromSeconds(60));

        // Loop exits cleanly (not via stop file, not via duration cap — via no-progress exit path)
        Assert.False(summary.StopRequested);
        Assert.Equal(1, summary.Ticks);
        Assert.Equal(0, summary.Advanced);
    }

    // ── Per-tick write scope: persistGoalTick fires once with exactly the goals that changed ──

    [Xunit.Fact(DisplayName = "PersistGoalTick_busy_exhausted_aborts_changed_goal_tick")]
    public void PersistGoalTickBusyExhaustedAbortsChangedGoalTick()
    {
        var (kernel, goal) = SimpleGoal("busy persistence survives");
        var driver = MakeDriver();
        var ticks = new List<BatchTickSummary>();
        var attempts = 0;

        var ex = Assert.Throws<InvalidOperationException>(() => new ConductorBatchLoop().Run(
            kernel, driver, ConductorAutonomyPolicy.Conservative, NoStopPath(),
            maxIterations: 2,
            onTick: ticks.Add,
            persistGoalTick: (_, _) =>
            {
                attempts++;
                throw SqliteBusy();
            },
            busyWriteDelay: _ => { }));

        Assert.Contains("DISPATCH_RECORD_WRITE_FAILED", ex.Message, StringComparison.Ordinal);
        Assert.Empty(ticks);
        Assert.True(attempts >= ConductorBatchLoop.DefaultMaxBusyWriteAttempts);
        Assert.Contains(goal.Id.Value[..8], ex.Message, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "PersistGoalTick_busy_retries_and_succeeds_before_budget")]
    public void PersistGoalTickBusyRetriesAndSucceedsBeforeBudget()
    {
        var (kernel, _) = SimpleGoal("busy persistence clears");
        var driver = MakeDriver();
        var ticks = new List<BatchTickSummary>();
        var attempts = 0;

        var summary = new ConductorBatchLoop().Run(
            kernel, driver, ConductorAutonomyPolicy.Conservative, NoStopPath(),
            maxIterations: 1,
            onTick: ticks.Add,
            persistGoalTick: (_, _) =>
            {
                attempts++;
                if (attempts < 3)
                    throw SqliteBusy();
            },
            busyWriteDelay: _ => { });

        Assert.Equal(1, summary.Ticks);
        Assert.True(attempts >= 3, $"Expected at least 3 persistence attempts, got {attempts}");
        var lines = ticks.SelectMany(tick => tick.ProgressLines ?? []).ToArray();
        Assert.Equal(2, lines.Count(line => line.Contains("TICK_WRITE_BUSY", StringComparison.Ordinal)));
        Assert.DoesNotContain(lines, line => line.Contains("TICK_WRITE_DEGRADED", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "PersistGoalTick_FiresOneBatchForGoalsThatChangedDisposition")]
    public void PersistGoalTick_FiresOneBatchForGoalsThatChangedDisposition()
    {
        // Two goals: A advances (workspace creation), B is held by concurrent cap.
        // persistGoalTick must be called once with A and without B.
        var kernel = new AgentOrchestratorKernel();
        var goalA = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "goal A");
        var goalB = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "goal B");

        var createWorkspaceCalls = 0;
        var driver = MakeDriver(
            getFacts: g => g.Id == goalA.Id
                ? GoalLifecycleFacts.None          // A: no workspace yet → workspace creation tick
                : new GoalLifecycleFacts(WorkspaceExists: true),
            // After A's workspace is created the running count hits cap, so B stays held.
            getRunningCount: () => createWorkspaceCalls >= 1
                ? ConductorAutonomyPolicy.Conservative.MaxConcurrentPaidWorkers
                : 0,
            createWorkspace: _ => { createWorkspaceCalls++; return "/tmp/ws"; });

        var persistedGoalBatches = new List<IReadOnlyCollection<GoalId>>();

        new ConductorBatchLoop().Run(
            kernel, driver, ConductorAutonomyPolicy.Conservative, NoStopPath(),
            maxIterations: 1,
            persistGoalTick: (_, changedGoalIds) => persistedGoalBatches.Add(changedGoalIds.ToArray()));

        // A changed disposition (workspace created); B was held with no state change in the tick batch.
        var persistedGoalIds = persistedGoalBatches.First();
        Assert.Contains(goalA.Id, persistedGoalIds);
        Assert.DoesNotContain(goalB.Id, persistedGoalIds);
        Assert.Single(persistedGoalIds);
    }

    private static AgentOrchestratorKernel WithGoalStatus(
        AgentOrchestratorKernel kernel,
        GoalId goalId,
        GoalStatus status)
    {
        var snapshot = kernel.ExportSnapshot();
        return AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            Goals = snapshot.Goals
                .Select(goal => goal.Id == goalId.Value ? goal with { Status = status } : goal)
                .ToArray()
        });
    }
}
