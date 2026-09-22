using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ConductorBatchLoopVerificationReconcileTests
{
    private static IReadOnlyList<AgentDefinition> DefaultAgents() => AgentCatalog.Default().Agents;

    private static WorkerContextPackageReceipt EarlyConvergenceReceipt(string candidateSha)
    {
        var evidenceHash = new string('a', 64);
        return new WorkerContextPackageReceipt(
            "ctxpkg-verification-reconcile",
            [new WorkerContextSectionReceipt(
                $"goal/review-finding-receipts/{evidenceHash}.json",
                1,
                1,
                evidenceHash,
                ContextDeliveryMode.OnDemandFile,
                ContextContractVersion.V1.Value,
                [AgentRole.Developer])],
            ProviderUsageValue.Unknown("test"),
            ProviderUsageValue.Unknown("test"),
            ProviderUsageValue.Unknown("test"),
            EarlyConvergenceEligible: true,
            EarlyConvergenceCandidateSha: candidateSha,
            EarlyConvergenceReceiptHashes: [evidenceHash]);
    }

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
        RunOneTick(kernel, repo);

        var reloadedGoal = (await repo.LoadAsync()).GetGoal(goal.Id);
        Assert.Equal(GoalStatus.Verified, reloadedGoal.Status);
        Assert.Contains(reloadedGoal.Timeline, evt =>
            evt.Message.Contains("reconciled all task verification gates", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "BatchLoop_reconciles_verified_no_change_dispatch_to_verified_after_reload")]
    public async Task BatchLoopReconcilesVerifiedNoChangeDispatchToVerifiedAfterReload()
    {
        var repo = CreateMigratedStateRepository(TempDb());
        var seed = new AgentOrchestratorKernel();
        var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(seed, DefaultAgents(), "Promote classified successful goal");
        var task = goal.Tasks.Single();
        const string baseCommit = "48422231916172e8d172a0cc0428d13d222c071c";
        seed.RecordTaskDispatch(
            goal.Id,
            task.Id,
            new TaskDispatchRecord(
                "codex-cli",
                "codex exec",
                "C:\\work",
                DateTimeOffset.UtcNow,
                ContextPackageReceipt: EarlyConvergenceReceipt(baseCommit)));
        seed.RecordDispatchBaseCommit(goal.Id, task.Id, baseCommit);
        seed.RecordCriterionRetryFeedback(goal.Id, task.Id, ["Re-run verification against the current candidate."]);
        seed.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
            "codex exec",
            "C:\\work",
            1,
            "WORKER_RESULT:\nfiles: none\ntests: pass - focused verification completed\nblockers: none\nEND_WORKER_RESULT",
            DispatchRejectionDiagnosticMarker.Format(true, 0, "none") + Environment.NewLine +
            DispatchFailureDiagnosticMarker.Format(DispatchFailureDiagnosticMarker.RequiredFileChangeEvidenceMissing),
            DateTimeOffset.UtcNow,
            WorkerResultPresent: true));
        Assert.Equal(WorkTaskStatus.Completed, task.Status);
        Assert.Equal(1, task.LastVerification!.ExitCode);
        var activeSnapshot = seed.ExportSnapshot().Goals.Single() with { Status = GoalStatus.Active };
        await repo.SaveGoalSnapshotsAsync([activeSnapshot]);

        var kernel = await repo.LoadAsync();
        var restoredVerification = kernel.GetTask(goal.Id, task.Id).LastVerification!;
        Assert.True(restoredVerification.CompletionVerdictVerifiedSuccess);
        Assert.Equal("verified-no-change-round", restoredVerification.CompletionVerdictRule);
        RunOneTick(kernel, repo);

        var reloaded = await repo.LoadAsync();
        var reloadedGoal = reloaded.GetGoal(goal.Id);
        Assert.Equal(GoalStatus.Verified, reloadedGoal.Status);
        Assert.Contains(reloadedGoal.Timeline, evt =>
            evt.Kind == ProgressKind.GoalPolicyDecision &&
            evt.Message.Contains("reconciled all task verification gates", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "ReconcileGoalVerificationStatus_does_not_advance_incomplete_goal")]
    public void ReconcileGoalVerificationStatusDoesNotAdvanceIncompleteGoal()
    {
        var kernel = new AgentOrchestratorKernel();
        var completed = new TaskSpec(TaskId.New(), "Complete this task", AgentRole.Developer);
        var pending = new TaskSpec(TaskId.New(), "Leave this task pending", AgentRole.Tester);
        var goal = kernel.CreateGoal("Keep incomplete goal active", [completed, pending]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        kernel.ReportTaskProgress(goal.Id, completed.Id, WorkTaskStatus.Completed, "Worker finished.");
        kernel.RecordTaskVerification(goal.Id, completed.Id, new TaskVerificationRecord(
            "dotnet test", "C:\\work", 0, "ok", "", DateTimeOffset.UtcNow));

        var reconciled = kernel.ReconcileGoalVerificationStatus(goal.Id, "All task gates passed.");

        Assert.False(reconciled);
        Assert.NotEqual(GoalStatus.Verified, goal.Status);
        Assert.NotEqual(WorkTaskStatus.Completed, pending.Status);
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

    [Xunit.Fact]
    public void BatchLoopCancelledUnchangedRetryDoesNotReplayDownstream()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(
            "Keep verified downstream rounds after unchanged cancellation",
            [
                new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer),
                new TaskSpec(TaskId.New(), "Test", AgentRole.Tester),
                new TaskSpec(TaskId.New(), "Review", AgentRole.Reviewer)
            ]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        CompleteCandidate(kernel, goal, developer, "aaa111");
        CompleteCandidate(kernel, goal, tester, "aaa111");
        CompleteCandidate(kernel, goal, reviewer, "aaa111");
        kernel.RetryTask(goal.Id, developer.Id, "Inspect unchanged candidate.");
        kernel.RecordTaskDispatch(
            goal.Id,
            developer.Id,
            new TaskDispatchRecord(
                "codex-cli", "retry", "C:\\work", DateTimeOffset.UtcNow,
                BaseCommit: "aaa111", WorktreeHeadSha: "aaa111", DirtyStateHash: "empty-status-hash"));
        var process = new TaskProcessRecord(
            999999, "retry", "C:\\work", "out.log", "err.log", "exit.txt", DateTimeOffset.UtcNow, null, null);
        kernel.RecordTaskProcessStarted(goal.Id, developer.Id, process);
        kernel.RecordTaskProcessCancelled(
            goal.Id,
            developer.Id,
            process with { CompletedAt = DateTimeOffset.UtcNow, WasCancelled = true },
            CancellationCandidateEvidence.ConfirmedUnchanged("aaa111", "head=aaa111; worktree=clean; commits_after_dispatch=0"));
        var downstreamRetryEvents = goal.Timeline.Count(evt =>
            evt.Kind == ProgressKind.TaskRetried &&
            evt.TaskId is { } taskId &&
            (taskId == tester.Id || taskId == reviewer.Id));

        new ConductorBatchLoop().Run(
            kernel,
            MakeDriver(),
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 1);

        Assert.Equal(WorkTaskStatus.Completed, tester.Status);
        Assert.Equal(WorkTaskStatus.Completed, reviewer.Status);
        Assert.Equal(downstreamRetryEvents, goal.Timeline.Count(evt =>
            evt.Kind == ProgressKind.TaskRetried &&
            evt.TaskId is { } taskId &&
            (taskId == tester.Id || taskId == reviewer.Id)));
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

    [Xunit.Fact]
    public async Task TickMerge_AcceptanceFailedStoreState_RemainsFailed()
    {
        var repo = CreateMigratedStateRepository(TempDb());
        var seed = new AgentOrchestratorKernel();
        var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(seed, DefaultAgents(), "Preserve failed acceptance");
        var task = goal.Tasks.Single();
        seed.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Worker finished.");
        seed.RecordTaskVerification(
            goal.Id,
            task.Id,
            new TaskVerificationRecord("dotnet test", "C:\\work", 0, "ok", "", DateTimeOffset.UtcNow));
        await repo.SaveAsync(seed);
        var baseline = seed.ExportSnapshot().Goals.Single();

        var tickKernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([baseline], []));
        tickKernel.RecordGoalPolicyDecision(goal.Id, "Batch loop tick retained stale verified state.");
        var staleTickSnapshot = tickKernel.ExportSnapshot().Goals.Single();

        await repo.TransactGoalAsync<bool>(
            goal.Id,
            (stored, _) =>
            {
                var storedKernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([stored!], []));
                Xunit.Assert.True(storedKernel.BeginGoalAcceptanceVerification(goal.Id, "Run acceptance."));
                Xunit.Assert.True(storedKernel.ReconcileGoalAcceptanceFailed(
                    goal.Id,
                    ["infrastructure tests"],
                    "Acceptance failed.",
                    "candidate-sha",
                    "main-sha"));
                return Task.FromResult((true, storedKernel.ExportSnapshot().Goals.Single(), true));
            });

        var results = await repo.SaveGoalSnapshotsWithMergeAsync([new GoalSnapshotSaveRequest(baseline, staleTickSnapshot)]);

        Xunit.Assert.Equal(GoalSnapshotSaveDisposition.Merged, Xunit.Assert.Single(results).Disposition);
        var reloadedGoal = (await repo.LoadAsync()).GetGoal(goal.Id);
        Xunit.Assert.Equal(GoalStatus.AcceptanceFailed, reloadedGoal.Status);
        Xunit.Assert.NotNull(reloadedGoal.LatestAcceptanceFailure);
    }

    private static void RunOneTick(
        AgentOrchestratorKernel kernel,
        SqliteOrchestratorStateRepository repo)
    {
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
    }

    private static void CompleteCandidate(
        AgentOrchestratorKernel kernel,
        Goal goal,
        TaskSpec task,
        string candidate)
    {
        kernel.RecordTaskDispatch(
            goal.Id,
            task.Id,
            new TaskDispatchRecord(
                task.RequiredRole.ToString(), "worker", "C:\\work", DateTimeOffset.UtcNow,
                BaseCommit: candidate, ResultCommit: candidate));
        kernel.RecordTaskVerification(
            goal.Id,
            task.Id,
            new TaskVerificationRecord(
                "verification", "C:\\work", 0, "passed", "", DateTimeOffset.UtcNow,
                ReviewedCommit: candidate));
    }
}
