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
public sealed class ConductorBatchLoopTestsOperatorIntents : ConductorBatchLoopTests
{
    public ConductorBatchLoopTestsOperatorIntents(ITestOutputHelper output)
        : base(output)
    {
    }

    [Xunit.Fact(DisplayName = "BatchLoop_applies_retry_intent_and_publishes_outcome_after_tick_persist")]
    public async Task BatchLoopAppliesRetryIntentAndPublishesOutcomeAfterTickPersist()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mcg-loop-operator-intent-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var (kernel, goal) = SimpleGoal("Apply operator retry");
            var task = goal.Tasks.Single();
            kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Failed, "Needs operator repair.");
            var store = new SqliteOperatorIntentStore(
                Path.Combine(root, "operator-intents.db"),
                Path.Combine(root, "logs"));
            var intent = new OperatorIntentRecord(
                Guid.NewGuid().ToString("N"),
                "loop-retry-key",
                OperatorIntentVerbs.Retry,
                goal.Id.Value,
                task.Id.Value,
                JsonSerializer.Serialize(
                    new RetryOperatorIntentPayload("Operator repaired through inbox.", null, RetryCause: RetryCause.ContractClarification),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                [],
                "operator",
                "cli",
                "local-process",
                DateTimeOffset.UtcNow);
            await store.EnqueueAsync(intent);
            var persistedGoalIds = new List<GoalId>();

            var summary = new ConductorBatchLoop(
                operatorIntents: new OperatorIntentCoordinator(store)).Run(
                kernel,
                MakeDriver(),
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 1,
                persistGoalTick: (_, goalIds) => persistedGoalIds.AddRange(goalIds));

            var outcome = await store.GetAsync(intent.Id);
            Assert.Equal(1, summary.Ticks);
            Assert.Equal(WorkTaskStatus.Assigned, kernel.GetTask(goal.Id, task.Id).Status);
            Assert.Contains(goal.Id, persistedGoalIds);
            Assert.Contains(goal.Timeline, item =>
                item.Kind == ProgressKind.GoalPolicyDecision &&
                item.Message.Contains($"operator-intent:{intent.Id}", StringComparison.Ordinal));
            Assert.NotNull(outcome);
            Assert.Equal(OperatorIntentStatus.Applied, outcome!.Status);
            Assert.Contains("Applied retry", outcome.Outcome, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
            }
        }
    }

    [Xunit.Fact(DisplayName = "BatchLoop_retry_intent_invalidates_running_acceptance_before_redispatch")]
    public async Task BatchLoopRetryIntentInvalidatesRunningAcceptanceBeforeRedispatch()
    {
        var root = CreateTempDirectory("mcg-loop-verifying-retry");
        var launches = new ConcurrentDictionary<string, ConductorParallelAcceptanceOwnedProcessLaunch>();
        try
        {
            var acceptanceProcessAlive = true;
            var (kernel, goal) = SimpleGoal("Retry a goal while acceptance is running");
            var task = goal.Tasks.Single();
            kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Done.");
            kernel.RecordTaskVerification(
                goal.Id,
                task.Id,
                new TaskVerificationRecord("focused test", root, 0, "passed", "", DateTimeOffset.UtcNow));
            kernel.BeginGoalAcceptanceVerification(goal.Id, "Acceptance attempt launched.");

            var attemptCoordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                Path.Combine(root, "attempts"),
                isProcessAlive: _ => acceptanceProcessAlive,
                launchOwnedProcess: launch =>
                {
                    launches[launch.Attempt.AttemptId] = launch;
                    return new ConductorParallelAcceptanceOwnedProcessLaunchResult(7100 + launches.Count);
                });
            var candidate = ConductorParallelAcceptanceCandidate.Create(
                goal,
                0,
                ["src/Retry.cs"],
                "branch-sha",
                "main-sha");
            var started = attemptCoordinator.Evaluate(
                candidate,
                ConductorAutonomyPolicy.Conservative,
                PassingRun);
            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Started, started.Kind);

            var store = new SqliteOperatorIntentStore(
                Path.Combine(root, "operator-intents.db"),
                Path.Combine(root, "logs"));
            var intent = new OperatorIntentRecord(
                "verifying-retry-intent",
                "verifying-retry-key",
                OperatorIntentVerbs.Retry,
                goal.Id.Value,
                task.Id.Value,
                JsonSerializer.Serialize(
                    new RetryOperatorIntentPayload("Acceptance evidence requires a correction.", null, RetryCause: RetryCause.CriterionEvidenceOwnerMismatch),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                [],
                "operator",
                "cli",
                "local-process",
                DateTimeOffset.UtcNow);
            await store.EnqueueAsync(intent);
            var dispatchStarts = 0;
            var ticks = new List<BatchTickSummary>();

            var summary = new ConductorBatchLoop(
                operatorIntents: new OperatorIntentCoordinator(store)).Run(
                kernel,
                MakeDriver(
                    getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                    dispatchAndStart: _ =>
                    {
                        dispatchStarts++;
                        return DispatchStartOutcome.Started();
                    },
                    parallelAcceptanceAttemptCoordinator: attemptCoordinator),
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 3,
                watchInterval: TimeSpan.FromMilliseconds(1),
                sleepFunc: _ => false,
                onTick: tick =>
                {
                    ticks.Add(tick);
                    if (ticks.Count == 2)
                    {
                        acceptanceProcessAlive = false;
                    }
                },
                persistGoalTick: (_, _) => { });

            var stale = ReadAttempt(started.Attempt.MetadataPath);
            Assert.Equal(1, summary.Advanced);
            Assert.Equal(2, summary.Held);
            Assert.Equal(1, dispatchStarts);
            Assert.Equal(GoalStatus.Active, goal.Status);
            Assert.Equal(WorkTaskStatus.Assigned, task.Status);
            Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.StaleCandidate, stale.Outcome);
            Assert.NotNull(stale.ReconciledAt);
            Assert.Contains("invalidated the current acceptance attempt", stale.Detail, StringComparison.Ordinal);
            Assert.Equal(3, ticks.Count);
            Assert.Contains(ticks[0].ProgressLines!, line =>
                line.Contains("ACCEPTANCE_INVALIDATED", StringComparison.Ordinal) &&
                line.Contains("attempt_staled=true", StringComparison.Ordinal) &&
                line.Contains("goal_reopened=true", StringComparison.Ordinal));
            Assert.DoesNotContain(ticks[0].ProgressLines!, line =>
                line.Contains("result=executed", StringComparison.Ordinal));
            Assert.Contains(ticks[1].ProgressLines!, line =>
                line.Contains("result=held", StringComparison.Ordinal) &&
                line.Contains("reason=invalidated_acceptance_attempt", StringComparison.Ordinal));
            Assert.DoesNotContain(ticks[1].ProgressLines!, line =>
                line.Contains("result=executed", StringComparison.Ordinal));
            Assert.Contains(ticks[2].ProgressLines!, line =>
                line.Contains("result=executed", StringComparison.Ordinal));

            attemptCoordinator.RunAttemptForTests(
                started.Attempt,
                candidate,
                ConductorAutonomyPolicy.Conservative,
                PassingRun);
            var afterLateCompletion = ReadAttempt(started.Attempt.MetadataPath);
            Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.StaleCandidate, afterLateCompletion.Outcome);
            Assert.NotNull(afterLateCompletion.ReconciledAt);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "BatchLoop_does_not_publish_applied_intent_when_tick_persist_fails")]
    public async Task BatchLoopDoesNotPublishAppliedIntentWhenTickPersistFails()
    {
        var root = CreateTempDirectory("mcg-loop-operator-intent-persist-failure");
        try
        {
            var (kernel, goal) = SimpleGoal("Keep claimed intent after failed persist");
            var task = goal.Tasks.Single();
            kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Failed, "failed");
            var store = new SqliteOperatorIntentStore(
                Path.Combine(root, "operator-intents.db"),
                Path.Combine(root, "logs"));
            var intent = new OperatorIntentRecord(
                "persist-failure-intent",
                "persist-failure-key",
                OperatorIntentVerbs.Retry,
                goal.Id.Value,
                task.Id.Value,
                JsonSerializer.Serialize(
                    new RetryOperatorIntentPayload("retry after persistence recovers", null, RetryCause: RetryCause.ContractClarification),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                [],
                "operator",
                "test",
                "test",
                DateTimeOffset.UtcNow);
            await store.EnqueueAsync(intent);
            var sleepCalls = 0;

            _ = new ConductorBatchLoop(
                operatorIntents: new OperatorIntentCoordinator(store)).Run(
                kernel,
                MakeDriver(
                    getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                    getRunningCount: () => ConductorAutonomyPolicy.Conservative.MaxConcurrentPaidWorkers),
                 ConductorAutonomyPolicy.Conservative,
                 NoStopPath(),
                 maxIterations: 2,
                 watchInterval: TimeSpan.FromSeconds(1),
                 sleepFunc: _ =>
                 {
                     sleepCalls++;
                     return true;
                 },
                 persistTick: _ => throw SqliteBusy(),
                 keepAliveWhenIdle: true,
                 busyWriteDelay: _ => { });

            var outcome = await store.GetAsync(intent.Id);
            Assert.Equal(OperatorIntentStatus.Claimed, outcome!.Status);
            Assert.Null(outcome.CompletedAt);
            Assert.Equal(1, sleepCalls);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "BatchLoop_rejects_operator_intent_outside_configured_goal_scope")]
    public async Task BatchLoopRejectsOperatorIntentOutsideConfiguredGoalScope()
    {
        var root = CreateTempDirectory("mcg-loop-operator-intent-scope");
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var agents = AgentCatalog.Default().Agents;
            var scopedGoal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
                kernel,
                agents,
                "Scoped conductor goal");
            var outsideGoal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
                kernel,
                agents,
                "Outside conductor goal");
            var outsideTask = outsideGoal.Tasks.Single();
            kernel.ReportTaskProgress(outsideGoal.Id, outsideTask.Id, WorkTaskStatus.Failed, "failed");
            var store = new SqliteOperatorIntentStore(
                Path.Combine(root, "operator-intents.db"),
                Path.Combine(root, "logs"));
            var intent = new OperatorIntentRecord(
                "outside-scope-intent",
                "outside-scope-key",
                OperatorIntentVerbs.Retry,
                outsideGoal.Id.Value,
                outsideTask.Id.Value,
                JsonSerializer.Serialize(
                    new RetryOperatorIntentPayload("retry outside scope", null, RetryCause: RetryCause.ContractClarification),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                [],
                "operator",
                "test",
                "test",
                DateTimeOffset.UtcNow);
            await store.EnqueueAsync(intent);

            _ = new ConductorBatchLoop(
                operatorIntents: new OperatorIntentCoordinator(store)).Run(
                kernel,
                MakeDriver(),
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 1,
                onlyGoalId: scopedGoal.Id.Value);

            var outcome = await store.GetAsync(intent.Id);
            Assert.Equal(OperatorIntentStatus.Rejected, outcome!.Status);
            Assert.Contains("outside conductor scope", outcome.Outcome, StringComparison.Ordinal);
            Assert.Equal(WorkTaskStatus.Failed, outsideTask.Status);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "BatchLoop_surfaces_rejected_intent_when_no_goal_is_eligible")]
    public async Task BatchLoopSurfacesRejectedIntentWhenNoGoalIsEligible()
    {
        var root = CreateTempDirectory("mcg-loop-operator-intent-rejected");
        try
        {
            var (kernel, goal) = SimpleGoal("Surface rejected parked-goal intent");
            kernel.ParkGoal(goal.Id, "operator parked");
            var store = new SqliteOperatorIntentStore(
                Path.Combine(root, "operator-intents.db"),
                Path.Combine(root, "logs"));
            var intent = new OperatorIntentRecord(
                "rejected-loop-intent",
                "rejected-loop-key",
                OperatorIntentVerbs.Retry,
                goal.Id.Value,
                TaskId.New().Value,
                JsonSerializer.Serialize(
                    new RetryOperatorIntentPayload("invalid task", null, RetryCause: RetryCause.ContractClarification),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                [],
                "operator",
                "test",
                "test",
                DateTimeOffset.UtcNow);
            await store.EnqueueAsync(intent);
            var ticks = new List<BatchTickSummary>();

            _ = new ConductorBatchLoop(
                operatorIntents: new OperatorIntentCoordinator(store)).Run(
                kernel,
                MakeDriver(),
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 1,
                onTick: ticks.Add,
                persistGoalTick: (_, _) => { });

            var outcome = await store.GetAsync(intent.Id);
            Assert.Equal(OperatorIntentStatus.Rejected, outcome!.Status);
            Assert.Contains(ticks.SelectMany(tick => tick.ProgressLines ?? []), line =>
                line.Contains("result=rejected", StringComparison.Ordinal));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "BatchLoop_stop_after_operator_intent_detaches_and_checkpoints")]
    public async Task BatchLoopStopAfterOperatorIntentDetachesAndCheckpoints()
    {
        var root = CreateTempDirectory("mcg-loop-stop-after-operator-intent");
        try
        {
            var (kernel, goal) = SimpleGoal("Stop after rejected operator intent");
            kernel.ParkGoal(goal.Id, "parked for operator");
            var store = new SqliteOperatorIntentStore(
                Path.Combine(root, "operator-intents.db"),
                Path.Combine(root, "logs"));
            var intent = new OperatorIntentRecord(
                "stop-after-intent",
                "stop-after-intent-key",
                OperatorIntentVerbs.Retry,
                goal.Id.Value,
                TaskId.New().Value,
                JsonSerializer.Serialize(
                    new RetryOperatorIntentPayload("invalid task", null, RetryCause: RetryCause.ContractClarification),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                [],
                "operator",
                "test",
                "test",
                DateTimeOffset.UtcNow);
            await store.EnqueueAsync(intent);
            var stopFile = Path.Combine(root, ConductorBatchLoop.StopFileName);
            var detachCalls = 0;
            var checkpointCalls = 0;

            var summary = new ConductorBatchLoop(
                operatorIntents: new OperatorIntentCoordinator(store),
                detachGoalRunningDispatches: (_, detachedGoal) =>
                {
                    Assert.Equal(goal.Id, detachedGoal.Id);
                    detachCalls++;
                }).Run(
                    kernel,
                    MakeDriver(),
                    ConductorAutonomyPolicy.Conservative,
                    stopFile,
                    maxIterations: 2,
                    onTick: _ => File.WriteAllText(stopFile, "stop"),
                    persistTick: _ => checkpointCalls++);

            var outcome = await store.GetAsync(intent.Id);
            Assert.True(summary.StopRequested);
            Assert.Equal(1, summary.Ticks);
            Assert.Equal(1, detachCalls);
            Assert.Equal(1, checkpointCalls);
            Assert.Equal(OperatorIntentStatus.Rejected, outcome!.Status);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "BatchLoop_fault_isolates_unavailable_operator_intent_store")]
    public void BatchLoopFaultIsolatesUnavailableOperatorIntentStore()
    {
        var root = CreateTempDirectory("mcg-loop-operator-intent-unavailable");
        try
        {
            var databasePath = Path.Combine(root, "operator-intents.db");
            File.WriteAllText(databasePath, "not a sqlite database");
            var (kernel, _) = SimpleGoal("Advance despite unavailable intent inbox");
            var workspaceCreates = 0;
            var summary = new ConductorBatchLoop(
                operatorIntents: new OperatorIntentCoordinator(
                    new SqliteOperatorIntentStore(
                        databasePath,
                        Path.Combine(root, "logs"),
                        readOnly: true))).Run(
                kernel,
                MakeDriver(createWorkspace: _ =>
                {
                    workspaceCreates++;
                    return root;
                }),
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 1);

            Assert.Equal(1, summary.Advanced);
            Assert.Equal(1, workspaceCreates);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "BatchLoop_running_recovery_sequence_applies_retry_progress_and_manual_verification")]
    public async Task BatchLoopRunningRecoverySequenceAppliesRetryProgressAndManualVerification()
    {
        var root = CreateTempDirectory("mcg-loop-operator-intent-recovery-sequence");
        try
        {
            var (kernel, goal) = SimpleGoal("Apply running-loop recovery sequence");
            var task = goal.Tasks.Single();
            kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Failed, "failed");
            var store = new SqliteOperatorIntentStore(
                Path.Combine(root, "operator-intents.db"),
                Path.Combine(root, "logs"));
            var coordinator = new OperatorIntentCoordinator(store);
            var driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                getRunningCount: () => ConductorAutonomyPolicy.Conservative.MaxConcurrentPaidWorkers);

            async Task SubmitAndTick(string id, string verb, object payload)
            {
                await store.EnqueueAsync(new OperatorIntentRecord(
                    id,
                    $"{id}-key",
                    verb,
                    goal.Id.Value,
                    task.Id.Value,
                    JsonSerializer.Serialize(
                        payload,
                        payload.GetType(),
                        new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                    [],
                    "operator",
                    "cli",
                    "local-process",
                    DateTimeOffset.UtcNow));
                _ = new ConductorBatchLoop(operatorIntents: coordinator).Run(
                    kernel,
                    driver,
                    ConductorAutonomyPolicy.Conservative,
                    NoStopPath(),
                    maxIterations: 1,
                    persistGoalTick: (_, _) => { });
                Assert.Equal(OperatorIntentStatus.Applied, (await store.GetAsync(id))!.Status);
            }

            await SubmitAndTick(
                "sequence-retry",
                OperatorIntentVerbs.Retry,
                new RetryOperatorIntentPayload("mechanical recovery", RetryRoundKind.Mechanical, RetryCause: RetryCause.MainDriftConflict));
            await SubmitAndTick(
                "sequence-progress",
                OperatorIntentVerbs.Progress,
                new ProgressOperatorIntentPayload(WorkTaskStatus.Completed, "existing work still stands"));
            var verification = new TaskVerificationRecord(
                "manual-verification passed",
                root,
                0,
                "operator checked existing result",
                string.Empty,
                DateTimeOffset.UtcNow);
            await SubmitAndTick(
                "sequence-verify",
                OperatorIntentVerbs.VerifyManual,
                new ManualVerificationOperatorIntentPayload(verification));

            Assert.Equal(WorkTaskStatus.Completed, kernel.GetTask(goal.Id, task.Id).Status);
            Assert.Equal(verification, kernel.GetTask(goal.Id, task.Id).LastVerification);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "BatchLoop_VerifyManualRepairsInconsistentVerifiedGateBeforeAcceptance")]
    public async Task BatchLoopVerifyManualRepairsInconsistentVerifiedGateBeforeAcceptance()
    {
        var root = CreateTempDirectory("mcg-loop-inconsistent-verify-manual");
        try
        {
            var (kernel, inconsistentGoal, _) = SeedInconsistentVerifiedGoalWithHealthyNeighbor();
            var task = inconsistentGoal.Tasks.Single();
            var store = new SqliteOperatorIntentStore(
                Path.Combine(root, "operator-intents.db"),
                Path.Combine(root, "logs"));
            var intent = new OperatorIntentRecord(
                "repair-inconsistent-gate",
                "repair-inconsistent-gate-key",
                OperatorIntentVerbs.VerifyManual,
                inconsistentGoal.Id.Value,
                task.Id.Value,
                JsonSerializer.Serialize(
                    new ManualVerificationOperatorIntentPayload(new TaskVerificationRecord(
                        "verify-manual",
                        root,
                        0,
                        "operator verified the completed task",
                        string.Empty,
                        DateTimeOffset.Parse("2026-08-08T02:24:00Z"))),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                [],
                "operator",
                "cli",
                "local-process",
                DateTimeOffset.Parse("2026-08-08T02:24:00Z"));
            await store.EnqueueAsync(intent);

            var acceptanceRuns = 0;
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                Path.Combine(root, "attempts"),
                runInline: true);
            var driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                runAcceptanceWithSlot: (goal, _) =>
                {
                    if (goal.Id == inconsistentGoal.Id)
                    {
                        acceptanceRuns++;
                    }

                    return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria;
                },
                getLandingFileScopes: _ => ["src/Mcg.AgentOrchestrator.App/Orchestration/ConductorBatchLoop.cs"],
                parallelAcceptanceAttemptCoordinator: coordinator);

            var summary = new ConductorBatchLoop(
                operatorIntents: new OperatorIntentCoordinator(store)).Run(
                    kernel,
                    driver,
                    ConductorAutonomyPolicy.Conservative,
                    NoStopPath(),
                    maxIterations: 2,
                    persistGoalTick: (_, _) => { });

            Assert.Equal(OperatorIntentStatus.Applied, (await store.GetAsync(intent.Id))!.Status);
            Assert.True(kernel.BuildVerificationGate(inconsistentGoal.Id).IsSatisfied);
            Assert.Equal(1, acceptanceRuns);
            Assert.True(summary.Advanced > 0);
            Assert.DoesNotContain(
                kernel.Goals,
                goal => goal.Id == inconsistentGoal.Id && goal.Status == GoalStatus.Cancelled);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "OperatorIntentCoordinator_recovers_claimed_intent_without_duplicate_state_mutation")]
    public async Task OperatorIntentCoordinatorRecoversClaimedIntentWithoutDuplicateStateMutation()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mcg-loop-operator-intent-recovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var (kernel, goal) = SimpleGoal("Recover claimed operator retry");
            var task = goal.Tasks.Single();
            kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Failed, "Needs operator repair.");
            var store = new SqliteOperatorIntentStore(
                Path.Combine(root, "operator-intents.db"),
                Path.Combine(root, "logs"));
            var intent = new OperatorIntentRecord(
                Guid.NewGuid().ToString("N"),
                "loop-recovery-key",
                OperatorIntentVerbs.Retry,
                goal.Id.Value,
                task.Id.Value,
                JsonSerializer.Serialize(
                    new RetryOperatorIntentPayload("Apply once.", null, RetryCause: RetryCause.ContractClarification),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                [],
                "operator",
                "cli",
                "local-process",
                DateTimeOffset.UtcNow);
            await store.EnqueueAsync(intent);

            var first = new OperatorIntentCoordinator(store).ExecutePending(kernel, goal);
            Assert.True(first.MutatedGoalState);
            var restored = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot());
            var restoredGoal = restored.GetGoal(goal.Id);

            var replay = new OperatorIntentCoordinator(store).ExecutePending(restored, restoredGoal);
            var outcome = await store.GetAsync(intent.Id);

            Assert.False(replay.MutatedGoalState);
            Assert.Single(restoredGoal.Timeline.Where(item => item.Kind == ProgressKind.TaskRetried));
            Assert.Equal(OperatorIntentStatus.Applied, outcome!.Status);
            Assert.Contains("recovered durable goal marker", outcome.Outcome, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
            }
        }
    }
}
