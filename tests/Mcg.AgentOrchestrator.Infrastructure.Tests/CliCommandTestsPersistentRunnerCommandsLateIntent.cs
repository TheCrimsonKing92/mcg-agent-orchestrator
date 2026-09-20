using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.CliProcessEnvironment)]
public sealed class CliCommandTestsPersistentRunnerCommandsLateIntent : CliCommandTestBase
{
    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task MissingGoalRequiresSuccessfulReloadBeforeRejection(bool reloadSucceeds)
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = CreateRefinedWorkspace(root);
            var kernel = new AgentOrchestratorKernel();
            var repository = new InMemoryTransactionalStateRepository(kernel);
            var store = SqliteOperatorIntentStore.ForDirectories(workspace.OrchestratorDirectory, workspace.LogDirectory);
            var intent = new OperatorIntentRecord("missing-goal", "missing-goal-key", OperatorIntentVerbs.Progress,
                GoalId.New().Value, TaskId.New().Value,
                JsonSerializer.Serialize(new ProgressOperatorIntentPayload(WorkTaskStatus.Completed, "must not invent a goal"), OperatorIntentJson.Options),
                [], "operator", "test", "test", DateTimeOffset.UtcNow);
            await store.EnqueueAsync(intent);
            var persistCalls = 0;
            var context = new CliExecutionContext(kernel, workspace, new InMemoryModelProviderRegistry([]),
                AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(), currentGoal: null,
                reloadKernel: () => CliPersistentStateRunner.LoadConductLoopKernel(repository),
                persistKernel: _ => persistCalls++, persistGoalKernel: (_, _) => persistCalls++,
                reloadResolvedParkedHumanWaitKernel: () => new AgentOrchestratorKernel(),
                reloadParkedGoalSafetyNetKernel: () => new AgentOrchestratorKernel(),
                reloadKernelForGoals: ids => reloadSucceeds
                    ? CliPersistentStateRunner.LoadConductLoopKernel(repository, ids)
                    : throw new IOException("Injected reload read failure"));

            _ = CaptureConsole(() => CliCommandHandlers.Execute(
                ["conduct", "--loop", "--policy", "Manual", "--max-iterations", "1"], context));

            var outcome = await store.GetAsync(intent.Id);
            Xunit.Assert.Equal(reloadSucceeds ? OperatorIntentStatus.Rejected : OperatorIntentStatus.Pending, outcome!.Status);
            Xunit.Assert.Equal(0, persistCalls);
            Xunit.Assert.Empty(kernel.Goals);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task ParkedGoalIntentArrivingAtReloadBoundaryAppliesAfterDurableCheckpoint(bool arriveAfterReload)
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = CreateRefinedWorkspace(root);
            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(TaskId.New(), "Record existing tester evidence", AgentRole.Tester);
            var goal = kernel.CreateGoal("Keep parked recovery deliverable", [task]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Failed, "Awaiting operator evidence");
            kernel.ParkGoal(goal.Id, "Preserve pending review");
            var repository = new InMemoryTransactionalStateRepository(kernel);
            var loopKernel = CliPersistentStateRunner.LoadConductLoopKernel(repository);
            Xunit.Assert.Empty(loopKernel.Goals);
            var store = SqliteOperatorIntentStore.ForDirectories(workspace.OrchestratorDirectory, workspace.LogDirectory);
            var verification = new TaskVerificationRecord("manual-verification", root, 0,
                "Operator observed the required behavior", string.Empty, DateTimeOffset.UtcNow);
            var intent = new OperatorIntentRecord("parked-boundary", "parked-boundary-key",
                OperatorIntentVerbs.VerifyManual, goal.Id.Value, task.Id.Value,
                JsonSerializer.Serialize(new ManualVerificationOperatorIntentPayload(verification), OperatorIntentJson.Options),
                [], "operator", "test", "test", DateTimeOffset.UtcNow);
            var enqueued = !arriveAfterReload;
            if (enqueued) await store.EnqueueAsync(intent);
            var checkpointCount = 0;
            AgentOrchestratorKernel Reload(IReadOnlyCollection<string> trackedIds)
            {
                var ids = store.ListActionableGoalIdsAsync().GetAwaiter().GetResult().Concat(trackedIds).Distinct().ToArray();
                var loaded = CliPersistentStateRunner.LoadConductLoopKernel(repository, ids);
                if (!enqueued)
                {
                    // Deterministically put arrival after hydration and before prewalk's second inbox query.
                    store.EnqueueAsync(intent).GetAwaiter().GetResult();
                    enqueued = true;
                }
                return loaded;
            }
            var context = new CliExecutionContext(loopKernel, workspace, new InMemoryModelProviderRegistry([]),
                AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(), currentGoal: null,
                reloadKernel: () => Reload([]), persistKernel: _ => { },
                persistGoalKernel: (current, ids) =>
                {
                    Xunit.Assert.NotEqual(OperatorIntentStatus.Applied, store.GetAsync(intent.Id).GetAwaiter().GetResult()!.Status);
                    repository.SaveGoalSnapshotsAsync(current.ExportSnapshot().Goals.Where(item => ids.Any(id => id.Value == item.Id)).ToArray()).GetAwaiter().GetResult();
                    checkpointCount++;
                },
                reloadResolvedParkedHumanWaitKernel: () => new AgentOrchestratorKernel(),
                reloadParkedGoalSafetyNetKernel: () => new AgentOrchestratorKernel(),
                reloadKernelForGoals: Reload);

            _ = CaptureConsole(() => CliCommandHandlers.Execute(
                ["conduct", "--loop", "--policy", "Manual", "--max-iterations", "2"], context));

            var outcome = await store.GetAsync(intent.Id);
            Xunit.Assert.Equal(OperatorIntentStatus.Applied, outcome!.Status);
            Xunit.Assert.Equal(1, checkpointCount);
            var stored = (await repository.LoadAsync()).GetGoal(goal.Id);
            Xunit.Assert.Equal(GoalStatus.Parked, stored.Status);
            Xunit.Assert.Equal(WorkTaskStatus.Completed, stored.Tasks.Single().Status);
            var recorded = stored.Tasks.Single().LastVerification;
            Xunit.Assert.NotNull(recorded);
            Xunit.Assert.Equal(verification.Command, recorded.Command);
            Xunit.Assert.Equal(verification.CompletedAt, recorded.CompletedAt);
            Xunit.Assert.Equal(verification.ExitCode, recorded.ExitCode);
            Xunit.Assert.Equal(verification.AuthoritativeStandardOutput, recorded.AuthoritativeStandardOutput);
            Xunit.Assert.Null(stored.Tasks.Single().LastDispatch);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
