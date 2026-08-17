using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Dashboard.Api;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsParallelAcceptance : ConductorBatchLoopTests
{
    public ConductorBatchLoopTestsParallelAcceptance(ITestOutputHelper output)
        : base(output)
    {
    }

    [Xunit.Fact]
    public void BatchLoopDocIntersectionAdmitsConcurrentlyWithEvidence()
    {
        var kernel = new AgentOrchestratorKernel();
        var goalA = CreateVerifiedSimpleGoal(kernel, "Update src/Mcg.AgentOrchestrator.App/Orchestration/A.cs");
        var goalB = CreateVerifiedSimpleGoal(kernel, "Update src/Mcg.AgentOrchestrator.App/Orchestration/B.cs");
        for (var attempt = 1;
             attempt < 128 && BuildPermitIndex(goalA) == BuildPermitIndex(goalB);
             attempt++)
        {
            kernel = new AgentOrchestratorKernel();
            goalA = CreateVerifiedSimpleGoal(kernel, "Update src/Mcg.AgentOrchestrator.App/Orchestration/A.cs");
            goalB = CreateVerifiedSimpleGoal(kernel, "Update src/Mcg.AgentOrchestrator.App/Orchestration/B.cs");
        }
        if (BuildPermitIndex(goalA) == BuildPermitIndex(goalB))
        {
            throw new InvalidOperationException(
                $"Could not generate goals on distinct build permits after 128 attempts; " +
                $"goalA={goalA.Id.Value}, goalB={goalB.Id.Value}, permit={BuildPermitIndex(goalA)}.");
        }

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
                    ? ["docs/test-design-discipline.md", "src/Mcg.AgentOrchestrator.App/Orchestration/A.cs"]
                    : ["docs\\test-design-discipline.md", "src/Mcg.AgentOrchestrator.App/Orchestration/B.cs"],
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

            Assert.Equal(0, startSummary.Advanced);
            Assert.Equal(2, startSummary.Held);
            Assert.Equal(GoalStatus.Verifying, goalA.Status);
            Assert.Equal(GoalStatus.Verifying, goalB.Status);
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
                    Assert.Equal(ConductorParallelAcceptanceAttemptCoordinator.GateDispatchKind, attempt.Kind);
                    Assert.Contains("docs/test-design-discipline.md", attempt.ScopePaths ?? []);
                    using var heartbeat = JsonDocument.Parse(File.ReadAllText(attempt.HeartbeatPath));
                    Assert.Equal(ConductorParallelAcceptanceAttemptCoordinator.GateDispatchKind, heartbeat.RootElement.GetProperty("kind").GetString());
                    Assert.Equal(attempt.OwnerProcessId, heartbeat.RootElement.GetProperty("childPid").GetInt32());
                    Assert.Contains(
                        heartbeat.RootElement.GetProperty("ownedPids").EnumerateArray(),
                        pid => pid.GetInt32() == attempt.OwnerProcessId);
                });
            Assert.Contains(startTick!.ProgressLines!, line =>
                line.Contains("ADMISSION", StringComparison.Ordinal) &&
                line.Contains("result=admitted", StringComparison.Ordinal) &&
                line.Contains("reason=documentation-exclusion", StringComparison.Ordinal) &&
                line.Contains("excludedPathCount=1", StringComparison.Ordinal) &&
                line.Contains("excludedPathSample=docs/test-design-discipline.md", StringComparison.Ordinal));
            Assert.Contains(startTick!.ProgressLines!, line => line.Contains("ACCEPTANCE", StringComparison.Ordinal) && line.Contains("result=started", StringComparison.Ordinal));
            Assert.Contains(reconcileTick!.ProgressLines!, line => line.Contains("ACCEPTANCE", StringComparison.Ordinal) && line.Contains("result=passed", StringComparison.Ordinal));
        }
        finally
        {
            waitForAttempts();
            TryDeleteDirectory(attemptRoot);
        }
    }

    [Xunit.Fact(DisplayName = "BatchLoop_live_oldest_attempt_does_not_block_second_slot_across_ticks")]
    public void BatchLoopLiveOldestAttemptDoesNotBlockSecondSlotAcrossTicks()
    {
        using var isolatedRoot = IsolatedDotnetRootScope();
        var kernel = new AgentOrchestratorKernel();
        var running = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
            kernel,
            DefaultAgents(),
            "Update src/Mcg.AgentOrchestrator.App/Orchestration/RunningAcrossTicks.cs");
        var primer = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
            kernel,
            DefaultAgents(),
            "Update src/Mcg.AgentOrchestrator.App/Orchestration/PrimerAcrossTicks.cs");
        var waiting = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
            kernel,
            DefaultAgents(),
            "Update src/Mcg.AgentOrchestrator.App/Orchestration/WaitingAcrossTicks.cs");
        for (var attempt = 1;
             attempt < 128 &&
             (BuildPermitIndex(running) == BuildPermitIndex(primer) ||
              BuildPermitIndex(running) == BuildPermitIndex(waiting));
             attempt++)
        {
            kernel = new AgentOrchestratorKernel();
            running = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
                kernel,
                DefaultAgents(),
                "Update src/Mcg.AgentOrchestrator.App/Orchestration/RunningAcrossTicks.cs");
            primer = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
                kernel,
                DefaultAgents(),
                "Update src/Mcg.AgentOrchestrator.App/Orchestration/PrimerAcrossTicks.cs");
            waiting = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
                kernel,
                DefaultAgents(),
                "Update src/Mcg.AgentOrchestrator.App/Orchestration/WaitingAcrossTicks.cs");
        }
        if (BuildPermitIndex(running) == BuildPermitIndex(primer) ||
            BuildPermitIndex(running) == BuildPermitIndex(waiting))
        {
            throw new InvalidOperationException(
                $"Could not generate a running goal with a permit distinct from both later goals after 128 attempts; " +
                $"running={BuildPermitIndex(running)}, primer={BuildPermitIndex(primer)}, waiting={BuildPermitIndex(waiting)}.");
        }

        var now = DateTimeOffset.UtcNow;
        PassVerificationAt(kernel, running, running.Tasks.Single(), now);
        using var releaseGates = new ManualResetEventSlim(false);
        using var runningEntered = new ManualResetEventSlim(false);
        using var primerEntered = new ManualResetEventSlim(false);
        using var waitingEntered = new ManualResetEventSlim(false);
        var attemptRoot = CreateTempDirectory("mcg-conductor-acceptance-attempts");
        var rejectRunningCandidateRebuild = false;
        Action waitForAttempts = () => { };

        try
        {
            var coordinator = ThreadedAcceptanceAttemptCoordinator(attemptRoot, out waitForAttempts);
            var driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                runAcceptanceWithSlot: (goal, _) =>
                {
                    if (goal.Id == running.Id)
                    {
                        runningEntered.Set();
                        Assert.True(releaseGates.Wait(TimeSpan.FromSeconds(5)));
                    }
                    else if (goal.Id == primer.Id)
                    {
                        primerEntered.Set();
                    }
                    else if (goal.Id == waiting.Id)
                    {
                        waitingEntered.Set();
                        Assert.True(releaseGates.Wait(TimeSpan.FromSeconds(5)));
                    }

                    return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria;
                },
                land: goal => new LandingResult(
                    goal.Id.Value,
                    goal.Id.Value[..8],
                    new LandingDecision.Promote(),
                    "integration",
                    true,
                    "ok"),
                getLandingFileScopes: goal =>
                {
                    if (goal.Id == running.Id && rejectRunningCandidateRebuild)
                    {
                        throw new IOException("running candidate scope temporarily unavailable");
                    }

                    return [$"src/Mcg.AgentOrchestrator.App/Orchestration/{goal.Id.Value}.cs"];
                },
                parallelAcceptanceAttemptCoordinator: coordinator);

            new ConductorBatchLoop().Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 1);
            Assert.True(runningEntered.Wait(TimeSpan.FromSeconds(5)));

            rejectRunningCandidateRebuild = true;
            PassVerificationAt(kernel, primer, primer.Tasks.Single(), now.AddMinutes(1));
            new ConductorBatchLoop().Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 1);
            Assert.True(primerEntered.Wait(TimeSpan.FromSeconds(5)));

            PassVerificationAt(kernel, waiting, waiting.Tasks.Single(), now.AddMinutes(2));
            BatchTickSummary? admissionTick = null;
            new ConductorBatchLoop().Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 1,
                onTick: current => admissionTick = current);

            Assert.Contains(admissionTick!.ProgressLines!, line =>
                line.Contains($"ACCEPTANCE goal={waiting.Id.Value[..8]}", StringComparison.Ordinal) &&
                line.Contains("result=started", StringComparison.Ordinal));
            Assert.DoesNotContain(admissionTick.ProgressLines!, line =>
                line.Contains($"goal={waiting.Id.Value[..8]}", StringComparison.Ordinal) &&
                line.Contains("result=deferred", StringComparison.Ordinal) &&
                line.Contains("reason=parallel-acceptance-fairness", StringComparison.Ordinal));
            Assert.True(waitingEntered.Wait(TimeSpan.FromSeconds(5)));

            var runningAttempt = ReadLatestAttempt(attemptRoot, running);
            var waitingAttempt = ReadLatestAttempt(attemptRoot, waiting);
            var heldPermits = new[]
            {
                ReadAcquirePermit(runningAttempt),
                ReadAcquirePermit(waitingAttempt)
            };
            var configuredPermits = Enumerable
                .Range(0, DotnetBuildEnvironmentManager.BuildConcurrencySlotCount)
                .Select(index => $"build-{index}")
                .ToHashSet(StringComparer.Ordinal);

            Assert.True(coordinator.HasLiveAttempt(running.Id.Value));
            Assert.True(coordinator.HasLiveAttempt(waiting.Id.Value));
            Assert.Equal(2, heldPermits.Distinct(StringComparer.Ordinal).Count());
            Assert.All(heldPermits, permit => Assert.Contains(permit, configuredPermits));
        }
        finally
        {
            releaseGates.Set();
            waitForAttempts();
            TryDeleteDirectory(attemptRoot);
        }
    }

    [Xunit.Fact(DisplayName = "BatchLoop_candidate_manifest_slot_count_limits_own_parallel_acceptance")]
    public void BatchLoopCandidateManifestSlotCountLimitsOwnParallelAcceptance()
    {
        using var isolatedRoot = IsolatedDotnetRootScope();
        var kernel = new AgentOrchestratorKernel();
        var goalA = CreateVerifiedSimpleGoal(kernel, "Update src/Mcg.AgentOrchestrator.App/Orchestration/A.cs");
        var goalB = CreateVerifiedSimpleGoal(kernel, "Update src/Mcg.AgentOrchestrator.App/Orchestration/B.cs");
        using var release = new ManualResetEventSlim();
        using var started = new CountdownEvent(1);
        var slots = new ConcurrentDictionary<string, int?>();
        var attemptRoot = CreateTempDirectory("mcg-conductor-manifest-slot-count");
        Action waitForAttempts = () => { };

        try
        {
            var driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                runAcceptanceWithSlot: (goal, slot) =>
                {
                    slots[goal.Id.Value] = slot;
                    started.Signal();
                    Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
                    return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria;
                },
                getLandingFileScopes: goal => goal.Id == goalA.Id
                    ? ["src/Mcg.AgentOrchestrator.App/Orchestration/A.cs"]
                    : ["src/Mcg.AgentOrchestrator.App/Orchestration/B.cs"],
                parallelAcceptanceAttemptCoordinator: ThreadedAcceptanceAttemptCoordinator(attemptRoot, out waitForAttempts),
                getAcceptanceSlotCount: goal =>
                    goal.Id == goalA.Id
                        ? ConductorBatchLoop.DefaultParallelAcceptanceCapacity
                        : 1);

            var summary = new ConductorBatchLoop().Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 1);

            Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(2, summary.Held);
            Assert.Equal(0, slots[goalA.Id.Value]);
            Assert.False(slots.ContainsKey(goalB.Id.Value));
            Assert.Equal(GoalStatus.Verifying, goalA.Status);
            Assert.Equal(GoalStatus.Verified, goalB.Status);
        }
        finally
        {
            release.Set();
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
        using var releaseAcceptance = new SemaphoreSlim(0);
        using var acceptanceStarted = new SemaphoreSlim(0);
        using var acceptanceFinished = new SemaphoreSlim(0);
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

                    acceptanceStarted.Release();
                    try
                    {
                        Assert.True(releaseAcceptance.Wait(TimeSpan.FromSeconds(5)));
                        return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria;
                    }
                    finally
                    {
                        Interlocked.Decrement(ref running);
                        acceptanceFinished.Release();
                    }
                },
                land: goal =>
                {
                    landed.Add(goal.Id.Value);
                    return new LandingResult(goal.Id.Value, goal.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "ok");
                },
                getLandingFileScopes: _ => ["src/Mcg.AgentOrchestrator.App/Orchestration/Same.cs"],
                parallelAcceptanceAttemptCoordinator: ThreadedAcceptanceAttemptCoordinator(attemptRoot, out waitForAttempts));

            BatchLoopSummary RunSingleTick() =>
                new ConductorBatchLoop().Run(
                    kernel,
                    driver,
                    ConductorAutonomyPolicy.Conservative,
                    NoStopPath(),
                    maxIterations: 1);

            void ReleaseStartedAcceptance()
            {
                Assert.True(acceptanceStarted.Wait(TimeSpan.FromSeconds(5)));
                releaseAcceptance.Release();
                Assert.True(acceptanceFinished.Wait(TimeSpan.FromSeconds(5)));
                waitForAttempts();
            }

            var firstSummary = RunSingleTick();
            ReleaseStartedAcceptance();

            var secondSummary = RunSingleTick();
            ReleaseStartedAcceptance();

            var thirdSummary = RunSingleTick();

            var totalAdvanced = firstSummary.Advanced + secondSummary.Advanced + thirdSummary.Advanced;
            var totalHeld = firstSummary.Held + secondSummary.Held + thirdSummary.Held;
            var observedSlots = slots.ToArray();

            Assert.Equal(2, totalAdvanced);
            Assert.True(totalHeld >= 2);
            Assert.False(overlapped);
            Assert.Equal(2, observedSlots.Length);
            Assert.All(observedSlots, slot => Assert.True(slot.HasValue));
        }
        finally
        {
            releaseAcceptance.Release(2);
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

    [Xunit.Fact]
    public void ProductionCohortRunsOneSharedGateAndBypassesOrdinaryMemberGates()
    {
        var root = CreateTempDirectory("mcg-speculative-cohort-advisory");
        var logPath = Path.Combine(root, ConductEventLogWriter.CurrentFileName);
        var kernel = new AgentOrchestratorKernel();
        var first = CreateVerifiedSimpleGoal(kernel, "Update first advisory source");
        var second = CreateVerifiedSimpleGoal(kernel, "Update second advisory source");
        var statusesBefore = new[] { first.Status, second.Status };
        var paths = new Dictionary<GoalId, IReadOnlyList<string>>
        {
            [first.Id] = ["src/Mcg.AgentOrchestrator.App/Dashboard/Components/FirstAdvisory.razor"],
            [second.Id] = ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests/SecondAdvisoryTests.cs"]
        };
        var acceptanceCalls = new List<string>();
        var landed = new List<string>();
        var cohortCalls = 0;
        string? sharedReceiptId = null;
        var mainRevision = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var projector = new GateReadyCandidateProjector(
            goalId => new GateReadyCandidateRevisionPair(
                goalId.Value.PadRight(40, 'b')[..40],
                mainRevision),
            goalId => new GateReadyLandingScopeObservation(true, paths[goalId]),
            (_, _, _) => new GateReadyMergeTreeObservation(true));

        try
        {
            var driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                runAcceptanceWithSlot: (goal, _) =>
                {
                    acceptanceCalls.Add(goal.Id.Value);
                    return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria;
                },
                land: goal =>
                {
                    landed.Add(goal.Id.Value);
                    return new LandingResult(
                        goal.Id.Value,
                        goal.Id.Value[..8],
                        new LandingDecision.Promote(),
                        "integration",
                        true,
                        "ok");
                },
                classifyRisk: _ => ChangeRiskTier.DocsOnly,
                getLandingFileScopes: goal => paths[goal.Id],
                isVerificationGateSatisfied: _ => true,
                gateReadyCandidateProjector: projector,
                runAcceptanceCohort: (selection, goals, policy) =>
                {
                    cohortCalls++;
                    var identity = AcceptanceCohortIdentity.Create(
                        selection.BindMembers(),
                        mainRevision,
                        "dddddddddddddddddddddddddddddddddddddddd",
                        "manifest-v1");
                    var receipt = new AcceptanceCohortReceipt(
                        $"receipt-{identity.Value}",
                        identity,
                        AcceptanceCohortGateOutcome.Passed,
                        DateTimeOffset.UtcNow,
                        10,
                        [],
                        GateExitCode: 0,
                        GateTestResultPaths: [Path.GetFullPath("production-cohort.trx")],
                        ValidForLanding: true);
                    sharedReceiptId = receipt.ReceiptId;
                    return new ConductorAcceptanceCohortRunResult(
                        receipt,
                        goals.Where(goal => selection.Members.Any(member => member.GoalId == goal.Id)).ToDictionary(
                            goal => goal.Id.Value,
                            goal => new ConductorAdvanceResult(
                                goal.Id.Value,
                                goal.Id.Value[..8],
                                policy.Name,
                                new ConductorAdvanceOutcome.Executed(
                                    GoalLifecycleState.Verified,
                                    $"shared receipt {receipt.ReceiptId}")),
                            StringComparer.Ordinal),
                        $"outcome=passed receipt={receipt.ReceiptId}");
                });

            var summary = new ConductorBatchLoop(
                conductEventLogWriter: new ConductEventLogWriter(logPath)).Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 1);
            var cohortEvents = File.ReadAllLines(logPath)
                .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(
                    line,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
                .Where(record => record.EventKind == "speculative-cohort-plan")
                .ToArray();

            Assert.Equal(2, summary.Advanced);
            Assert.Equal(1, cohortCalls);
            Assert.Empty(acceptanceCalls);
            Assert.Empty(landed);
            Assert.NotNull(sharedReceiptId);
            Assert.Equal(statusesBefore, new[] { first.Status, second.Status });
            var receipt = Assert.Single(cohortEvents);
            Assert.Null(receipt.GoalId);
            Assert.Contains("advisory=true", receipt.Detail, StringComparison.Ordinal);
            Assert.Contains(first.Id.Value[..8], receipt.Detail, StringComparison.Ordinal);
            Assert.Contains(second.Id.Value[..8], receipt.Detail, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact]
    public void IncompatibleCohortCandidates_UseOrdinaryAcceptance()
    {
        var kernel = new AgentOrchestratorKernel();
        var first = CreateVerifiedSimpleGoal(kernel, "Update first overlapping source");
        var second = CreateVerifiedSimpleGoal(kernel, "Update second overlapping source");
        var acceptanceCalls = new List<string>();
        var landed = new List<string>();
        var cohortCalls = 0;
        var sharedPath = "src/Mcg.AgentOrchestrator.App/Orchestration/Shared.cs";
        var mainRevision = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var projector = new GateReadyCandidateProjector(
            goalId => new GateReadyCandidateRevisionPair(
                goalId.Value.PadRight(40, 'b')[..40],
                mainRevision),
            _ => new GateReadyLandingScopeObservation(true, [sharedPath]),
            (_, _, _) => new GateReadyMergeTreeObservation(true));
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            runAcceptanceWithSlot: (goal, _) =>
            {
                acceptanceCalls.Add(goal.Id.Value);
                return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria;
            },
            land: goal =>
            {
                landed.Add(goal.Id.Value);
                return new LandingResult(
                    goal.Id.Value,
                    goal.Id.Value[..8],
                    new LandingDecision.Promote(),
                    "integration",
                    true,
                    "ok");
            },
            classifyRisk: _ => ChangeRiskTier.DocsOnly,
            getLandingFileScopes: _ => [sharedPath],
            isVerificationGateSatisfied: _ => true,
            gateReadyCandidateProjector: projector,
            runAcceptanceCohort: (_, _, _) =>
            {
                cohortCalls++;
                throw new InvalidOperationException("Incompatible candidates must not enter a production cohort.");
            });

        var summary = new ConductorBatchLoop().Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 1);

        Assert.Equal(2, summary.Advanced);
        Assert.Equal(0, cohortCalls);
        Assert.Equal(2, acceptanceCalls.Count);
        Assert.Equal(2, landed.Count);
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
        using var isolatedRoot = IsolatedDotnetRootScope();
        var kernel = new AgentOrchestratorKernel();
        var goals = Enumerable.Range(0, ConductorBatchLoop.DefaultParallelAcceptanceCapacity + 1)
            .Select(index => CreateVerifiedSimpleGoal(
                kernel,
                $"Update src/Mcg.AgentOrchestrator.App/Orchestration/Slot{index}.cs"))
            .ToArray();
        for (var attempt = 1;
             attempt < 128 &&
             goals
                 .Take(ConductorBatchLoop.DefaultParallelAcceptanceCapacity)
                 .Select(BuildPermitIndex)
                 .Distinct()
                 .Count() < DotnetBuildEnvironmentManager.BuildConcurrencySlotCount;
             attempt++)
        {
            kernel = new AgentOrchestratorKernel();
            goals = Enumerable.Range(0, ConductorBatchLoop.DefaultParallelAcceptanceCapacity + 1)
                .Select(index => CreateVerifiedSimpleGoal(
                    kernel,
                    $"Update src/Mcg.AgentOrchestrator.App/Orchestration/Slot{index}.cs"))
                .ToArray();
        }
        if (goals
            .Take(ConductorBatchLoop.DefaultParallelAcceptanceCapacity)
            .Select(BuildPermitIndex)
            .Distinct()
            .Count() < DotnetBuildEnvironmentManager.BuildConcurrencySlotCount)
        {
            throw new InvalidOperationException(
                $"Could not generate {ConductorBatchLoop.DefaultParallelAcceptanceCapacity} acceptance goals " +
                $"covering {DotnetBuildEnvironmentManager.BuildConcurrencySlotCount} build permits after 128 attempts.");
        }

        using var release = new ManualResetEventSlim(false);
        using var firstWaveStarted = new CountdownEvent(ConductorBatchLoop.DefaultParallelAcceptanceCapacity);
        var running = 0;
        var maxRunning = 0;
        var slots = new ConcurrentQueue<int?>();
        object gate = new();
        var landed = new HashSet<string>(StringComparer.Ordinal);
        var attemptRoot = CreateTempDirectory("mcg-conductor-acceptance-attempts");
        Action waitForAttempts = () => { };

        try
        {
            var coordinator = ThreadedAcceptanceAttemptCoordinator(attemptRoot, out waitForAttempts);
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
                parallelAcceptanceAttemptCoordinator: coordinator);

            BatchTickSummary? firstTick = null;
            var firstSummary = new ConductorBatchLoop().Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 1,
                onTick: t => firstTick = t);

            Assert.Equal(0, firstSummary.Advanced);
            Assert.Equal(ConductorBatchLoop.DefaultParallelAcceptanceCapacity + 1, firstSummary.Held);
            Assert.True(firstWaveStarted.Wait(TimeSpan.FromSeconds(5)));

            var inFlightAttempts = goals
                .Take(ConductorBatchLoop.DefaultParallelAcceptanceCapacity)
                .Select(goal => ReadLatestAttempt(attemptRoot, goal))
                .ToArray();
            var heldPermits = inFlightAttempts
                .Select(ReadAcquirePermit)
                .ToArray();
            var configuredPermits = Enumerable
                .Range(0, DotnetBuildEnvironmentManager.BuildConcurrencySlotCount)
                .Select(index => $"build-{index}")
                .ToHashSet(StringComparer.Ordinal);
            Assert.All(inFlightAttempts, attempt => Assert.True(coordinator.HasLiveAttempt(attempt.GoalId)));
            Assert.Equal(ConductorBatchLoop.DefaultParallelAcceptanceCapacity, heldPermits.Distinct(StringComparer.Ordinal).Count());
            Assert.All(heldPermits, permit => Assert.Contains(permit, configuredPermits));

            BatchTickSummary? saturatedTick = null;
            var saturatedSummary = new ConductorBatchLoop().Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 1,
                onTick: current => saturatedTick = current);
            var deferredPrefix = goals[^1].Id.Value[..8];

            Assert.Equal(0, saturatedSummary.Advanced);
            Assert.Equal(ConductorBatchLoop.DefaultParallelAcceptanceCapacity + 1, saturatedSummary.Held);
            Assert.Contains(saturatedTick!.ProgressLines!, line =>
                line.Contains("ADMISSION", StringComparison.Ordinal) &&
                line.Contains("result=deferred", StringComparison.Ordinal) &&
                line.Contains("reason=parallel-acceptance-slot-cap", StringComparison.Ordinal));
            Assert.DoesNotContain(saturatedTick.ProgressLines!, line =>
                line.Contains("result=deferred", StringComparison.Ordinal) &&
                line.Contains("reason=parallel-acceptance-fairness", StringComparison.Ordinal));
            Assert.DoesNotContain(saturatedTick.ProgressLines!, line =>
                line.Contains($"ACCEPTANCE goal={deferredPrefix}", StringComparison.Ordinal) &&
                line.Contains("result=started", StringComparison.Ordinal));

            release.Set();
            waitForAttempts();

            var totalAdvanced = 0;
            var deferredGoalEventuallyStarted = false;
            for (var tick = 0; tick < 10 && totalAdvanced < goals.Length; tick++)
            {
                BatchTickSummary? retryTick = null;
                var retrySummary = new ConductorBatchLoop().Run(
                    kernel,
                    driver,
                    ConductorAutonomyPolicy.Conservative,
                    NoStopPath(),
                    maxIterations: 1,
                    onTick: current => retryTick = current);
                totalAdvanced += retrySummary.Advanced;
                deferredGoalEventuallyStarted |= retryTick?.ProgressLines?.Any(line =>
                    line.Contains($"ACCEPTANCE goal={deferredPrefix}", StringComparison.Ordinal) &&
                    line.Contains("result=started", StringComparison.Ordinal)) == true;
                waitForAttempts();
            }

            Assert.Equal(ConductorBatchLoop.DefaultParallelAcceptanceCapacity + 1, totalAdvanced);
            Assert.True(deferredGoalEventuallyStarted);
            Assert.Equal(ConductorBatchLoop.DefaultParallelAcceptanceCapacity, maxRunning);
            Assert.DoesNotContain(slots, slot => !slot.HasValue);
            Assert.Equal(
                ConductorBatchLoop.DefaultParallelAcceptanceCapacity,
                slots
                    .Where(slot => slot.HasValue)
                    .Select(slot => slot!.Value)
                    .Distinct()
                    .Count());
            Assert.Contains(firstTick!.ProgressLines!, line =>
                line.Contains("ADMISSION", StringComparison.Ordinal) &&
                line.Contains("result=deferred", StringComparison.Ordinal) &&
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

    [Xunit.Fact(DisplayName = "BatchLoop_invalid_parallel_acceptance_slot_settings_escalate_without_retry")]
    public void BatchLoopInvalidParallelAcceptanceSlotSettingsEscalateWithoutRetry()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateVerifiedSimpleGoal(
            kernel,
            "Update src/Mcg.AgentOrchestrator.App/Orchestration/InvalidSlotSettings.cs");
        var slotReads = 0;
        var escalationReasons = new List<string>();
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            runAcceptanceWithSlot: (_, _) => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
            writeEscalation: (_, _, reason) => escalationReasons.Add(reason),
            getAcceptanceSlotCount: _ =>
            {
                slotReads++;
                throw new InvalidDataException("slotCount must be between 1 and 4");
            });

        var summary = new ConductorBatchLoop().Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 2,
            watchInterval: TimeSpan.FromMilliseconds(1),
            sleepFunc: _ => false);

        Assert.Equal(1, slotReads);
        Assert.Equal(0, summary.Held);
        Assert.Equal(1, summary.Escalated);
        Assert.Contains(
            escalationReasons,
            reason => reason.Contains("invalid parallel acceptance slot settings", StringComparison.Ordinal));
        Assert.DoesNotContain(escalationReasons, reason => reason.Contains("retry", StringComparison.OrdinalIgnoreCase));
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
                        Enumerable.Range(0, ConductorBatchLoop.DefaultParallelAcceptanceCapacity)
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
                        @"C:\mcg-dotnet-isolated\goals\deadbeef\artifacts\bin\Mcg.AgentOrchestrator.Core.dll",
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

    [Xunit.Fact(DisplayName = "ParallelAcceptance_invalidated_attempt_blocks_replacement_until_exit_or_stale_heartbeat")]
    public void ParallelAcceptanceInvalidatedAttemptBlocksReplacementUntilExitOrStaleHeartbeat()
    {
        var (_, goal) = SimpleGoal("Retry acceptance without overlapping the old gate");
        var attemptRoot = CreateTempDirectory("mcg-conductor-invalidated-acceptance-attempts");
        var now = DateTimeOffset.Parse("2026-07-30T05:00:00Z");
        var launchAttempts = 0;
        var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
            attemptRoot,
            utcNow: () => now,
            isProcessAlive: _ => true,
            launchOwnedProcess: _ =>
            {
                launchAttempts++;
                return new ConductorParallelAcceptanceOwnedProcessLaunchResult(7200 + launchAttempts);
            },
            recentHeartbeatGrace: TimeSpan.FromSeconds(30));
        var candidate = ConductorParallelAcceptanceCandidate.Create(
            goal,
            0,
            ["src/Retry.cs"],
            "branch-a",
            "main-a");

        try
        {
            var first = coordinator.Evaluate(
                candidate,
                ConductorAutonomyPolicy.Conservative,
                PassingRun);
            Assert.True(coordinator.InvalidateCurrent(goal.Id.Value, "retry invalidated first attempt"));

            var firstHeld = coordinator.Evaluate(
                candidate,
                ConductorAutonomyPolicy.Conservative,
                PassingRun);

            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Running, firstHeld.Kind);
            Assert.Equal(first.Attempt.AttemptId, firstHeld.Attempt.AttemptId);
            Assert.Equal(1, launchAttempts);

            File.WriteAllText(first.Attempt.ExitCodePath, "1");
            now = now.AddSeconds(1);
            var second = coordinator.Evaluate(
                candidate,
                ConductorAutonomyPolicy.Conservative,
                PassingRun);
            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Started, second.Kind);
            Assert.Equal(2, launchAttempts);

            Assert.True(coordinator.InvalidateCurrent(goal.Id.Value, "retry invalidated second attempt"));
            var secondHeld = coordinator.Evaluate(
                candidate,
                ConductorAutonomyPolicy.Conservative,
                PassingRun);
            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Running, secondHeld.Kind);
            Assert.Equal(second.Attempt.AttemptId, secondHeld.Attempt.AttemptId);

            now = now.AddMinutes(1);
            var third = coordinator.Evaluate(
                candidate,
                ConductorAutonomyPolicy.Conservative,
                PassingRun);
            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Started, third.Kind);
            Assert.Equal(3, launchAttempts);
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
        var now = new DateTimeOffset(2026, 7, 23, 4, 0, 0, TimeSpan.Zero);
        var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
            attemptRoot,
            utcNow: () => now,
            isProcessAlive: _ => false,
            recentHeartbeatGrace: TimeSpan.FromMinutes(5),
            launchOwnedProcess: _ => new ConductorParallelAcceptanceOwnedProcessLaunchResult(7010));
        var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, ["src/Dead.cs"], "branch", "main");

        try
        {
            coordinator.Evaluate(candidate, ConductorAutonomyPolicy.Conservative, PassingRun);
            var recent = coordinator.Evaluate(candidate, ConductorAutonomyPolicy.Conservative, PassingRun);
            now = now.AddMinutes(6);
            var terminal = coordinator.Evaluate(candidate, ConductorAutonomyPolicy.Conservative, PassingRun);

            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Running, recent.Kind);
            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.TerminalWithoutRun, terminal.Kind);
            Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.ProcessDied, terminal.Attempt.Outcome);
            Assert.Equal(1, terminal.Attempt.TransientFailureCount);
        }
        finally
        {
            TryDeleteDirectory(attemptRoot);
        }
    }

    [Xunit.Fact(DisplayName = "BatchLoop_process_died_acceptance_relaunches_without_exposing_verified")]
    public void BatchLoopProcessDiedAcceptanceRelaunchesWithoutExposingVerified()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateVerifiedSimpleGoal(kernel, "Update src/Mcg.AgentOrchestrator.App/Orchestration/DeadRelaunch.cs");
        var attemptRoot = CreateTempDirectory("mcg-conductor-acceptance-attempts");
        var now = new DateTimeOffset(2026, 7, 23, 4, 0, 0, TimeSpan.Zero);
        var launches = 0;
        var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
            attemptRoot,
            utcNow: () => now,
            isProcessAlive: _ => false,
            recentHeartbeatGrace: TimeSpan.FromMinutes(5),
            launchOwnedProcess: _ => new ConductorParallelAcceptanceOwnedProcessLaunchResult(8100 + ++launches));
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            runAcceptanceWithSlot: (_, _) => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
            getLandingFileScopes: _ => ["src/Mcg.AgentOrchestrator.App/Orchestration/DeadRelaunch.cs"],
            parallelAcceptanceAttemptCoordinator: coordinator);

        try
        {
            new ConductorBatchLoop().Run(kernel, driver, ConductorAutonomyPolicy.Conservative, NoStopPath(), maxIterations: 1);
            Assert.Equal(GoalStatus.Verifying, goal.Status);
            Assert.Equal(1, launches);

            now = now.AddMinutes(6);
            var staleSummary = new ConductorBatchLoop().Run(kernel, driver, ConductorAutonomyPolicy.Conservative, NoStopPath(), maxIterations: 1);
            Assert.Equal(GoalStatus.Verifying, goal.Status);
            Assert.Equal(1, staleSummary.Held);
            Assert.Equal(1, launches);

            var relaunchSummary = new ConductorBatchLoop().Run(kernel, driver, ConductorAutonomyPolicy.Conservative, NoStopPath(), maxIterations: 1);
            Assert.Equal(GoalStatus.Verifying, goal.Status);
            Assert.Equal(1, relaunchSummary.Held);
            Assert.Equal(2, launches);
        }
        finally
        {
            TryDeleteDirectory(attemptRoot);
        }
    }

    [Xunit.Fact(DisplayName = "ParallelAcceptance_running_attempt_writes_periodic_heartbeat")]
    public void ParallelAcceptanceRunningAttemptWritesPeriodicHeartbeat()
    {
        var (_, goal) = SimpleGoal("Update src/Mcg.AgentOrchestrator.App/Orchestration/Heartbeat.cs");
        var attemptRoot = CreateTempDirectory("mcg-conductor-acceptance-attempts");
        using var secondRunningHeartbeat = new ManualResetEventSlim(false);
        var runningHeartbeats = 0;
        var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
            attemptRoot,
            runInline: true,
            heartbeatInterval: TimeSpan.FromMilliseconds(1),
            heartbeatWritten: (_, state) =>
            {
                if (state == "running" && Interlocked.Increment(ref runningHeartbeats) >= 2)
                {
                    secondRunningHeartbeat.Set();
                }
            });
        var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, ["src/Heartbeat.cs"], "branch", "main");

        try
        {
            var completed = coordinator.Evaluate(
                candidate,
                ConductorAutonomyPolicy.Conservative,
                (attemptCandidate, attemptPolicy, _, _) =>
                {
                    Assert.True(secondRunningHeartbeat.Wait(TimeSpan.FromSeconds(5)));
                    return PassingRun(attemptCandidate, attemptPolicy);
                });

            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Completed, completed.Kind);
            Assert.True(runningHeartbeats >= 2);
            Assert.True(File.Exists(completed.Attempt.HeartbeatPath));
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

    [Xunit.Fact(DisplayName = "ParallelAcceptance_owned_process_start_info_redirects_stdio")]
    public void ParallelAcceptanceOwnedProcessStartInfoRedirectsStdio()
    {
        var root = CreateTempDirectory("mcg-conductor-acceptance-start-info");
        try
        {
            var metadataPath = Path.Combine(root, "attempt.json");
            var attempt = new ConductorParallelAcceptanceAttempt(
                "attempt-1",
                GoalId.New().Value,
                "attempt1",
                0,
                "branch",
                "main",
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                Environment.ProcessId,
                ConductorParallelAcceptanceAttemptOutcome.Running,
                Path.Combine(root, "attempt.out.log"),
                Path.Combine(root, "attempt.err.log"),
                Path.Combine(root, "attempt.exit.txt"),
                Path.Combine(root, "attempt.heartbeat.json"),
                Path.Combine(root, "attempt.result.json"),
                metadataPath,
                ExecutionDirectory: root);

            var startInfo = ConductorParallelAcceptanceAttemptCoordinator.BuildOwnedProcessStartInfo(
                attempt,
                "dotnet",
                ["Mcg.AgentOrchestrator.App.dll"]);

            Assert.False(startInfo.UseShellExecute);
            Assert.True(startInfo.CreateNoWindow);
            Assert.True(startInfo.RedirectStandardInput);
            Assert.True(startInfo.RedirectStandardOutput);
            Assert.True(startInfo.RedirectStandardError);
            Assert.Equal(root, startInfo.WorkingDirectory);
            Assert.Equal(root, startInfo.Environment[OrchestratorWorkspace.RepoRootEnvironmentVariable]);
            Assert.Equal(
                ["Mcg.AgentOrchestrator.App.dll", ConductorParallelAcceptanceAttemptCoordinator.OwnedProcessSubcommandName, metadataPath],
                startInfo.ArgumentList.ToArray());
        }
        finally
        {
            TryDeleteDirectory(root);
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
                        var prefix = GoalAcceptanceVerifier.AcceptanceAttemptResultsPrefixForTests;
                        Assert.False(string.IsNullOrWhiteSpace(prefix));
                        var trxPath = $"{prefix}.dotnet-test.trx";
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
                Assert.False(File.Exists(AcceptanceAttemptArtifactCustody.MarkerPath(
                    DotnetBuildEnvironmentManager.GoalArtifactsPath(goal.Id))));
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
            DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(goal.Id);
        }
    }

    [Xunit.Fact(DisplayName = "BatchLoop_transient_launch_failure_retries_before_escalating_at_cap")]
    public void BatchLoopTransientLaunchFailureRetriesBeforeEscalatingAtCap()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateVerifiedSimpleGoal(kernel, "Update src/Mcg.AgentOrchestrator.App/Orchestration/TransientLaunch.cs");
        var attemptRoot = CreateTempDirectory("mcg-conductor-acceptance-attempts");
        var escalations = new List<string>();
        var launches = 0;
        var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
            attemptRoot,
            launchOwnedProcess: _ =>
            {
                launches++;
                throw new IOException("The process cannot access the file 'attempt.out.log' because it is being used by another process");
            });
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            runAcceptanceWithSlot: (_, _) => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
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
            Assert.Equal(GoalStatus.Verifying, goal.Status);
            Assert.Null(goal.LatestAcceptanceFailure);
            Assert.Equal(ConductorBatchLoop.ParallelAcceptanceTransientFailureCap, launches);

            var restartSummary = new ConductorBatchLoop().Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 1);

            Assert.Equal(1, restartSummary.Escalated);
            Assert.Equal(ConductorBatchLoop.ParallelAcceptanceTransientFailureCap, launches);
            Assert.Equal(GoalStatus.Verifying, goal.Status);
            Assert.Null(goal.LatestAcceptanceFailure);
        }
        finally
        {
            TryDeleteDirectory(attemptRoot);
        }
    }

    [Xunit.Fact(DisplayName = "BatchLoop_persistent_acceptance_infrastructure_deferral_retries_then_escalates_without_worker_round")]
    public void BatchLoopPersistentAcceptanceInfrastructureDeferralRetriesThenEscalatesWithoutWorkerRound()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateVerifiedSimpleGoal(kernel, "Update src/Mcg.AgentOrchestrator.App/Orchestration/BaselineDeferral.cs");
        var task = goal.Tasks.Single();
        var attemptRoot = CreateTempDirectory("mcg-conductor-acceptance-attempts");
        var escalations = new List<string>();
        var acceptanceAttempts = 0;
        var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(attemptRoot, runInline: true);
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            runAcceptanceWithSlot: (_, _) =>
            {
                acceptanceAttempts++;
                throw new AcceptanceInfrastructureDeferredException(
                    "trusted-main-build-failed",
                    1,
                    "baseline assembly unavailable");
            },
            writeEscalation: (_, _, reason) => escalations.Add(reason),
            getLandingFileScopes: _ => ["src/Mcg.AgentOrchestrator.App/Orchestration/BaselineDeferral.cs"],
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
            Assert.Equal(ConductorBatchLoop.ParallelAcceptanceTransientFailureCap, acceptanceAttempts);
            Assert.Equal(ConductorBatchLoop.ParallelAcceptanceTransientFailureCap, latest.TransientFailureCount);
            Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.InfrastructureDeferred, latest.Outcome);
            Assert.Single(escalations);
            Assert.Contains("infrastructure-deferred", escalations.Single(), StringComparison.OrdinalIgnoreCase);
            Assert.Equal(GoalStatus.Verifying, goal.Status);
            Assert.Equal(WorkTaskStatus.Completed, task.Status);
            Assert.Null(goal.LatestAcceptanceFailure);

            var restartSummary = new ConductorBatchLoop().Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 1);

            Assert.Equal(1, restartSummary.Escalated);
            Assert.Equal(ConductorBatchLoop.ParallelAcceptanceTransientFailureCap, acceptanceAttempts);
            Assert.Equal(WorkTaskStatus.Completed, task.Status);
            Assert.Null(goal.LatestAcceptanceFailure);
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
        Assert.Equal(GoalStatus.Active, goal.Status);
        Assert.Equal(WorkTaskStatus.Assigned, task.Status);
        Assert.Null(goal.LatestAcceptanceFailure);
        Assert.Equal(GoalLifecycleState.WorkspaceReady, GoalLifecycle.ResolveState(goal, new GoalLifecycleFacts(WorkspaceExists: true)));
        Assert.Contains("WorkerSandboxPreparer_second_round_reuses_prep_receipt", retryMessage!, StringComparison.Ordinal);
        Assert.Contains("acceptance failed checks", retryMessage!, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "BatchLoop_failed_parallel_acceptance_artifact_reconciles_goal_to_AcceptanceFailed")]
    public void BatchLoopFailedParallelAcceptanceArtifactReconcilesGoalToAcceptanceFailed()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateVerifiedSimpleGoal(kernel, "Update src/Mcg.AgentOrchestrator.App/Orchestration/FailedGate.cs");
        var attemptRoot = CreateTempDirectory("mcg-conductor-acceptance-attempts");
        var policy = ConductorAutonomyPolicy.Conservative with { MaxCriterionRetries = 0 };
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            runAcceptanceWithSlot: (_, _) => new AcceptanceVerificationSummary(
                false,
                [],
                "Acceptance failed.",
                ["FailedGateTests.Fails"]),
            getLandingFileScopes: _ => ["src/Mcg.AgentOrchestrator.App/Orchestration/FailedGate.cs"],
            parallelAcceptanceAttemptCoordinator: new ConductorParallelAcceptanceAttemptCoordinator(
                attemptRoot,
                runInline: true));

        try
        {
            var summary = new ConductorBatchLoop().Run(
                kernel,
                driver,
                policy,
                NoStopPath(),
                maxIterations: 1);

            Assert.Equal(1, summary.Escalated);
            Assert.Equal(GoalStatus.AcceptanceFailed, goal.Status);
            Assert.NotNull(goal.LatestAcceptanceFailure);
            Assert.Contains("FailedGateTests.Fails", goal.LatestAcceptanceFailure!.FailedChecks);
        }
        finally
        {
            TryDeleteDirectory(attemptRoot);
        }
    }

    [Xunit.Fact(DisplayName = "BatchLoop_environmental_interference_restores_Verified_for_regate")]
    public void BatchLoopEnvironmentalInterferenceRestoresVerifiedForRegate()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateVerifiedSimpleGoal(kernel, "Update src/Mcg.AgentOrchestrator.App/Orchestration/Regate.cs");
        var task = goal.Tasks.Single();
        var attemptRoot = CreateTempDirectory("mcg-conductor-acceptance-attempts");
        var interference = new AcceptanceCheckResult(
            "structural test coverage: core tests",
            false,
            1,
            "classification: gate-environment-interference",
            FailureClassification: AcceptanceFailureClassifications.GateEnvironmentInterference);
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            runAcceptanceWithSlot: (_, _) => new AcceptanceVerificationSummary(false, [interference]),
            getLandingFileScopes: _ => ["src/Mcg.AgentOrchestrator.App/Orchestration/Regate.cs"],
            parallelAcceptanceAttemptCoordinator: new ConductorParallelAcceptanceAttemptCoordinator(
                attemptRoot,
                runInline: true));

        try
        {
            var summary = new ConductorBatchLoop().Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 1);

            Assert.Equal(1, summary.Held);
            Assert.Equal(GoalStatus.Verified, goal.Status);
            Assert.Equal(WorkTaskStatus.Completed, task.Status);
            Assert.Equal(0, task.CriterionRetryCount);
            Assert.Null(goal.LatestAcceptanceFailure);
        }
        finally
        {
            TryDeleteDirectory(attemptRoot);
        }
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
                @"C:\mcg-dotnet-isolated\goals\deadbeef\artifacts\bin\Mcg.AgentOrchestrator.Core.dll",
                [new BuildLockHolder(null, "unknown-probe-timeout", null, false)],
                "handle64-timeout",
                "acceptance-output",
                "classify-build-lock")),
            ConductorParallelAcceptanceAttemptOutcome.BlockedBuildLock);
        AssertBackgroundOutcome(
            "InfrastructureDeferredTyped.cs",
            (_, _) => throw new AcceptanceInfrastructureDeferredException(
                "trusted-main-build-failed",
                1,
                "baseline assembly unavailable"),
            ConductorParallelAcceptanceAttemptOutcome.InfrastructureDeferred);
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

    [Xunit.Fact]
    public void ParallelAcceptance_waiter_skips_persisted_running_attempt()
    {
        using var _ = IsolatedDotnetRootScope();
        var kernel = new AgentOrchestratorKernel();
        var running = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
            kernel,
            DefaultAgents(),
            "Update src/Mcg.AgentOrchestrator.App/Orchestration/Running.cs");
        var waiting = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
            kernel,
            DefaultAgents(),
            "Update src/Mcg.AgentOrchestrator.App/Orchestration/Waiting.cs");
        var now = DateTimeOffset.UtcNow;
        PassVerificationAt(kernel, running, running.Tasks.Single(), now);
        PassVerificationAt(kernel, waiting, waiting.Tasks.Single(), now.AddMinutes(1));
        var attemptRoot = CreateTempDirectory("mcg-conductor-acceptance-attempts");

        try
        {
            var processAlive = true;
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                attemptRoot,
                utcNow: () => now,
                isProcessAlive: _ => processAlive,
                launchOwnedProcess: _ => new ConductorParallelAcceptanceOwnedProcessLaunchResult(8701),
                recentHeartbeatGrace: TimeSpan.FromMinutes(1));
            var candidate = ConductorParallelAcceptanceCandidate.Create(
                running,
                0,
                ["src/Mcg.AgentOrchestrator.App/Orchestration/Running.cs"],
                "branch",
                "main");

            var decision = coordinator.Evaluate(
                candidate,
                ConductorAutonomyPolicy.Conservative,
                PassingRun);
            var liveGoalIds = coordinator.GetLiveAttemptGoalIds([running.Id.Value, waiting.Id.Value]);
            var oldestWaiter = ConductorBatchLoop.SelectOldestParallelAcceptanceWaiter(
                [running, waiting],
                liveGoalIds);

            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Started, decision.Kind);
            Assert.True(coordinator.HasLiveAttempt(running.Id.Value));
            Assert.Equal(waiting.Id, oldestWaiter!.Id);
            Assert.False(ConductorBatchLoop.ShouldDeferForParallelAcceptanceFairness(oldestWaiter.Id.Value));

            now = now.AddMinutes(2);
            Assert.False(coordinator.HasLiveAttempt(running.Id.Value));
            now = now.AddMinutes(-2);
            processAlive = false;
            Assert.False(coordinator.HasLiveAttempt(running.Id.Value));
        }
        finally
        {
            TryDeleteDirectory(attemptRoot);
        }
    }

    [Xunit.Fact(DisplayName = "BatchLoop_parallel_acceptance_bounded_overtake_defers_newer_after_cap")]
    public void BatchLoopParallelAcceptanceBoundedOvertakeDefersNewerAfterCap()
    {
        Assert.Equal(1, ConductorBatchLoop.ParallelAcceptanceBoundedOvertakeLimit);
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
                runAcceptanceWithSlot: (_, _) => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
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
        var clock = new RecordingTimeProvider();

        try
        {
            var driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                runAcceptanceWithSlot: (_, _) =>
                {
                    clock.Advance(TimeSpan.FromMilliseconds(1234));
                    return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria;
                },
                getLandingFileScopes: _ => ["src/Mcg.AgentOrchestrator.App/Orchestration/LeaseEvents.cs"],
                parallelAcceptanceAttemptCoordinator: new ConductorParallelAcceptanceAttemptCoordinator(
                    attemptRoot,
                    runInline: true,
                    timeProvider: clock));

            new ConductorBatchLoop(conductEventLogWriter: writer).Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 1);

            var records = File.ReadAllLines(logPath)
                .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(line, new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
                .ToArray();

            var acquire = Assert.Single(records, record =>
                record.EventKind == "acceptance-lease" &&
                record.GoalId == goal.Id.Value[..8] &&
                record.Detail.Contains("ACCEPTANCE_LEASE_ACQUIRE", StringComparison.Ordinal));
            var permitRelease = Assert.Single(records, record =>
                record.EventKind == "acceptance-lease" &&
                record.GoalId == goal.Id.Value[..8] &&
                record.Detail.Contains("ACCEPTANCE_LEASE_PERMIT_RELEASE", StringComparison.Ordinal));
            var terminalRelease = Assert.Single(records, record =>
                record.EventKind == "acceptance-lease" &&
                record.GoalId == goal.Id.Value[..8] &&
                string.Equals(
                    record.Detail.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(),
                    "ACCEPTANCE_LEASE_RELEASE",
                    StringComparison.Ordinal));

            static string ReadToken(ConductEventRecord record, string token) =>
                Assert.Single(
                    record.Detail.Split(' ', StringSplitOptions.RemoveEmptyEntries),
                    part => part.StartsWith($"{token}=", StringComparison.Ordinal))[(token.Length + 1)..];

            Assert.Equal(ReadToken(acquire, "goal"), ReadToken(permitRelease, "goal"));
            Assert.Equal(ReadToken(acquire, "attempt"), ReadToken(permitRelease, "attempt"));
            Assert.Equal(ReadToken(acquire, "permit"), ReadToken(permitRelease, "permit"));
            Assert.Equal(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), ReadToken(permitRelease, "holderPid"));
            Assert.Equal("execution-lock", ReadToken(permitRelease, "releaseKind"));
            Assert.Equal("1234", ReadToken(permitRelease, "heldMs"));
            Assert.DoesNotContain("releaseKind=", terminalRelease.Detail, StringComparison.Ordinal);
            Assert.DoesNotContain("heldMs=", terminalRelease.Detail, StringComparison.Ordinal);
            Assert.True(Array.IndexOf(records, permitRelease) < Array.IndexOf(records, terminalRelease));
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

    [Xunit.Fact(DisplayName = "ParallelAttempt_RegistrationFailure_FailsAndReleasesLease")]
    public void ParallelAttemptRegistrationFailureFailsAndReleasesLease()
    {
        using var _ = IsolatedDotnetRootScope();
        var (_, goal) = SimpleGoal("Update docs/RegistrationFailure.md");
        var attemptRoot = CreateTempDirectory("mcg-conductor-registration-failure");
        var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(attemptRoot, runInline: true);
        var candidate = ConductorParallelAcceptanceCandidate.Create(
            goal,
            0,
            ["docs/RegistrationFailure.md"],
            "branch",
            "main");
        DotnetBuildEnvironment? leasedEnvironment = null;
        const string registrationFailure =
            "worker-process-registration-failed; pid=36824; stage=owned-process-group-attachment; " +
            "cleanup=process-tree-termination-requested; exception=OwnedProcessAttachmentException; " +
            "native_error_code=5; native_message=Access is denied.; candidate_in_job=true";

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
                        throw new InvalidOperationException(registrationFailure);
                    });

                Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Completed, decision.Kind);
                Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Failed, decision.Attempt.Outcome);
                Assert.NotNull(decision.Run?.Exception);
                Assert.Contains(registrationFailure, decision.Run!.Exception!.Message, StringComparison.Ordinal);
                Assert.Contains(registrationFailure, decision.Attempt.Detail, StringComparison.Ordinal);
                Assert.Contains(registrationFailure, File.ReadAllText(decision.Attempt.StderrPath), StringComparison.Ordinal);
            });

            Assert.NotNull(leasedEnvironment);
            Assert.Contains("ACCEPTANCE_LEASE_RELEASE", output, StringComparison.Ordinal);
            Assert.Equal(1, CountOccurrences(output, "ACCEPTANCE_LEASE_RELEASE"));
            using var reacquired = DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(
                leasedEnvironment!,
                TimeSpan.Zero);
        }
        finally
        {
            TryDeleteDirectory(attemptRoot);
        }
    }

    [Xunit.Fact(DisplayName = "ParallelAttempt_ExternalOldProductionSeam_FastExitRed")]
    public void ParallelAttemptExternalOldProductionSeamFastExitRed()
    {
        RunExternalOwnedStartScenario(useLegacyStartThenAttach: true);
    }

    [Xunit.Fact(DisplayName = "ParallelAttempt_OwnedStart_ReachesNormalResult")]
    public void ParallelAttemptOwnedStartReachesNormalResult()
    {
        RunExternalOwnedStartScenario(useLegacyStartThenAttach: false);
    }

    private static void RunExternalOwnedStartScenario(bool useLegacyStartThenAttach)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var isolatedDotnetRoot = IsolatedDotnetRootScope();
        var root = CreateSeededGitRepository();
        var attemptRoot = CreateTempDirectory("mcg-conductor-owned-start");
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateVerifiedSimpleGoal(kernel, "Update docs/OwnedStart.md");
        var externalProcesses = new ConcurrentBag<Process>();

        try
        {
            Directory.CreateDirectory(Path.Combine(root, "config"));
            File.WriteAllText(
                Path.Combine(root, "config", "acceptance-manifest.json"),
                AcceptanceManifestTestDefaults.WithEngine(
                    """
                    {
                      "version": 1,
                      "checks": [
                        {
                          "name": "background owned start marker",
                          "type": "command",
                          "command": "powershell",
                          "arguments": [
                            "-NoProfile",
                            "-Command",
                            "Set-Content -LiteralPath 'background-owned-start.marker' -Value started"
                          ],
                          "timeoutMinutes": 1
                        }
                      ],
                      "forbiddenChangedPathGlobs": []
                    }
                    """));
            RunGit(root, "add", "config/acceptance-manifest.json");
            RunGit(root, "commit", "-m", "Seed acceptance manifest");

            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var stateRepository = OpenStateRepository(workspace.SqliteStatePath);
            var worktree = GoalWorktrees.Ensure(root, goal.Id);
            Directory.CreateDirectory(Path.Combine(worktree, "docs"));
            File.WriteAllText(Path.Combine(worktree, "docs", "OwnedStart.md"), "owned start lifecycle");
            RunGit(worktree, "add", "-A");
            RunGit(worktree, "commit", "-m", "Add owned start lifecycle fixture");
            stateRepository.SaveAsync(kernel).GetAwaiter().GetResult();

            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                attemptRoot,
                executionDirectory: root,
                launchOwnedProcess: launch => LaunchExternalAcceptanceProcess(
                    launch,
                    useLegacyStartThenAttach,
                    isolatedDotnetRoot.Value,
                    externalProcesses));
            var candidate = ConductorParallelAcceptanceCandidate.Create(
                goal,
                0,
                ["docs/OwnedStart.md"]);
            ConductorParallelAcceptanceRunResult UnexpectedInlineRun(
                ConductorParallelAcceptanceCandidate candidateIgnored,
                ConductorAutonomyPolicy policyIgnored,
                DotnetBuildEnvironmentLease? leaseIgnored,
                CancellationToken cancellationTokenIgnored) =>
                throw new InvalidOperationException("External acceptance unexpectedly ran inline.");

            var decision = coordinator.Evaluate(
                candidate,
                ConductorAutonomyPolicy.Conservative,
                UnexpectedInlineRun);
            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Started, decision.Kind);
            var initialAttempt = decision.Attempt;
            var externalProcess = Assert.Single(externalProcesses);
            Assert.True(
                externalProcess.WaitForExit(60000),
                $"External acceptance PID {externalProcess.Id} did not exit. stdout={TryReadAllTextShared(initialAttempt.StdoutPath)} stderr={TryReadAllTextShared(initialAttempt.StderrPath)}");
            externalProcess.WaitForExit();
            var completedAttempt = JsonSerializer.Deserialize<ConductorParallelAcceptanceAttempt>(
                File.ReadAllText(initialAttempt.MetadataPath),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.NotNull(completedAttempt);

            var expectedOutcome = useLegacyStartThenAttach
                ? ConductorParallelAcceptanceAttemptOutcome.Failed
                : ConductorParallelAcceptanceAttemptOutcome.Passed;
            Assert.True(
                completedAttempt!.Outcome == expectedOutcome,
                $"Expected {expectedOutcome}, actual {completedAttempt.Outcome}: {completedAttempt.Detail}");
            var markerPath = Path.Combine(worktree, "background-owned-start.marker");
            if (useLegacyStartThenAttach)
            {
                Assert.False(File.Exists(markerPath));
                Assert.Contains("stage=owned-process-group-attachment", completedAttempt.Detail, StringComparison.Ordinal);
                Assert.Contains("native_error_code=", completedAttempt.Detail, StringComparison.Ordinal);
                Assert.Contains("native_message=", completedAttempt.Detail, StringComparison.Ordinal);
                Assert.Contains("candidate_has_exited=true", completedAttempt.Detail, StringComparison.Ordinal);
                Assert.Contains("candidate_exit_code=0", completedAttempt.Detail, StringComparison.Ordinal);
                Assert.Contains("owner_in_job=", completedAttempt.Detail, StringComparison.Ordinal);
                Assert.Contains("candidate_in_job=", completedAttempt.Detail, StringComparison.Ordinal);
                Assert.Contains("candidate_in_owned_job=", completedAttempt.Detail, StringComparison.Ordinal);
                Assert.Contains("owner_job_limit_flags=", completedAttempt.Detail, StringComparison.Ordinal);
                Assert.Contains("owner_job_ui_restrictions=", completedAttempt.Detail, StringComparison.Ordinal);
                Assert.Contains("owned_job_limit_flags=", completedAttempt.Detail, StringComparison.Ordinal);
                Assert.Contains("owned_job_ui_restrictions=", completedAttempt.Detail, StringComparison.Ordinal);

                var candidateProcessId = ParseRegistrationFailureProcessId(completedAttempt.Detail);
                Assert.False(
                    IsProcessRunning(candidateProcessId),
                    $"Failed legacy candidate remained alive after exact-PID cleanup: {DescribeProcess(candidateProcessId)}");
            }
            else
            {
                Assert.Equal("started", File.ReadAllText(markerPath).Trim());
                Assert.True(File.Exists(completedAttempt.ResultPath));
            }

            var stdout = TryReadAllTextShared(completedAttempt.StdoutPath);
            Assert.Equal(1, CountOccurrences(stdout, "ACCEPTANCE_LEASE_RELEASE"));
            Assert.Empty(new SpawnRegistry(workspace.SqliteStatePath).ListActive());
        }
        finally
        {
            foreach (var process in externalProcesses)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }

                    process.WaitForExit(5000);
                }
                catch
                {
                }
                finally
                {
                    process.Dispose();
                }
            }
            TryDeleteDirectory(attemptRoot);
            TryDeleteDirectory(root);
        }
    }

    private static ConductorParallelAcceptanceOwnedProcessLaunchResult LaunchExternalAcceptanceProcess(
        ConductorParallelAcceptanceOwnedProcessLaunch launch,
        bool useLegacyStartThenAttach,
        string isolatedDotnetRoot,
        ConcurrentBag<Process> externalProcesses)
    {
        var appAssembly = Path.Combine(AppContext.BaseDirectory, "Mcg.AgentOrchestrator.App.dll");
        Assert.True(File.Exists(appAssembly), $"App assembly was not available at {appAssembly}");
        var startInfo = ConductorParallelAcceptanceAttemptCoordinator.BuildOwnedProcessStartInfo(
            launch.Attempt,
            "dotnet",
            [appAssembly]);
        // The production child is intentionally hermetic and scrubs MCG_* variables. This real-process
        // fixture must reapply its test-only lease root so unrelated stable-slot users cannot preempt the
        // owned-start seam that the test is meant to exercise.
        startInfo.Environment[DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable] = isolatedDotnetRoot;
        Assert.Equal(
            isolatedDotnetRoot,
            startInfo.Environment[DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable]);
        if (useLegacyStartThenAttach)
        {
            startInfo.Environment[GoalAcceptanceVerifier.LegacyOwnedStartNegativeControlVariable] =
                "wait-for-fast-exit";
        }

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start external acceptance negative-control child.");
        process.OutputDataReceived += static (_, _) => { };
        process.ErrorDataReceived += static (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.StandardInput.Close();
        externalProcesses.Add(process);
        return new ConductorParallelAcceptanceOwnedProcessLaunchResult(process.Id);
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

    [Xunit.Fact(DisplayName = "ParallelAcceptance_colliding_goals_acquire_distinct_available_permits")]
    public void ParallelAcceptanceCollidingGoalsAcquireDistinctAvailablePermits()
    {
        using var isolatedRoot = IsolatedDotnetRootScope();
        var (_, goalA) = SimpleGoal("Update src/Mcg.AgentOrchestrator.App/Orchestration/HoldA.cs");
        var (_, goalB) = SimpleGoal("Update src/Mcg.AgentOrchestrator.App/Orchestration/HoldB.cs");
        for (var attempt = 1;
             attempt < 128 && BuildPermitIndex(goalA) != BuildPermitIndex(goalB);
             attempt++)
        {
            (_, goalB) = SimpleGoal("Update src/Mcg.AgentOrchestrator.App/Orchestration/HoldB.cs");
        }
        if (BuildPermitIndex(goalA) != BuildPermitIndex(goalB))
        {
            throw new InvalidOperationException(
                $"Could not generate goals on the same build permit after 128 attempts; " +
                $"goalA={goalA.Id.Value} permit={BuildPermitIndex(goalA)}, " +
                $"goalB={goalB.Id.Value} permit={BuildPermitIndex(goalB)}.");
        }

        var attemptRoot = CreateTempDirectory("mcg-conductor-acceptance-attempts");
        using var releaseFirst = new ManualResetEventSlim(false);
        using var firstHasLease = new ManualResetEventSlim(false);
        using var secondReachedPreSlot = new ManualResetEventSlim(false);
        using var secondHasLease = new ManualResetEventSlim(false);
        var preSlotRuns = 0;
        var coordinator = ThreadedAcceptanceAttemptCoordinator(
            attemptRoot,
            out var waitForAttempts,
            (_, _) =>
            {
                if (Interlocked.Increment(ref preSlotRuns) == 2)
                {
                    secondReachedPreSlot.Set();
                }

                return null;
            });
        var candidateA = ConductorParallelAcceptanceCandidate.Create(goalA, 0, ["src/HoldA.cs"], "branch-a", "main");
        var candidateB = ConductorParallelAcceptanceCandidate.Create(goalB, 0, ["src/HoldB.cs"], "branch-b", "main");
        var secondRan = false;
        DotnetBuildEnvironment? firstLeaseEnvironment = null;
        DotnetBuildEnvironment? secondLeaseEnvironment = null;

        try
        {
            var first = coordinator.Evaluate(
                candidateA,
                ConductorAutonomyPolicy.Conservative,
                (attemptCandidate, _, stableSlotLease, _) =>
                {
                    Assert.NotNull(stableSlotLease);
                    firstLeaseEnvironment = stableSlotLease.Environment;
                    firstHasLease.Set();
                    releaseFirst.Wait();
                    return PassingRun(attemptCandidate, ConductorAutonomyPolicy.Conservative);
                });
            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Started, first.Kind);
            Assert.True(firstHasLease.Wait(TimeSpan.FromSeconds(5)));
            var secondEnvironment = DotnetBuildEnvironmentManager.CreateAttempt(goalB.Id, "contention-probe");
            Assert.Equal(firstLeaseEnvironment!.ExecutionLockPath, secondEnvironment.ExecutionLockPath);
            Assert.IsType<DotnetBuildLeaseAcquisition.SlotsBusy>(
                DotnetBuildEnvironmentManager.TryAcquireLeaseExecutionLock(secondEnvironment, TimeSpan.Zero));

            var second = coordinator.Evaluate(
                candidateB,
                ConductorAutonomyPolicy.Conservative,
                (attemptCandidate, attemptPolicy, stableSlotLease, _) =>
                {
                    Assert.NotNull(stableSlotLease);
                    secondLeaseEnvironment = stableSlotLease.Environment;
                    secondRan = true;
                    secondHasLease.Set();
                    return PassingRun(attemptCandidate, attemptPolicy);
                    });
            Assert.True(secondReachedPreSlot.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(secondHasLease.Wait(TimeSpan.FromSeconds(5)));
            var completed = WaitForAttemptOutcome(coordinator, candidateB, ConductorParallelAcceptanceAttemptOutcome.Passed);

            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Started, second.Kind);
            Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Passed, completed.Attempt.Outcome);
            Assert.True(secondRan);
            Assert.Equal(2, Volatile.Read(ref preSlotRuns));
            Assert.NotEqual(firstLeaseEnvironment.ExecutionLockPath, secondLeaseEnvironment!.ExecutionLockPath);
            Assert.NotEqual(firstLeaseEnvironment.BuildPermitIndex, secondLeaseEnvironment.BuildPermitIndex);
            releaseFirst.Set();
        }
        finally
        {
            releaseFirst.Set();
            waitForAttempts();
            TryDeleteDirectory(attemptRoot);
        }
    }

    [Xunit.Fact(DisplayName = "ParallelAcceptance_all_permits_busy_reports_typed_wait_reason")]
    public void ParallelAcceptanceAllPermitsBusyReportsTypedWaitReason()
    {
        using var _ = IsolatedDotnetRootScope();
        var (_, goal) = SimpleGoal("Update src/Mcg.AgentOrchestrator.App/Orchestration/AllBusy.cs");
        var attemptRoot = CreateTempDirectory("mcg-conductor-acceptance-attempts");
        var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, ["src/AllBusy.cs"], "branch", "main");
        var slot0 = DotnetBuildEnvironmentManager.CreateStableSlotAttempt(0);
        var slot1 = DotnetBuildEnvironmentManager.CreateStableSlotAttempt(1);
        using var lease0 = Assert.IsType<DotnetBuildLeaseAcquisition.Acquired>(
            DotnetBuildEnvironmentManager.TryAcquireLeaseExecutionLock(slot0, TimeSpan.Zero)).Lease;
        using var lease1 = Assert.IsType<DotnetBuildLeaseAcquisition.Acquired>(
            DotnetBuildEnvironmentManager.TryAcquireLeaseExecutionLock(slot1, TimeSpan.Zero)).Lease;
        var clock = new RecordingTimeProvider();
        var waitStartedAt = clock.GetUtcNow();
        var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
            attemptRoot,
            runInline: true,
            timeProvider: clock,
            buildPermitBusyTimeout: DotnetBuildEnvironmentManager.DefaultSlotBusyPollTimeout,
            buildPermitSleep: clock.Advance);
        var ran = false;
        DotnetBuildEnvironmentManager.ProcessCommandLineSnapshotForTests = () =>
            new ProcessCommandLineSnapshot(new Dictionary<int, string>());

        try
        {
            var output = AsyncLocalConsoleRouter.Capture(() =>
            {
                var decision = coordinator.Evaluate(
                    candidate,
                    ConductorAutonomyPolicy.Conservative,
                    (attemptCandidate, attemptPolicy, _, _) =>
                    {
                        ran = true;
                        return PassingRun(attemptCandidate, attemptPolicy);
                    });

                Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.BlockedBuildSlot, decision.Attempt.Outcome);
                Assert.Equal(AcceptanceBuildPermitWaitReason.AllPermitsBusy, decision.Attempt.BuildPermitWaitReason);
            });

            Assert.False(ran);
            Assert.Contains("waitReason=all-permits-busy", output, StringComparison.Ordinal);
            Assert.Contains("permit=build-0|build-1", output, StringComparison.Ordinal);
            Assert.Contains(
                $"holderPid={Environment.ProcessId}|{Environment.ProcessId}",
                output,
                StringComparison.Ordinal);
            Assert.DoesNotContain("permit=acceptance-", output, StringComparison.Ordinal);
            Assert.DoesNotContain("waitReason=designated-permit-busy-while-free", output, StringComparison.Ordinal);
            Assert.Equal(
                DotnetBuildEnvironmentManager.DefaultSlotBusyPollTimeout,
                clock.GetUtcNow() - waitStartedAt);
        }
        finally
        {
            DotnetBuildEnvironmentManager.ProcessCommandLineSnapshotForTests = null;
            TryDeleteDirectory(attemptRoot);
        }
    }

    [Xunit.Fact(DisplayName = "ParallelAcceptance_attempt_yields_to_live_cli_holder_and_reclaims_dead_holder")]
    public void ParallelAcceptanceAttemptYieldsToLiveCliHolderAndReclaimsDeadHolder()
    {
        using var isolatedRoot = IsolatedDotnetRootScope();
        var (_, liveGoal) = SimpleGoal("Update src/Mcg.AgentOrchestrator.App/Orchestration/LiveCli.cs");
        var (_, deadGoal) = SimpleGoal("Update src/Mcg.AgentOrchestrator.App/Orchestration/DeadCli.cs");
        for (var attempt = 1;
             attempt < 128 && BuildPermitIndex(liveGoal) == BuildPermitIndex(deadGoal);
             attempt++)
        {
            (_, deadGoal) = SimpleGoal("Update src/Mcg.AgentOrchestrator.App/Orchestration/DeadCli.cs");
        }
        Assert.NotEqual(BuildPermitIndex(liveGoal), BuildPermitIndex(deadGoal));
        var attemptRoot = CreateTempDirectory("mcg-conductor-acceptance-attempts");
        var liveCandidate = ConductorParallelAcceptanceCandidate.Create(liveGoal, 0, ["src/LiveCli.cs"], "branch-live", "main");
        var deadCandidate = ConductorParallelAcceptanceCandidate.Create(deadGoal, 0, ["src/DeadCli.cs"], "branch-dead", "main");
        var liveEnvironment = DotnetBuildEnvironmentManager.CreateAttempt(liveGoal.Id, "live-cli");
        var deadEnvironment = DotnetBuildEnvironmentManager.CreateAttempt(deadGoal.Id, "dead-cli");
        var liveRan = false;
        var deadRan = false;
        DotnetBuildEnvironment? reclaimedEnvironment = null;

        try
        {
            var livePermitIndex = liveEnvironment.BuildPermitIndex
                ?? throw new InvalidOperationException("Live goal build permit was not assigned.");
            var liveLeases = new List<FileStream>();
            try
            {
                liveLeases.Add(DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(liveEnvironment));
                foreach (var permitIndex in Enumerable.Range(0, DotnetBuildEnvironmentManager.BuildConcurrencySlotCount))
                {
                    if (permitIndex != livePermitIndex)
                    {
                        liveLeases.Add(DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(
                            DotnetBuildEnvironmentManager.CreateStableSlotAttempt(permitIndex),
                            TimeSpan.Zero));
                    }
                }

                var liveCoordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                    attemptRoot,
                    runInline: true,
                    buildPermitBusyTimeout: TimeSpan.Zero);
                var liveBlocked = liveCoordinator.Evaluate(
                    liveCandidate,
                    ConductorAutonomyPolicy.Conservative,
                    (attemptCandidate, attemptPolicy, _, _) =>
                    {
                        liveRan = true;
                        return PassingRun(attemptCandidate, attemptPolicy);
                    });
                Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.BlockedBuildSlot, liveBlocked.Attempt.Outcome);
                Assert.Equal(AcceptanceBuildPermitWaitReason.AllPermitsBusy, liveBlocked.Attempt.BuildPermitWaitReason);
                Assert.False(liveRan);
            }
            finally
            {
                foreach (var liveLease in liveLeases)
                {
                    liveLease.Dispose();
                }
            }

            File.WriteAllText(deadEnvironment.ExecutionLockPath, "999999");
            var deadCoordinator = new ConductorParallelAcceptanceAttemptCoordinator(attemptRoot, runInline: true);
            var reclaimed = deadCoordinator.Evaluate(
                deadCandidate,
                ConductorAutonomyPolicy.Conservative,
                (attemptCandidate, attemptPolicy, stableSlotLease, _) =>
                {
                    Assert.NotNull(stableSlotLease);
                    reclaimedEnvironment = stableSlotLease.Environment;
                    deadRan = true;
                    return PassingRun(attemptCandidate, attemptPolicy);
                });

            Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Passed, reclaimed.Attempt.Outcome);
            Assert.True(deadRan);
            Assert.NotEqual(liveEnvironment.BuildPermitIndex, reclaimedEnvironment!.BuildPermitIndex);
            Assert.Equal(deadEnvironment.BuildPermitIndex, reclaimedEnvironment.BuildPermitIndex);
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

    [Xunit.Fact(DisplayName = "ParallelAcceptance_parked_goal_cancels_at_next_target_boundary_and_releases_lease")]
    public void ParallelAcceptanceParkedGoalCancelsAtNextTargetBoundaryAndReleasesLease()
    {
        using var _ = IsolatedDotnetRootScope();
        var root = CreateSeededGitRepository();
        Directory.CreateDirectory(Path.Combine(root, "config"));
        File.WriteAllText(
            Path.Combine(root, "config", "acceptance-manifest.json"),
            AcceptanceManifestTestDefaults.WithEngine(
                """
                {
                  "version": 1,
                  "checks": [
                    { "name": "first target", "type": "command", "command": "first-target", "arguments": ["--ok"] },
                    { "name": "second target", "type": "command", "command": "second-target", "arguments": ["--should-not-run"] }
                  ],
                  "forbiddenChangedPathGlobs": []
                }
                """));
        RunGit(root, "add", "config/acceptance-manifest.json");
        RunGit(root, "commit", "-m", "Seed acceptance manifest");
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateVerifiedSimpleGoal(kernel, "Update src/Mcg.AgentOrchestrator.App/Orchestration/ParkBoundary.cs");
        var stateRepository = OpenStateRepository(workspace.SqliteStatePath);
        var attemptRoot = CreateTempDirectory("mcg-conductor-acceptance-attempts");
        var calls = new List<string[]>();
        var firstTargetCompleted = false;

        try
        {
            var worktree = GoalWorktrees.Ensure(root, goal.Id);
            Directory.CreateDirectory(Path.Combine(worktree, "config"));
            Directory.CreateDirectory(Path.Combine(worktree, "src", "Mcg.AgentOrchestrator.App", "Orchestration"));
            File.WriteAllText(
                Path.Combine(worktree, "src", "Mcg.AgentOrchestrator.App", "Orchestration", "ParkBoundary.cs"),
                "namespace Mcg.AgentOrchestrator.App.Orchestration; internal static class ParkBoundary { }");
            RunGit(worktree, "add", "-A");
            RunGit(worktree, "commit", "-m", "Goal work");
            stateRepository.SaveAsync(kernel).GetAwaiter().GetResult();

            var verifier = new GoalAcceptanceVerifier((args, _, _) =>
            {
                calls.Add(args);
                if (args.Length > 0 && args[0] == "first-target")
                {
                    firstTargetCompleted = true;
                    kernel.ParkGoal(goal.Id, "operator parked during acceptance attempt");
                    stateRepository.SaveAsync(kernel).GetAwaiter().GetResult();
                }

                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "ok"));
            });
            var driver = new ConductorDriver(
                kernel,
                workspace,
                verifier,
                DefaultAgents(),
                WorkerProfileCatalog.Default());
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(attemptRoot, runInline: true);
            var candidate = ConductorParallelAcceptanceCandidate.Create(
                goal,
                0,
                ["src/Mcg.AgentOrchestrator.App/Orchestration/ParkBoundary.cs"]);

            var output = AsyncLocalConsoleRouter.Capture(() =>
            {
                var decision = coordinator.Evaluate(
                    candidate,
                    ConductorAutonomyPolicy.Conservative,
                    driver.RunParallelLandingAcceptance);

                Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Cancelled, decision.Attempt.Outcome);
            });

            Assert.True(firstTargetCompleted);
            Assert.DoesNotContain(calls, call => call.Length > 0 && call[0] == "second-target");
            Assert.Contains("ACCEPTANCE_LEASE_RELEASE", output);
            Assert.Equal(1, CountOccurrences(output, "ACCEPTANCE_LEASE_RELEASE"));
            using var reacquired = DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(
                DotnetBuildEnvironmentManager.CreateStableSlotAttempt(0),
                TimeSpan.Zero);
        }
        finally
        {
            TryDeleteDirectory(attemptRoot);
            TryDeleteDirectory(root);
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
                runAcceptanceWithSlot: (_, _) => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
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
                runAcceptanceWithSlot: (_, _) => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
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

    private static EnvVarScope IsolatedDotnetRootScope() =>
        new EnvVarScope(
            DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable,
            Path.Combine(Path.GetTempPath(), $"{DotnetBuildEnvironmentManager.RootDirectoryName}-batch-loop-{Guid.NewGuid():N}"));

    private sealed class EnvVarScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _previousValue;
        private readonly string _value;

        public EnvVarScope(string name, string value)
        {
            _name = name;
            _value = value;
            _previousValue = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public string Value => _value;

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
        out Action waitForAttempts,
        ConductorParallelAcceptanceTryRunPreSlot? tryRunPreSlot = null)
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
            tryRunPreSlot: tryRunPreSlot,
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

    private static ConductorParallelAcceptanceAttempt ReadLatestAttempt(string attemptRoot, Goal goal) =>
        Directory.EnumerateFiles(Path.Combine(attemptRoot, goal.Id.Value), "*.attempt.json")
            .Select(ReadAttempt)
            .OrderByDescending(attempt => attempt.StartedAt)
            .First();

    private static string ReadAcquirePermit(ConductorParallelAcceptanceAttempt attempt)
    {
        var receipt = Assert.Single(
            attempt.LeaseReceipts ?? [],
            line => line.Contains("ACCEPTANCE_LEASE_ACQUIRE", StringComparison.Ordinal));
        var permitToken = Assert.Single(
            receipt.Split(' ', StringSplitOptions.RemoveEmptyEntries),
            token => token.StartsWith("permit=", StringComparison.Ordinal));
        return permitToken["permit=".Length..];
    }

    private static AcceptanceMakespanSample MeasureFixedGateMakespan(
        int parallelCapacity,
        int fixedGateDurationMs)
    {
        using var isolatedRoot = IsolatedDotnetRootScope();
        var kernel = new AgentOrchestratorKernel();
        var goals = Enumerable.Range(0, 2)
            .Select(index => CreateVerifiedSimpleGoal(
                kernel,
                $"Update src/Mcg.AgentOrchestrator.App/Orchestration/Makespan{index}.cs"))
            .ToArray();
        for (var attempt = 1;
             attempt < 128 && goals.Select(BuildPermitIndex).Distinct().Count() < goals.Length;
             attempt++)
        {
            kernel = new AgentOrchestratorKernel();
            goals = Enumerable.Range(0, 2)
                .Select(index => CreateVerifiedSimpleGoal(
                    kernel,
                    $"Update src/Mcg.AgentOrchestrator.App/Orchestration/Makespan{index}.cs"))
                .ToArray();
        }
        Assert.Equal(goals.Length, goals.Select(BuildPermitIndex).Distinct().Count());

        var logicalTimeMs = 0;
        var timings = new ConcurrentQueue<(int Started, int Completed)>();
        using var gateStarted = new SemaphoreSlim(0);
        var gates = new ConcurrentQueue<AcceptanceMeasurementGate>();
        var landed = new HashSet<string>(StringComparer.Ordinal);
        var attemptRoot = CreateTempDirectory("mcg-conductor-measured-makespan");
        Action waitForAttempts = () => { };

        try
        {
            var coordinator = ThreadedAcceptanceAttemptCoordinator(attemptRoot, out waitForAttempts);
            var driver = MakeDriver(
                getFacts: goal => landed.Contains(goal.Id.Value)
                    ? new GoalLifecycleFacts(WorkspaceExists: true, IsMerged: true, IsRecorded: true, IsCleanedUp: true)
                    : new GoalLifecycleFacts(WorkspaceExists: true),
                runAcceptanceWithSlot: (_, slot) =>
                {
                    var gate = new AcceptanceMeasurementGate(Volatile.Read(ref logicalTimeMs), slot);
                    gates.Enqueue(gate);
                    gateStarted.Release();
                    Assert.True(gate.Release.Wait(TimeSpan.FromSeconds(5)));
                    timings.Enqueue((gate.StartedAtMs, Volatile.Read(ref logicalTimeMs)));
                    return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria;
                },
                getLandingFileScopes: goal =>
                {
                    var index = Array.FindIndex(goals, candidate => candidate.Id == goal.Id);
                    return [$"src/Mcg.AgentOrchestrator.App/Orchestration/Makespan{index}.cs"];
                },
                land: goal =>
                {
                    landed.Add(goal.Id.Value);
                    return new LandingResult(
                        goal.Id.Value,
                        goal.Id.Value[..8],
                        new LandingDecision.Promote(),
                        "integration",
                        true,
                        "ok");
                },
                parallelAcceptanceAttemptCoordinator: coordinator,
                getAcceptanceSlotCount: _ => parallelCapacity);

            var releasedGateCount = 0;
            for (var tick = 0; tick < 6 && landed.Count < goals.Length; tick++)
            {
                new ConductorBatchLoop().Run(
                    kernel,
                    driver,
                    ConductorAutonomyPolicy.Conservative,
                    NoStopPath(),
                    maxIterations: 1);

                var expectedWaveCount = Math.Min(parallelCapacity, goals.Length - releasedGateCount);
                for (var index = 0; index < expectedWaveCount; index++)
                {
                    Assert.True(
                        gateStarted.Wait(TimeSpan.FromSeconds(5)),
                        $"Expected {expectedWaveCount} acceptance gates to start in tick {tick}.");
                }

                var wave = gates.ToArray()
                    .Skip(releasedGateCount)
                    .Take(expectedWaveCount)
                    .ToArray();
                Assert.Equal(expectedWaveCount, wave.Length);
                if (expectedWaveCount > 1)
                {
                    Assert.All(wave, gate => Assert.True(gate.PermitIndex.HasValue));
                    Assert.Equal(expectedWaveCount, wave.Select(gate => gate.PermitIndex).Distinct().Count());
                }

                if (wave.Length > 0)
                {
                    Interlocked.Add(ref logicalTimeMs, fixedGateDurationMs);
                    foreach (var gate in wave)
                    {
                        gate.Release.Set();
                    }

                    releasedGateCount += wave.Length;
                }

                waitForAttempts();
            }

            Assert.Equal(goals.Length, landed.Count);
            var completedTimings = timings.ToArray();
            Assert.Equal(goals.Length, completedTimings.Length);
            var firstStarted = completedTimings.Min(timing => timing.Started);
            var lastCompleted = completedTimings.Max(timing => timing.Completed);
            var gateDurationsMs = completedTimings
                .Select(timing => (double)(timing.Completed - timing.Started))
                .Order()
                .ToArray();
            var permits = goals
                .Select(goal => ReadAcquirePermit(ReadLatestAttempt(attemptRoot, goal)))
                .Order(StringComparer.Ordinal)
                .ToArray();

            if (parallelCapacity > 1)
            {
                Assert.Equal(goals.Length, permits.Distinct(StringComparer.Ordinal).Count());
            }

            return new AcceptanceMakespanSample(
                lastCompleted - firstStarted,
                gateDurationsMs,
                permits);
        }
        finally
        {
            foreach (var gate in gates)
            {
                gate.Release.Set();
            }

            waitForAttempts();
            foreach (var gate in gates)
            {
                gate.Dispose();
            }

            TryDeleteDirectory(attemptRoot);
        }
    }

    [Xunit.Fact]
    public void BatchLoopAmbiguousTerminalClaimsReconcileBeforeLiveClaimDeferral()
    {
        var kernel = new AgentOrchestratorKernel();
        var liveGoal = CreateVerifiedSimpleGoal(kernel, "Update src/Mcg.AgentOrchestrator.App/Orchestration/SharedTerminal.cs");
        var terminalGoal = CreateVerifiedSimpleGoal(kernel, "Also update src/Mcg.AgentOrchestrator.App/Orchestration/SharedTerminal.cs");
        var attemptRoot = CreateTempDirectory("mcg-conductor-ambiguous-terminal-attempts");
        try
        {
            var seedCoordinator = new ConductorParallelAcceptanceAttemptCoordinator(attemptRoot, runInline: true);
            var liveCandidate = ConductorParallelAcceptanceCandidate.Create(
                liveGoal,
                0,
                ["src/Mcg.AgentOrchestrator.App/Orchestration/SharedTerminal.cs"]);
            var terminalCandidate = ConductorParallelAcceptanceCandidate.Create(
                terminalGoal,
                1,
                ["src/Mcg.AgentOrchestrator.App/Orchestration/SharedTerminal.cs"]);
            var live = seedCoordinator.Evaluate(
                liveCandidate,
                ConductorAutonomyPolicy.Conservative,
                PassingRun).Attempt;
            var terminal = seedCoordinator.Evaluate(
                terminalCandidate,
                ConductorAutonomyPolicy.Conservative,
                PassingRun).Attempt;
            File.Delete(live.ResultPath);
            File.Delete(live.ExitCodePath);
            File.WriteAllText(
                live.MetadataPath,
                JsonSerializer.Serialize(
                    live with
                    {
                        Outcome = ConductorParallelAcceptanceAttemptOutcome.Running,
                        OwnerProcessId = Environment.ProcessId,
                        CompletedAt = null,
                        ReconciledAt = null,
                        Detail = null
                    },
                    new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));

            var reconcileCoordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                attemptRoot,
                isProcessAlive: _ => true,
                launchOwnedProcess: _ => throw new InvalidOperationException("existing attempts must be observed, not relaunched"));
            var driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                getLandingFileScopes: _ => liveCandidate.ScopePaths,
                runAcceptanceWithSlot: (_, _) => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
                parallelAcceptanceAttemptCoordinator: reconcileCoordinator);

            _ = new ConductorBatchLoop().Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 1);

            Assert.Equal(GoalStatus.Verifying, liveGoal.Status);
            Assert.NotNull(ReadAttempt(terminal.MetadataPath).ReconciledAt);
            Assert.Equal(GoalStatus.Verified, terminalGoal.Status);
        }
        finally
        {
            TryDeleteDirectory(attemptRoot);
        }
    }

    [Xunit.Fact]
    public void BatchLoopSameGoalTerminalAttemptsAllReconcile()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateVerifiedSimpleGoal(kernel, "Update src/Mcg.AgentOrchestrator.App/Orchestration/SameGoal.cs");
        var attemptRoot = CreateTempDirectory("mcg-conductor-same-goal-terminal-attempts");
        try
        {
            var seedCoordinator = new ConductorParallelAcceptanceAttemptCoordinator(attemptRoot, runInline: true);
            var candidate = ConductorParallelAcceptanceCandidate.Create(
                goal,
                0,
                ["src/Mcg.AgentOrchestrator.App/Orchestration/SameGoal.cs"],
                "branch",
                "main");
            var first = seedCoordinator.Evaluate(
                candidate,
                ConductorAutonomyPolicy.Conservative,
                PassingRun).Attempt;
            var secondId = first.AttemptId + "-duplicate";
            string DuplicatePath(string path) => path.Replace(first.AttemptId, secondId, StringComparison.Ordinal);
            var second = first with
            {
                AttemptId = secondId,
                StartedAt = first.StartedAt.AddSeconds(1),
                LastHeartbeatAt = first.LastHeartbeatAt.AddSeconds(1),
                StdoutPath = DuplicatePath(first.StdoutPath),
                StderrPath = DuplicatePath(first.StderrPath),
                ExitCodePath = DuplicatePath(first.ExitCodePath),
                HeartbeatPath = DuplicatePath(first.HeartbeatPath),
                ResultPath = DuplicatePath(first.ResultPath),
                MetadataPath = DuplicatePath(first.MetadataPath)
            };
            File.Copy(first.ResultPath, second.ResultPath);
            File.Copy(first.ExitCodePath, second.ExitCodePath);
            File.WriteAllText(
                second.MetadataPath,
                JsonSerializer.Serialize(
                    second,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));

            var landed = false;
            var reconcileCoordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                attemptRoot,
                launchOwnedProcess: _ => throw new InvalidOperationException("terminal receipt reconciliation must not launch"));
            var driver = MakeDriver(
                getFacts: _ => landed
                    ? new GoalLifecycleFacts(WorkspaceExists: true, IsMerged: true, IsRecorded: true, IsCleanedUp: true)
                    : new GoalLifecycleFacts(WorkspaceExists: true),
                land: landedGoal =>
                {
                    landed = true;
                    return new LandingResult(
                        landedGoal.Id.Value,
                        landedGoal.Id.Value[..8],
                        new LandingDecision.Promote(),
                        "integration",
                        true,
                        "ok");
                },
                getLandingFileScopes: _ => candidate.ScopePaths,
                runAcceptanceWithSlot: (_, _) => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
                parallelAcceptanceAttemptCoordinator: reconcileCoordinator);

            var summary = new ConductorBatchLoop().Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 1);

            Assert.Equal(1, summary.Advanced);
            Assert.True(landed);
            Assert.NotNull(ReadAttempt(first.MetadataPath).ReconciledAt);
            Assert.NotNull(ReadAttempt(second.MetadataPath).ReconciledAt);
        }
        finally
        {
            TryDeleteDirectory(attemptRoot);
        }
    }

    [Xunit.Fact]
    public void BatchLoopSameGoalLiveAttemptIsObservedAfterNewerTerminalSiblingReconciles()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateVerifiedSimpleGoal(kernel, "Update src/Mcg.AgentOrchestrator.App/Orchestration/SameGoalLive.cs");
        var attemptRoot = CreateTempDirectory("mcg-conductor-same-goal-live-attempts");
        try
        {
            var seedCoordinator = new ConductorParallelAcceptanceAttemptCoordinator(attemptRoot, runInline: true);
            var candidate = ConductorParallelAcceptanceCandidate.Create(
                goal,
                0,
                ["src/Mcg.AgentOrchestrator.App/Orchestration/SameGoalLive.cs"],
                "branch",
                "main");
            var terminal = seedCoordinator.Evaluate(
                candidate,
                ConductorAutonomyPolicy.Conservative,
                PassingRun).Attempt;
            var liveId = terminal.AttemptId + "-live-predecessor";
            string LivePath(string path) => path.Replace(terminal.AttemptId, liveId, StringComparison.Ordinal);
            var live = terminal with
            {
                AttemptId = liveId,
                StartedAt = terminal.StartedAt.AddSeconds(-1),
                LastHeartbeatAt = DateTimeOffset.UtcNow,
                OwnerProcessId = Environment.ProcessId,
                Outcome = ConductorParallelAcceptanceAttemptOutcome.Running,
                StdoutPath = LivePath(terminal.StdoutPath),
                StderrPath = LivePath(terminal.StderrPath),
                ExitCodePath = LivePath(terminal.ExitCodePath),
                HeartbeatPath = LivePath(terminal.HeartbeatPath),
                ResultPath = LivePath(terminal.ResultPath),
                MetadataPath = LivePath(terminal.MetadataPath),
                CompletedAt = null,
                ReconciledAt = null,
                Detail = null
            };
            File.WriteAllText(
                live.MetadataPath,
                JsonSerializer.Serialize(
                    live,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));

            var launches = 0;
            var landed = false;
            var reconcileCoordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                attemptRoot,
                isProcessAlive: _ => true,
                launchOwnedProcess: _ =>
                {
                    launches++;
                    return new ConductorParallelAcceptanceOwnedProcessLaunchResult(Environment.ProcessId);
                });
            var driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                land: landedGoal =>
                {
                    landed = true;
                    return new LandingResult(
                        landedGoal.Id.Value,
                        landedGoal.Id.Value[..8],
                        new LandingDecision.Promote(),
                        "integration",
                        true,
                        "ok");
                },
                getLandingFileScopes: _ => candidate.ScopePaths,
                runAcceptanceWithSlot: (_, _) => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
                parallelAcceptanceAttemptCoordinator: reconcileCoordinator);

            var summary = new ConductorBatchLoop().Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 1);

            Assert.Equal(0, summary.Advanced);
            Assert.Equal(1, summary.Held);
            Assert.False(landed);
            Assert.Equal(0, launches);
            Assert.Equal(GoalStatus.Verifying, goal.Status);
            Assert.NotNull(ReadAttempt(terminal.MetadataPath).ReconciledAt);
            Assert.Null(ReadAttempt(live.MetadataPath).ReconciledAt);
        }
        finally
        {
            TryDeleteDirectory(attemptRoot);
        }
    }

    [Xunit.Fact]
    public void HandoffCanonicalRegistrationBindsDiscoveredMetadataPath()
    {
        var (_, goal) = SimpleGoal("Update src/Mcg.AgentOrchestrator.App/Orchestration/CanonicalClaim.cs");
        var attemptRoot = CreateTempDirectory("mcg-conductor-canonical-claim");
        try
        {
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(attemptRoot, runInline: true);
            var candidate = ConductorParallelAcceptanceCandidate.Create(
                goal,
                0,
                ["src/Mcg.AgentOrchestrator.App/Orchestration/CanonicalClaim.cs"],
                "branch",
                "main");
            var seeded = coordinator.Evaluate(
                candidate,
                ConductorAutonomyPolicy.Conservative,
                PassingRun).Attempt;
            var decoyPath = Path.Combine(attemptRoot, "decoy", "claim.attempt.json");
            File.WriteAllText(
                seeded.MetadataPath,
                JsonSerializer.Serialize(
                    seeded with { MetadataPath = decoyPath },
                    new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));

            var discovered = Assert.Single(coordinator.GetUnreconciledAttempts([goal.Id.Value]));
            Assert.Equal(Path.GetFullPath(seeded.MetadataPath), discovered.MetadataPath);
            var decision = coordinator.ObserveExistingAttempt(discovered, candidate);
            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Completed, decision.Kind);
            coordinator.MarkReconciled(decision.Attempt);

            Assert.NotNull(ReadAttempt(seeded.MetadataPath).ReconciledAt);
            Assert.False(File.Exists(decoyPath));
        }
        finally
        {
            TryDeleteDirectory(attemptRoot);
        }
    }

    private static double Median(IReadOnlyList<AcceptanceMakespanSample> samples) =>
        samples.Count % 2 == 0
            ? (samples[(samples.Count / 2) - 1].MakespanMs + samples[samples.Count / 2].MakespanMs) / 2
            : samples[samples.Count / 2].MakespanMs;

    private static string FormatRange(IReadOnlyList<AcceptanceMakespanSample> samples) =>
        $"{samples.Min(sample => sample.MakespanMs):F1}-{samples.Max(sample => sample.MakespanMs):F1}";

    private static string FormatGateDurations(IEnumerable<AcceptanceMakespanSample> samples) =>
        string.Join('|', samples.Select(sample => string.Join(',', sample.GateDurationsMs.Select(duration => $"{duration:F1}"))));

    private static string FormatPermits(IEnumerable<AcceptanceMakespanSample> samples) =>
        string.Join('|', samples.Select(sample => string.Join(',', sample.Permits)));

    private sealed record AcceptanceMakespanSample(
        double MakespanMs,
        IReadOnlyList<double> GateDurationsMs,
        IReadOnlyList<string> Permits);

    private sealed class AcceptanceMeasurementGate(int startedAtMs, int? permitIndex) : IDisposable
    {
        public int StartedAtMs { get; } = startedAtMs;

        public int? PermitIndex { get; } = permitIndex;

        public ManualResetEventSlim Release { get; } = new(false);

        public void Dispose() => Release.Dispose();
    }

    private static int BuildPermitIndex(Goal goal) =>
        goal.Id.Value[..8]
            .ToLowerInvariant()
            .Sum(ch => (int)ch) %
        DotnetBuildEnvironmentManager.BuildConcurrencySlotCount;

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
            if (ConductorParallelAcceptanceAttemptCoordinator.IsBoundedInfrastructureOutcome(expected))
            {
                Assert.Equal(1, terminal.Attempt.TransientFailureCount);
            }
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
        // Failsafe bound, NOT an assertion about speed: the attempt runs on a dedicated thread doing real
        // lease-file I/O, so this must be generous enough that only a genuine hang trips it. The former
        // fixed 50 x 20ms (~1s) poll made machine speed the pass condition and failed under concurrent
        // acceptance lanes with "Expected: BlockedBuildSlot, Actual: Running".
        var waitBudget = Stopwatch.StartNew();
        while (waitBudget.Elapsed < TimeSpan.FromSeconds(30))
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

    private static int ParseRegistrationFailureProcessId(string detail)
    {
        const string token = "pid=";
        var start = detail.IndexOf(token, StringComparison.Ordinal);
        if (start < 0)
        {
            throw new InvalidOperationException("Registration failure detail did not include a candidate pid.");
        }

        start += token.Length;
        var end = detail.IndexOf(';', start);
        var value = end < 0 ? detail[start..] : detail[start..end];
        return int.TryParse(value, out var processId)
            ? processId
            : throw new InvalidOperationException($"Registration failure candidate pid was invalid: {value}");
    }
}
