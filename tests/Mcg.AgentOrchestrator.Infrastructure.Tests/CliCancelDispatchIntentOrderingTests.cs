using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class CliCancelDispatchIntentOrderingTests
{
    [Xunit.Fact]
    public void WorkerCancellationWithCancelledBuildStillSuppressesCompletionAfterRestart()
    {
        var f = new CompletionBuildEvidenceFixture();
        f.Runner().RefreshLatestProcess(f.Kernel, f.GoalId, f.TaskId);
        f.Runner().CancelLatestProcess(f.Kernel, f.GoalId, f.TaskId);
        Assert.Equal(CompletionBuildEvidencePhase.Cancelled, f.Task.CompletionBuildEvidence!.Phase);
        Assert.True(f.Task.LastProcess!.WasCancelled);
        new SqliteOrchestratorStateRepository(f.StatePath).SaveAsync(f.Kernel).GetAwaiter().GetResult();
        f.Reload();

        var outcome = f.Runner().RefreshLatestProcessWithOutcome(f.Kernel, f.GoalId, f.TaskId);

        Assert.Null(outcome.Verification);
        Assert.Equal(WorkTaskStatus.Cancelled, f.Task.Status);
        Assert.Empty(f.Task.VerificationHistory);
        Assert.False(f.Task.CompletionBuildEvidence!.Applied);
        Assert.True(f.Task.CompletionBuildEvidence.CustodyReleased);
        Assert.Equal(1, f.Launches);
        Assert.Equal(1, f.Stops);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task OperationOnlyCancelWithCurrentWorkerAppliesTerminalDispositionWithoutStoppingWorker(bool alreadyCompleted)
    {
        var f = new CompletionBuildEvidenceFixture();
        f.Runner().RefreshLatestProcess(f.Kernel, f.GoalId, f.TaskId);
        var operation = f.Task.CompletionBuildEvidence!;
        Assert.True(BackgroundDispatchRunner.HasCurrentCompletionBuildWorker(f.Task));
        var worker = f.Task.LastProcess!;
        if (alreadyCompleted) f.Release();
        var context = new CliExecutionContext(f.Kernel, OrchestratorWorkspace.ForDirectory(f.Root),
            new InMemoryModelProviderRegistry([]), AgentCatalog.Default().Agents,
            WorkerProfileCatalog.Default(), f.Goal);
        var store = new CancelDispatchIntentTestStore();
        CliCommandHandlers.ExecuteCancelDispatch(
            ["cancel-dispatch", "1", "--completion-build-operation", operation.Id], context, store,
            _ => throw new InvalidOperationException("Operation-only cancellation must not stop the worker."));
        var coordinator = new OperatorIntentCoordinator(store)
        {
            CancelLatestProcess = (_, _, _) => throw new InvalidOperationException("Operation-only cancellation must not stop the worker."),
            CancelCompletionBuild = (kernel, goalId, taskId, target) =>
                f.Runner().CancelCompletionBuildEvidence(kernel, goalId, taskId, target)
        };
        coordinator.ExecutePending(f.Kernel, f.Goal);
        new SqliteOrchestratorStateRepository(f.StatePath).SaveAsync(f.Kernel).GetAwaiter().GetResult();
        coordinator.CompletePersisted([f.GoalId]);
        Assert.Equal(OperatorIntentStatus.Applied, Assert.Single(await store.ListForGoalAsync(f.GoalId.Value)).Status);
        Assert.Equal(alreadyCompleted ? CompletionBuildEvidencePhase.Success : CompletionBuildEvidencePhase.Cancelled,
            f.Task.CompletionBuildEvidence!.Phase);
        Assert.True(f.Task.CompletionBuildEvidence.CustodyReleased);
        Assert.False(f.Task.LastProcess!.WasCancelled);
        f.Reload();

        Assert.True(ExitedDispatchReconciliationExecutor.ReconcileTask(f.Kernel, f.GoalId, f.TaskId, f.Runner()));

        Assert.Equal(worker.ProcessId, f.Task.LastProcess!.ProcessId);
        Assert.Equal(worker.StartedAt, f.Task.LastProcess.StartedAt);
        Assert.False(f.Task.LastProcess.WasCancelled);
        Assert.Equal(alreadyCompleted ? WorkTaskStatus.Completed : WorkTaskStatus.Failed, f.Task.Status);
        Assert.Equal(alreadyCompleted, Assert.Single(f.Task.VerificationHistory).Succeeded);
        if (alreadyCompleted) Assert.NotNull(f.Task.LastDispatch!.ResultCommit);
        else
        {
            Assert.Contains("build_evidence_outcome=Cancelled", f.Task.LastVerification!.StandardError);
            Assert.Null(f.Task.LastDispatch!.ResultCommit);
        }
        Assert.True(f.Task.CompletionBuildEvidence!.Applied);
        Assert.True(f.Task.CompletionBuildEvidence.CustodyReleased);
        Assert.Equal(alreadyCompleted ? 0 : 1, f.Stops);
        Assert.Equal(1, f.Launches);
        Assert.Null(new ReconcileSweepRemediationStore(f.StatePath).TryGetAcceptanceLeaseOwner(f.GoalId.Value));
        new SqliteOrchestratorStateRepository(f.StatePath).SaveAsync(f.Kernel).GetAwaiter().GetResult();
        f.Reload();
        Assert.True(ExitedDispatchReconciliationExecutor.ReconcileTask(f.Kernel, f.GoalId, f.TaskId, f.Runner()));
        Assert.Single(f.Task.VerificationHistory);
        Assert.Equal(alreadyCompleted ? WorkTaskStatus.Completed : WorkTaskStatus.Failed, f.Task.Status);
        Assert.Equal(alreadyCompleted ? 0 : 1, f.Stops);
        Assert.Equal(1, f.Launches);
    }

    [Xunit.Fact]
    public async Task ResetOperationSelectorEnqueuesBeforeConductorCancellationWithoutStoppingHistoricalWorker()
    {
        var f = new CompletionBuildEvidenceFixture();
        f.Runner().RefreshLatestProcess(f.Kernel, f.GoalId, f.TaskId);
        var operation = f.Task.CompletionBuildEvidence!;
        f.ResetWorkerMetadata();
        var context = new CliExecutionContext(f.Kernel, OrchestratorWorkspace.ForDirectory(f.Root),
            new InMemoryModelProviderRegistry([]), AgentCatalog.Default().Agents,
            WorkerProfileCatalog.Default(), f.Goal);
        var calls = new List<string>();
        var store = new CancelDispatchIntentTestStore { BeforeEnqueue = () => calls.Add("append") };
        CliCommandHelp.ThrowIfInvalidFlags(["cancel-dispatch", "1", "--completion-build-operation", operation.Id]);
        var save = CliCommandHandlers.ExecuteCancelDispatch(
            ["cancel-dispatch", "1", "--completion-build-operation", operation.Id], context, store,
            _ => throw new InvalidOperationException("Historical worker must never be stopped."));
        Assert.False(save);
        Assert.Equal(["append"], calls);
        Assert.Equal(0, f.Stops);
        var intent = Assert.Single(await store.ListForGoalAsync(f.GoalId.Value));
        var payload = JsonSerializer.Deserialize<CancelDispatchOperatorIntentPayload>(intent.PayloadJson, OperatorIntentJson.Options)!;
        Assert.Equal(CompletionBuildEvidenceTarget.From(operation), payload.CompletionBuild);
        Assert.Equal(operation.WorkerProcess.ProcessId, payload.ProcessId);

        var coordinator = new OperatorIntentCoordinator(store)
        {
            CancelLatestProcess = (_, _, _) => throw new InvalidOperationException("Worker cancellation is outside this target."),
            CancelCompletionBuild = (kernel, goalId, taskId, target) =>
            {
                calls.Add("cancel-operation");
                return f.Runner().CancelCompletionBuildEvidence(kernel, goalId, taskId, target);
            }
        };
        coordinator.ExecutePending(f.Kernel, f.Goal);
        new SqliteOrchestratorStateRepository(f.StatePath).SaveAsync(f.Kernel).GetAwaiter().GetResult();
        coordinator.CompletePersisted([f.GoalId]);
        Assert.Equal(["append", "cancel-operation"], calls);
        Assert.Equal(OperatorIntentStatus.Applied, (await store.GetAsync(intent.Id))!.Status);
        Assert.Equal(CompletionBuildEvidencePhase.Cancelled, f.Task.CompletionBuildEvidence!.Phase);
        Assert.True(f.Task.CompletionBuildEvidence.CustodyReleased);
        Assert.Null(f.Task.LastProcess);
        Assert.Null(f.Task.LastVerification);
        Assert.NotEqual(WorkTaskStatus.Cancelled, f.Task.Status);
    }

    [Xunit.Fact]
    public async Task ResetOperationFailedEnqueueOrStaleSelectorNeverActs()
    {
        var f = new CompletionBuildEvidenceFixture();
        f.Runner().RefreshLatestProcess(f.Kernel, f.GoalId, f.TaskId);
        var operation = f.Task.CompletionBuildEvidence!;
        f.ResetWorkerMetadata();
        var context = new CliExecutionContext(f.Kernel, OrchestratorWorkspace.ForDirectory(f.Root),
            new InMemoryModelProviderRegistry([]), AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(), f.Goal);
        var store = new CancelDispatchIntentTestStore { ThrowOnEnqueue = true };
        Assert.Throws<IOException>(() => CliCommandHandlers.ExecuteCancelDispatch(
            ["cancel-dispatch", "1", "--completion-build-operation", operation.Id], context, store));
        store.ThrowOnEnqueue = false;
        Assert.Throws<InvalidOperationException>(() => CliCommandHandlers.ExecuteCancelDispatch(
            ["cancel-dispatch", "1", "--completion-build-operation", "foreign"], context, store));
        Assert.Empty(await store.ListForGoalAsync(f.GoalId.Value));
        Assert.Equal(0, f.Stops);
        Assert.True(f.Task.CompletionBuildEvidence!.IsPending);
    }

    [Xunit.Fact]
    public async Task CommandAppendsIntentBeforeStoppingWithoutWritingCancelledState()
    {
        var (context, goal, task) = CreateContext();
        var calls = new List<string>();
        var store = new CancelDispatchIntentTestStore { BeforeEnqueue = () => calls.Add("append") };

        var shouldSave = CliCommandHandlers.ExecuteCancelDispatch(
            ["cancel-dispatch", "1"], context, store, _ => calls.Add("stop"));

        Xunit.Assert.Equal(["append", "stop"], calls);
        Xunit.Assert.False(shouldSave);
        Xunit.Assert.Equal(WorkTaskStatus.Running, task.Status);
        Xunit.Assert.False(task.LastProcess!.WasCancelled);
        var intent = Xunit.Assert.Single(await store.ListForGoalAsync(goal.Id.Value));
        Xunit.Assert.Equal(OperatorIntentVerbs.CancelDispatch, intent.Verb);
        Xunit.Assert.Equal(task.Id.Value, intent.TaskId);
        var payload = JsonSerializer.Deserialize<CancelDispatchOperatorIntentPayload>(
            intent.PayloadJson, OperatorIntentJson.Options)!;
        Xunit.Assert.Equal(task.LastProcess.ProcessId, payload.ProcessId);
        Xunit.Assert.Equal(task.LastProcess.StartedAt, payload.ProcessStartedAt);
        Xunit.Assert.Equal(BackgroundDispatchRunner.BuildDispatchId(goal.Id, task.Id, task.LastDispatch!), payload.DispatchId);
        Xunit.Assert.DoesNotContain(goal.Timeline, evt => evt.Kind == ProgressKind.TaskCancelled);
    }

    [Xunit.Fact]
    public void FailedAppendLeavesWorkerRunningAndStateUntouched()
    {
        var (context, _, task) = CreateContext();
        var stopped = false;
        var store = new CancelDispatchIntentTestStore { ThrowOnEnqueue = true };

        var error = Xunit.Assert.Throws<IOException>(() => CliCommandHandlers.ExecuteCancelDispatch(
            ["cancel-dispatch", "1"], context, store, _ => stopped = true));

        Xunit.Assert.Contains("intent append failed", error.Message, StringComparison.Ordinal);
        Xunit.Assert.False(stopped);
        Xunit.Assert.Equal(WorkTaskStatus.Running, task.Status);
        Xunit.Assert.False(task.LastProcess!.WasCancelled);
    }

    [Xunit.Theory]
    [Xunit.InlineData("refresh-dispatch")]
    [Xunit.InlineData("refresh-dispatches")]
    public async Task PendingCancelIntentBlocksDirectCliRefresh(string command)
    {
        var (context, goal, task) = CreateContext(DateTimeOffset.UtcNow.AddHours(-2));
        var store = SqliteOperatorIntentStore.ForDirectories(
            context.Workspace.OrchestratorDirectory, context.Workspace.LogDirectory);
        await store.EnqueueAsync(BackgroundDispatchRunnerTestsOperatorCancelRequeueRace.Intent(goal, task));
        File.WriteAllText(task.LastProcess!.ExitCodePath, "1");

        try
        {
            CliCommandHandlers.Execute(command == "refresh-dispatch"
                ? [command, "1"] : [command], context);

            Xunit.Assert.Equal(WorkTaskStatus.Running, task.Status);
            Xunit.Assert.DoesNotContain(goal.Timeline, evt => evt.Kind == ProgressKind.TaskRetried);
        }
        finally
        {
            File.Delete(task.LastProcess.ExitCodePath);
        }
    }

    private static (CliExecutionContext Context, Goal Goal, TaskSpec Task) CreateContext(DateTimeOffset? startedAt = null)
    {
        var (kernel, goal, task) = BackgroundDispatchRunnerTestsOperatorCancelRequeueRace.RunningTask(startedAt);
        var workspace = OrchestratorWorkspace.ForDirectory(
            Path.Combine(Path.GetTempPath(), $"cancel-dispatch-test-{Guid.NewGuid():N}"));
        var context = new CliExecutionContext(kernel, workspace,
            new InMemoryModelProviderRegistry([]), AgentCatalog.Default().Agents,
            WorkerProfileCatalog.Default(), goal);
        return (context, goal, task);
    }
}
