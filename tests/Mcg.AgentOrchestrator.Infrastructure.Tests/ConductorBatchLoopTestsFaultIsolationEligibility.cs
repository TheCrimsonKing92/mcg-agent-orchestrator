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
public sealed class ConductorBatchLoopTestsFaultIsolationEligibility : ConductorBatchLoopTests
{
    public ConductorBatchLoopTestsFaultIsolationEligibility(ITestOutputHelper output)
        : base(output)
    {
    }

    // ── Fault isolation: a throwing goal is escalated, others still advance ─

    [Xunit.Fact(DisplayName = "BatchLoop_InconsistentVerifiedGate_IsolatedAndHealthyGoalDispatchedSameTick")]
    public void BatchLoopInconsistentVerifiedGateIsolatedAndHealthyGoalDispatchedSameTick()
    {
        var (kernel, inconsistentGoal, healthyGoal) = SeedInconsistentVerifiedGoalWithHealthyNeighbor();
        var dispatchedGoalIds = new List<GoalId>();
        var acceptanceRuns = 0;
        var escalations = new List<string>();
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: goal =>
            {
                dispatchedGoalIds.Add(goal.Id);
                return DispatchStartOutcome.Started();
            },
            runAcceptanceWithSlot: (_, _) =>
            {
                acceptanceRuns++;
                return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria;
            },
            writeEscalation: (_, _, reason) => escalations.Add(reason));

