using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ConductorBatchLoopVerificationReconcileTests
{
    private static IReadOnlyList<AgentDefinition> DefaultAgents() => AgentCatalog.Default().Agents;

    private static string TempDb() =>
        Path.Combine(Path.GetTempPath(), $"mcg-verification-reconcile-{Guid.NewGuid():N}.db");

    private static string NoStopPath() =>
        Path.Combine(Path.GetTempPath(), $"conduct-stop-{Guid.NewGuid():N}.txt");

    private static ConductorDriver MakeDriver() =>
        new(
            _ => GoalLifecycleFacts.None,
            () => 0,
            _ => "/tmp/workspace",
            _ => DispatchStartOutcome.Started(),
            null,
            null,
            _ => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
            null,
            null,
            null,
            null,
            null,
            _ => new GoalWorktreeRebaseResult(GoalWorktreeRebaseStatus.AlreadyFastForwardable, "goal/test", "OK", [], null),
            (goal, _) => new LandingResult(goal.Id.Value, goal.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "Landed"),
            null,
            _ => { },
            _ => new GoalWorktreeRemoveResult("Workspace cleaned up.", null, [], null),
            (_, _, _) => { },
            _ => null);

    [Xunit.Fact(DisplayName = "BatchLoop_reconciles_all_passed_completed_tasks_to_verified_in_one_tick")]
    public async Task BatchLoopReconcilesAllPassedCompletedTasksToVerifiedInOneTick()
    {
        var repo = CreateMigratedStateRepository(TempDb());
        var seed = new AgentOrchestratorKernel();
        var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(seed, DefaultAgents(), "Promote all-passed goal");
        var task = goal.Tasks.Single();
        seed.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec", "C:\\work", DateTimeOffset.UtcNow));
        seed.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord("dotnet test", "C:\\work", 0, "ok", "", DateTimeOffset.UtcNow));
        var activeSnapshot = seed.ExportSnapshot().Goals.Single() with { Status = GoalStatus.Active };
        await repo.SaveGoalSnapshotsAsync([activeSnapshot]);

        var kernel = await repo.LoadAsync();
        var baselines = kernel.ExportSnapshot().Goals.ToDictionary(snapshot => snapshot.Id, StringComparer.Ordinal);
        void PersistGoalTick(AgentOrchestratorKernel checkpoint, IReadOnlyCollection<GoalId> changedGoalIds)
        {
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

        new ConductorBatchLoop().Run(
            kernel,
            MakeDriver(),
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 1,
            persistGoalTick: PersistGoalTick);

        var reloaded = await repo.LoadAsync();
        var reloadedGoal = reloaded.GetGoal(goal.Id);
        Assert.Equal(GoalStatus.Verified, reloadedGoal.Status);
        Assert.Contains(reloadedGoal.Timeline, evt =>
            evt.Kind == ProgressKind.GoalPolicyDecision &&
            evt.Message.Contains("reconciled all task verification gates", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "BatchLoop_failed_verification_hold_remains_visible_on_subsequent_ticks")]
    public void BatchLoopFailedVerificationHoldRemainsVisibleOnSubsequentTicks()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "Keep failed verification visible");
        var task = goal.Tasks.Single();
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Worker finished.");
        kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord("dotnet test", "C:\\work", 1, "", "failed", DateTimeOffset.UtcNow));
        var ticks = new List<BatchTickSummary>();

        new ConductorBatchLoop().Run(
            kernel,
            MakeDriver(),
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 2,
            watchInterval: TimeSpan.FromMilliseconds(1),
            sleepFunc: _ => false,
            onTick: ticks.Add);

        Assert.Equal(GoalStatus.Active, goal.Status);
        var heldTicks = ticks.Count(tick => tick.ProgressLines.Any(line =>
            line.Contains($"GOAL goal={goal.Id.Value[..8]}", StringComparison.Ordinal) &&
            line.Contains("result=held", StringComparison.Ordinal) &&
            line.Contains("state=AwaitingVerification", StringComparison.Ordinal)));
        Assert.Equal(2, heldTicks);
    }

    [Xunit.Fact(DisplayName = "Tick_merge_reconciles_verified_status_from_stored_row_when_snapshot_is_stale")]
    public async Task TickMergeReconcilesVerifiedStatusFromStoredRowWhenSnapshotIsStale()
    {
        var repo = CreateMigratedStateRepository(TempDb());
        var seed = new AgentOrchestratorKernel();
        var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(seed, DefaultAgents(), "Merge stored verification");
        var task = goal.Tasks.Single();
        seed.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Worker finished.");
        await repo.SaveAsync(seed);
        var baseline = seed.ExportSnapshot().Goals.Single();

        var tickKernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([baseline], []));
        tickKernel.RecordGoalPolicyDecision(goal.Id, "Batch loop tick 1: held at AwaitingVerification.");
        var staleTickSnapshot = tickKernel.ExportSnapshot().Goals.Single();

        await repo.TransactGoalAsync<bool>(
            goal.Id,
            (stored, _) =>
            {
                var storedKernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([stored!], []));
                storedKernel.RecordTaskVerification(
                    goal.Id,
                    task.Id,
                    new TaskVerificationRecord("dotnet test", "C:\\work", 0, "ok", "", DateTimeOffset.UtcNow));
                var storedActiveSnapshot = storedKernel.ExportSnapshot().Goals.Single() with { Status = GoalStatus.Active };
                return Task.FromResult((true, storedActiveSnapshot, true));
            });

        var results = await repo.SaveGoalSnapshotsWithMergeAsync([new GoalSnapshotSaveRequest(baseline, staleTickSnapshot)]);

        var result = Assert.Single(results);
        Assert.Equal(GoalSnapshotSaveDisposition.Merged, result.Disposition);
        Assert.Contains("reconciled stored all-task verification gates to Verified", result.Message, StringComparison.Ordinal);
        var reloadedGoal = (await repo.LoadAsync()).GetGoal(goal.Id);
        Assert.Equal(GoalStatus.Verified, reloadedGoal.Status);
        Assert.Contains(reloadedGoal.Timeline, evt =>
            evt.Kind == ProgressKind.GoalPolicyDecision &&
            evt.Message.Contains("Tick merge reconciled stored all-task verification gates", StringComparison.Ordinal));
    }
}
