using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Xml.Linq;

using static ConductorDriverTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class ConductorDriverTestsAdmissionRefusal
{
    private static async Task<(OrchestratorWorkspace Workspace, RetryContextFingerprint Fingerprint)>
        SeedFailedTaskWithActiveRetryReservationAsync(
            AgentOrchestratorKernel kernel,
            Goal goal,
            TaskSpec task)
    {
        var workspace = OrchestratorWorkspace.ForDirectory(ConductorDriverTests.CreateTempDirectory());
        _ = StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
        var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
        var fingerprint = new RetryContextFingerprint(
            RetryContextFingerprint.CurrentSchemaVersion,
            "same-paid-candidate");
        var firstDispatchAt = DateTimeOffset.Parse("2026-09-05T10:00:00Z");
        var firstDispatch = new TaskDispatchRecord(
            "test-worker",
            "powershell.exe -Command first",
            "C:\\tmp",
            firstDispatchAt,
            RetryContextFingerprint: fingerprint,
            PaidRoute: PaidRouteClassification.Paid);
        kernel.RecordTaskDispatch(goal.Id, task.Id, firstDispatch);
        await repository.SaveAsync(kernel);
        var firstReservation = await RetryAdmissionReservationStore.TryReserveAsync(
            workspace.SqliteStatePath,
            goal.Id,
            task.Id,
            fingerprint,
            PaidRouteClassification.Paid,
            RetryCause.EnvironmentApparatusFailure,
            firstDispatch,
            firstDispatchAt,
            "orphaned-owner",
            DateTimeOffset.UtcNow.AddMinutes(5));
        Assert.NotNull(firstReservation);
        Assert.True(firstReservation!.Admission.AllowsProcessStart);
        kernel.ReplaceGoalWithSnapshot(firstReservation.Snapshot);
        kernel.RecordDispatchExecutionResult(
            goal.Id,
            task.Id,
            new TaskVerificationRecord(
                firstDispatch.Command,
                firstDispatch.WorkingDirectory,
                1,
                string.Empty,
                "powershell.exe: ParserError: Unexpected token '}' in expression.",
                DateTimeOffset.UtcNow,
                ProviderFailureKind: ProviderFailureKind.RateLimit));
        await repository.SaveAsync(kernel);
        Assert.Equal(WorkTaskStatus.Failed, kernel.GetTask(goal.Id, task.Id).Status);
        return (workspace, fingerprint);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_prepared_dispatch_without_start_reports_both_state_views")]
    public async Task ConductorDriverPreparedDispatchWithoutStartReportsBothStateViews()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        var seeded = await SeedFailedTaskWithActiveRetryReservationAsync(kernel, goal, task);
        kernel.RetryTask(goal.Id, task.Id, "Retry the refused candidate.", RetryCause.EnvironmentApparatusFailure);
        var preparedTask = kernel.GetTask(goal.Id, task.Id);
        var preparedDispatch = new TaskDispatchRecord(
            "test-worker",
            "powershell.exe -Command second",
            "C:\\tmp",
            DateTimeOffset.Parse("2026-09-05T10:01:00Z"),
            RetryContextFingerprint: seeded.Fingerprint,
            PaidRoute: PaidRouteClassification.Paid);
        kernel.RecordTaskDispatch(goal.Id, task.Id, preparedDispatch);
        var prepared = new WorkerProfileDispatchResult(preparedTask, @"C:\repo\.orchestrator\prompts\task.md");
        var processes = new GoalDispatchOperations().StartDispatches(
            kernel,
            seeded.Workspace,
            kernel.GetGoal(goal.Id),
            refreshBeforeStart: false,
            checkpointBeforeWorkerStart: (checkpointKernel, checkpointGoalId, checkpointTaskId, phase) =>
            {
                Assert.Equal(goal.Id, checkpointGoalId);
                Assert.Equal(task.Id, checkpointTaskId);
                Assert.Equal(DispatchRecordCheckpointPhase.BeforeRetryAdmission, phase);
                new SqliteOrchestratorStateRepository(seeded.Workspace.SqliteStatePath)
                    .SaveAsync(checkpointKernel).GetAwaiter().GetResult();
            });
        var result = new SubscriptionStartResult(
            [prepared],
            processes,
            new ParallelExecutionPlan([], []),
            []);

        var refusal = Assert.Single(processes.StartRefusals!);
        Assert.Equal(task.Id, refusal.TaskId);
        Assert.Contains("Prepared retry reservation is owned until", refusal.Reason, StringComparison.Ordinal);
        Assert.Null(kernel.GetTask(goal.Id, task.Id).LastProcess);
        var outcome = ConductorDriver.ClassifySubscriptionStartForConductor(result);

        Assert.Equal(DispatchStartOutcomeCategory.SpawnFailed, outcome.Category);
        Assert.Contains(
            $"task {task.Id.Value[..8]}: Prepared retry reservation is owned until",
            outcome.Reason,
            StringComparison.Ordinal);
        Assert.Contains($"{task.Id.Value[..8]}:status=Running:admission=none", outcome.Reason, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_repeated_refused_start_stays_set_aside_after_first_retry_and_dispatch")]
    public async Task ConductorDriverRepeatedRefusedStartStaysSetAsideAfterFirstRetryAndDispatch()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        var seeded = await SeedFailedTaskWithActiveRetryReservationAsync(kernel, goal, task);
        var repository = new SqliteOrchestratorStateRepository(seeded.Workspace.SqliteStatePath);
        var heldGoal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "held sibling");
        string? setAsideReason = null;
        var retriesBeforeTicks = kernel.GetGoal(goal.Id).Timeline.Count(evt => evt.Kind == ProgressKind.TaskRetried);
        var dispatchesBeforeTicks = kernel.GetGoal(goal.Id).Timeline.Count(evt => evt.Kind == ProgressKind.TaskDispatchRecorded);
        int retriesAfterFirstTick = -1, dispatchesAfterFirstTick = -1;
        var startAttempts = 0;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: currentGoal =>
            {
                if (currentGoal.Id == heldGoal.Id)
                    return DispatchStartOutcome.EmptyBatch("Held sibling remains eligible.");
                startAttempts++;
                var preparedDispatch = new TaskDispatchRecord("test-worker", "powershell.exe -Command retry",
                    "C:\\tmp", DateTimeOffset.Parse("2026-09-05T10:01:00Z"),
                    RetryContextFingerprint: seeded.Fingerprint,
                    PaidRoute: PaidRouteClassification.Paid);
                kernel.RecordTaskDispatch(currentGoal.Id, task.Id, preparedDispatch);
                var preparedTask = kernel.GetTask(currentGoal.Id, task.Id);
                var processes = new GoalDispatchOperations().StartDispatches(kernel, seeded.Workspace,
                    kernel.GetGoal(currentGoal.Id),
                    refreshBeforeStart: false,
                    checkpointBeforeWorkerStart: (checkpointKernel, checkpointGoalId, checkpointTaskId, phase) =>
                    {
                        Assert.Equal(currentGoal.Id, checkpointGoalId);
                        Assert.Equal(task.Id, checkpointTaskId);
                        Assert.Equal(DispatchRecordCheckpointPhase.BeforeRetryAdmission, phase);
                        repository.SaveAsync(checkpointKernel).GetAwaiter().GetResult();
                    });
                return ConductorDriver.ClassifySubscriptionStartForConductor(new SubscriptionStartResult(
                    [new WorkerProfileDispatchResult(preparedTask, @"C:\repo\.orchestrator\prompts\task.md")],
                    processes, new ParallelExecutionPlan([], []), []));
            },
            startRecordedDispatches: currentGoal => currentGoal.Id == heldGoal.Id
                ? DispatchStartOutcome.EmptyBatch("Held sibling remains eligible.")
                : DispatchStartOutcome.EmptyBatch("Prepared retry remains held."),
            buildServerShutdown: () => { },
            retryTaskWithCause: (goalId, taskId, message, roundKind, cause) =>
                kernel.RetryTask(goalId, taskId, message, cause, retryRoundKind: roundKind),
            recordCriterionRetryFeedback: kernel.RecordCriterionRetryFeedback,
            writeEscalation: (_, _, reason) => setAsideReason = reason);

        var summary = new ConductorBatchLoop().Run(kernel, driver, ConductorAutonomyPolicy.Permissive,
            Path.Combine(Path.GetTempPath(), $"mcg-no-stop-{Guid.NewGuid():N}"),
            maxIterations: 2,
            persistTick: checkpointKernel => repository.SaveAsync(checkpointKernel).GetAwaiter().GetResult(),
            watchInterval: TimeSpan.FromMilliseconds(1),
            sleepFunc: _ =>
            {
                var persisted = repository.LoadGoalAsync(goal.Id).GetAwaiter().GetResult();
                Assert.NotNull(persisted);
                kernel.ReplaceGoalStateWithSnapshot(persisted!, []);
                Assert.Equal(GoalStatus.Active, kernel.GetGoal(goal.Id).Status);
                var durableTask = kernel.GetTask(goal.Id, task.Id);
                // The checkpoint preserves the recorded dispatch; refusal must not restore the stale failed snapshot.
                Assert.Equal(WorkTaskStatus.Running, durableTask.Status);
                Assert.Null(durableTask.LastProcess);
                Assert.Equal("powershell.exe -Command retry", durableTask.LastDispatch?.Command);
                Assert.NotNull(durableTask.LatestRetryAt);
                retriesAfterFirstTick = kernel.GetGoal(goal.Id).Timeline.Count(evt => evt.Kind == ProgressKind.TaskRetried);
                dispatchesAfterFirstTick = kernel.GetGoal(goal.Id).Timeline.Count(evt => evt.Kind == ProgressKind.TaskDispatchRecorded);
                return false;
            });
        Assert.Equal(2, summary.Ticks);
        Assert.Equal(1, startAttempts);
        Assert.Equal(retriesBeforeTicks + 1, retriesAfterFirstTick);
        Assert.Equal(dispatchesBeforeTicks + 1, dispatchesAfterFirstTick);
        Assert.Equal(retriesAfterFirstTick, kernel.GetGoal(goal.Id).Timeline.Count(evt => evt.Kind == ProgressKind.TaskRetried));
        Assert.Equal(dispatchesAfterFirstTick, kernel.GetGoal(goal.Id).Timeline.Count(evt => evt.Kind == ProgressKind.TaskDispatchRecorded));
        Assert.Contains(task.Id.Value[..8], setAsideReason, StringComparison.Ordinal);
        Assert.Contains("Prepared retry reservation is owned until", setAsideReason, StringComparison.Ordinal);
        Assert.Equal(WorkTaskStatus.Running, kernel.GetTask(goal.Id, task.Id).Status);
        Assert.Null(kernel.GetTask(goal.Id, task.Id).LastProcess);
        var coldKernel = await repository.LoadAsync();
        var coldGoal = coldKernel.GetGoal(goal.Id);
        Assert.Equal(GoalLifecycleState.Dispatched, GoalLifecycle.ResolveState(coldGoal));
        Assert.Equal(dispatchesAfterFirstTick, coldGoal.Timeline.Count(evt => evt.Kind == ProgressKind.TaskDispatchRecorded));
        Assert.Equal(retriesAfterFirstTick, coldGoal.Timeline.Count(evt => evt.Kind == ProgressKind.TaskRetried));
    }

}