        var summary = new ConductorBatchLoop().Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 1);

        Assert.Equal(1, summary.Ticks);
        Assert.Equal(1, summary.Escalated);
        Assert.Contains(healthyGoal.Id, dispatchedGoalIds);
        Assert.Equal(0, acceptanceRuns);
        Assert.Equal(GoalStatus.Verified, kernel.GetGoal(inconsistentGoal.Id).Status);
        Assert.False(kernel.BuildVerificationGate(inconsistentGoal.Id).IsSatisfied);
        Assert.Contains(escalations, reason =>
            reason.Contains("authoritative task verification gate unsatisfied", StringComparison.Ordinal));
        Assert.Contains(kernel.GetGoal(inconsistentGoal.Id).Timeline, item =>
            item.Kind == ProgressKind.GoalPolicyDecision &&
            item.Message.Contains("authoritative task verification gate unsatisfied", StringComparison.Ordinal));
    }

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

    [Xunit.Fact(DisplayName = "BatchLoop_ExpectedAcceptanceCancellation_HeldWithoutFaultIsolation")]
    public void BatchLoopExpectedAcceptanceCancellationHeldWithoutFaultIsolation()
    {
        var (kernel, goal) = SimpleGoal("expected acceptance cancellation");
        PassVerification(kernel, goal, goal.Tasks.Single());
        var escalations = new List<string>();
        var reapedGoalIds = new List<GoalId>();
        var decision = AcceptanceAttemptCancellation.Decide(
            GoalStatus.Parked,
            goalRecordReadable: true,
            attemptInvalidationRecorded: false);
        var driver = MakeDriver(
            runAcceptance: _ => throw new AcceptanceAttemptCancelledException(decision),
            writeEscalation: (_, _, reason) => escalations.Add(reason),
            isVerificationGateSatisfied: _ => true);

        var summary = new ConductorBatchLoop(
            reapGoalRunningDispatches: (_, reaped) => reapedGoalIds.Add(reaped.Id),
            detachGoalRunningDispatches: (_, _) => { }).Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 1);

        Assert.Equal(0, summary.Escalated);
        Assert.Empty(escalations);
        Assert.Empty(reapedGoalIds);
        Assert.Equal(GoalStatus.Verified, kernel.GetGoal(goal.Id).Status);
        Assert.DoesNotContain(kernel.GetGoal(goal.Id).Timeline, item =>
            item.Kind == ProgressKind.GoalPolicyDecision &&
            item.Message.Contains("fault isolating goal", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "BatchLoop_GenuineAcceptanceFault_IsStillFaultIsolated")]
    public void BatchLoopGenuineAcceptanceFaultIsStillFaultIsolated()
    {
        var (kernel, goal) = SimpleGoal("genuine acceptance fault");
        PassVerification(kernel, goal, goal.Tasks.Single());
        var reapedGoalIds = new List<GoalId>();
        var driver = MakeDriver(
            runAcceptance: _ => throw new InvalidOperationException("genuine acceptance fault"),
            isVerificationGateSatisfied: _ => true);

        var summary = new ConductorBatchLoop(
            reapGoalRunningDispatches: (_, reaped) => reapedGoalIds.Add(reaped.Id)).Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 1);

        Assert.Equal(1, summary.Escalated);
        Assert.Equal([goal.Id], reapedGoalIds);
        Assert.Contains(kernel.GetGoal(goal.Id).Timeline, item =>
            item.Kind == ProgressKind.GoalPolicyDecision &&
            item.Message.Contains("fault isolating goal", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "BatchLoop_FaultIsolation_RetryAdvanceThrowEscalatesOneGoalAndContinuesBatch")]
    public void BatchLoop_FaultIsolation_RetryAdvanceThrowEscalatesOneGoalAndContinuesBatch()
    {
        var kernel = new AgentOrchestratorKernel();
        var faultyGoal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "retry fault goal");
        var healthyGoal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "healthy retry neighbor");
        PassVerification(kernel, faultyGoal, faultyGoal.Tasks.Single());
        PassVerification(kernel, healthyGoal, healthyGoal.Tasks.Single());

        var attempts = new Dictionary<GoalId, int>();
        var reapedGoalIds = new List<GoalId>();
        const string failureMessage =
            "Advance failed at stage=owned-process-group-attachment native_error_code=5";
        var healthyLanded = false;
        var healthyRecorded = false;
        var healthyCleanedUp = false;
        var driver = MakeDriver(
            getFacts: goal => goal.Id == healthyGoal.Id
                ? new GoalLifecycleFacts(
                    WorkspaceExists: true,
                    IsMerged: healthyLanded,
                    IsRecorded: healthyRecorded,
                    IsCleanedUp: healthyCleanedUp)
                : new GoalLifecycleFacts(WorkspaceExists: true),
            runAcceptance: goal =>
            {
                attempts.TryGetValue(goal.Id, out var attempt);
                attempts[goal.Id] = ++attempt;
                if (goal.Id == faultyGoal.Id)
                {
                    if (attempt == 1)
                        return false;

                    throw new UnauthorizedAccessException(failureMessage);
                }

                return true;
            },
            land: goal =>
            {
                if (goal.Id == healthyGoal.Id)
                    healthyLanded = true;

                return new LandingResult(
                    goal.Id.Value,
                    goal.Id.Value[..8],
                    new LandingDecision.Promote(),
                    "integration",
                    true,
                    "Landed");
            },
            record: goal =>
            {
                if (goal.Id == healthyGoal.Id)
                    healthyRecorded = true;
            },
            cleanup: goal =>
            {
                if (goal.Id == healthyGoal.Id)
                {
                    healthyCleanedUp = true;
                    kernel.CompleteGoal(goal.Id, "Healthy neighbor completed normally.");
                }

                return new GoalWorktreeRemoveResult("Workspace cleaned up.", null, [], null);
            });

        var ticks = new List<BatchTickSummary>();
        var summary = new ConductorBatchLoop(
            reapGoalRunningDispatches: (_, goal) => reapedGoalIds.Add(goal.Id)).Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 3,
                maxVerifyRetries: 2,
                onTick: ticks.Add);

        Assert.Equal(2, attempts[faultyGoal.Id]);
        Assert.Equal(1, attempts[healthyGoal.Id]);
        Assert.Equal(1, summary.Escalated);
        Assert.Equal(0, summary.Retried);
        Assert.Equal(3, summary.Advanced);
        Assert.True(healthyLanded);
        Assert.True(healthyRecorded);
        Assert.True(healthyCleanedUp);
        Assert.Equal(GoalStatus.Completed, kernel.GetGoal(healthyGoal.Id).Status);
        Assert.Equal([faultyGoal.Id], reapedGoalIds);
        Assert.Contains(ticks.SelectMany(tick => tick.ProgressLines ?? []), line =>
            line.StartsWith(
                $"GOAL goal={faultyGoal.Id.Value[..8]} result=escalated reason=",
                StringComparison.Ordinal) &&
            line.Contains("native_error_code=5", StringComparison.Ordinal));
        Assert.Contains(kernel.GetGoal(faultyGoal.Id).Timeline, item =>
            item.Kind == ProgressKind.GoalPolicyDecision &&
            item.Message.Contains("native_error_code=5", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "BatchLoop_FaultIsolation_TypedCriticalRetryAdvanceThrowStillTerminatesLoop")]
    public void BatchLoop_FaultIsolation_CriticalRetryAdvanceThrowStillTerminatesLoop()
    {
        var (kernel, goal) = SimpleGoal("critical retry fault goal");
        PassVerification(kernel, goal, goal.Tasks.Single());

        var attempts = 0;
        var reapedGoalIds = new List<GoalId>();
        var expected = new DispatchRecordWriteException(
            DispatchRecordWriteFailureCause.Unrecoverable,
            DispatchRecordCheckpointPhase.ProcessMayHaveStarted,
            "dispatch-start",
            goal.Id,
            goal.Tasks.Single().Id,
            8,
            new SqliteException("retry advance persistence failed", 8));
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            runAcceptance: _ =>
            {
                attempts++;
                if (attempts == 1)
                    return false;

                throw expected;
            });

        var actual = Assert.Throws<DispatchRecordWriteException>(() =>
            new ConductorBatchLoop(
                reapGoalRunningDispatches: (_, reapedGoal) => reapedGoalIds.Add(reapedGoal.Id)).Run(
                    kernel,
                    driver,
                    ConductorAutonomyPolicy.Conservative,
                    NoStopPath(),
                    maxIterations: 1,
                    maxVerifyRetries: 2));

        Assert.Same(expected, actual);
        Assert.Equal(2, attempts);
        Assert.Empty(reapedGoalIds);
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

            var retirementDetail = "Operator retired missing goal branch.";
            GoalOperationJournal.RecordTerminalDisposition(
                root,
                retiredGoal,
                new GoalTerminalDisposition(GoalTerminalDispositionKind.Retired, retirementDetail));
            kernel.CompleteGoal(retiredGoal.Id, retirementDetail);
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

    [Xunit.Fact(DisplayName = "BatchLoop_goal_mark_landed_merge_disposition_is_excluded_by_typed_source")]
    public void BatchLoopGoalMarkLandedMergeDispositionIsExcludedByTypedSource()
    {
        var root = CreateSeededGitRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var landedGoal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "Goal marked landed");
            var activeGoal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "Active conductor goal");
            PassVerification(kernel, landedGoal, landedGoal.Tasks.Single());
            GoalOperationJournal.RecordTerminalDisposition(
                root,
                landedGoal,
                new GoalTerminalDisposition(
                    GoalTerminalDispositionKind.Landed,
                    $"Goal {landedGoal.Id.Value[..8]} was marked landed via goal-mark-landed.",
                    GoalTerminalDispositionSource.MergeEvidence));
            kernel.CompleteGoal(landedGoal.Id, "Goal marked landed.");

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
            Assert.DoesNotContain(landedGoal.Id, advancedGoalIds);
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
}
